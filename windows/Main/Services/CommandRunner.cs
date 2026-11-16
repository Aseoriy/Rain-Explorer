using System.Diagnostics;
using System.IO;
using System.Text;
using RainExplorer.Models;

namespace RainExplorer.Services;

/// <summary>
/// Runs one command line typed into Rain's command box (Git window), in a folder, through the shell picked
/// in Settings (cmd or PowerShell), and streams its output. There is no console attached: stdin is closed
/// and Git credential prompts are disabled, so programs that need to ask something fail fast instead of
/// hanging. Use "Open terminal" for those. Port of the Linux CommandRunner (which uses bash).
/// </summary>
public static class CommandRunner
{
    public static bool UsesPowerShell =>
        SettingsStore.Instance.Settings.TerminalApp == TerminalApp.PowerShell;

    /// <summary>The prompt shown before each command in the output.</summary>
    public static string Prompt => UsesPowerShell ? "PS>" : ">";

    internal static ProcessStartInfo BuildStartInfo(string command, string workingDirectory, bool powerShell)
    {
        var start = new ProcessStartInfo
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

        if (powerShell)
        {
            // -EncodedCommand avoids every quoting rule between .NET, the command line and PowerShell.
            string script = "[Console]::OutputEncoding = [Text.Encoding]::UTF8; $ProgressPreference = 'SilentlyContinue'; "
                + command;
            start.FileName = "powershell.exe";
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-EncodedCommand");
            start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        }
        else
        {
            // /s keeps everything between the outer quotes exactly as typed (quotes, &, &&, pipes).
            // chcp makes the programs it starts (git, python, …) print UTF-8. cmd's own built-ins (echo,
            // dir) keep the code page cmd started with, so their non-ASCII text can come out garbled;
            // running them in a second cmd would fix that but breaks quoted & and | in the command.
            start.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            start.Arguments = $"/d /s /c \"chcp 65001 >nul & {command}\"";
        }

        // No console: make tools print plainly and never wait for input.
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "never";
        start.Environment["GIT_PAGER"] = "cat";
        start.Environment["PAGER"] = "cat";
        start.Environment["GIT_EDITOR"] = "true";
        start.Environment["GH_PROMPT_DISABLED"] = "1";
        return start;
    }

    /// <summary>Run <paramref name="command"/>; returns the exit code, or null when it couldn't start.</summary>
    public static Task<int?> RunAsync(string command, string workingDirectory, Action<string> onOutput,
        CancellationToken ct) =>
        RunAsync(command, workingDirectory, onOutput, UsesPowerShell, ct);

    internal static async Task<int?> RunAsync(string command, string workingDirectory, Action<string> onOutput,
        bool powerShell, CancellationToken ct)
    {
        using var process = new Process { StartInfo = BuildStartInfo(command, workingDirectory, powerShell) };
        try
        {
            if (!process.Start()) return null;
        }
        catch (Exception ex)
        {
            onOutput($"Couldn't start {(powerShell ? "PowerShell" : "Command Prompt")}: {ex.Message}\n");
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
