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
    private readonly object _saveGate = new();
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

        // A transient IOException here means another process is mid-write (a sharing
        // violation), NOT that the file is corrupt. Retry a few times before giving up
        // so we never mistake "busy" for "broken" and fall back to empty defaults —
        // that fallback, followed by a Save, is exactly what used to wipe the pins.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                var loaded = JsonSerializer.Deserialize<AppSettings>(sr.ReadToEnd(), JsonOpts)
                             ?? new AppSettings();
                Normalize(loaded);
                return loaded;
            }
            catch (IOException)
            {
                Thread.Sleep(60);   // file busy — let the other writer finish, then retry
            }
            catch
            {
                break;              // genuine parse error — stop and preserve below
            }
        }

        // The file exists but won't parse (or stayed locked). Preserve it for recovery
        // instead of silently overwriting it with defaults, and flag the failure so we
        // neither re-seed nor Save over it (either would lose the user's pinned items).
        try { File.Copy(_path, _path + ".corrupt", overwrite: true); } catch { }
        LoadFailed = true;
        return new AppSettings();
    }

    /// <summary>Force an immediate save (e.g. on app exit).</summary>
    public void Flush() => Save();

    // Write to a temp file then atomically swap it in, so a crash or a killed
    // process mid-write can never leave a truncated/corrupt settings.json
    // (which would silently load as defaults on next launch).
    private void Save()
    {
        // We couldn't read the existing file at startup (locked or corrupt). Writing now
        // would overwrite a file we never successfully loaded — clobbering whatever pins
        // it holds with our empty in-memory defaults. Refuse: the user's data on disk
        // (and the .corrupt copy) stays intact until a clean run can read it.
        if (LoadFailed) return;

        lock (_saveGate)
        {
            string? tmp = null;
            try
            {
                string json = JsonSerializer.Serialize(Settings, JsonOpts);
                tmp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write,
                           FileShare.None, 4096, FileOptions.WriteThrough))
                using (var sw = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
                {
                    sw.Write(json);
                    sw.Flush();
                    fs.Flush(flushToDisk: true);
                }

                if (File.Exists(_path)) File.Replace(tmp, _path, null);
                else File.Move(tmp, _path);
                tmp = null;
            }
            catch
            {
                // Never fall back to writing settings.json directly: a failed or
                // interrupted write must not truncate the last known-good file.
                if (tmp is not null) { try { File.Delete(tmp); } catch { } }
            }
        }
    }

    private static void Normalize(AppSettings settings)
    {
        // JSON nulls are valid input even though these collections are initialized
        // for normal construction. Keep older/hand-edited settings from causing a
        // later sidebar or workspace operation to throw.
        settings.Pinned ??= new();
        settings.CustomGroups ??= new();
        settings.TabProjects ??= new();
        settings.SidebarOrder ??= new();
        settings.GitHubAccounts ??= new();
        settings.DefaultFolder ??= string.Empty;
        settings.Theme ??= "Violet";
        settings.FontFamily ??= "Segoe UI";
        settings.GitExecutablePath ??= string.Empty;
        settings.GitDefaultCloneFolder ??= string.Empty;
        settings.QuickAccessName ??= "Quick access";
        settings.DrivesName ??= "Drives";
        settings.SkippedUpdateVersion ??= string.Empty;

        foreach (var group in settings.CustomGroups)
        {
            if (group is null) continue;
            group.Items ??= new();
            group.Name ??= "New list";
            group.Id ??= Guid.NewGuid().ToString("N");
        }
        foreach (var project in settings.TabProjects)
        {
            if (project is null) continue;
            project.Tabs ??= new();
            project.Id ??= Guid.NewGuid().ToString("N");
            project.Name ??= "New project";
        }

        if (settings.SavedSession is { } session)
        {
            session.LeftTabs ??= new();
            session.LeftPinnedTabs ??= new();
            session.LeftTabGroups ??= new();
            session.LeftTabGroupNames ??= new();
            session.RightTabs ??= new();
            session.RightPinnedTabs ??= new();
            session.RightTabGroups ??= new();
            session.RightTabGroupNames ??= new();
        }
    }
}
