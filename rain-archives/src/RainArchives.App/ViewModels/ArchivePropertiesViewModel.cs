using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using RainArchives.App.Infrastructure;
using RainArchives.Contracts;

namespace RainArchives.App.ViewModels;

public enum ArchivePropertiesState
{
    NoArchive,
    Loading,
    Ready,
    Empty,
    Unsupported,
    Offline,
    Stale,
    Error,
}

public sealed record ArchivePropertyItemViewModel(
    string Name,
    string Value,
    string HelpText);

/// <summary>
/// Fixed presentation projection over validated Contracts descriptors. Provider identity,
/// provider text, source paths, fingerprint values, and mutation authority are never retained.
/// </summary>
public sealed class ArchivePropertiesViewModel : ObservableObject
{
    private const ArchiveCapability KnownCapabilities = ArchiveCapability.List
        | ArchiveCapability.OpenEntryStream
        | ArchiveCapability.Test
        | ArchiveCapability.Extract
        | ArchiveCapability.ReadEncrypted
        | ArchiveCapability.ReadVolumes
        | ArchiveCapability.Create
        | ArchiveCapability.UpdateByRewrite
        | ArchiveCapability.EncryptContent
        | ArchiveCapability.EncryptNames
        | ArchiveCapability.CreateVolumes;

    private ArchivePropertiesState _state = ArchivePropertiesState.NoArchive;
    private string _stateTitle = "No archive properties";
    private string _stateMessage = "Open an archive to inspect validated properties.";
    private string _selectedEntryTitle = "No entry selected";
    private string _selectedEntryMessage = "Select an entry to inspect its validated metadata.";

    public ObservableCollection<ArchivePropertyItemViewModel> ArchiveItems { get; } = [];
    public ObservableCollection<ArchivePropertyItemViewModel> EntryItems { get; } = [];

    public ArchivePropertiesState State
    {
        get => _state;
        private set => SetProperty(ref _state, value);
    }

    public string StateTitle
    {
        get => _stateTitle;
        private set => SetProperty(ref _stateTitle, value);
    }

    public string StateMessage
    {
        get => _stateMessage;
        private set => SetProperty(ref _stateMessage, value);
    }

    public string SelectedEntryTitle
    {
        get => _selectedEntryTitle;
        private set => SetProperty(ref _selectedEntryTitle, value);
    }

    public string SelectedEntryMessage
    {
        get => _selectedEntryMessage;
        private set => SetProperty(ref _selectedEntryMessage, value);
    }

    public bool HasArchiveProperties => ArchiveItems.Count > 0;
    public bool HasSelectedEntry => EntryItems.Count > 0;

    public void Synchronize(
        ArchiveDescriptor? archive,
        ArchiveBrowserState browserState,
        ArchiveEntryDescriptor? selectedEntry,
        bool persistentKeysEnabled)
    {
        var state = MapState(browserState);
        var archiveItems = Array.Empty<ArchivePropertyItemViewModel>();
        var entryItems = Array.Empty<ArchivePropertyItemViewModel>();
        var (stateTitle, stateMessage) = GetStatePresentation(state);
        var selectedTitle = "No entry selected";
        var selectedMessage = "Select an entry to inspect its validated metadata.";

        if (state is ArchivePropertiesState.Ready
            or ArchivePropertiesState.Empty
            or ArchivePropertiesState.Unsupported)
        {
            ValidateArchive(archive);
            archiveItems = BuildArchiveItems(archive!);

            if (state == ArchivePropertiesState.Ready && selectedEntry is not null)
            {
                ValidateEntry(selectedEntry, persistentKeysEnabled);
                entryItems = BuildEntryItems(selectedEntry, persistentKeysEnabled);
                selectedTitle = UntrustedDisplayText.Escape(selectedEntry.DisplayName);
                selectedMessage = "Validated metadata for the current session selection.";
            }
            else if (state == ArchivePropertiesState.Empty)
            {
                selectedMessage = "This archive has no entries.";
            }
            else if (state == ArchivePropertiesState.Unsupported)
            {
                selectedMessage = "Entry properties are unavailable because listing is not supported.";
            }
        }

        Replace(ArchiveItems, archiveItems);
        Replace(EntryItems, entryItems);
        StateTitle = stateTitle;
        StateMessage = stateMessage;
        SelectedEntryTitle = selectedTitle;
        SelectedEntryMessage = selectedMessage;
        State = state;
        OnPropertyChanged(nameof(HasArchiveProperties));
        OnPropertyChanged(nameof(HasSelectedEntry));
    }

