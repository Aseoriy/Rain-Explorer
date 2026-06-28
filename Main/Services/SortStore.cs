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

    private SortStore()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RainExplorer");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "sorts.json");
        _map = Load();
    }

    public SortPref Get(string folder) =>
        _map.TryGetValue(Norm(folder), out var p) ? p : Default;

    public void Set(string folder, SortPref pref)
    {
        _map[Norm(folder)] = pref;
        Save();
    }

    private static string Norm(string folder) =>
        folder.TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();

    private Dictionary<string, SortPref> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var data = JsonSerializer.Deserialize<Dictionary<string, SortPref>>(File.ReadAllText(_path));
                if (data is not null) return data;
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
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_map));
        }
        catch
        {
            // Best-effort; a failed write shouldn't break browsing.
        }
    }
}
