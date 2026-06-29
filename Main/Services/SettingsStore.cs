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
        // Be forgiving on read: a single stray comma, comment, casing difference, or
        // BOM must NOT throw — a parse failure used to silently reset every setting
        // (including pinned Quick Access items) back to defaults on the next launch.
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
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

    /// <summary>True when no settings file existed at startup (a genuine first run).</summary>
    public bool IsFirstRun { get; private set; }

    /// <summary>True when a settings file existed but could not be parsed. In that case
    /// we must NOT seed defaults — doing so would permanently clobber the user's pins.</summary>
    public bool LoadFailed { get; private set; }

    private AppSettings Load()
    {
        if (!File.Exists(_path))
        {
            IsFirstRun = true;
            return new AppSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOpts)
                   ?? new AppSettings();
        }
        catch
        {
            // The file exists but won't parse. Preserve it for recovery instead of
            // silently overwriting it with defaults, and flag the failure so we don't
            // re-seed (which would permanently lose the user's pinned items).
            try { File.Copy(_path, _path + ".corrupt", overwrite: true); } catch { }
            LoadFailed = true;
            return new AppSettings();
        }
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
