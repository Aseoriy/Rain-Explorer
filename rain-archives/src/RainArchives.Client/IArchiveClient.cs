using RainArchives.Contracts;

namespace RainArchives.Client;

public interface IArchiveClient : IAsyncDisposable
{
    /// <summary>Negotiated expanded creation-tree budget; explicit mappings and rebuild retain their own bounds.</summary>
    int MaximumCreationTreeEntries => ProtocolConstants.MaximumZipCreationEntries;

    event EventHandler<OperationEvent>? OperationEventReceived;

    bool SupportsFeature(string feature);

    Task<Guid> CreateSecretAsync(
        ReadOnlyMemory<char> secret,
        CancellationToken cancellationToken = default);

    Task<bool> DiscardSecretAsync(
        Guid slotId,
        CancellationToken cancellationToken = default);

    Task<OpenArchiveResponse> OpenArchiveAsync(
        string sourcePath,
        Guid? passwordSlotId = null,
        CancellationToken cancellationToken = default);

    Task<OpenNestedArchiveResponse> OpenNestedArchiveAsync(
        ArchiveSessionId parentSessionId,
        EntryId entryId,
        long maximumBytes = ProtocolConstants.DefaultNestedArchiveBytes,
        Guid? parentPasswordSlotId = null,
        Guid? nestedPasswordSlotId = null,
        CancellationToken cancellationToken = default);

    Task<ListEntriesResponse> ListEntriesAsync(
        ArchiveSessionId sessionId,
        int offset = 0,
        int limit = 512,
        CancellationToken cancellationToken = default);

    Task<ArchiveEntryStreamLease> OpenEntryStreamAsync(
        ArchiveSessionId sessionId,
        EntryId entryId,
        long maximumBytes = ProtocolConstants.DefaultEntryStreamBytes,
        Guid? passwordSlotId = null,
        CancellationToken cancellationToken = default);

    Task<CloseArchiveResponse> CloseArchiveAsync(
        ArchiveSessionId sessionId,
        CancellationToken cancellationToken = default);

    Task<StartOperationResponse> StartExtractionAsync(
        ArchiveSessionId sessionId,
        IReadOnlyList<EntryId> entryIds,
        string destinationPath,
        Guid? passwordSlotId = null,
        CancellationToken cancellationToken = default);

    Task<StartOperationResponse> StartIntegrityTestAsync(
        ArchiveSessionId sessionId,
        IReadOnlyList<EntryId> entryIds,
        Guid? passwordSlotId = null,
        CancellationToken cancellationToken = default);

    Task<StartOperationResponse> StartZipCreationAsync(
        string destinationPath,
        IReadOnlyList<ArchiveCreationFile> files,
        ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
        CancellationToken cancellationToken = default);

    Task<StartOperationResponse> StartZipTreeCreationAsync(
        string destinationPath,
        IReadOnlyList<ArchiveCreationFile> files,
        IReadOnlyList<ArchiveCreationDirectory> directories,
        ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
        CancellationToken cancellationToken = default);

