using System.IO;
using System.Text.Json;
using RainExplorer.Models;

namespace RainExplorer.Services;

/// <summary>
/// A small most-recently-used list of files and folders, persisted to
/// %AppData%\RainExplorer\recents.json. Drives the Home dashboard.
/// </summary>
public sealed class RecentsStore
{
    public static RecentsStore Instance { get; } = new();

    private const int Cap = 50;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _path;
    private List<RecentEntry> _items;

    private RecentsStore()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RainExplorer");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "recents.json");
        _items = Load();
    }

    public IReadOnlyList<RecentEntry> Items => _items;

    /// <summary>Record an access, moving it to the front and de-duplicating by path.</summary>
    public void Add(string path, bool isDirectory)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        _items.RemoveAll(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
        _items.Insert(0, new RecentEntry { Path = path, IsDirectory = isDirectory, AccessedUtc = DateTime.UtcNow });
        if (_items.Count > Cap) _items.RemoveRange(Cap, _items.Count - Cap);
        Save();
    }

    public void Clear()
    {
        _items.Clear();
        Save();
    }

    private List<RecentEntry> Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<List<RecentEntry>>(File.ReadAllText(_path)) ?? new();
        }
        catch { /* corrupt — start fresh */ }
        return new();
    }

    private void Save()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_items, JsonOpts)); }
        catch { /* best-effort */ }
    }
}
