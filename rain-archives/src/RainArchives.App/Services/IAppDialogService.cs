using RainArchives.Contracts;
using RainArchives.Client;
using RainArchives.App.ViewModels;

namespace RainArchives.App.Services;

public interface IAppDialogService
{
    string? ChooseArchive();
    ArchiveCreationSelection? ChooseArchiveCreation(bool supportsDirectoryCreation);
    ArchiveCreationSelection? ChooseArchiveCreation(
        bool supportsDirectoryCreation,
        GetZipWriteCapabilitiesResponse capabilities) =>
        ChooseArchiveCreation(supportsDirectoryCreation);
    ArchiveCreationSelection? ChooseArchiveCreation(
        bool supportsDirectoryCreation,
        GetZipWriteCapabilitiesResponse? zipCapabilities,
        GetManagedWriteCapabilitiesResponse? managedCapabilities) =>
        zipCapabilities is null
            ? ChooseArchiveCreation(supportsDirectoryCreation)
            : ChooseArchiveCreation(supportsDirectoryCreation, zipCapabilities);
    ArchiveCreationSelection? ChooseArchiveCreation(
        bool supportsDirectoryCreation,
        GetZipWriteCapabilitiesResponse? zipCapabilities,
        GetTarWriteCapabilitiesResponse? tarCapabilities,
        GetManagedWriteCapabilitiesResponse? managedCapabilities) =>
        ChooseArchiveCreation(
            supportsDirectoryCreation,
            zipCapabilities,
            managedCapabilities);
    ArchiveRebuildCopySelection? ChooseArchiveRebuildCopy(
        string sourcePath,
        IReadOnlyList<ArchiveEntryDescriptor> entries) =>
        throw new NotSupportedException("Rebuild copy dialogs are not implemented by this dialog service.");
    ArchiveRebuildCopySelection? ChooseArchiveRebuildCopy(
        string sourcePath,
        IReadOnlyList<ArchiveEntryDescriptor> entries,
        IReadOnlyList<ZipRebuildTreePlanNode> treePlan) =>
        ChooseArchiveRebuildCopy(sourcePath, entries);
    ArchiveRebuildCopySelection? ChooseArchiveRebuildCopy(
        IArchiveClient client,
        ArchiveSessionId sessionId,
        string sourcePath,
        IReadOnlyList<ArchiveEntryDescriptor> entries,
        IReadOnlyList<ZipRebuildTreePlanNode> treePlan) =>
        ChooseArchiveRebuildCopy(sourcePath, entries, treePlan);
    ArchiveRebuildCopySelection? ChooseArchiveRebuildCopy(
        IArchiveClient client,
        ArchiveSessionId sessionId,
        string sourcePath,
        IReadOnlyList<ArchiveEntryDescriptor> entries,
        IReadOnlyList<ZipRebuildTreePlanNode> treePlan,
        GetZipWriteCapabilitiesResponse capabilities) =>
        ChooseArchiveRebuildCopy(client, sessionId, sourcePath, entries, treePlan);
    string? ChooseNewExtractionDestination(string archivePath, bool selectedEntriesOnly);
    Task<char[]?> RequestPasswordAsync(string archiveName, CancellationToken cancellationToken);
    Task ShowArchiveRecoveryAsync(IArchiveClient client, CancellationToken cancellationToken);
    async Task<RecoveredArchiveDialogResult?> ShowArchiveRecoveryWithResultAsync(
        IArchiveClient client,
        CancellationToken cancellationToken)
    {
        await ShowArchiveRecoveryAsync(client, cancellationToken).ConfigureAwait(true);
        return null;
    }
    Task ShowSettingsAsync(IArchiveClient? client, CancellationToken cancellationToken) =>
        Task.CompletedTask;
    void ShowActivityCenter(ActivityCenterViewModel viewModel)
    {
    }
    void ShowError(string title, string message);
    void ShowInformation(string title, string message);
}

public sealed record RecoveredArchiveDialogResult(
    ReopenNestedWorkspaceRecoveryResponse Response,
    bool OpenedWithPassword);
