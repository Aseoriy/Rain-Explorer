using RainArchives.Contracts;

namespace RainArchives.Core;

public sealed record ArchiveProbeResult(string Format, int Confidence, string? Reason = null)
{
    public static ArchiveProbeResult NoMatch(string reason) => new("unknown", 0, reason);
}

public sealed record ProviderArchiveEntry(
    int Ordinal,
    string RawPath,
    ArchiveEntryKind Kind,
    long? OriginalSize,
    long? CompressedSize,
    DateTimeOffset? ModifiedTimeUtc,
    int? WindowsAttributes,
    string? CompressionMethod,
    string? IntegrityValue,
    bool IsEncrypted,
    bool IsSolid,
    string? LinkTarget);

public interface IArchiveProvider
{
    string Id { get; }

    ArchiveProbeResult Probe(ReadOnlySpan<byte> prefix, string sourcePath);

    ValueTask<IArchiveHandle> OpenAsync(
        string sourcePath,
        ReadOnlyMemory<byte> passwordUtf8,
        CancellationToken cancellationToken);
}

public interface IArchiveHandle : IAsyncDisposable
{
    string Format { get; }
    string ProviderId { get; }
    ArchiveCapability Capabilities { get; }
    bool IsSolid { get; }
    ArchiveVolumeSetKind VolumeSetKind { get; }
    bool IsMultipart => VolumeSetKind == ArchiveVolumeSetKind.ConfirmedMultipart;
    IReadOnlyList<ProviderArchiveEntry> Entries { get; }

    ValueTask<Stream> OpenEntryStreamAsync(int providerOrdinal, CancellationToken cancellationToken);

    ValueTask CopyEntryToAsync(
        int providerOrdinal,
        Stream destination,
        CancellationToken cancellationToken);
}

public sealed class ArchiveProviderException : Exception
{
    public ArchiveProviderException(
        ArchiveErrorCategory category,
        string code,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Category = category;
        Code = code;
    }

    public ArchiveErrorCategory Category { get; }
    public string Code { get; }
}
