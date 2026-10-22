using System.Diagnostics;
using System.IO;

namespace RainExplorer.Services;

/// <summary>Starts shell targets without changing Rain Explorer's own current directory.</summary>
internal static class ProcessLauncher
{
    private static readonly HashSet<string> DirectExecutableExtensions = new(
        StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".scr",
    };

    public static Process? StartShell(string path) => Process.Start(CreateShellStartInfo(path));

    internal static ProcessStartInfo CreateOpenWithStartInfo(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var start = new ProcessStartInfo("rundll32.exe") { UseShellExecute = true };
        start.ArgumentList.Add("shell32.dll,OpenAs_RunDLL");
        start.ArgumentList.Add(path);
        return start;
    }

    internal static ProcessStartInfo CreateShellStartInfo(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var start = new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        };

        // Shell-associated documents and shortcuts retain their native behavior. Direct
        // executables get the same working directory users expect when opening them from
        // their containing folder, without changing Environment.CurrentDirectory.
        if (GetExecutableWorkingDirectory(path) is { } directory)
            start.WorkingDirectory = directory;

        return start;
    }

    internal static string? GetExecutableWorkingDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !Path.IsPathFullyQualified(path)
            || !DirectExecutableExtensions.Contains(Path.GetExtension(path)))
            return null;

        try
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
            return string.IsNullOrWhiteSpace(directory) ? null : directory;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    internal static string? GetShellVerbWorkingDirectory(string path, string? canonicalVerb)
    {
        if (!string.Equals(canonicalVerb, "open", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(canonicalVerb, "runas", StringComparison.OrdinalIgnoreCase))
            return null;

        return GetExecutableWorkingDirectory(path);
    }
}
