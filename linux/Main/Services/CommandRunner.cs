using System.Diagnostics;
using System.Text;

namespace RainExplorer.Services;

/// <summary>
/// Runs one command line typed into Rain's command box, in a folder, through bash (or sh) and streams its
/// output. There is no terminal attached: stdin is closed and Git/SSH credential prompts are disabled, so
/// programs that need to ask something fail fast instead of hanging. Use "Open terminal" for those.
/// </summary>
public static class CommandRunner
{
    public static string Shell => ProcessLauncher.FindExecutable("bash") ?? "/bin/sh";

    /// <summary>Run <paramref name="command"/>; returns the exit code, or null when it couldn't start.</summary>
    public static async Task<int?> RunAsync(string command, string workingDirectory, Action<string> onOutput,
        CancellationToken ct)
    {
        var start = new ProcessStartInfo(Shell)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(command);
        // No terminal: make tools print plainly and never wait for input.
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_PAGER"] = "cat";
        start.Environment["PAGER"] = "cat";
        start.Environment["GIT_EDITOR"] = "true";
        start.Environment["TERM"] = "dumb";
        start.Environment["GH_PROMPT_DISABLED"] = "1";
        start.Environment.Remove("SSH_ASKPASS_REQUIRE");

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) return null;
        }
        catch (Exception ex)
        {
            onOutput($"Couldn't start {Shell}: {ex.Message}\n");
            return null;
        }
        process.StandardInput.Close();

        Task pumpOut = Pump(process.StandardOutput, onOutput);
        Task pumpErr = Pump(process.StandardError, onOutput);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            await Task.WhenAll(pumpOut, pumpErr).WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None)
                .ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        catch (TimeoutException)
        {
            // A background child kept the output open; the command itself finished.
            return process.HasExited ? process.ExitCode : null;
        }
    }

    private static async Task Pump(StreamReader reader, Action<string> onOutput)
    {
        char[] buffer = new char[2048];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
            onOutput(new string(buffer, 0, read));
    }
}
