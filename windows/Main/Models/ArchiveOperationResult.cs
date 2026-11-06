namespace RainExplorer.Models;

public enum ArchiveOperationOutcome
{
    Completed,
    CompletedWithWarnings,
    Cancelled,
    Busy,
    InvalidRequest,
    InvalidSelection,
    InvalidDestination,
    DestinationConflict,
    Unsupported,
    Stale,
    Offline,
    UnsafeOutput,
    Failed,
}

/// <summary>A fixed-field result. Provider messages, paths, and secret data never cross into UI copy.</summary>
public sealed record ArchiveOperationResult(
    ArchiveOperationOutcome Outcome,
    ActivityOperationFailure Failure = ActivityOperationFailure.None,
    long? FailureLimit = null,
    ArchiveDestinationConflictReason DestinationConflictReason =
        ArchiveDestinationConflictReason.None)
{
    public bool Succeeded => Outcome is ArchiveOperationOutcome.Completed
        or ArchiveOperationOutcome.CompletedWithWarnings;
}