    private static ArchivePropertiesState MapState(ArchiveBrowserState state) => state switch
    {
        ArchiveBrowserState.NoArchive => ArchivePropertiesState.NoArchive,
        ArchiveBrowserState.Loading => ArchivePropertiesState.Loading,
        ArchiveBrowserState.Ready => ArchivePropertiesState.Ready,
        ArchiveBrowserState.Empty => ArchivePropertiesState.Empty,
        ArchiveBrowserState.Unsupported => ArchivePropertiesState.Unsupported,
        ArchiveBrowserState.Offline => ArchivePropertiesState.Offline,
        ArchiveBrowserState.Stale => ArchivePropertiesState.Stale,
        ArchiveBrowserState.Error => ArchivePropertiesState.Error,
        _ => throw new InvalidDataException("The archive browser returned an unknown state."),
    };

    private static (string Title, string Message) GetStatePresentation(ArchivePropertiesState state) =>
        state switch
        {
            ArchivePropertiesState.NoArchive =>
                ("No archive properties", "Open an archive to inspect validated properties."),
            ArchivePropertiesState.Loading =>
                ("Loading properties", "Properties remain unavailable until the new session is authoritative."),
            ArchivePropertiesState.Ready =>
                ("Archive properties", "Values come from the current validated archive session."),
            ArchivePropertiesState.Empty =>
                ("Archive properties", "The current validated archive contains no entries."),
            ArchivePropertiesState.Unsupported =>
                ("Limited archive properties", "The connected worker did not authorize entry listing for this session."),
            ArchivePropertiesState.Offline =>
                ("Properties unavailable", "The authenticated archive worker is offline. No stale values are shown."),
            ArchivePropertiesState.Stale =>
                ("Source changed", "The archive session is stale. Reopen it before trusting properties."),
            ArchivePropertiesState.Error =>
                ("Properties unavailable", "Validated archive properties could not be loaded."),
            _ => throw new InvalidDataException("The archive properties state is invalid."),
        };

    private static ArchivePropertyItemViewModel[] BuildArchiveItems(ArchiveDescriptor archive)
    {
        var readCapabilities = new List<string>();
        if (archive.Capabilities.HasFlag(ArchiveCapability.List))
        {
            readCapabilities.Add("browse");
        }

        if (archive.Capabilities.HasFlag(ArchiveCapability.OpenEntryStream))
        {
            readCapabilities.Add("bounded text preview");
        }

        if (archive.Capabilities.HasFlag(ArchiveCapability.Test))
        {
            readCapabilities.Add("integrity test");
        }

        if (archive.Capabilities.HasFlag(ArchiveCapability.Extract))
        {
            readCapabilities.Add("new-destination extraction");
        }

        if (readCapabilities.Count == 0)
        {
            readCapabilities.Add("none advertised");
        }

        var volumeState = archive.VolumeSetKind switch
        {
            ArchiveVolumeSetKind.Single => "Proven single-volume source",
            ArchiveVolumeSetKind.ConfirmedMultipart => "Confirmed multipart; list only",
            ArchiveVolumeSetKind.Unknown => "Volume set unproven; list only",
            null => "Volume identity unavailable in the negotiated protocol",
            _ => throw new InvalidDataException("The archive volume-set kind is invalid."),
        };

        return
        [
            Item("Format", FormatDisplayName(archive.Format), "Canonical format family; provider identity is not displayed."),
            Item("Entries", archive.EntryCount.ToString("N0", CultureInfo.CurrentCulture), "Validated bounded descriptor count."),
            Item("Read capabilities", string.Join(", ", readCapabilities), "Only capabilities authorized for this open session."),
            Item(
                "Archive access",
                archive.IsReadOnly
                    ? "Read-only session"
                    : archive.Capabilities.HasFlag(ArchiveCapability.UpdateByRewrite)
                        ? "Read and rebuild-to-copy capable"
                        : "Read session; no supported write action",
                "No capability shown here permits in-place archive mutation."),
            Item(
                "Modification",
                archive.Capabilities.HasFlag(ArchiveCapability.UpdateByRewrite)
                    ? "Rebuild to a distinct new copy available"
                    : "No archive modification capability",
                "Rain Archives never modifies this source archive in place."),
            Item("Volume state", volumeState, "Multipart or unproven volume sets remain list-only."),
            Item("Solid archive", YesNo(archive.IsSolid), "Solid-container state reported by the validated descriptor."),
            Item("Encrypted entries", YesNo(archive.HasEncryptedEntries), "Presence only; no password or secret-slot data is shown."),
            Item(
                "Integrity testing",
                archive.Capabilities.HasFlag(ArchiveCapability.Test) ? "Available" : "Unavailable",
                "Availability is not an integrity result."),
            Item(
                "Bounded preview",
                archive.Capabilities.HasFlag(ArchiveCapability.OpenEntryStream) ? "Available" : "Unavailable",
                "Preview remains inert read-only text and never invokes external handlers."),
            Item("Source state", "Bound to the current session fingerprint", "Fingerprint values and filesystem identity are intentionally hidden."),
            Item(
                "Session warnings",
                archive.Warnings.Count.ToString("N0", CultureInfo.CurrentCulture),
                "Only the count is shown; provider and free-form warning text is excluded."),
        ];
    }

