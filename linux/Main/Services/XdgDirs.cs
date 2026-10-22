namespace RainExplorer.Services;

/// <summary>
/// Resolves the user's well-known folders from <c>~/.config/user-dirs.dirs</c>
/// (written by xdg-user-dirs, honoured by KDE and GNOME). .NET maps some special
/// folders on Linux but returns $HOME for Documents and has no Downloads entry.
/// </summary>
public static class XdgDirs
{
    private static readonly Lazy<Dictionary<string, string>> Entries = new(Load);

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string ConfigHome =>
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } c ? c : Path.Combine(Home, ".config");

    public static string DataHome =>
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } d ? d : Path.Combine(Home, ".local", "share");

    public static string CacheHome =>
        Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } c ? c : Path.Combine(Home, ".cache");

    public static string Desktop => Get("DESKTOP", "Desktop");
    public static string Documents => Get("DOCUMENTS", "Documents");
    public static string Downloads => Get("DOWNLOAD", "Downloads");
    public static string Music => Get("MUSIC", "Music");
    public static string Pictures => Get("PICTURES", "Pictures");
    public static string Videos => Get("VIDEOS", "Videos");

    private static string Get(string key, string fallbackName) =>
        Entries.Value.TryGetValue(key, out string? path) && !string.IsNullOrWhiteSpace(path)
            ? path
            : Path.Combine(Home, fallbackName);

    private static Dictionary<string, string> Load()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            string file = Path.Combine(ConfigHome, "user-dirs.dirs");
            if (!File.Exists(file)) return map;
            foreach (string raw in File.ReadLines(file))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0 || !line.StartsWith("XDG_", StringComparison.Ordinal)
                    || !line[..eq].EndsWith("_DIR", StringComparison.Ordinal))
                    continue;
                string key = line[4..(eq - 4)];
                string value = line[(eq + 1)..].Trim().Trim('"');
                value = value.Replace("$HOME", Home, StringComparison.Ordinal);
                // xdg-user-dirs sets a folder to $HOME/ to disable it; treat that as unset.
                if (string.Equals(value.TrimEnd('/'), Home, StringComparison.Ordinal)) continue;
                map[key] = value;
            }
        }
        catch
        {
            // Unreadable config — fall back to the conventional English folder names.
        }
        return map;
    }
}
