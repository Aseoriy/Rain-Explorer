using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace RainExplorer.Services;

public sealed record GitProcessRequest(
    string ExecutablePath,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    byte[]? StandardInput = null,
    bool ReadOnly = false);

public sealed record GitProcessResult(
    int ExitCode,
    byte[] StandardOutput,
    string StandardError,
    bool WasCanceled,
    bool OutcomeUnknown = false)
{
    public bool Succeeded => ExitCode == 0 && !WasCanceled && !OutcomeUnknown;
    public string StandardOutputText => Encoding.UTF8.GetString(StandardOutput);
}

public interface IGitProcessRunner
{
    Task<GitProcessResult> RunAsync(GitProcessRequest request, CancellationToken cancellationToken);
}

public sealed class GitProcessRunner : IGitProcessRunner
{
    private const int MaxErrorLength = 32_768;
    private const int MaxOutputBytes = 64 * 1024 * 1024;
    private const int MaxErrorBytes = MaxErrorLength * 4;
    private static readonly TimeSpan TerminationTimeout = TimeSpan.FromSeconds(3);
    private static readonly ConcurrentDictionary<int, Task> LingeringProcesses = new();
    private static readonly SemaphoreSlim GitAdmission = new(1, 1);

    public async Task<GitProcessResult> RunAsync(
        GitProcessRequest request, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = request.ExecutablePath,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (string argument in request.Arguments)
            start.ArgumentList.Add(argument);

        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "auto";
        start.Environment["LC_ALL"] = "C";
        if (request.ReadOnly) start.Environment["GIT_OPTIONAL_LOCKS"] = "0";

        await GitAdmission.WaitAsync(cancellationToken);
        bool releaseAdmission = true;
        Process? ownedProcess = new Process { StartInfo = start, EnableRaisingEvents = true };
        Process process = ownedProcess;
        BoundedCaptureStream? ownedOutput = new(MaxOutputBytes);
        BoundedCaptureStream output = ownedOutput;
        BoundedCaptureStream? ownedError = new(MaxErrorBytes);
        BoundedCaptureStream errorOutput = ownedError;
        CancellationTokenSource? ownedIoCancellation = null;
        try
        {
            try
            {
                if (!process.Start())
                    return new GitProcessResult(-1, [], "Git could not be started.", false);
            }
            catch (Exception ex)
            {
                return new GitProcessResult(-1, [], GitSecurity.Redact(ex.Message), false);
            }

            // Keep draining stderr after cancellation so a child process cannot block on a
            // full redirected pipe while it is being terminated.
            Task errorTask = process.StandardError.BaseStream.CopyToAsync(
                errorOutput, CancellationToken.None);
            CancellationTokenSource ioCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ownedIoCancellation = ioCancellation;
            // Keep draining stdout independently of request cancellation. An escaped hook
            // holding this pipe open is also how quarantine knows the process tree is not
            // fully gone yet.
            Task outputTask = process.StandardOutput.BaseStream.CopyToAsync(
                output, CancellationToken.None);
            Task inputTask = WriteInputAsync(process, request.StandardInput, ioCancellation.Token);

            bool canceled = false;
            bool outcomeUnknown = false;
            try
            {
                await process.WaitForExitAsync(cancellationToken);
                try
                {
                    await Task.WhenAll(inputTask, outputTask, errorTask)
                        .WaitAsync(TerminationTimeout, cancellationToken);
                }
                catch (TimeoutException)
                {
                    // The root exited but a hook/descendant still owns a redirected pipe.
                    // Keep the process resources alive and quarantine subsequent Git work.
                    outcomeUnknown = true;
                }
            }
            catch (OperationCanceledException)
            {
                canceled = true;
                await ioCancellation.CancelAsync();
                outcomeUnknown = !await TerminateAndDrainAsync(
                    process, inputTask, outputTask, errorTask);
            }
            catch (Exception ex)
            {
                await ioCancellation.CancelAsync();
                bool stopped = await TerminateAndDrainAsync(
                    process, inputTask, outputTask, errorTask);
                string error = GitSecurity.Redact(ex.Message);
                if (!stopped)
                    error += " Git is still shutting down, so its result is unknown.";
                byte[] capturedOutput = outputTask.IsCompleted ? output.ToArray() : [];
                int exitCode = ExitCodeOrMinusOne(process);
                if (!stopped)
                {
                    TrackLingeringOperation(
                        process, ioCancellation, output, errorOutput,
                        inputTask, outputTask, errorTask);
                    releaseAdmission = false;
                    ownedProcess = null;
                    ownedIoCancellation = null;
                    ownedOutput = null;
                    ownedError = null;
                }
                return new GitProcessResult(exitCode,
                    capturedOutput, error, false, OutcomeUnknown: !stopped);
            }

            string stderr;
            if (outcomeUnknown && !errorTask.IsCompleted)
                stderr = "Git is still shutting down, so its result is unknown.";
            else
            {
                try
                {
                    await errorTask.WaitAsync(TerminationTimeout, CancellationToken.None);
                    stderr = Encoding.UTF8.GetString(errorOutput.ToArray());
                }
                catch
                {
                    stderr = canceled && !process.HasExited
                        ? "Git did not exit cleanly after cancellation. Its result is unknown."
                        : string.Empty;
                }
            }
            stderr = GitSecurity.Redact(stderr.Trim());
            if (outcomeUnknown && !stderr.Contains("result is unknown", StringComparison.OrdinalIgnoreCase))
                stderr = string.IsNullOrEmpty(stderr)
                    ? "Git is still shutting down, so its result is unknown."
                    : stderr + " Git is still shutting down, so its result is unknown.";
            if (stderr.Length > MaxErrorLength) stderr = stderr[..MaxErrorLength] + "…";

            byte[] standardOutput = outputTask.IsCompleted ? output.ToArray() : [];
            int finalExitCode = ExitCodeOrMinusOne(process);
            bool resultUnknown = outcomeUnknown;
            if (output.WasTruncated)
            {
                resultUnknown = true;
                stderr = string.IsNullOrEmpty(stderr)
                    ? "Git output exceeded Rain Explorer's safety limit, so the result is incomplete."
                    : stderr + " Git output exceeded Rain Explorer's safety limit, so the result is incomplete.";
            }
            if (outcomeUnknown)
            {
                TrackLingeringOperation(
                    process, ioCancellation, output, errorOutput,
                    inputTask, outputTask, errorTask);
                releaseAdmission = false;
                ownedProcess = null;
                ownedIoCancellation = null;
                ownedOutput = null;
                ownedError = null;
            }

            return new GitProcessResult(
                finalExitCode,
                standardOutput,
                stderr,
                canceled,
                resultUnknown);
        }
        finally
        {
            ownedIoCancellation?.Dispose();
            ownedError?.Dispose();
            ownedOutput?.Dispose();
            ownedProcess?.Dispose();
            if (releaseAdmission) GitAdmission.Release();
        }
    }