    private static ArchivePropertyItemViewModel[] BuildEntryItems(
        ArchiveEntryDescriptor entry,
        bool persistentKeysEnabled)
    {
        var items = new List<ArchivePropertyItemViewModel>
        {
            Item("Name", UntrustedDisplayText.Escape(entry.DisplayName), "Archive-controlled text is visibly escaped."),
            Item("Type", KindName(entry.Kind), "Validated entry kind; links and reparses are never followed."),
            Item(
                "Duplicate occurrence",
                $"#{(long)entry.DuplicateOrdinal + 1:N0}",
                "Deterministic occurrence within the normalized archive-relative key."),
        };

        if (persistentKeysEnabled && entry.NormalizedPath is not null)
        {
            items.Add(Item(
                "Archive-relative key",
                UntrustedDisplayText.Escape(entry.NormalizedPath),
                "Presentation resolution only for this exact archive fingerprint; never a filesystem path."));
        }

        if (entry.OriginalSize is { } originalSize && entry.Kind != ArchiveEntryKind.Directory)
        {
            items.Add(Item("Original size", FormatBytes(originalSize), "Validated descriptor value; streamed limits remain authoritative."));
        }

        if (entry.CompressedSize is { } compressedSize && entry.Kind != ArchiveEntryKind.Directory)
        {
            items.Add(Item("Compressed size", FormatBytes(compressedSize), "Provider-reported packed size where available."));
        }

        if (entry.OriginalSize is > 0
            && entry.CompressedSize is { } compressed
            && compressed >= 0
            && entry.Kind != ArchiveEntryKind.Directory)
        {
            var ratio = (decimal)compressed / entry.OriginalSize.Value * 100m;
            items.Add(Item(
                "Compression ratio",
                $"{ratio.ToString("0.##", CultureInfo.InvariantCulture)}% of original size",
                "Computed only when both sizes are known and the original size is greater than zero."));
        }

        if (entry.ModifiedTimeUtc is { } modified)
        {
            items.Add(Item(
                "Modified",
                modified.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fffffff 'UTC'", CultureInfo.InvariantCulture),
                "Displayed as an absolute UTC timestamp; unavailable timestamps are omitted."));
        }

        if (entry.WindowsAttributes is { } attributes)
        {
            items.Add(Item(
                "Windows attributes",
                $"0x{unchecked((uint)attributes):X8}",
                "Exact represented attribute bits; unsupported semantics are not inferred."));
        }

        items.Add(Item("Link state", LinkState(entry), "Link metadata is informational and is never materialized or followed."));
        items.Add(Item("Encrypted", YesNo(entry.IsEncrypted), "Descriptor state only; no secret or password-validation claim."));
        items.Add(Item(
            "Integrity metadata",
            entry.IntegrityValue is null
                ? "Not supplied"
                : "Present; verification not implied",
            "Provider-controlled integrity text is not displayed. Use Test for an authoritative operation result."));
        items.Add(Item("Safety", SafetyName(entry.SafetyStatus), "Fixed classification only; provider or free-form reason text is excluded."));
        items.Add(Item("Solid entry", YesNo(entry.IsSolid), "Validated descriptor state."));
        return [.. items];
    }

    private static void ValidateArchive(ArchiveDescriptor? archive)
    {
        if (archive is null
            || archive.SessionId.Value == Guid.Empty
            || string.IsNullOrWhiteSpace(archive.Format)
            || string.IsNullOrWhiteSpace(archive.ProviderId)
            || archive.EntryCount < 0
            || archive.Fingerprint is null
            || archive.Fingerprint.Length < 0
            || !IsSha256(archive.Fingerprint.PrefixSha256)
            || archive.Warnings is null
            || (archive.Capabilities & ~KnownCapabilities) != 0
            || archive.IsMultipart != archive.Capabilities.HasFlag(ArchiveCapability.ReadVolumes)
            || archive.Warnings.Any(warning => warning is null)
            || !IsVolumeStateValid(archive))
        {
            throw new InvalidDataException("The archive properties descriptor is invalid.");
        }
    }

