using RainArchives.Contracts;

namespace RainExplorer.Models;

public enum ArchiveWriteFormat
{
    Zip,
    Tar,
    TarGZip,
    TarBZip2,
    GZip,
    BZip2,
    Rar,
}

public enum ArchiveWriteCapabilityState
{
    Ready,
    Unavailable,
    Offline,
    Stale,
    Error,
}

public enum ArchiveCreationSourceKind
{
    File,
    Directory,
}

/// <summary>A selected physical root. The Worker remains authoritative for its filesystem shape.</summary>
public sealed record ArchiveCreationSource(
    ArchiveCreationSourceKind Kind,
    string SourcePath,
    string ArchivePath);

public sealed record ArchiveCreationFormatOption(
    ArchiveWriteFormat Format,
    string Name,
    string Extension,
    bool SupportsDirectories,
    IReadOnlyList<ArchiveCompressionPreset> CompressionPresets,
    int MaximumEntries,
    long MaximumSourceBytes,
    int MaximumTreeEntries = ProtocolConstants.MaximumZipCreationEntries);

public sealed record ArchiveCreationCapabilitySnapshot(
    ArchiveWriteCapabilityState State,
    IReadOnlyList<ArchiveCreationFormatOption> Formats,
    RarEncoderAvailability? RarAvailability = null)
{
    public bool IsReady => State == ArchiveWriteCapabilityState.Ready && Formats.Count > 0;

    public static ArchiveCreationCapabilitySnapshot Fixed(ArchiveWriteCapabilityState state) =>
        new(state, Array.Empty<ArchiveCreationFormatOption>());
}

public sealed record ArchiveCreationRequest(
    string DestinationPath,
    IReadOnlyList<ArchiveCreationSource> Sources,
    ArchiveWriteFormat Format,
    ArchiveCompressionPreset CompressionPreset);

/// <summary>Exact UI-review authority for one currently-open, filesystem-backed ZIP session.</summary>
public sealed record ArchiveRebuildCapabilitySnapshot(
    ArchiveWriteCapabilityState State,
    ArchiveSessionId SessionId,
    ArchiveFingerprint? Fingerprint,
    string SourcePath,
    IReadOnlyList<ArchiveCompressionPreset> CompressionPresets)
{
    public bool IsReady => State == ArchiveWriteCapabilityState.Ready
        && Fingerprint is not null
        && CompressionPresets.Count > 0;

    public static ArchiveRebuildCapabilitySnapshot Fixed(ArchiveWriteCapabilityState state) =>
        new(state, default, null, string.Empty, Array.Empty<ArchiveCompressionPreset>());
}

public sealed record ArchiveRebuildRequest(
    ArchiveRebuildCapabilitySnapshot Authority,
    string DestinationPath,
    ArchiveCompressionPreset CompressionPreset);
