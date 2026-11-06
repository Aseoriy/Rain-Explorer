using System.Text.Json.Serialization;

namespace RainArchives.Contracts;

public sealed record WorkerHelloRequest(
    int ProtocolMajor,
    int ProtocolMinor,
    string WorkerBuild,
    string BootstrapNonce,
    IReadOnlyList<string> Features);

public sealed record ClientWelcomeResponse(
    int ProtocolMajor,
    int ProtocolMinor,
    string ClientBuild,
    IReadOnlyList<string> EnabledFeatures);

public sealed record CreateSecretSlotRequest();
public sealed record CreateSecretSlotResponse(Guid SlotId, DateTimeOffset ExpiresAtUtc);
public sealed record DiscardSecretSlotRequest(Guid SlotId);
public sealed record DiscardSecretSlotResponse(bool Discarded);

public sealed record OpenArchiveRequest(string SourcePath, Guid? PasswordSlotId = null);
public sealed record OpenArchiveResponse(ArchiveDescriptor Archive);

public sealed record OpenNestedArchiveRequest(
    ArchiveSessionId ParentSessionId,
    EntryId EntryId,
    long MaximumBytes,
    Guid? ParentPasswordSlotId = null,
    Guid? NestedPasswordSlotId = null);

public sealed record OpenNestedArchiveResponse(
    ArchiveDescriptor Archive,
    ArchiveSessionId ParentSessionId,
    EntryId SourceEntryId,
    int Depth,
    long MaterializedBytes,
    long ChainMaterializedBytes);

public sealed record CloseArchiveRequest(ArchiveSessionId SessionId);
public sealed record CloseArchiveResponse(bool Closed);

public sealed record ListEntriesRequest(ArchiveSessionId SessionId, int Offset = 0, int Limit = 512);
public sealed record ListEntriesResponse(
    IReadOnlyList<ArchiveEntryDescriptor> Entries,
    int Offset,
    int TotalCount,
    int? NextOffset);

public sealed record OpenEntryStreamRequest(
    ArchiveSessionId SessionId,
    EntryId EntryId,
    long MaximumBytes,
    string PipeName,
    string BootstrapNonce,
    Guid? PasswordSlotId = null);

public sealed record OpenEntryStreamResponse(
    EntryStreamId StreamId,
    EntryId EntryId,
    long? DeclaredLength);

public sealed record CancelEntryStreamRequest(EntryStreamId StreamId);
public sealed record CancelEntryStreamResponse(bool CancellationRequested);

public sealed record StartExtractionRequest(
    ArchiveSessionId SessionId,
    IReadOnlyList<EntryId> EntryIds,
    string DestinationPath,
    Guid? PasswordSlotId = null);

public sealed record StartIntegrityTestRequest(
    ArchiveSessionId SessionId,
    IReadOnlyList<EntryId> EntryIds,
    Guid? PasswordSlotId = null);

public enum ArchiveCompressionPreset
{
    Store,
    Fast,
    Balanced,
    Maximum,
}

public enum ManagedArchiveCreationFormat
{
    TarGZip,
    TarBZip2,
    GZip,
    BZip2,
}

public enum RarEncoderAvailability
{
    Available,
    NotInstalled,
    UpdateRequired,
    UntrustedBinary,
    UnsupportedPlatform,
    RestartRequired,
}

public sealed record ArchiveCreationFile(string SourcePath, string ArchivePath);

public sealed record ArchiveCreationDirectory(string SourcePath, string ArchivePath);

public sealed record StartZipCreationRequest(
    string DestinationPath,
    IReadOnlyList<ArchiveCreationFile> Files,
    ArchiveCompressionPreset CompressionPreset = ArchiveCompressionPreset.Balanced);

public sealed record StartZipTreeCreationRequest(
    string DestinationPath,
    IReadOnlyList<ArchiveCreationFile> Files,
    IReadOnlyList<ArchiveCreationDirectory> Directories,
    ArchiveCompressionPreset CompressionPreset = ArchiveCompressionPreset.Balanced);

public sealed record StartTarCreationRequest(
    string DestinationPath,
    IReadOnlyList<ArchiveCreationFile> Files,
    ArchiveCompressionPreset CompressionPreset = ArchiveCompressionPreset.Store);

