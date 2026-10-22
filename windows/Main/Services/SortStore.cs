using System.IO;
using System.Text.Json;

namespace RainExplorer.Services;

/// <summary>A remembered sort for a folder: column key + direction (1 asc, -1 desc).</summary>
public sealed record SortPref(string Key, int Dir);

/// <summary>
/// Persists per-folder sort preferences to %AppData%\RainExplorer\sorts.json.
/// Singleton so every tab shares the same remembered choices.
/// </summary>
public sealed class SortStore
{
    public static SortStore Instance { get; } = new();

    private static readonly SortPref Default = new("Name", 1);
    private readonly string _path;
    private readonly Dictionary<string, SortPref> _map;
    private readonly object _saveGate = new();

    private SortStore()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RainExplorer");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "sorts.json");
        _map = Load();
    }

    public SortPref Get(string folder)
    {
        if (!_map.TryGetValue(Norm(folder), out var p) || p is null) return Default;
        return p.Dir is 1 or -1 ? p : p with { Dir = p.Dir < 0 ? -1 : 1 };
    }

    public void Set(string folder, SortPref pref)
    {
        if (pref is null) return;
        _map[Norm(folder)] = pref.Dir is 1 or -1
            ? pref
            : pref with { Dir = pref.Dir < 0 ? -1 : 1 };
        Save();
    }

    private static string Norm(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return string.Empty;
        try
        {
            string full = Path.GetFullPath(folder.Trim());
            string root = Path.GetPathRoot(full) ?? string.Empty;
            return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
                ? root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant()
                : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        }
        catch { return folder.Trim().ToLowerInvariant(); }
    }

    private Dictionary<string, SortPref> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, SortPref>>(File.ReadAllText(_path));
                if (data is not null)
                {
                    var normalized = new Dictionary<string, SortPref>(StringComparer.OrdinalIgnoreCase);
                    foreach (var pair in data)
                    {
                        if (pair.Value is null) continue;
                        normalized[Norm(pair.Key)] = pair.Value.Dir is 1 or -1
                            ? pair.Value
                            : pair.Value with { Dir = pair.Value.Dir < 0 ? -1 : 1 };
                    }
                    return normalized;
                }
            }
        }
        catch
        {
            // Corrupt/unreadable file — start fresh rather than crash.
        }
        return new Dictionary<string, SortPref>();
    }

    private void Save()
    {
        lock (_saveGate)
        {
            string? tmp = null;
            try
            {
                tmp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write,
                           FileShare.None, 4096, FileOptions.WriteThrough))
                using (var sw = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
                {
                    sw.Write(JsonSerializer.Serialize(_map));
                    sw.Flush();
                    fs.Flush(flushToDisk: true);
                }
                if (File.Exists(_path)) File.Replace(tmp, _path, null);
                else File.Move(tmp, _path);
                tmp = null;
            }
            catch
            {
                // Best-effort; never truncate the last known-good preference file.
                if (tmp is not null) { try { File.Delete(tmp); } catch { } }
            }
        }
    }
}
