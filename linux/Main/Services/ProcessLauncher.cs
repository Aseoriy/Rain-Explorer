using System.Diagnostics;

namespace RainExplorer.Services;

/// <summary>Opens files and URLs with the desktop's default handler.</summary>
internal static class ProcessLauncher
{
    /// <summary>
    /// Open <paramref name="path"/> (a file, folder or URL) with its default application via <c>xdg-open</c>, which
    /// delegates to KDE, GNOME or the configured MIME handler. The path is passed as one
    /// argument (never through a shell) and the child starts in the file's folder.
    /// </summary>
    public static void OpenDefault(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var start = new ProcessStartInfo("xdg-open")
        {
            UseShellExecute = false,
            WorkingDirectory = Directory.Exists(path) ? path
                : File.Exists(path) ? Path.GetDirectoryName(path) ?? XdgDirs.Home
                : XdgDirs.Home,
        };
        start.ArgumentList.Add(path);
        StartDetached(start);
    }

    /// <summary>Start a process without keeping a handle or redirected streams open.</summary>
    public static void StartDetached(ProcessStartInfo start)
    {
        using var process = Process.Start(start)
                            ?? throw new InvalidOperationException($"Couldn't start {start.FileName}.");
    }

    /// <summary>Find an executable on $PATH (or accept an absolute path).</summary>
    public static string? FindExecutable(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (name.Contains('/'))
            return File.Exists(name) ? name : null;
        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar)) return null;
        foreach (string dir in pathVar.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