public sealed record StartTarTreeCreationRequest(
    string DestinationPath,
    IReadOnlyList<ArchiveCreationFile> Files,
    IReadOnlyList<ArchiveCreationDirectory> Directories,
    ArchiveCompressionPreset CompressionPreset = ArchiveCompressionPreset.Store);

public sealed record StartRarCreationRequest(
    string DestinationPath,
    IReadOnlyList<ArchiveCreationFile> Files,
    ArchiveCompressionPreset CompressionPreset = ArchiveCompressionPreset.Balanced);

public sealed record StartRarTreeCreationRequest(
    string DestinationPath,
    IReadOnlyList<ArchiveCreationFile> Files,
    IReadOnlyList<ArchiveCreationDirectory> Directories,
    ArchiveCompressionPreset CompressionPreset = ArchiveCompressionPreset.Balanced);

public sealed record StartTarGZipCreationRequest(
    string DestinationPath,
    IReadOnlyList<ArchiveCreationFile> Files,
    ArchiveCompressionPreset CompressionPreset = ArchiveCompressionPreset.Balanced);

public sealed record StartTarGZipTreeCreationRequest(
    string DestinationPath,
    IReadOnlyList<ArchiveCreationFile> Files,
    IReadOnlyList<ArchiveCreationDirectory> Directories,
    ArchiveCompressionPreset CompressionPreset = ArchiveCompressionPreset.Balanced);

public sealed record StartTarBZip2CreationRequest(
    string DestinationPath,
    IReadOnlyList<ArchiveCreationFile> Files,
    ArchiveCompressionPreset CompressionPreset = ArchiveCompressionPreset.Balanced);

public sealed record StartTarBZip2TreeCreationRequest(
    string DestinationPath,
    IReadOnlyList<ArchiveCreationFile> Files,
    IReadOnlyList<ArchiveCreationDirectory> Directories,
    ArchiveCompressionPreset CompressionPreset = ArchiveCompressionPreset.Balanced);

public sealed record StartGZipCreationRequest(
    string DestinationPath,
    string SourcePath,
    ArchiveCompressionPreset CompressionPreset = ArchiveCompressionPreset.Balanced);

public sealed record StartBZip2CreationRequest(
    string DestinationPath,
    string SourcePath,
    ArchiveCompressionPreset CompressionPreset = ArchiveCompressionPreset.Balanced);

public sealed record ArchiveEntryKey(string NormalizedPath, int DuplicateOrdinal);

public sealed record ArchiveRebuildCopyFile(string SourcePath, string ArchivePath);

public sealed record StartZipRebuildCopyRequest(
    ArchiveSessionId SessionId,
    string DestinationPath,
    IReadOnlyList<ArchiveEntryKey> EntriesToDelete,
    IReadOnlyList<ArchiveRebuildCopyFile> Files,
    ArchiveCompressionPreset CompressionPreset = ArchiveCompressionPreset.Balanced);

public sealed record ArchiveRebuildRename(
    ArchiveEntryKey EntryKey,
    string FinalArchivePath);

public sealed record StartZipRebuildRenameRequest(
    ArchiveSessionId SessionId,
    string DestinationPath,
    IReadOnlyList<ArchiveEntryKey> EntriesToDelete,
    IReadOnlyList<ArchiveRebuildCopyFile> Files,
    IReadOnlyList<ArchiveRebuildRename> Renames,
    ArchiveCompressionPreset CompressionPreset = ArchiveCompressionPreset.Balanced);

public sealed record GetZipRebuildTreePlanRequest(ArchiveSessionId SessionId);

public sealed record ZipRebuildTreePlanNode(
    string NormalizedPath,
    bool HasExplicitDirectoryEntry,
    int DescendantCount,
    int MaterialEntryCount,
    string SubtreeRevision);

public sealed record GetZipRebuildTreePlanResponse(
    IReadOnlyList<ZipRebuildTreePlanNode> Directories);

public sealed record GetZipRebuildSourceFolderReviewRequest(
    ArchiveSessionId SessionId,
    IReadOnlyList<ArchiveCreationDirectory> Directories);

public sealed record ZipRebuildSourceFolderReview(
    string SourcePath,
    string ArchiveRoot,
    int DescendantCount,
    int FileCount);

public sealed record GetZipRebuildSourceFolderReviewResponse(
    IReadOnlyList<ZipRebuildSourceFolderReview> Directories);

