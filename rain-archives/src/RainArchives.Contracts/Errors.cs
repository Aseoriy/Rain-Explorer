namespace RainArchives.Contracts;

public enum ArchiveErrorCategory
{
    Unknown,
    InvalidRequest,
    UnsupportedFormat,
    UnsupportedCapability,
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
    Cancelled,
    ProviderFailure,
    WorkerFailure,
    ProtocolMismatch,
}

public sealed record ArchiveError(
    string Code,
    ArchiveErrorCategory Category,
    string Message,
    bool IsRetryable = false,
    EntryId? EntryId = null,
    string? DiagnosticId = null);

public sealed record ErrorResponse(ArchiveError Error);
