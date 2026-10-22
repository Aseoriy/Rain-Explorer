using System.Diagnostics;

namespace RainExplorer.Services;

/// <summary>
/// Opens a terminal at a folder. Uses the terminal chosen in Settings when there is one,
/// otherwise the desktop's own terminal (Konsole on KDE, GNOME Console/Terminal on
/// GNOME), then other common emulators.
/// </summary>
public static class TerminalLauncher
{
    /// <summary>Known terminals, in auto-detect order, with the flag each uses for the start folder.
    /// A null flag means the terminal is started in the folder via its working directory.</summary>
    private static readonly (string Exe, string Label, string? DirFlag)[] Known =
    {
        ("konsole", "Konsole", "--workdir"),
        ("kgx", "GNOME Console", "--working-directory"),
        ("gnome-terminal", "GNOME Terminal", "--working-directory"),
        ("ptyxis", "Ptyxis", "--working-directory"),
        ("xfce4-terminal", "Xfce Terminal", "--working-directory"),
        ("tilix", "Tilix", "--working-directory"),
        ("kitty", "kitty", "--directory"),
        ("alacritty", "Alacritty", "--working-directory"),
        ("wezterm", "WezTerm", null),
        ("foot", "foot", null),
        ("x-terminal-emulator", "System default", null),
        ("xterm", "xterm", null),
    };

    /// <summary>Terminals installed on this machine, for the Settings picker.</summary>
    public static IReadOnlyList<(string Exe, string Label)> Installed() =>
        Known.Where(k => ProcessLauncher.FindExecutable(k.Exe) is not null)
             .Select(k => (k.Exe, k.Label))
             .ToList();

    public static bool TryOpen(string dir, out string? error)
    {
        error = null;
        string configured = SettingsStore.Instance.Settings.TerminalCommand;
        var candidates = new List<(string Exe, string? DirFlag)>();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var known = Known.FirstOrDefault(k => k.Exe == Path.GetFileName(configured));
            candidates.Add((configured, known.Exe is null ? null : known.DirFlag));
        }
        else
        {
            string desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty;
            IEnumerable<(string Exe, string Label, string? DirFlag)> ordered = Known;
            if (desktop.Contains("GNOME", StringComparison.OrdinalIgnoreCase))
                ordered = Known.OrderBy(k => k.Exe is "kgx" or "gnome-terminal" or "ptyxis" ? 0 : 1);
            candidates.AddRange(ordered.Select(k => (k.Exe, k.DirFlag)));
        }

        foreach (var (exe, flag) in candidates)
        {
            string? resolved = ProcessLauncher.FindExecutable(exe);
            if (resolved is null) continue;
            try
            {
                var start = new ProcessStartInfo(resolved) { UseShellExecute = false, WorkingDirectory = dir };
                if (flag is not null)
                {
                    start.ArgumentList.Add(flag);
                    start.ArgumentList.Add(dir);
                }
                ProcessLauncher.StartDetached(start);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
        }

        error ??= string.IsNullOrWhiteSpace(configured)
            ? "No terminal emulator was found. Choose one in Settings."
            : $"The terminal “{configured}” couldn't be started.";
        return false;
    }
}
