namespace RainExplorer.Services;

/// <summary>
/// File-name based MIME detection from the shared-mime-info database
/// (<c>/usr/share/mime/globs2</c>), the same data Dolphin, Nautilus and xdg-open use.
/// Used to pick the desktop's icon for a file and to list "Open with" applications.
/// </summary>
public static class MimeService
{
    private sealed class Db
    {
        public readonly Dictionary<string, (int Weight, string Mime)> Literal = new(StringComparer.Ordinal);
        public readonly Dictionary<string, (int Weight, string Mime)> Suffix = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, (int Weight, string Mime)> SuffixCaseSensitive = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> GenericIcons = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Icons = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal);
        public readonly Dictionary<string, List<string>> Parents = new(StringComparer.Ordinal);
        public int LongestSuffixDots;
    }

    private static readonly Lazy<Db> Database = new(Load);

    public const string Directory = "inode/directory";
    public const string Unknown = "application/octet-stream";

    /// <summary>MIME type for a file name (without reading its contents).</summary>
    public static string ForFileName(string fileName)
    {
        var db = Database.Value;
        if (db.Literal.TryGetValue(fileName, out var literal)) return literal.Mime;

        // Try the longest multi-part suffix first so "a.tar.gz" wins over ".gz".
        string[] parts = fileName.Split('.');
        for (int start = Math.Max(1, parts.Length - 1 - db.LongestSuffixDots); start < parts.Length; start++)
        {
            string suffix = string.Join('.', parts[start..]);
            if (db.SuffixCaseSensitive.TryGetValue(suffix, out var cs)) return cs.Mime;
            if (db.Suffix.TryGetValue(suffix, out var hit)) return hit.Mime;
        }
        return Unknown;
    }

    /// <summary>Icon names to try for a MIME type, most specific first (spec order).</summary>
    public static IEnumerable<string> IconNames(string mime)
    {
        var db = Database.Value;
        mime = Canonical(mime);
        if (db.Icons.TryGetValue(mime, out string? explicitIcon)) yield return explicitIcon;
        yield return mime.Replace('/', '-');
        if (db.GenericIcons.TryGetValue(mime, out string? generic)) yield return generic;
        foreach (string parent in ParentsOf(mime))
        {
            yield return parent.Replace('/', '-');
            if (db.GenericIcons.TryGetValue(parent, out string? parentGeneric)) yield return parentGeneric;
        }
        int slash = mime.IndexOf('/');
        if (slash > 0) yield return mime[..slash] + "-x-generic";
    }

    /// <summary>The MIME type itself plus every type it is a subclass of (text/x-csrc → text/plain).</summary>
    public static IEnumerable<string> SelfAndParents(string mime)
    {
        mime = Canonical(mime);
        yield return mime;
        foreach (string parent in ParentsOf(mime)) yield return parent;
    }

    private static IEnumerable<string> ParentsOf(string mime)
    {
        var db = Database.Value;
        var seen = new HashSet<string>(StringComparer.Ordinal) { mime };
        var queue = new Queue<string>();
        queue.Enqueue(mime);
        while (queue.Count > 0)
        {
            string current = queue.Dequeue();
            if (!db.Parents.TryGetValue(current, out var parents)) continue;
            foreach (string parent in parents)
            {
                if (!seen.Add(parent)) continue;
                yield return parent;
                queue.Enqueue(parent);
            }
        }
        if (mime.StartsWith("text/", StringComparison.Ordinal) && seen.Add("text/plain")) yield return "text/plain";
    }

    public static string Canonical(string mime) =>
        Database.Value.Aliases.TryGetValue(mime, out string? real) ? real : mime;

    /// <summary>Human description like "PNG image", from the database's XML when available.</summary>
    public static string? Comment(string mime)
    {
        try
        {
            foreach (string root in MimeRoots())
            {
                string file = Path.Combine(root, mime + ".xml");
                if (!File.Exists(file)) continue;
                foreach (string line in File.ReadLines(file))
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("<comment>", StringComparison.Ordinal))
                        return System.Net.WebUtility.HtmlDecode(trimmed[9..trimmed.IndexOf("</comment>", StringComparison.Ordinal)]);
                }
            }
        }
        catch { }
        return null;
    }

    private static IEnumerable<string> MimeRoots()
    {
        yield return Path.Combine(XdgDirs.DataHome, "mime");
        string dirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS") is { Length: > 0 } d ? d : "/usr/local/share:/usr/share";
        foreach (string dir in dirs.Split(':', StringSplitOptions.RemoveEmptyEntries))
            yield return Path.Combine(dir, "mime");
    }

    private static Db Load()
    {
        var db = new Db();
        // Lower-priority roots first so user/local entries override system ones.
        foreach (string root in MimeRoots().Reverse())
        {
            try
            {
                LoadGlobs(db, Path.Combine(root, "globs2"));
                LoadPairs(Path.Combine(root, "generic-icons"), db.GenericIcons);
                LoadPairs(Path.Combine(root, "icons"), db.Icons);
                LoadPairs(Path.Combine(root, "aliases"), db.Aliases, ' ');
                LoadParents(db, Path.Combine(root, "subclasses"));
            }
            catch { /* a broken database just means generic icons */ }
        }
        return db;
    }

    private static void LoadGlobs(Db db, string file)
    {
        if (!File.Exists(file)) return;
        foreach (string line in File.ReadLines(file))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            string[] parts = line.Split(':');
            if (parts.Length < 3 || !int.TryParse(parts[0], out int weight)) continue;
            string mime = parts[1];
            string glob = parts[2];
            bool caseSensitive = parts.Length > 3 && parts[3].Contains("cs", StringComparison.Ordinal);

            if (glob.StartsWith("*.", StringComparison.Ordinal) && glob.IndexOfAny(['*', '?', '['], 1) < 0)
            {
                string suffix = glob[2..];
                var target = caseSensitive ? db.SuffixCaseSensitive : db.Suffix;
                if (!target.TryGetValue(suffix, out var existing) || weight > existing.Weight)
                    target[suffix] = (weight, mime);
                db.LongestSuffixDots = Math.Max(db.LongestSuffixDots, suffix.Count(ch => ch == '.') + 1);
            }
            else if (glob.IndexOfAny(['*', '?', '[']) < 0)
            {
                if (!db.Literal.TryGetValue(glob, out var existing) || weight > existing.Weight)
                    db.Literal[glob] = (weight, mime);
            }
        }
    }

    private static void LoadPairs(string file, Dictionary<string, string> target, char separator = ':')
    {
        if (!File.Exists(file)) return;
        foreach (string line in File.ReadLines(file))
        {
            int idx = line.IndexOf(separator);
            if (idx <= 0 || line[0] == '#') continue;
            target[line[..idx]] = line[(idx + 1)..].Trim();
        }
    }

    private static void LoadParents(Db db, string file)
    {
        if (!File.Exists(file)) return;
        foreach (string line in File.ReadLines(file))
        {
            int idx = line.IndexOf(' ');
            if (idx <= 0 || line[0] == '#') continue;
            string child = line[..idx];
            if (!db.Parents.TryGetValue(child, out var list)) db.Parents[child] = list = new List<string>();
            list.Add(line[(idx + 1)..].Trim());
        }
    }
}