public enum ArchiveRebuildTreeActionKind
{
    Move,
    Delete,
}

public sealed record ArchiveRebuildTreeAction(
    string NormalizedPath,
    string ExpectedRevision,
    ArchiveRebuildTreeActionKind Kind,
    string? FinalArchivePath = null);

public sealed record StartZipRebuildTreeEditRequest(
    ArchiveSessionId SessionId,
    string DestinationPath,
    IReadOnlyList<ArchiveEntryKey> EntriesToDelete,
    IReadOnlyList<ArchiveRebuildCopyFile> Files,
    IReadOnlyList<ArchiveRebuildRename> Renames,
    IReadOnlyList<ArchiveCreationDirectory> Directories,
    IReadOnlyList<ArchiveRebuildTreeAction> TreeActions,
    ArchiveCompressionPreset CompressionPreset = ArchiveCompressionPreset.Balanced);

public enum ArchiveRebuildMetadataTargetKind
{
    RetainedEntryKey,
    MappedFinalPath,
}

public sealed record ArchiveRebuildMetadataTarget(
    ArchiveRebuildMetadataTargetKind Kind,
    ArchiveEntryKey? RetainedEntryKey = null,
    string? MappedFinalArchivePath = null,
    ArchiveEntryKind? MappedKind = null);

public sealed record ArchiveDosLocalTimestamp(
    int Year,
    int Month,
    int Day,
    int Hour,
    int Minute,
    int Second);

public sealed record ArchiveRebuildMetadataDirective(
    ArchiveRebuildMetadataTarget Target,
    ArchiveDosLocalTimestamp? LastWriteTime = null,
    ushort? AttributesLowWord = null);

public sealed record StartZipRebuildMetadataRequest(
    ArchiveSessionId SessionId,
    string DestinationPath,
    IReadOnlyList<ArchiveEntryKey> EntriesToDelete,
    IReadOnlyList<ArchiveRebuildCopyFile> Files,
    IReadOnlyList<ArchiveRebuildRename> Renames,
    IReadOnlyList<ArchiveCreationDirectory> Directories,
    IReadOnlyList<ArchiveRebuildTreeAction> TreeActions,
    IReadOnlyList<ArchiveRebuildMetadataDirective> Metadata,
    ArchiveCompressionPreset CompressionPreset = ArchiveCompressionPreset.Balanced);

public sealed record GetZipWriteCapabilitiesRequest(
    ArchiveSessionId? SessionId = null);

public sealed record GetZipWriteCapabilitiesResponse(
    bool CanCreate,
    bool CanRebuild,
    bool SupportsAdd,
    bool SupportsReplace,
    bool SupportsDelete,
    bool SupportsFileRename,
    bool SupportsTreeEdit,
    bool SupportsMetadata,
    IReadOnlyList<ArchiveCompressionPreset> CompressionPresets,
    int MaximumEntries,
    long MaximumSourceBytes,
    bool SupportsEncryption,
    bool SupportsFilenameEncryption,
    bool SupportsSplitOutput,
    bool SupportsExistingDestinationReplacement,
    bool SupportsInPlaceUpdate);

public sealed record GetTarWriteCapabilitiesRequest();

public sealed record GetTarWriteCapabilitiesResponse(
    [property: JsonRequired] bool CanCreate,
    [property: JsonRequired] bool CanRebuild,
    [property: JsonRequired] bool SupportsTreeCreation,
    [property: JsonRequired] IReadOnlyList<ArchiveCompressionPreset> CompressionPresets,
    [property: JsonRequired] int MaximumEntries,
    [property: JsonRequired] long MaximumSourceBytes,
    [property: JsonRequired] bool SupportsEncryption,
    [property: JsonRequired] bool SupportsFilenameEncryption,
    [property: JsonRequired] bool SupportsSplitOutput,
    [property: JsonRequired] bool SupportsExistingDestinationReplacement,
    [property: JsonRequired] bool SupportsInPlaceUpdate);

public sealed record GetRarWriteCapabilitiesRequest();

