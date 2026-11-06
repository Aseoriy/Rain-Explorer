using System.Text.Json.Serialization;

namespace RainArchives.Contracts;

public enum ExtractionRecoveryPhase
{
    Admitted,
    Writing,
    Finalized,
    CommitIntent,
    Committed,
}

public enum ExtractionRecoveryInventoryState
{
    Incomplete,
    Finalized,
    PublicationUncertain,
    PublishedWithoutTerminal,
}

public sealed record ExtractionRecoverySummary(
    [property: JsonRequired] string RecoveryId,
    [property: JsonRequired] ExtractionRecoveryInventoryState State,
    [property: JsonRequired] ExtractionRecoveryPhase Phase,
    [property: JsonRequired] int Sequence,
    [property: JsonRequired] DateTimeOffset RecordedAtUtc,
    [property: JsonRequired] bool HasIncompleteTail);

public sealed record ListExtractionRecoveriesRequest();

public sealed record ListExtractionRecoveriesResponse(
    [property: JsonRequired] IReadOnlyList<ExtractionRecoverySummary> Recoveries,
    [property: JsonRequired] int ActiveOperationCount,
    [property: JsonRequired] int SuspiciousOperationCount,
    [property: JsonRequired] int IncompleteJournalCount,
    [property: JsonRequired] bool IsTruncated,
    [property: JsonRequired] bool InspectionIncomplete);

public sealed record InspectExtractionRecoveryRequest(
    [property: JsonRequired] string RecoveryId);

public enum ExtractionRecoveryParentState
{
    MatchesJournal,
    Missing,
    IdentityMismatch,
    ReparsePointOrWrongKind,
    AccessDenied,
    Unavailable,
    NotInspected,
}

public enum ExtractionRecoveryArtifactState
{
    NotBoundByJournal,
    NotInspected,
    Absent,
    IdentityMatchRevisionUnbound,
    ExactMatch,
    IdentityMismatch,
    RevisionMismatch,
    WrongKind,
    ReparsePoint,
    UnexpectedHardLinks,
    UnexpectedDataStreams,
    Excessive,
    AccessDenied,
    Unavailable,
}

public enum ExtractionRecoveryAssessment
{
    NoOwnedArtifactObserved,
    OwnedIncompleteStagingObserved,
    OwnedFinalizedStagingObserved,
    OwnedFinalObserved,
    MultipleOrConflictingArtifacts,
    ParentChanged,
    ChangedOrSuspicious,
    Excessive,
    InspectionIncomplete,
}

public sealed record ExtractionRecoveryArtifactInspection(
    [property: JsonRequired] ExtractionRecoveryArtifactState State,
    int? ObservedFileCount = null,
    int? ObservedDirectoryCount = null,
    long? ObservedTotalBytes = null,
    int? ObservedNamedStreamCount = null,
    string? ObservedRevision = null);

public sealed record InspectExtractionRecoveryResponse(
    [property: JsonRequired] ExtractionRecoverySummary Recovery,
    [property: JsonRequired] string DestinationPath,
    [property: JsonRequired] bool IsRemoteDestination,
    int? JournalFileCount,
    int? JournalDirectoryCount,
    long? JournalTotalBytes,
    int? JournalNamedStreamCount,
    string? JournalRevision,
    [property: JsonRequired] ExtractionRecoveryParentState DestinationParent,
    [property: JsonRequired] ExtractionRecoveryArtifactInspection StagingArtifact,
    [property: JsonRequired] ExtractionRecoveryArtifactInspection FinalArtifact,
    [property: JsonRequired] ExtractionRecoveryAssessment Assessment,
    [property: JsonRequired] DateTimeOffset InspectedAtUtc,
    [property: JsonRequired] bool SupportsMutation,
    [property: JsonRequired] bool SupportsRetry,
    [property: JsonRequired] bool SupportsCleanup);
