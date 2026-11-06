using System.IO;
using RainArchives.Contracts;

namespace RainArchives.App.ViewModels;

/// <summary>
/// Fixed-field presentation authority for archive-browser commands. The snapshot is derived only
/// from the newest validated session, browser state, and the bounded visible selection.
/// </summary>
public sealed record ArchiveCommandContext(
    EntryId? PrimaryEntryId,
    int SelectionCount,
    bool CanNavigate,
    bool CanPreview,
    bool CanShowProperties,
    bool CanTestIntegrity,
    bool CanExtractArchive,
    bool CanExtractSelected,
    string StatusAnnouncement)
{
    public static ArchiveCommandContext Unavailable { get; } = new(
        null,
        0,
        CanNavigate: false,
        CanPreview: false,
        CanShowProperties: false,
        CanTestIntegrity: false,
        CanExtractArchive: false,
        CanExtractSelected: false,
        "Archive context commands are unavailable.");

    public bool CanActivatePrimary => CanNavigate || CanPreview;

    internal static ArchiveCommandContext Create(
        ArchiveDescriptor? archive,
        ArchiveBrowserState browserState,
        bool isBusy,
        IReadOnlyList<ArchiveEntryViewModel> visibleEntries,
        IReadOnlyCollection<EntryId> requestedSelection,
        ArchiveEntryViewModel? requestedPrimary)
    {
        ArgumentNullException.ThrowIfNull(visibleEntries);
        ArgumentNullException.ThrowIfNull(requestedSelection);
        if (visibleEntries.Count > ArchiveBrowserViewModel.VisiblePageSize
            || requestedSelection.Count > ArchiveBrowserViewModel.VisiblePageSize)
        {
            throw new InvalidDataException(
                $"Archive command selection exceeds the {ArchiveBrowserViewModel.VisiblePageSize:N0}-row presentation bound.");
        }

        var visibleById = new Dictionary<EntryId, ArchiveEntryViewModel>(visibleEntries.Count);
        foreach (var entry in visibleEntries)
        {
            if (!visibleById.TryAdd(entry.EntryId, entry))
            {
                throw new InvalidDataException("The visible archive selection contains a duplicate entry identity.");
            }
        }

        var selectedIds = new HashSet<EntryId>();
        foreach (var entryId in requestedSelection)
        {
            if (visibleById.ContainsKey(entryId))
            {
                _ = selectedIds.Add(entryId);
            }
        }

        ArchiveEntryViewModel? primary = null;
        if (requestedPrimary is not null
            && visibleById.TryGetValue(requestedPrimary.EntryId, out var canonicalPrimary))
        {
            primary = canonicalPrimary;
            _ = selectedIds.Add(canonicalPrimary.EntryId);
        }

        var sessionIsCurrent = archive is not null
            && !isBusy
            && browserState is ArchiveBrowserState.Ready or ArchiveBrowserState.Empty;
        if (!sessionIsCurrent)
        {
            return Unavailable;
        }

        var canNavigate = primary?.Descriptor.Kind == ArchiveEntryKind.Directory;
        var canPreview = primary?.Descriptor.Kind == ArchiveEntryKind.File
            && archive!.Capabilities.HasFlag(ArchiveCapability.OpenEntryStream);
        var canShowProperties = true;
        var canTestIntegrity = archive!.Capabilities.HasFlag(ArchiveCapability.Test);
        var canExtractArchive = archive.Capabilities.HasFlag(ArchiveCapability.Extract);
        var canExtractSelected = selectedIds.Count > 0 && canExtractArchive;

        return new ArchiveCommandContext(
            primary?.EntryId,
            selectedIds.Count,
            canNavigate,
            canPreview,
            canShowProperties,
            canTestIntegrity,
            canExtractArchive,
            canExtractSelected,
            BuildStatus(
                selectedIds.Count,
                canNavigate,
                canPreview,
                canShowProperties,
                canTestIntegrity,
                canExtractSelected));
    }

    private static string BuildStatus(
        int selectionCount,
        bool canNavigate,
        bool canPreview,
        bool canShowProperties,
        bool canTestIntegrity,
        bool canExtractSelected)
    {
        var available = new List<string>(5);
        if (canNavigate)
        {
            available.Add("folder navigation");
        }

        if (canPreview)
        {
            available.Add("safe preview");
        }

        if (canShowProperties)
        {
            available.Add("properties");
        }

        if (canTestIntegrity)
        {
            available.Add("integrity testing");
        }

        if (canExtractSelected)
        {
            available.Add("selected-entry extraction");
        }

        var selection = selectionCount == 0
            ? "No archive item is selected."
            : selectionCount == 1
                ? "One archive item is selected."
                : $"{selectionCount:N0} archive items are selected.";
        return available.Count == 0
            ? $"{selection} No context command is available."
            : $"{selection} Available commands: {string.Join(", ", available)}.";
    }
}
