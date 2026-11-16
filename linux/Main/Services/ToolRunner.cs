using System.Diagnostics;

namespace RainExplorer.Services;

/// <summary>
/// Runs a small command-line helper (pdftoppm, ffprobe, git…) without a shell and collects its output.
/// Arguments are passed one by one, so file names never need quoting. A missing tool, a non-zero exit or
/// a timeout all come back as a failed <see cref="Result"/>; cancellation kills the process and throws.
/// </summary>
internal static class ToolRunner
{
    public sealed record Result(bool Started, int ExitCode, byte[] Output, string Error)
    {
        public bool Succeeded => Started && ExitCode == 0;
        public string Text => System.Text.Encoding.UTF8.GetString(Output);
    }

    public static bool IsAvailable(string tool) => ProcessLauncher.FindExecutable(tool) is not null;

    public static async Task<Result> RunAsync(
        string tool, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct,
        string? workingDirectory = null, IReadOnlyDictionary<string, string>? environment = null,
        long maxOutputBytes = 64L * 1024 * 1024)
    {
        string? exe = ProcessLauncher.FindExecutable(tool);
        if (exe is null) return new Result(false, -1, [], $"{tool} is not installed.");

        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? XdgDirs.Home,
        };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        if (environment is not null)
            foreach (var (key, value) in environment) start.Environment[key] = value;

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) return new Result(false, -1, [], $"Couldn't start {tool}.");
        }
        catch (Exception ex)
        {
            return new Result(false, -1, [], ex.Message);
        }
        process.StandardInput.Close();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            Task<byte[]> stdout = ReadAllAsync(process.StandardOutput.BaseStream, maxOutputBytes, timeoutCts.Token);
            Task<string> stderr = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            byte[] output = await stdout.ConfigureAwait(false);
            string error = await stderr.ConfigureAwait(false);
            return new Result(true, process.ExitCode, output, error);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            ct.ThrowIfCancellationRequested();
            return new Result(true, -1, [], $"{tool} took too long.");
        }
        catch (Exception ex)
        {
            Kill(process);
            return new Result(true, -1, [], ex.Message);
        }
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream, long max, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > max) throw new InvalidOperationException("Output too large.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }
}
