using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using RainExplorer.Models;

namespace RainExplorer.Services;

/// <summary>What a "Rain Archives" right-click entry asks Rain Explorer to do.</summary>
internal enum ArchiveMenuVerb
{
    Open,
    Add,
    ExtractToNewFolder,
    ExtractTo,
    Test,
}

internal interface IContextMenuRegistry
{
    bool KeyExists(string path);
    string? GetString(string path, string? name);
    void SetString(string path, string? name, string value);
    void DeleteTree(string path);
}

/// <summary>
/// The optional "Rain Archives" cascading entry in the classic right-click menu (on Windows 11
/// it sits under "Show more options"). Everything lives in the current user's hive and every
/// top-level key carries an owner marker, so Rain never edits or removes a key it did not
/// create. Archives get Open, Extract files, Extract to new folder, Test and Add to archive;
/// other files and folders get Add to archive only. There is no "Extract here": Rain Archives
/// only ever extracts into a fresh folder and never merges into an existing one.
/// </summary>
internal static class ArchiveContextMenuService
{
    internal const string CommandSwitch = "--rain-archives";
    internal const string UnregisterSwitch = "--unregister-archive-menu";
    internal const string VerbKeyName = "RainExplorer.Archives";
    internal const string OwnerValueName = "RainExplorerOwner";
    internal const string OwnerValue = "SailHub.RainExplorer.ArchiveMenu.v1";
    internal const string MenuTitle = "Rain Archives";

    internal static string ArchiveMenuPath(string extension) =>
        $@"Software\Classes\SystemFileAssociations\{extension}\shell\{VerbKeyName}";
    internal const string OtherFilesMenuPath = @"Software\Classes\*\shell\" + VerbKeyName;
    internal const string FolderMenuPath = @"Software\Classes\Directory\shell\" + VerbKeyName;

    private static readonly (string Key, string Title, ArchiveMenuVerb Verb)[] ArchiveCommands =
    {
        // Explorer orders sub-commands by key name, hence the numeric prefixes.
        ("10open", "Open", ArchiveMenuVerb.Open),
        ("20extractto", "Extract files…", ArchiveMenuVerb.ExtractTo),
        ("30extractnew", "Extract to new folder", ArchiveMenuVerb.ExtractToNewFolder),
        ("40test", "Test archive", ArchiveMenuVerb.Test),
        ("50add", "Add to archive…", ArchiveMenuVerb.Add),
    };

    private static readonly (string Key, string Title, ArchiveMenuVerb Verb)[] AddOnlyCommands =
    {
        ("50add", "Add to archive…", ArchiveMenuVerb.Add),
    };

    internal static IEnumerable<string> AllMenuPaths =>
        ArchiveFileTypes.AssociationExtensions.Select(ArchiveMenuPath)
            .Append(OtherFilesMenuPath)
            .Append(FolderMenuPath);

    internal static string VerbArgument(ArchiveMenuVerb verb) => verb switch
    {
        ArchiveMenuVerb.Open => "open",
        ArchiveMenuVerb.Add => "add",
        ArchiveMenuVerb.ExtractToNewFolder => "extract-new",
        ArchiveMenuVerb.ExtractTo => "extract-to",
        ArchiveMenuVerb.Test => "test",
        _ => throw new ArgumentOutOfRangeException(nameof(verb)),
    };

    internal static bool TryParseVerb(string? value, out ArchiveMenuVerb verb)
    {
        foreach (ArchiveMenuVerb candidate in Enum.GetValues<ArchiveMenuVerb>())
        {
            if (string.Equals(VerbArgument(candidate), value, StringComparison.OrdinalIgnoreCase))
            {
                verb = candidate;
                return true;
            }
        }
        verb = default;
        return false;
    }

    /// <summary>Parses <c>--rain-archives &lt;verb&gt; &lt;path&gt;</c>.</summary>
    internal static bool TryParseCommandLine(IReadOnlyList<string> args, out ArchiveMenuVerb verb, out string path)
    {
        verb = default;
        path = string.Empty;
        if (args.Count < 3
            || !string.Equals(args[0].Trim(), CommandSwitch, StringComparison.OrdinalIgnoreCase)
            || !TryParseVerb(args[1].Trim(), out verb))
            return false;
        string candidate = args[2].Trim().Trim('"');
        if (candidate.Length == 0 || !Path.IsPathFullyQualified(candidate)) return false;
        path = Path.GetFullPath(candidate);
        return true;
    }

