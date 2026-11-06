using RainArchives.Contracts;

namespace RainArchives.Core;

public sealed record ArchiveWriteEntry(
    string ArchivePath,
    long Length,
    DateTimeOffset LastWriteTime,
    int? WindowsAttributes = null);

public sealed record ArchiveWriteDirectory(
    string ArchivePath,
    DateTimeOffset LastWriteTime,
    int? WindowsAttributes = null);

/// <summary>
/// The exact ordered entry shape a staged writer may use for provider-owned
/// readback validation.  The provider never receives a filesystem path.
/// </summary>
public sealed record ArchiveStagedOutputManifestEntry(
    string ArchivePath,
    ArchiveEntryKind Kind,
    long? Length,
    DateTimeOffset LastWriteTime,
    string? ContentSha256 = null);

public interface IArchiveStagedOutputValidator
{
    string Format { get; }

    ValueTask ValidateAsync(
        Stream archive,
        IReadOnlyList<ArchiveStagedOutputManifestEntry> expected,
        CancellationToken cancellationToken);
}

public interface IArchiveWriterProvider
{
    string Id { get; }
    string Format { get; }
    IReadOnlySet<ArchiveCompressionPreset> CompressionPresets { get; }

    ValueTask<IArchiveWriterHandle> CreateAsync(
        Stream destination,
        ArchiveCompressionPreset compressionPreset,
        CancellationToken cancellationToken);
}

public interface IArchiveWriterHandle : IAsyncDisposable
{
    ValueTask AddDirectoryAsync(
        ArchiveWriteDirectory entry,
        CancellationToken cancellationToken);

    ValueTask AddFileAsync(
        ArchiveWriteEntry entry,
        Stream source,
        Func<long, ValueTask> reportEntryBytes,
        CancellationToken cancellationToken);

    ValueTask AddFileFromProducerAsync(
        ArchiveWriteEntry entry,
        Func<Stream, CancellationToken, ValueTask> writeSource,
        Func<long, ValueTask> reportEntryBytes,
        CancellationToken cancellationToken) =>
        ValueTask.FromException(new ArchiveProviderException(
            ArchiveErrorCategory.UnsupportedCapability,
            "writer.entry_producer_unsupported",
            "The archive writer does not support checksum-copy producers."));

    ValueTask CompleteAsync(CancellationToken cancellationToken);
}

public sealed class ArchiveWriterProviderRegistry
{
    private readonly IReadOnlyDictionary<string, IArchiveWriterProvider> _providers;

    public ArchiveWriterProviderRegistry(IEnumerable<IArchiveWriterProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var byFormat = new Dictionary<string, IArchiveWriterProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in providers)
        {
            ArgumentNullException.ThrowIfNull(provider);
            if (string.IsNullOrWhiteSpace(provider.Id)
                || string.IsNullOrWhiteSpace(provider.Format)
                || provider.CompressionPresets is null
                || !byFormat.TryAdd(provider.Format, provider))
            {
                throw new InvalidOperationException(
                    "Archive writer providers must have unique non-empty formats and valid capabilities.");
            }
        }

        _providers = byFormat;
    }

    public IArchiveWriterProvider GetRequired(string format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        return _providers.TryGetValue(format, out var provider)
            ? provider
            : throw new ArchiveProviderException(
                ArchiveErrorCategory.UnsupportedFormat,
                "creation.format_not_supported",
                "The requested archive creation format is not available.");
    }
}
