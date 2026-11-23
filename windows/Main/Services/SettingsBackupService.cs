using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using RainExplorer.Models;

namespace RainExplorer.Services;

/// <summary>What a backup holds, for the "Restore?" question.</summary>
public sealed record BackupSummary(DateTime? Created, string? AppVersion, int Pins, int Lists, int Projects, int Shortcuts);

/// <summary>
/// One-file backups of everything Rain remembers: settings, Quick Access pins, sidebar lists, tab projects,
/// open tabs, shortcuts and per-folder sorting. Restoring writes the files and restarts Rain, so every
/// window starts from the restored state; the replaced settings are kept next to them.
/// </summary>
public static class SettingsBackupService
{
    public const string Format = "rain-explorer-backup";
    public const int FormatVersion = 1;
    public const string FileExtension = "rainbackup";
    public const string BeforeRestoreSuffix = ".before-restore";
    /// <summary>Backups only restore on the platform that made them: folders and shell settings differ.</summary>
    public const string Platform = "windows";

    /// <summary>The backup file's contents for the current settings (and the sorts file in <paramref name="configDir"/>).</summary>
    public static string Create(AppSettings settings, string configDir)
    {
        var root = new JsonObject
        {
            ["format"] = Format,
            ["formatVersion"] = FormatVersion,
            ["platform"] = Platform,
            ["appVersion"] = UpdateService.CurrentVersionString,
            ["created"] = DateTime.Now.ToString("o"),
            ["settings"] = JsonSerializer.SerializeToNode(settings, SettingsJson.Options),
        };
        string sorts = Path.Combine(configDir, "sorts.json");
        try
        {
            if (File.Exists(sorts) && JsonNode.Parse(File.ReadAllText(sorts)) is JsonObject sortsNode)
                root["sorts"] = sortsNode;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Check a backup file. Throws <see cref="InvalidDataException"/> with a readable reason.</summary>
    public static (string SettingsJson, string? SortsJson, BackupSummary Summary) Read(string json)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip,
            }) as JsonObject ?? throw new InvalidDataException("This file isn't a Rain Explorer backup.");
        }
        catch (JsonException)
        {
            throw new InvalidDataException("This file isn't a Rain Explorer backup (it isn't valid JSON).");
        }
        if ((string?)root["format"] != Format)
            throw new InvalidDataException("This file isn't a Rain Explorer backup.");
        if ((int?)root["formatVersion"] is not int version || version > FormatVersion)
            throw new InvalidDataException("This backup was made by a newer Rain Explorer. Update Rain, then try again.");
        string? platform = (string?)root["platform"];
        if (platform is not null && platform != Platform)
            throw new InvalidDataException(
                $"This backup is from Rain Explorer for {Capitalize(platform)}. Its folders and settings don't carry over to Windows.");
        if (root["settings"] is not JsonObject settingsNode)
            throw new InvalidDataException("This backup has no settings in it.");

        AppSettings settings;
        try
        {
            settings = settingsNode.Deserialize<AppSettings>(SettingsJson.Options)
                       ?? throw new InvalidDataException("This backup's settings are empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"This backup's settings couldn't be read: {ex.Message}");
        }

        DateTime? created = DateTime.TryParse((string?)root["created"], null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var when) ? when : null;
        var summary = new BackupSummary(created, (string?)root["appVersion"],
            settings.Pinned?.Count ?? 0, settings.CustomGroups?.Count ?? 0, settings.TabProjects?.Count ?? 0,
            settings.ShortcutOverrides?.Count ?? 0);
        string? sorts = root["sorts"] is JsonObject sortsNode
            ? sortsNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) : null;
        return (settingsNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), sorts, summary);
    }

    /// <summary>
    /// Write a checked backup into <paramref name="configDir"/>: the current settings.json is kept as
    /// settings.json.before-restore first. Each file is replaced atomically.
    /// </summary>
    public static void WriteRestore(string configDir, string settingsJson, string? sortsJson)
    {
        Directory.CreateDirectory(configDir);
        string settingsPath = Path.Combine(configDir, "settings.json");
        if (File.Exists(settingsPath))
            File.Copy(settingsPath, settingsPath + BeforeRestoreSuffix, overwrite: true);
        WriteAtomic(settingsPath, settingsJson);
        if (sortsJson is not null) WriteAtomic(Path.Combine(configDir, "sorts.json"), sortsJson);
    }

    private static void WriteAtomic(string path, string contents)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
            {
                writer.Write(contents);
                writer.Flush();
                fs.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { }
            throw;
        }
    }

    public static string Describe(BackupSummary s)
    {
        var parts = new List<string>
        {
            Count(s.Pins, "pinned place"), Count(s.Lists, "sidebar list"), Count(s.Projects, "tab project"),
        };
        if (s.Shortcuts > 0) parts.Add(Count(s.Shortcuts, "custom shortcut"));
        string from = s.Created is { } c ? $"made {c:d MMM yyyy, HH:mm}" : "with no date";
        string version = string.IsNullOrEmpty(s.AppVersion) ? "" : $" by Rain {s.AppVersion}";
        return $"This backup was {from}{version}. It has {string.Join(", ", parts[..^1])} and {parts[^1]}.";
    }

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
