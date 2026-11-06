using System.Text.Json.Serialization;

namespace RainArchives.Contracts;

public enum ArchiveCreationRecoveryPhase
{
    Admitted,
    Writing,
    Finalized,
    CommitIntent,
    Committed,
}

public sealed record ArchiveCreationRecoverySummary(
    [property: JsonRequired] string RecoveryId,
    [property: JsonRequired] OperationId OperationId,
    [property: JsonRequired] ArchiveCreationRecoveryPhase Phase,
    [property: JsonRequired] int Sequence,
    [property: JsonRequired] DateTimeOffset RecordedAtUtc,
    [property: JsonRequired] bool HasIncompleteTail);

public sealed record ListArchiveCreationRecoveriesRequest();

public sealed record ListArchiveCreationRecoveriesResponse(
    [property: JsonRequired] IReadOnlyList<ArchiveCreationRecoverySummary> Recoveries,
    [property: JsonRequired] int ActiveOperationCount,
    [property: JsonRequired] int SuspiciousOperationCount,
    [property: JsonRequired] int IncompleteJournalCount,
    [property: JsonRequired] bool IsTruncated,
    [property: JsonRequired] bool InspectionIncomplete);

public sealed record InspectArchiveCreationRecoveryRequest(
    [property: JsonRequired] string RecoveryId);

public enum ArchiveCreationRecoveryParentState
{
    MatchesJournal,
    Missing,
    IdentityMismatch,
    ReparsePointOrWrongKind,
    AccessDenied,
    Unavailable,
    NotInspected,
}

public enum ArchiveCreationRecoveryArtifactState
{
    NotBoundByJournal,
    NotInspected,
    Absent,
    IdentityMatchLengthUnbound,
    ExactMatch,
    IdentityMismatch,
    LengthMismatch,
    WrongKind,
    ReparsePoint,
    UnexpectedHardLinks,
    AccessDenied,
    Unavailable,
}

public enum ArchiveCreationRecoveryAssessment
{
    NoOwnedArtifactObserved,
    OwnedStagingObserved,
    OwnedFinalObserved,
    MultipleOrConflictingArtifacts,
    ParentChanged,
    InspectionIncomplete,
}

public sealed record ArchiveCreationRecoveryArtifactInspection(
    [property: JsonRequired] ArchiveCreationRecoveryArtifactState State,
    long? ObservedLength = null,
    long? LinkCount = null);

public sealed record InspectArchiveCreationRecoveryResponse(
    [property: JsonRequired] ArchiveCreationRecoverySummary Recovery,
    [property: JsonRequired] string DestinationPath,
    [property: JsonRequired] string StagingFileName,
    long? JournalBoundLength,
    [property: JsonRequired] bool IsRemoteDestination,
    [property: JsonRequired] ArchiveCreationRecoveryParentState DestinationParent,
    [property: JsonRequired] ArchiveCreationRecoveryArtifactInspection StagingArtifact,
    [property: JsonRequired] ArchiveCreationRecoveryArtifactInspection FinalArtifact,
    [property: JsonRequired] ArchiveCreationRecoveryAssessment Assessment,
    [property: JsonRequired] DateTimeOffset InspectedAtUtc);

public enum ArchiveCreationRecoveryDispositionAction
{
    CommitOwnedStaging,
    DiscardOwnedStaging,
    AcceptPublishedFinal,
    AcknowledgeNoOwnedArtifact,
}

public enum ArchiveCreationRecoveryDispositionResult
{
    Completed,
    AlreadyCompleted,
}

public sealed record ApplyArchiveCreationRecoveryDispositionRequest(
    [property: JsonRequired] string RecoveryId,
    [property: JsonRequired] ArchiveCreationRecoveryPhase ExpectedPhase,
    [property: JsonRequired] int ExpectedSequence,
    [property: JsonRequired] DateTimeOffset ExpectedRecordedAtUtc,
    [property: JsonRequired] ArchiveCreationRecoveryDispositionAction Action);

public sealed record ApplyArchiveCreationRecoveryDispositionResponse(
    [property: JsonRequired] string RecoveryId,
    [property: JsonRequired] ArchiveCreationRecoveryDispositionAction Action,
    [property: JsonRequired] ArchiveCreationRecoveryDispositionResult Result,
    [property: JsonRequired] ArchiveCreationRecoveryAssessment FinalAssessment,
    [property: JsonRequired] DateTimeOffset ResolvedAtUtc);
