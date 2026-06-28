using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RainExplorer.Models;

namespace RainExplorer.Services;

/// <summary>
/// Loads/saves <see cref="AppSettings"/> to %AppData%\RainExplorer\settings.json.
/// Singleton; auto-saves whenever a setting changes.
/// </summary>
public sealed class SettingsStore
{
    public static SettingsStore Instance { get; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    public AppSettings Settings { get; }

    private SettingsStore()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RainExplorer");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");

        Settings = Load();
        Settings.PropertyChanged += (_, _) => Save();
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOpts)
                       ?? new AppSettings();
        }
        catch
        {
            // Corrupt/unreadable — fall back to defaults.
        }
        return new AppSettings();
    }

    /// <summary>Force an immediate save (e.g. on app exit).</summary>
    public void Flush() => Save();

    // Write to a temp file then atomically swap it in, so a crash or a killed
    // process mid-write can never leave a truncated/corrupt settings.json
    // (which would silently load as defaults on next launch).
    private void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(Settings, JsonOpts);
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(_path)) File.Replace(tmp, _path, null);
            else File.Move(tmp, _path);
        }
        catch
        {
            // Last resort: a plain write (e.g. if File.Replace isn't supported here).
            try { File.WriteAllText(_path, JsonSerializer.Serialize(Settings, JsonOpts)); }
            catch { /* best-effort */ }
        }
    }
}
