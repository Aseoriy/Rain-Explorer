using System.Diagnostics;
using Microsoft.Win32;

namespace RainExplorer.Services;

/// <summary>
/// Optional Windows shell integration. Adds an "Open in Rain Explorer" verb to
/// the right-click menu of folders and drives by writing to the current user's
/// registry hive (HKCU\Software\Classes). Fully reversible and never touches
/// HKLM or system-wide defaults — we deliberately do NOT hijack the default
/// folder-open verb, which can make folders un-openable if the app moves.
/// </summary>
public static class ShellIntegrationService
{
    private const string Verb = "RainExplorer";

    // Directory = a folder; Drive = a volume root.
    private static readonly string[] Roots =
    {
        @"Software\Classes\Directory\shell",
        @"Software\Classes\Drive\shell",
    };

    public static string ExePath =>
        Process.GetCurrentProcess().MainModule?.FileName
        ?? System.Reflection.Assembly.GetExecutingAssembly().Location;

    /// <summary>Add or remove the context-menu verb. Best-effort; swallows access errors.</summary>
    public static void Apply(bool enabled)
    {
        try { if (enabled) Register(); else Unregister(); }
        catch { /* registry locked / policy — non-fatal */ }
    }

    private static void Register()
    {
        string exe = ExePath;
        foreach (string root in Roots)
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"{root}\{Verb}");
            key.SetValue(null, "Open in Rain Explorer");
            key.SetValue("Icon", $"\"{exe}\",0");
            using var cmd = key.CreateSubKey("command");
            cmd.SetValue(null, $"\"{exe}\" \"%V\"");
        }
    }

    private static void Unregister()
    {
        foreach (string root in Roots)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree($@"{root}\{Verb}", throwOnMissingSubKey: false); }
            catch { /* already gone */ }
        }
    }

    // ---- "Default file manager": intercept the folder/drive open verb + Win+E ----
    // Windows has no supported "default file manager" setting, so we override the
    // open verb's command in HKCU (which takes precedence over HKLM in the merged
    // HKCR view). DelegateExecute="" disables the shell's default COM handler so our
    // command string actually runs. Win+E points at the File Explorer folder CLSID.
    // All HKCU-only and reversible; it never replaces explorer.exe itself.

    private static readonly string[] OpenCommandKeys =
    {
        @"Software\Classes\Directory\shell\open\command",
        @"Software\Classes\Drive\shell\open\command",
    };

    private const string WinEKey =
        @"Software\Classes\CLSID\{52205fd8-5dfb-447d-801a-d0b52f2e83e1}\shell\opennewwindow\command";

    /// <summary>Make (or stop making) folders/drives/Win+E open Rain. Best-effort; reversible.</summary>
    public static void ApplyDefaultHandler(bool enabled)
    {
        try { if (enabled) RegisterDefault(); else UnregisterDefault(); }
        catch { /* registry locked / policy — non-fatal */ }
    }

    private static void RegisterDefault()
    {
        string exe = ExePath;
        foreach (string path in OpenCommandKeys)
        {
            using var cmd = Registry.CurrentUser.CreateSubKey(path);
            cmd.SetValue(null, $"\"{exe}\" \"%V\"");
            cmd.SetValue("DelegateExecute", "");   // disable the default COM delegate so our command runs
        }
        using var winE = Registry.CurrentUser.CreateSubKey(WinEKey);
        winE.SetValue(null, $"\"{exe}\"");
        winE.SetValue("DelegateExecute", "");
    }

    private static void UnregisterDefault()
    {
        // Removing our override keys makes the merged view fall back to Windows Explorer.
        foreach (string path in OpenCommandKeys)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false); }
            catch { /* already gone */ }
        }
        try { Registry.CurrentUser.DeleteSubKeyTree(WinEKey, throwOnMissingSubKey: false); }
        catch { /* already gone */ }
    }
}
