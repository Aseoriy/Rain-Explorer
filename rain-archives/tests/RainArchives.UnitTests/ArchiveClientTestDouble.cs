using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.UnitTests;

internal abstract class ArchiveClientTestDouble : IArchiveClient
{
    public virtual event EventHandler<OperationEvent>? OperationEventReceived
    {
        add { }
        remove { }
    }

    public virtual bool SupportsFeature(string feature) => true;
    public virtual GetZipWriteCapabilitiesResponse ZipWriteCapabilities { get; init; } =
        new(
            CanCreate: true,
            CanRebuild: true,
            SupportsAdd: true,
            SupportsReplace: true,
            SupportsDelete: true,
            SupportsFileRename: true,
            SupportsTreeEdit: true,
            SupportsMetadata: true,
            CompressionPresets: Enum.GetValues<ArchiveCompressionPreset>(),
            MaximumEntries: ProtocolConstants.MaximumZipCreationEntries,
            MaximumSourceBytes: 64L * 1024 * 1024 * 1024,
            SupportsEncryption: false,
            SupportsFilenameEncryption: false,
            SupportsSplitOutput: false,
            SupportsExistingDestinationReplacement: false,
            SupportsInPlaceUpdate: false);

    public virtual Task<GetZipWriteCapabilitiesResponse> GetZipWriteCapabilitiesAsync(
        ArchiveSessionId? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ZipWriteCapabilities);
    }

    public virtual Task<GetTarWriteCapabilitiesResponse> GetTarWriteCapabilitiesAsync(
        CancellationToken cancellationToken = default) => throw Unsupported();

    public virtual Task<GetManagedWriteCapabilitiesResponse> GetManagedWriteCapabilitiesAsync(
        CancellationToken cancellationToken = default) => throw Unsupported();

    public virtual Task<GetOperationActivityResponse> GetOperationActivityAsync(
        CancellationToken cancellationToken = default) => throw Unsupported();

    public virtual Task<GetSupportDiagnosticsResponse> GetSupportDiagnosticsAsync(
        CancellationToken cancellationToken = default) => throw Unsupported();

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public virtual Task<Guid> CreateSecretAsync(
        ReadOnlyMemory<char> secret,
        CancellationToken cancellationToken = default) => throw Unsupported();

    public virtual Task<bool> DiscardSecretAsync(
        Guid slotId,
        CancellationToken cancellationToken = default) => throw Unsupported();

    public virtual Task<OpenArchiveResponse> OpenArchiveAsync(
        string sourcePath,
        Guid? passwordSlotId = null,
        CancellationToken cancellationToken = default) => throw Unsupported();

    public virtual Task<OpenNestedArchiveResponse> OpenNestedArchiveAsync(
        ArchiveSessionId parentSessionId,
        EntryId entryId,
        long maximumBytes = ProtocolConstants.DefaultNestedArchiveBytes,
        Guid? parentPasswordSlotId = null,
        Guid? nestedPasswordSlotId = null,
        CancellationToken cancellationToken = default) => throw Unsupported();

    public virtual Task<ListEntriesResponse> ListEntriesAsync(
        ArchiveSessionId sessionId,
        int offset = 0,
        int limit = 512,
        CancellationToken cancellationToken = default) => throw Unsupported();

    public virtual Task<ArchiveEntryStreamLease> OpenEntryStreamAsync(
        ArchiveSessionId sessionId,
        EntryId entryId,
        long maximumBytes = ProtocolConstants.DefaultEntryStreamBytes,
        Guid? passwordSlotId = null,
        CancellationToken cancellationToken = default) => throw Unsupported();

    public virtual Task<CloseArchiveResponse> CloseArchiveAsync(
        ArchiveSessionId sessionId,
        CancellationToken cancellationToken = default) => throw Unsupported();

    public virtual Task<StartOperationResponse> StartExtractionAsync(
        ArchiveSessionId sessionId,
        IReadOnlyList<EntryId> entryIds,
        string destinationPath,
        Guid? passwordSlotId = null,
        CancellationToken cancellationToken = default) => throw Unsupported();

    public virtual Task<StartOperationResponse> StartIntegrityTestAsync(
        ArchiveSessionId sessionId,
        IReadOnlyList<EntryId> entryIds,
        Guid? passwordSlotId = null,
        CancellationToken cancellationToken = default) => throw Unsupported();

    public virtual Task<StartOperationResponse> StartZipCreationAsync(
        string destinationPath,
        IReadOnlyList<ArchiveCreationFile> files,
        ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
        CancellationToken cancellationToken = default) => throw Unsupported();

    public virtual Task<StartOperationResponse> StartZipTreeCreationAsync(
        string destinationPath,
        IReadOnlyList<ArchiveCreationFile> files,
        IReadOnlyList<ArchiveCreationDirectory> directories,
        ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
        CancellationToken cancellationToken = default) => throw Unsupported();

    public virtual Task<StartOperationResponse> StartTarCreationAsync(
        string destinationPath,
        IReadOnlyList<ArchiveCreationFile> files,
        CancellationToken cancellationToken = default) => throw Unsupported();

    public virtual Task<StartOperationResponse> StartTarTreeCreationAsync(
        string destinationPath,
        IReadOnlyList<ArchiveCreationFile> files,
        IReadOnlyList<ArchiveCreationDirectory> directories,
        CancellationToken cancellationToken = default) => throw Unsupported();

    public virtual Task<StartOperationResponse> StartTarGZipCreationAsync(
        string destinationPath,
        IReadOnlyList<ArchiveCreationFile> files,
        ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
        CancellationToken cancellationToken = default) => throw Unsupported();

    public virtual Task<StartOperationResponse> StartTarGZipTreeCreationAsync(
        string destinationPath,
        IReadOnlyList<ArchiveCreationFile> files,
        IReadOnlyList<ArchiveCreationDirectory> directories,
        ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
        CancellationToken cancellationToken = default) => throw Unsupported();

    public virtual Task<StartOperationResponse> StartTarBZip2CreationAsync(
        string destinationPath,
        IReadOnlyList<ArchiveCreationFile> files,
        ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
        CancellationToken cancellationToken = default) => throw Unsupported();

    public virtual Task<StartOperationResponse> StartTarBZip2TreeCreationAsync(
        string destinationPath,
        IReadOnlyList<ArchiveCreationFile> files,
        IReadOnlyList<ArchiveCreationDirectory> directories,
        ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
        CancellationToken cancellationToken = default) => throw Unsupported();

    public virtual Task<StartOperationResponse> StartGZipCreationAsync(
        string destinationPath,
        string sourcePath,
        ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
        CancellationToken cancellationToken = default) => throw Unsupported();

    public virtual Task<StartOperationResponse> StartBZip2CreationAsync(
        string destinationPath,
        string sourcePath,
        ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
        CancellationToken cancellationToken = default) => throw Unsupported();

    public virtual Task<StartOperationResponse> StartZipRebuildCopyAsync(
        ArchiveSessionId sessionId,
        string destinationPath,
        IReadOnlyList<ArchiveEntryKey> entriesToDelete,
        IReadOnlyList<ArchiveRebuildCopyFile> files,
        ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
        CancellationToken cancellationToken = default) => throw Unsupported();

    public virtual Task<StartOperationResponse> StartZipRebuildRenameAsync(
        ArchiveSessionId sessionId,
        string destinationPath,
        IReadOnlyList<ArchiveEntryKey> entriesToDelete,
        IReadOnlyList<ArchiveRebuildCopyFile> files,
        IReadOnlyList<ArchiveRebuildRename> renames,
        ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
        CancellationToken cancellationToken = default) => throw Unsupported();

    public virtual Task<GetZipRebuildTreePlanResponse> GetZipRebuildTreePlanAsync(
        ArchiveSessionId sessionId,
        CancellationToken cancellationToken = default) => throw Unsupported();

    public virtual Task<StartOperationResponse> StartZipRebuildTreeEditAsync(
        ArchiveSessionId sessionId,
        string destinationPath,
        IReadOnlyList<ArchiveEntryKey> entriesToDelete,
        IReadOnlyList<ArchiveRebuildCopyFile> files,
        IReadOnlyList<ArchiveRebuildRename> renames,
        IReadOnlyList<ArchiveCreationDirectory> directories,
        IReadOnlyList<ArchiveRebuildTreeAction> treeActions,
        ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
        CancellationToken cancellationToken = default) => throw Unsupported();

    public virtual Task<StartOperationResponse> StartZipRebuildMetadataAsync(
        ArchiveSessionId sessionId,
        string destinationPath,
        IReadOnlyList<ArchiveEntryKey> entriesToDelete,
        IReadOnlyList<ArchiveRebuildCopyFile> files,
        IReadOnlyList<ArchiveRebuildRename> renames,
        IReadOnlyList<ArchiveCreationDirectory> directories,
        IReadOnlyList<ArchiveRebuildTreeAction> treeActions,
        IReadOnlyList<ArchiveRebuildMetadataDirective> metadata,
        ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
        CancellationToken cancellationToken = default) => throw Unsupported();

    public virtual Task<ListExtractionRecoveriesResponse> ListExtractionRecoveriesAsync(
        CancellationToken cancellationToken = default) => throw Unsupported();

    public virtual Task<InspectExtractionRecoveryResponse> InspectExtractionRecoveryAsync(
        string recoveryId,
        CancellationToken cancellationToken = default) => throw Unsupported();

    public virtual Task<ListArchiveCreationRecoveriesResponse> ListArchiveCreationRecoveriesAsync(
        CancellationToken cancellationToken = default) => throw Unsupported();

    public virtual Task<InspectArchiveCreationRecoveryResponse>
        InspectArchiveCreationRecoveryAsync(
            string recoveryId,
            CancellationToken cancellationToken = default) => throw Unsupported();

    public virtual Task<ApplyArchiveCreationRecoveryDispositionResponse>
        ApplyArchiveCreationRecoveryDispositionAsync(
            string recoveryId,
            ArchiveCreationRecoveryPhase expectedPhase,
            int expectedSequence,
            DateTimeOffset expectedRecordedAtUtc,
            ArchiveCreationRecoveryDispositionAction action,
            CancellationToken cancellationToken = default) => throw Unsupported();

    public virtual Task<ListNestedWorkspaceRecoveriesResponse>
        ListNestedWorkspaceRecoveriesAsync(
            CancellationToken cancellationToken = default) => throw Unsupported();

    public virtual Task<InspectNestedWorkspaceRecoveryResponse>
        InspectNestedWorkspaceRecoveryAsync(
            string recoveryId,
            CancellationToken cancellationToken = default) => throw Unsupported();

    public virtual Task<DiscardNestedWorkspaceRecoveryResponse>
        DiscardNestedWorkspaceRecoveryAsync(
            string recoveryId,
            string expectedRevision,
            int expectedSourceCount,
            long expectedTotalSourceBytes,
            CancellationToken cancellationToken = default) => throw Unsupported();

    public virtual Task<InspectNestedWorkspaceReopenResponse>
        InspectNestedWorkspaceReopenAsync(
            string recoveryId,
            CancellationToken cancellationToken = default) => throw Unsupported();

    public virtual Task<ReopenNestedWorkspaceRecoveryResponse>
        ReopenNestedWorkspaceRecoveryAsync(
            string recoveryId,
            string expectedRevision,
            int expectedSourceCount,
            long expectedTotalSourceBytes,
            string sourceId,
            long expectedSourceLength,
            string expectedContentSha256,
            Guid? passwordSlotId = null,
            CancellationToken cancellationToken = default) => throw Unsupported();

    public virtual Task<CancelOperationResponse> CancelOperationAsync(
        OperationId operationId,
        CancellationToken cancellationToken = default) => throw Unsupported();

    public virtual Task<OperationEvent> WaitForTerminalAsync(
        OperationId operationId,
        CancellationToken cancellationToken = default) => throw Unsupported();

    private static NotSupportedException Unsupported() =>
        new("This archive-client operation is not configured by the test double.");
}
