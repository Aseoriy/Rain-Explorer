using System.Diagnostics;
using RainExplorer.Models;

namespace RainExplorer.Services;

/// <summary>Opens a shell at a given folder, honoring the user's cmd/PowerShell preference.</summary>
public static class TerminalLauncher
{
    public static bool TryOpen(string dir, out string? error)
    {
        error = null;
        string exe = SettingsStore.Instance.Settings.TerminalApp == TerminalApp.PowerShell
            ? "powershell.exe" : "cmd.exe";
        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = dir });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