    private static async Task WriteInputAsync(
        Process process, byte[]? input, CancellationToken cancellationToken)
    {
        try
        {
            if (input is { Length: > 0 })
                await process.StandardInput.BaseStream.WriteAsync(input, cancellationToken);
        }
        finally
        {
            try { process.StandardInput.Close(); } catch { }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch { }
    }

    private static int ExitCodeOrMinusOne(Process process)
    {
        try { return process.HasExited ? process.ExitCode : -1; }
        catch { return -1; }
    }

    private static async Task<bool> TerminateAndDrainAsync(Process process, params Task[] pipeTasks)
    {
        TryKill(process);
        Task exitTask;
        try { exitTask = process.WaitForExitAsync(CancellationToken.None); }
        catch { exitTask = Task.CompletedTask; }

        var observed = new Task[pipeTasks.Length + 1];
        observed[0] = IgnoreFailureAsync(exitTask);
        for (int i = 0; i < pipeTasks.Length; i++)
            observed[i + 1] = IgnoreFailureAsync(pipeTasks[i]);

        try
        {
            await Task.WhenAll(observed)
                .WaitAsync(TerminationTimeout, CancellationToken.None);
        }
        catch (TimeoutException) { }
        try { return process.HasExited && pipeTasks.All(task => task.IsCompleted); }
        catch { return false; }
    }

    private static async Task IgnoreFailureAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch { }
    }

    private static void TrackLingeringOperation(
        Process process,
        CancellationTokenSource ioCancellation,
        BoundedCaptureStream output,
        BoundedCaptureStream errorOutput,
        params Task[] pipeTasks)
    {
        int operationId = Interlocked.Increment(ref _nextLingeringOperationId);
        var registered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task cleanup = ContinueCleanupAsync(
            operationId, process, ioCancellation, output, errorOutput,
            pipeTasks, registered.Task);
        if (!LingeringProcesses.TryAdd(operationId, cleanup))
        {
            registered.TrySetCanceled(CancellationToken.None);
            return;
        }
        registered.TrySetResult();
    }

    private static async Task ContinueCleanupAsync(
        int operationId,
        Process process,
        CancellationTokenSource ioCancellation,
        BoundedCaptureStream output,
        BoundedCaptureStream errorOutput,
        Task[] pipeTasks,
        Task registered)
    {
        bool removeRegistration = false;
        try
        {
            await registered.ConfigureAwait(false);
            removeRegistration = true;
            Task pipesFinished = Task.WhenAll(pipeTasks.Select(IgnoreFailureAsync));
            while (true)
            {
                bool processExited;
                try { processExited = process.HasExited; }
                catch { processExited = true; }
                if (!processExited) TryKill(process);
                if (processExited && pipesFinished.IsCompleted) break;
                await Task.WhenAny(
                    pipesFinished,
                    Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None)).ConfigureAwait(false);
            }
            await IgnoreFailureAsync(pipesFinished).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Registration failed; the caller still transferred resource ownership.
        }
        finally
        {
            ioCancellation.Dispose();
            errorOutput.Dispose();
            output.Dispose();
            process.Dispose();
            GitAdmission.Release();
            if (removeRegistration)
                LingeringProcesses.TryRemove(operationId, out _);
        }
    }

    private static int _nextLingeringOperationId;

    private sealed class BoundedCaptureStream(int capacity) : Stream
    {
        private readonly MemoryStream _captured = new(Math.Min(capacity, 64 * 1024));

        public bool WasTruncated { get; private set; }
        public byte[] ToArray() => _captured.ToArray();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _captured.Length;
        public override long Position
        {
            get => _captured.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            int remaining = capacity - checked((int)_captured.Length);
            int keep = Math.Min(remaining, buffer.Length);
            if (keep > 0) _captured.Write(buffer[..keep]);
            if (keep < buffer.Length) WasTruncated = true;
        }

        public override Task WriteAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _captured.Dispose();
            base.Dispose(disposing);
        }
    }
}

public static class GitSecurity
{
    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        string result = System.Text.RegularExpressions.Regex.Replace(
            value,
            @"(?i)(https?://)([^/\s:@]+):([^@\s/]+)@",
            "$1[redacted]@");
        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            @"(?i)(authorization\s*:\s*(?:bearer|token)\s+)[^\s]+",
            "$1[redacted]");
        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            @"(?i)\b(gh[opusr]_[A-Za-z0-9_]+|github_pat_[A-Za-z0-9_]+)\b",
            "[redacted-token]");
        return result;
    }
}