    Task<StartOperationResponse> StartTarCreationAsync(
        string destinationPath,
        IReadOnlyList<ArchiveCreationFile> files,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("TAR creation is not implemented by this client.");

    Task<StartOperationResponse> StartTarTreeCreationAsync(
        string destinationPath,
        IReadOnlyList<ArchiveCreationFile> files,
        IReadOnlyList<ArchiveCreationDirectory> directories,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("TAR directory creation is not implemented by this client.");

    Task<StartOperationResponse> StartRarCreationAsync(
        string destinationPath,
        IReadOnlyList<ArchiveCreationFile> files,
        ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("RAR creation is not implemented by this client.");

    Task<StartOperationResponse> StartRarTreeCreationAsync(
        string destinationPath,
        IReadOnlyList<ArchiveCreationFile> files,
        IReadOnlyList<ArchiveCreationDirectory> directories,
        ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("RAR directory creation is not implemented by this client.");

    Task<StartOperationResponse> StartTarGZipCreationAsync(
        string destinationPath,
        IReadOnlyList<ArchiveCreationFile> files,
        ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("TAR.GZ creation is not implemented by this client.");

    Task<StartOperationResponse> StartTarGZipTreeCreationAsync(
        string destinationPath,
        IReadOnlyList<ArchiveCreationFile> files,
        IReadOnlyList<ArchiveCreationDirectory> directories,
        ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("TAR.GZ directory creation is not implemented by this client.");

    Task<StartOperationResponse> StartTarBZip2CreationAsync(
        string destinationPath,
        IReadOnlyList<ArchiveCreationFile> files,
        ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("TAR.BZ2 creation is not implemented by this client.");

    Task<StartOperationResponse> StartTarBZip2TreeCreationAsync(
        string destinationPath,
        IReadOnlyList<ArchiveCreationFile> files,
        IReadOnlyList<ArchiveCreationDirectory> directories,
        ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("TAR.BZ2 directory creation is not implemented by this client.");

    Task<StartOperationResponse> StartGZipCreationAsync(
        string destinationPath,
        string sourcePath,
        ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("GZIP creation is not implemented by this client.");

    Task<StartOperationResponse> StartBZip2CreationAsync(
        string destinationPath,
        string sourcePath,
        ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("BZIP2 creation is not implemented by this client.");

    Task<StartOperationResponse> StartZipRebuildCopyAsync(
        ArchiveSessionId sessionId,
        string destinationPath,
        IReadOnlyList<ArchiveEntryKey> entriesToDelete,
        IReadOnlyList<ArchiveRebuildCopyFile> files,
        ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("ZIP Rebuild Copy is not implemented by this client.");

    Task<StartOperationResponse> StartZipRebuildRenameAsync(
        ArchiveSessionId sessionId,
        string destinationPath,
        IReadOnlyList<ArchiveEntryKey> entriesToDelete,
        IReadOnlyList<ArchiveRebuildCopyFile> files,
        IReadOnlyList<ArchiveRebuildRename> renames,
        ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("ZIP Rebuild Rename is not implemented by this client.");

    Task<GetZipRebuildTreePlanResponse> GetZipRebuildTreePlanAsync(
        ArchiveSessionId sessionId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("ZIP Rebuild Tree Plan is not implemented by this client.");

    Task<GetZipRebuildSourceFolderReviewResponse> GetZipRebuildSourceFolderReviewAsync(
        ArchiveSessionId sessionId,
        IReadOnlyList<ArchiveCreationDirectory> directories,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("ZIP Rebuild source-folder review is not implemented by this client.");

    Task<StartOperationResponse> StartZipRebuildTreeEditAsync(
        ArchiveSessionId sessionId,
        string destinationPath,
        IReadOnlyList<ArchiveEntryKey> entriesToDelete,
        IReadOnlyList<ArchiveRebuildCopyFile> files,
        IReadOnlyList<ArchiveRebuildRename> renames,
        IReadOnlyList<ArchiveCreationDirectory> directories,
        IReadOnlyList<ArchiveRebuildTreeAction> treeActions,
        ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("ZIP Rebuild Tree Edit is not implemented by this client.");

    Task<StartOperationResponse> StartZipRebuildMetadataAsync(
        ArchiveSessionId sessionId,
        string destinationPath,
        IReadOnlyList<ArchiveEntryKey> entriesToDelete,
        IReadOnlyList<ArchiveRebuildCopyFile> files,
        IReadOnlyList<ArchiveRebuildRename> renames,
        IReadOnlyList<ArchiveCreationDirectory> directories,
        IReadOnlyList<ArchiveRebuildTreeAction> treeActions,
        IReadOnlyList<ArchiveRebuildMetadataDirective> metadata,
        ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("ZIP Rebuild Metadata is not implemented by this client.");

    Task<GetZipWriteCapabilitiesResponse> GetZipWriteCapabilitiesAsync(
        ArchiveSessionId? sessionId = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("ZIP write capabilities are not implemented by this client.");

    Task<GetTarWriteCapabilitiesResponse> GetTarWriteCapabilitiesAsync(
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("TAR write capabilities are not implemented by this client.");

    Task<GetRarWriteCapabilitiesResponse> GetRarWriteCapabilitiesAsync(
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("RAR write capabilities are not implemented by this client.");

    Task<GetManagedWriteCapabilitiesResponse> GetManagedWriteCapabilitiesAsync(
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Managed write capabilities are not implemented by this client.");

    Task<GetOperationActivityResponse> GetOperationActivityAsync(
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Operation activity is not implemented by this client.");

    Task<GetSupportDiagnosticsResponse> GetSupportDiagnosticsAsync(
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Support diagnostics are not implemented by this client.");

    Task<ListExtractionRecoveriesResponse> ListExtractionRecoveriesAsync(
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Extraction recovery inspection is not implemented by this client.");

    Task<InspectExtractionRecoveryResponse> InspectExtractionRecoveryAsync(
        string recoveryId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Extraction recovery inspection is not implemented by this client.");

    Task<ListArchiveCreationRecoveriesResponse> ListArchiveCreationRecoveriesAsync(
        CancellationToken cancellationToken = default);

    Task<InspectArchiveCreationRecoveryResponse> InspectArchiveCreationRecoveryAsync(
        string recoveryId,
        CancellationToken cancellationToken = default);

    Task<ApplyArchiveCreationRecoveryDispositionResponse> ApplyArchiveCreationRecoveryDispositionAsync(
        string recoveryId,
        ArchiveCreationRecoveryPhase expectedPhase,
        int expectedSequence,
        DateTimeOffset expectedRecordedAtUtc,
        ArchiveCreationRecoveryDispositionAction action,
        CancellationToken cancellationToken = default);

    Task<ListNestedWorkspaceRecoveriesResponse> ListNestedWorkspaceRecoveriesAsync(
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Nested-workspace recovery is not implemented by this client.");

    Task<InspectNestedWorkspaceRecoveryResponse> InspectNestedWorkspaceRecoveryAsync(
        string recoveryId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Nested-workspace recovery is not implemented by this client.");

    Task<DiscardNestedWorkspaceRecoveryResponse> DiscardNestedWorkspaceRecoveryAsync(
        string recoveryId,
        string expectedRevision,
        int expectedSourceCount,
        long expectedTotalSourceBytes,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Nested-workspace recovery is not implemented by this client.");

    Task<InspectNestedWorkspaceReopenResponse> InspectNestedWorkspaceReopenAsync(
        string recoveryId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Independent nested-workspace recovery reopen is not implemented by this client.");

    Task<ReopenNestedWorkspaceRecoveryResponse> ReopenNestedWorkspaceRecoveryAsync(
        string recoveryId,
        string expectedRevision,
        int expectedSourceCount,
        long expectedTotalSourceBytes,
        string sourceId,
        long expectedSourceLength,
        string expectedContentSha256,
        Guid? passwordSlotId = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Independent nested-workspace recovery reopen is not implemented by this client.");

    Task<CancelOperationResponse> CancelOperationAsync(
        OperationId operationId,
        CancellationToken cancellationToken = default);

    Task<OperationEvent> WaitForTerminalAsync(
        OperationId operationId,
        CancellationToken cancellationToken = default);
}
