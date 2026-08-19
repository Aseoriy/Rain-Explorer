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
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly string _path;
    private readonly object _saveGate = new();
    private List<RecentEntry> _items;
    private bool _loadFailed;
    private bool _retryLoad;

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
        // Clearing is an explicit user choice; do not merge a previously locked file
        // back into the list when the save is retried.
        _loadFailed = false;
        _retryLoad = false;
        Save();
    }

    private List<RecentEntry> Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var saved = JsonSerializer.Deserialize<List<RecentEntry>>(File.ReadAllText(_path), JsonOpts);
                return saved is null
                    ? new()
                    : saved.Where(e => e is not null && !string.IsNullOrWhiteSpace(e.Path))
                          .Take(Cap).ToList();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A sharing violation or temporarily unavailable profile is not corrupt
            // data. Keep retrying on later writes instead of disabling persistence.
            _retryLoad = true;
        }
        catch
        {
            // Preserve a malformed file so a later repair can recover the user's
            // history instead of silently replacing it with an empty list.
            _loadFailed = true;
            try { File.Copy(_path, _path + ".corrupt", overwrite: true); } catch { }
        }
        return new();
    }

    private void Save()
    {
        if (_loadFailed) return;
        lock (_saveGate)
        {
            if (_retryLoad && !TryMergeDeferredLoad()) return;

            string? tmp = null;
            try
            {
                tmp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write,
                           FileShare.None, 4096, FileOptions.WriteThrough))
                using (var sw = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
                {
                    sw.Write(JsonSerializer.Serialize(_items, JsonOpts));
                    sw.Flush();
                    fs.Flush(flushToDisk: true);
                }
                if (File.Exists(_path)) File.Replace(tmp, _path, null);
                else File.Move(tmp, _path);
                tmp = null;
            }
            catch
            {
                if (tmp is not null) { try { File.Delete(tmp); } catch { } }
            }
        }
    }

    private bool TryMergeDeferredLoad()
    {
        try
        {
            if (File.Exists(_path))
            {
                var saved = JsonSerializer.Deserialize<List<RecentEntry>>(
                    File.ReadAllText(_path), JsonOpts) ?? [];
                foreach (RecentEntry entry in saved
                    .Where(e => e is not null && !string.IsNullOrWhiteSpace(e.Path)))
                {
                    if (_items.Count >= Cap) break;
                    if (_items.Any(current => string.Equals(
                            current.Path, entry.Path, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    _items.Add(entry);
                }
            }

            _retryLoad = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        catch
        {
            _loadFailed = true;
            try { File.Copy(_path, _path + ".corrupt", overwrite: true); } catch { }
            return false;
        }
    }
}