    /// <summary>"Applies to" filter keeping the add-only entry off archives, which already
    /// have the full menu (otherwise .zip files would show two "Rain Archives" entries).</summary>
    internal static string NonArchiveFilter() =>
        "NOT (" + string.Join(" OR ", ArchiveFileTypes.AssociationExtensions
            .Select(extension => $"System.FileExtension:=\"{extension}\"")) + ")";

    // ---- Live registry -------------------------------------------------------

    private const uint ShcneAssocChanged = 0x08000000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, nint item1, nint item2);

    /// <summary>Add or remove the menu for the current user. Best-effort, never throws.
    /// Returns false when some key could not be written or belonged to something else.</summary>
    public static bool Apply(bool enabled)
    {
        try
        {
            var registry = new CurrentUserRegistry();
            bool ok = enabled
                ? Register(registry, ShellIntegrationService.ExePath)
                : Unregister(registry);
            try { SHChangeNotify(ShcneAssocChanged, 0, 0, 0); } catch { }
            return ok;
        }
        catch
        {
            return false;
        }
    }

    // ---- Testable core -------------------------------------------------------

    internal static bool Register(IContextMenuRegistry registry, string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || exePath.Contains('"'))
            throw new ArgumentException("The executable path is invalid.", nameof(exePath));

        bool allWritten = true;
        foreach (string extension in ArchiveFileTypes.AssociationExtensions)
            allWritten &= WriteMenu(registry, ArchiveMenuPath(extension), exePath, ArchiveCommands, appliesTo: null);
        allWritten &= WriteMenu(registry, OtherFilesMenuPath, exePath, AddOnlyCommands, NonArchiveFilter());
        allWritten &= WriteMenu(registry, FolderMenuPath, exePath, AddOnlyCommands, appliesTo: null);
        return allWritten;
    }

    internal static bool Unregister(IContextMenuRegistry registry)
    {
        bool allRemoved = true;
        foreach (string path in AllMenuPaths)
        {
            if (!registry.KeyExists(path)) continue;
            if (IsOwned(registry, path)) registry.DeleteTree(path);
            else allRemoved = false;   // someone else's key with our name: leave it alone
        }
        return allRemoved;
    }

    internal static bool IsRegistered(IContextMenuRegistry registry) =>
        AllMenuPaths.All(path => registry.KeyExists(path) && IsOwned(registry, path));

    private static bool IsOwned(IContextMenuRegistry registry, string path) =>
        string.Equals(registry.GetString(path, OwnerValueName), OwnerValue, StringComparison.Ordinal);

    private static bool WriteMenu(
        IContextMenuRegistry registry,
        string menuPath,
        string exePath,
        IReadOnlyList<(string Key, string Title, ArchiveMenuVerb Verb)> commands,
        string? appliesTo)
    {
        if (registry.KeyExists(menuPath))
        {
            if (!IsOwned(registry, menuPath)) return false;
            // Rewrite from scratch so a moved exe or a removed command leaves nothing stale.
            registry.DeleteTree(menuPath);
        }

        // Owner marker first: an interrupted write is still recognisably ours to repair.
        registry.SetString(menuPath, OwnerValueName, OwnerValue);
        registry.SetString(menuPath, "MUIVerb", MenuTitle);
        registry.SetString(menuPath, "Icon", $"\"{exePath}\",0");
        registry.SetString(menuPath, "SubCommands", string.Empty);
        if (appliesTo is not null) registry.SetString(menuPath, "AppliesTo", appliesTo);

        foreach (var (key, title, verb) in commands)
        {
            string commandPath = $@"{menuPath}\shell\{key}";
            registry.SetString(commandPath, "MUIVerb", title);
            registry.SetString($@"{commandPath}\command", null,
                $"\"{exePath}\" {CommandSwitch} {VerbArgument(verb)} \"%1\"");
        }
        return true;
    }

    private sealed class CurrentUserRegistry : IContextMenuRegistry
    {
        public bool KeyExists(string path)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(path, writable: false);
            return key is not null;
        }

        public string? GetString(string path, string? name)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(path, writable: false);
            return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }

        public void SetString(string path, string? name, string value)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(path, writable: true);
            key.SetValue(name, value, RegistryValueKind.String);
        }

        public void DeleteTree(string path) =>
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
    }
}