public sealed record GetRarWriteCapabilitiesResponse(
    [property: JsonRequired] bool CanCreate,
    [property: JsonRequired] bool CanRebuild,
    [property: JsonRequired] bool SupportsTreeCreation,
    [property: JsonRequired] IReadOnlyList<ArchiveCompressionPreset> CompressionPresets,
    [property: JsonRequired] int MaximumEntries,
    [property: JsonRequired] long MaximumSourceBytes,
    [property: JsonRequired] RarEncoderAvailability Availability,
    [property: JsonRequired] bool SupportsEncryption,
    [property: JsonRequired] bool SupportsFilenameEncryption,
    [property: JsonRequired] bool SupportsSplitOutput,
    [property: JsonRequired] bool SupportsExistingDestinationReplacement,
    [property: JsonRequired] bool SupportsInPlaceUpdate);

public sealed record ManagedWriteCapability(
    [property: JsonRequired] ManagedArchiveCreationFormat Format,
    [property: JsonRequired] bool CanCreate,
    [property: JsonRequired] bool SupportsTreeCreation,
    [property: JsonRequired] bool CanRebuild,
    [property: JsonRequired] IReadOnlyList<ArchiveCompressionPreset> CompressionPresets,
    [property: JsonRequired] int MaximumEntries,
    [property: JsonRequired] long MaximumSourceBytes,
    [property: JsonRequired] bool SupportsEncryption,
    [property: JsonRequired] bool SupportsFilenameEncryption,
    [property: JsonRequired] bool SupportsSplitOutput,
    [property: JsonRequired] bool SupportsExistingDestinationReplacement,
    [property: JsonRequired] bool SupportsInPlaceUpdate);

public sealed record GetManagedWriteCapabilitiesRequest();

public sealed record GetManagedWriteCapabilitiesResponse(
    [property: JsonRequired] IReadOnlyList<ManagedWriteCapability> Capabilities);

public sealed record GetOperationActivityRequest();

public sealed record GetOperationActivityResponse(
    [property: JsonRequired] DateTimeOffset SnapshotAtUtc,
    [property: JsonRequired] int MaximumActiveAndQueuedOperations,
    [property: JsonRequired] int MaximumConcurrentOperations,
    [property: JsonRequired] OperationQueueOrdering QueueOrdering,
    [property: JsonRequired] int MaximumRetainedTerminalOperations,
    [property: JsonRequired] bool SupportsReordering,
    [property: JsonRequired] bool SupportsPause,
    [property: JsonRequired] bool SupportsRetry,
    [property: JsonRequired] bool PersistsAcrossWorkerRestart,
    [property: JsonRequired] bool HistoryTruncated,
    [property: JsonRequired] IReadOnlyList<OperationActivitySnapshot> Operations);

public enum WorkerContainmentModel
{
    SameUserWorkerProcess,
}

public sealed record GetSupportDiagnosticsRequest();

public sealed record GetSupportDiagnosticsResponse(
    [property: JsonRequired] int ProtocolMajor,
    [property: JsonRequired] int ProtocolMinor,
    [property: JsonRequired] int EnabledFeatureCount,
    [property: JsonRequired] WorkerContainmentModel ContainmentModel,
    [property: JsonRequired] bool IsSameUserSecuritySandbox,
    [property: JsonRequired] int MaximumActiveAndQueuedOperations,
    [property: JsonRequired] int MaximumConcurrentOperations,
    [property: JsonRequired] OperationQueueOrdering QueueOrdering,
    [property: JsonRequired] int MaximumRetainedTerminalOperations,
    [property: JsonRequired] int ActiveAndQueuedOperationCount,
    [property: JsonRequired] int RetainedTerminalOperationCount,
    [property: JsonRequired] bool OperationHistoryTruncated,
    [property: JsonRequired] bool SupportsQueueReordering,
    [property: JsonRequired] bool SupportsPause,
    [property: JsonRequired] bool SupportsRetry,
    [property: JsonRequired] bool SupportsAutomaticRecoveryDisposition,
    [property: JsonRequired] bool PersistsOperationActivityAcrossWorkerRestart);

public sealed record StartOperationResponse(OperationId OperationId);
public sealed record CancelOperationRequest(OperationId OperationId);
public sealed record CancelOperationResponse(bool CancellationRequested);
public sealed record CancelRequestRequest(Guid TargetRequestId);
public sealed record CancelRequestResponse(bool CancellationRequested);

public sealed record CloseRequest();
public sealed record CloseResponse(bool Closing);
