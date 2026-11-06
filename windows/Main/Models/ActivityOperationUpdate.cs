namespace RainExplorer.Models;

public enum ActivityOperationKind
{
    IntegrityTest,
    Extraction,
    TransferOut,
    ArchiveCreation,
    ArchiveRebuild,
}

public enum ActivityOperationPhase
{
    Queued,
    Validating,
    WaitingForInput,
    Running,
    Finalizing,
    Completed,
    CompletedWithWarnings,
    Cancelled,
    Failed,
}

public enum ActivityOperationFailure
{
    None,
    InvalidRequest,
    Unsupported,
    InvalidArchive,
    CorruptArchive,
    PasswordRequired,
    IncorrectPassword,
    UnsafePath,
    PathCollision,
    ResourceLimit,
    SourceChanged,
    DestinationConflict,
    PermissionDenied,
    InsufficientSpace,
    ProviderFailure,
    WorkerUnavailable,
    ProtocolMismatch,
    UnsafeOutput,
    Unknown,
    CreationEntryLimit,
    CreationSourceSizeLimit,
    CreationMetadataLimit,
    CreationPathLimit,
    CreationOutputLimit,
    CreationRarWorkspacePathTooLong,
    CreationRarWorkspaceIoFailure,
}

/// <summary>
/// Fixed UI-only detail for an extraction destination conflict. This intentionally
/// contains no provider error code, message, path, or archive/member text.
/// </summary>
public enum ArchiveDestinationConflictReason
{
    None,
    ExistingDestination,
    AppearedBeforeCommit,
    StagingValidationConflict,
    TemporaryFileHardLinkConflict,
    Unknown,
}

internal static class ArchiveDestinationConflictText
{
    public static string Describe(ArchiveDestinationConflictReason reason) => reason switch
    {
        ArchiveDestinationConflictReason.ExistingDestination =>
            "The extraction destination already exists. Nothing was overwritten.",
        ArchiveDestinationConflictReason.AppearedBeforeCommit =>
            "⚠️ The extraction destination appeared before commit. It was preserved and no retry was started.",
        ArchiveDestinationConflictReason.TemporaryFileHardLinkConflict =>
            "⚠️ Another program linked a temporary extracted file. Choose a folder that is not being synced. No retry was started.",
        ArchiveDestinationConflictReason.StagingValidationConflict =>
            "⚠️ Temporary-file validation found a conflict. No retry was started.",
        _ => "⚠️ An extraction destination conflict occurred. No retry was started.",
    };
}

/// <summary>Fixed-field, provider-neutral activity update for a long-running operation.</summary>
public sealed record ActivityOperationUpdate(
    ActivityOperationKind Kind,
    ActivityOperationPhase Phase,
    long BytesProcessed = 0,
    long? TotalBytes = null,
    int EntriesProcessed = 0,
    int? TotalEntries = null,
    TimeSpan? Elapsed = null,
    double? BytesPerSecond = null,
    double? CompressionRatio = null,
    string? CurrentItem = null,
    int WarningCount = 0,
    ActivityOperationFailure Failure = ActivityOperationFailure.None,
    long? FailureLimit = null,
    ArchiveDestinationConflictReason DestinationConflictReason =
        ArchiveDestinationConflictReason.None)
{
    public const int MaximumCurrentItemCharacters = 140;
}

/// <summary>Fixed creation diagnostics; never render provider messages or paths.</summary>
internal static class ArchiveCreationFailureText
{
    public static string? Describe(ActivityOperationFailure failure, long? limit = null) => failure switch
    {
        ActivityOperationFailure.CreationEntryLimit => limit is > 0 and <= 65_536
            ? $"Too many items: this format allows {limit.Value:N0} files and folders. Choose a smaller selection."
            : "Too many files and folders. Choose a smaller selection.",
        ActivityOperationFailure.CreationSourceSizeLimit => limit is > 0 and <= 68_719_476_736
            ? $"The selected files exceed the {limit.Value / (1024d * 1024 * 1024):0.##} GiB source-size limit. Choose a smaller selection."
            : "The selected files exceed the source-size limit. Choose a smaller selection.",
        ActivityOperationFailure.CreationMetadataLimit =>
            "The file names and folder paths exceed the archive metadata budget. Choose a smaller selection.",
        ActivityOperationFailure.CreationPathLimit =>
            "A selected file or folder path is too long. Shorten its path or choose a closer source folder.",
        ActivityOperationFailure.CreationOutputLimit =>
            "The archive exceeded its output-size budget. Choose a smaller selection.",
        ActivityOperationFailure.CreationRarWorkspacePathTooLong =>
            "A file or folder name is too long for RAR preparation. Shorten nested names or select a smaller subfolder.",
        ActivityOperationFailure.CreationRarWorkspaceIoFailure =>
            "Rain could not prepare the temporary files for RAR creation. Check available disk space and folder permissions.",
        _ => null,
    };
}