    private static void ValidateEntry(ArchiveEntryDescriptor entry, bool persistentKeysEnabled)
    {
        if (entry.EntryId.Value == Guid.Empty
            || entry.ParentEntryId?.Value == Guid.Empty
            || string.IsNullOrEmpty(entry.RawPath)
            || string.IsNullOrEmpty(entry.DisplayName)
            || !Enum.IsDefined(entry.Kind)
            || !Enum.IsDefined(entry.SafetyStatus)
            || entry.OriginalSize is < 0
            || entry.CompressedSize is < 0
            || entry.DuplicateOrdinal < 0
            || persistentKeysEnabled && string.IsNullOrEmpty(entry.NormalizedPath)
            || !persistentKeysEnabled && (entry.NormalizedPath is not null || entry.DuplicateOrdinal != 0))
        {
            throw new InvalidDataException("The selected entry properties descriptor is invalid.");
        }
    }

    internal static string FormatDisplayName(string format) => format.Trim().ToLowerInvariant() switch
    {
        "zip" => "ZIP",
        "7z" => "7-Zip",
        "rar" => "RAR",
        "tar" => "TAR",
        "tar.gz" => "TAR.GZ",
        "tar.bz2" => "TAR.BZ2",
        "tar.xz" => "TAR.XZ",
        "gzip" or "gz" => "GZIP",
        "bzip2" or "bz2" => "BZIP2",
        "xz" => "XZ",
        _ => "Other negotiated archive format",
    };

    private static string KindName(ArchiveEntryKind kind) => kind switch
    {
        ArchiveEntryKind.Unknown => "Unknown",
        ArchiveEntryKind.File => "Regular file",
        ArchiveEntryKind.Directory => "Directory",
        ArchiveEntryKind.SymbolicLink => "Symbolic link metadata",
        ArchiveEntryKind.HardLink => "Hard-link metadata",
        ArchiveEntryKind.ReparsePoint => "Reparse-point metadata",
        _ => throw new InvalidDataException("The selected entry kind is invalid."),
    };

    private static string LinkState(ArchiveEntryDescriptor entry) =>
        entry.Kind is ArchiveEntryKind.SymbolicLink
            or ArchiveEntryKind.HardLink
            or ArchiveEntryKind.ReparsePoint
            || entry.LinkTarget is not null
                ? "Link metadata present; target is not followed"
                : "Not a link";

    private static string SafetyName(EntrySafetyStatus status) => status switch
    {
        EntrySafetyStatus.Safe => "Safe for supported operations",
        EntrySafetyStatus.UnsafePath => "Unsafe archive path",
        EntrySafetyStatus.PathCollision => "Archive path collision",
        EntrySafetyStatus.UnsupportedLink => "Unsupported link metadata",
        EntrySafetyStatus.UnsupportedEntryKind => "Unsupported entry kind",
        EntrySafetyStatus.ResourceLimit => "Resource limit",
        _ => throw new InvalidDataException("The selected entry safety state is invalid."),
    };

    private static string FormatBytes(long bytes) =>
        bytes < 1024
            ? $"{bytes:N0} B"
            : $"{bytes:N0} B ({FormatScaledBytes(bytes)})";

    private static string FormatScaledBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        var value = (decimal)bytes;
        var unit = 0;
        while (value >= 1024m && unit < units.Length - 1)
        {
            value /= 1024m;
            unit++;
        }

        return $"{value.ToString("0.##", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    private static string YesNo(bool value) => value ? "Yes" : "No";

    private static ArchivePropertyItemViewModel Item(string name, string value, string helpText) =>
        new(name, value, helpText);

    private static bool IsSha256(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9'
            or >= 'a' and <= 'f'
            or >= 'A' and <= 'F');

    private static bool IsVolumeStateValid(ArchiveDescriptor archive)
    {
        if (archive.VolumeSetKind is null)
        {
            return true;
        }

        const ArchiveCapability dataCapabilities = ArchiveCapability.OpenEntryStream
            | ArchiveCapability.Test
            | ArchiveCapability.Extract
            | ArchiveCapability.UpdateByRewrite;
        var hasDataCapabilities = (archive.Capabilities & dataCapabilities) != 0;
        return archive.VolumeSetKind switch
        {
            ArchiveVolumeSetKind.Single => !archive.IsMultipart
                && !archive.Capabilities.HasFlag(ArchiveCapability.ReadVolumes),
            ArchiveVolumeSetKind.ConfirmedMultipart => archive.IsMultipart
                && archive.Capabilities.HasFlag(ArchiveCapability.ReadVolumes)
                && !hasDataCapabilities,
            ArchiveVolumeSetKind.Unknown => !archive.IsMultipart
                && !archive.Capabilities.HasFlag(ArchiveCapability.ReadVolumes)
                && !hasDataCapabilities,
            _ => false,
        };
    }

    private static void Replace<T>(ObservableCollection<T> target, IReadOnlyList<T> values)
    {
        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
        }
    }
}
