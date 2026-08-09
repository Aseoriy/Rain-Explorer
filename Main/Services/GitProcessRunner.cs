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
    bool WasCanceled)
{
    public bool Succeeded => ExitCode == 0 && !WasCanceled;
    public string StandardOutputText => Encoding.UTF8.GetString(StandardOutput);
}

public interface IGitProcessRunner
{
    Task<GitProcessResult> RunAsync(GitProcessRequest request, CancellationToken cancellationToken);
}

public sealed class GitProcessRunner : IGitProcessRunner
{
    private const int MaxErrorLength = 32_768;

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
        };
        foreach (string argument in request.Arguments)
            start.ArgumentList.Add(argument);

        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "auto";
        start.Environment["LC_ALL"] = "C";
        if (request.ReadOnly) start.Environment["GIT_OPTIONAL_LOCKS"] = "0";

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
                return new GitProcessResult(-1, [], "Git could not be started.", false);
        }
        catch (Exception ex)
        {
            return new GitProcessResult(-1, [], GitSecurity.Redact(ex.Message), false);
        }

        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        var output = new MemoryStream();
        Task outputTask = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
        Task inputTask = WriteInputAsync(process, request.StandardInput, cancellationToken);

        bool canceled = false;
        try
        {
            await Task.WhenAll(inputTask, outputTask, process.WaitForExitAsync(cancellationToken));
        }
        catch (OperationCanceledException)
        {
            canceled = true;
            TryKill(process);
            try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
        }
        catch (Exception ex)
        {
            TryKill(process);
            string error = GitSecurity.Redact(ex.Message);
            return new GitProcessResult(process.HasExited ? process.ExitCode : -1,
                output.ToArray(), error, false);
        }

        string stderr;
        try { stderr = await errorTask; }
        catch { stderr = string.Empty; }
        stderr = GitSecurity.Redact(stderr.Trim());
        if (stderr.Length > MaxErrorLength) stderr = stderr[..MaxErrorLength] + "…";

        return new GitProcessResult(
            process.HasExited ? process.ExitCode : -1,
            output.ToArray(),
            stderr,
            canceled);
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
