namespace RainExplorer.Services;

/// <summary>
/// Path helpers for a case-sensitive filesystem. The Windows build compares paths
/// case-insensitively; on Linux "Photos" and "photos" are different folders, so every
/// path comparison in the port goes through these ordinal helpers.
/// </summary>
public static class PathUtil
{
    public static readonly StringComparer Comparer = StringComparer.Ordinal;
    public const StringComparison Comparison = StringComparison.Ordinal;

    /// <summary>Encode a literal filesystem path without interpreting # or % as URI syntax.</summary>
    public static string ToFileUri(string path) =>
        "file://" + string.Join('/', Path.GetFullPath(path).Split('/').Select(Uri.EscapeDataString));

    /// <summary>Full path without a trailing separator ("/" stays "/").</summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        string full = Path.GetFullPath(path.Trim());
        return full.Length > 1 ? full.TrimEnd(Path.DirectorySeparatorChar) : full;
    }

    public static bool Same(string? left, string? right)
    {
        if (left is null || right is null) return false;
        try { return string.Equals(Normalize(left), Normalize(right), Comparison); }
        catch { return string.Equals(left, right, Comparison); }
    }

    /// <summary>True when <paramref name="child"/> is strictly inside <paramref name="parent"/>.</summary>
    public static bool IsStrictlyInside(string parent, string child)
    {
        try
        {
            string p = Normalize(parent);
            string c = Normalize(child);
            if (p == "/") return c.Length > 1 && c[0] == '/';
            return c.StartsWith(p + Path.DirectorySeparatorChar, Comparison);
        }
        catch { return false; }
    }

    public static bool IsSameOrInside(string parent, string child) =>
        Same(parent, child) || IsStrictlyInside(parent, child);

    /// <summary>Last path segment, or the path itself for "/".</summary>
    public static string DisplayName(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        string trimmed = path.Length > 1 ? path.TrimEnd(Path.DirectorySeparatorChar) : path;
        string name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    /// <summary>Shorten a path under the home folder to "~/…" for compact display.</summary>
    public static string Tildify(string path)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return path;
        if (string.Equals(path, home, Comparison)) return "~";
        return path.StartsWith(home + "/", Comparison) ? "~" + path[home.Length..] : path;
    }

    /// <summary>Expand a leading "~" typed into the address bar.</summary>
    public static string ExpandTilde(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '~') return path;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.Length == 1) return home;
        return path[1] == '/' ? home + path[1..] : path;
    }

    /// <summary>Linux file names may contain anything except "/" and NUL.</summary>
    public static bool IsValidFileName(string name) =>
        !string.IsNullOrEmpty(name) && name != "." && name != ".."
        && name.IndexOfAny(['/', '\0']) < 0;
}
