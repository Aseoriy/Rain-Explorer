using System.Text.Json.Serialization;

namespace RainArchives.Contracts;

public enum OperationKind
{
    Extract,
    IntegrityTest,
    CreateArchive,
    RebuildArchive,
}

public enum OperationPhase
{
    Queued,
    Validating,
    AwaitingPassword,
    AwaitingConflict,
    AwaitingRiskApproval,
    Running,
    Finalizing,
    Completed,
    CompletedWithWarnings,
    Cancelled,
    Failed,
}

public enum OperationEventKind
{
    StateChanged,
    Progress,
    Warning,
    Terminal,
}

public enum OperationQueueOrdering
{
    Unspecified,
}

public sealed record OperationProgress(
    long BytesProcessed,
    long? TotalBytes,
    int EntriesProcessed,
    int? TotalEntries,
    EntryId? CurrentEntryId,
    string? CurrentEntryName,
    TimeSpan Elapsed,
    double? BytesPerSecond,
    double? CompressionRatio);

public sealed record OperationActivityProgress(
    [property: JsonRequired] long BytesProcessed,
    long? TotalBytes,
    [property: JsonRequired] int EntriesProcessed,
    int? TotalEntries,
    [property: JsonRequired] TimeSpan Elapsed,
    double? BytesPerSecond,
    double? CompressionRatio);

public sealed record OperationActivitySnapshot(
    [property: JsonRequired] OperationId OperationId,
    [property: JsonRequired] OperationKind Kind,
    [property: JsonRequired] OperationPhase Phase,
    [property: JsonRequired] long LastEventSequence,
    [property: JsonRequired] DateTimeOffset CreatedAtUtc,
    [property: JsonRequired] DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    OperationActivityProgress? Progress,
    [property: JsonRequired] int WarningCount,
    [property: JsonRequired] bool CanCancel);

public sealed record OperationEvent(
    OperationId OperationId,
    long Sequence,
    OperationEventKind Kind,
    OperationPhase Phase,
    OperationProgress? Progress = null,
    ArchiveWarning? Warning = null,
    ArchiveError? Error = null);
