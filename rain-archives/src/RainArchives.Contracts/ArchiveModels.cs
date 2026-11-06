using System.Text.Json.Serialization;

namespace RainArchives.Contracts;

[Flags]
public enum ArchiveCapability
{
    None = 0,
    List = 1 << 0,
    OpenEntryStream = 1 << 1,
    Test = 1 << 2,
    Extract = 1 << 3,
    ReadEncrypted = 1 << 4,
    ReadVolumes = 1 << 5,
    Create = 1 << 6,
    UpdateByRewrite = 1 << 7,
    EncryptContent = 1 << 8,
    EncryptNames = 1 << 9,
    CreateVolumes = 1 << 10,
}

public enum ArchiveEntryKind
{
    Unknown,
    File,
    Directory,
    SymbolicLink,
    HardLink,
    ReparsePoint,
}

public enum ArchiveVolumeSetKind
{
    Unknown,
    Single,
    ConfirmedMultipart,
}

public enum EntrySafetyStatus
{
    Safe,
    UnsafePath,
    PathCollision,
    UnsupportedLink,
    UnsupportedEntryKind,
    ResourceLimit,
}

public sealed record ArchiveFingerprint(
    long Length,
    DateTimeOffset LastWriteTimeUtc,
    string? FileIdentity,
    string PrefixSha256);

public sealed record ArchiveDescriptor(
    ArchiveSessionId SessionId,
    string Format,
    string ProviderId,
    ArchiveCapability Capabilities,
    ArchiveFingerprint Fingerprint,
    int EntryCount,
    bool IsSolid,
    bool HasEncryptedEntries,
    bool IsMultipart,
    bool IsReadOnly,
    IReadOnlyList<ArchiveWarning> Warnings,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ArchiveVolumeSetKind? VolumeSetKind = null);

public sealed record ArchiveEntryDescriptor(
    EntryId EntryId,
    EntryId? ParentEntryId,
    int ProviderOrdinal,
    string RawPath,
    string DisplayName,
    ArchiveEntryKind Kind,
    long? OriginalSize,
    long? CompressedSize,
    DateTimeOffset? ModifiedTimeUtc,
    int? WindowsAttributes,
    string? CompressionMethod,
    string? IntegrityValue,
    bool IsEncrypted,
    bool IsSolid,
    string? LinkTarget,
    EntrySafetyStatus SafetyStatus,
    string? SafetyReason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NormalizedPath = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int DuplicateOrdinal = 0);

public sealed record ArchiveWarning(string Code, string Message, EntryId? EntryId = null);
