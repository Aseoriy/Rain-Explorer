using System.IO.Compression;
using RainArchives.Contracts;
using RainArchives.Core;
using SharpCompress.Common;
using SharpCompress.Crypto;
using SharpCompress.Providers;
using SharpCompress.Readers;

namespace RainArchives.Providers.SharpCompress;

internal sealed class SequentialReaderArchiveHandle : IArchiveHandle
{
    private const long MaximumKnownEntryOutputBytes = 64L << 30;
    private const long MaximumUnknownEntryOutputBytes = 8L << 30;

    private readonly string _sourcePath;
    private readonly ReaderOptions _options;
    private readonly IReadOnlyList<ProviderArchiveEntry> _entries;
    private readonly string? _outerCompressionFormat;
    private readonly SemaphoreSlim _readerGate = new(1, 1);
    private readonly SemaphoreSlim _outerValidationGate = new(1, 1);
    private readonly object _stateGate = new();
    private IAsyncReader? _reader;
    private int _readerOrdinal = -1;
    private bool _readerEntryConsumed = true;
    private bool _readerNeedsReset = true;
    private long _readerDecodedWorkBytes;
    private bool _streamOpen;
    private int _streamOrdinal = -1;
    private bool _outerCompressionValidated;
    private bool _disposed;

    private SequentialReaderArchiveHandle(
        string sourcePath,
        ReaderOptions options,
        string format,
        IReadOnlyList<ProviderArchiveEntry> entries,
        string? outerCompressionFormat)
    {
        _sourcePath = sourcePath;
        _options = options;
        Format = format;
        _entries = entries;
        _outerCompressionFormat = outerCompressionFormat;
    }

    public string Format { get; }
    public string ProviderId => "sharpcompress/0.50.4";
    public ArchiveCapability Capabilities =>
        ArchiveCapability.List
        | ArchiveCapability.OpenEntryStream
        | ArchiveCapability.Test
        | ArchiveCapability.Extract;
    public bool IsSolid => false;
    public ArchiveVolumeSetKind VolumeSetKind => ArchiveVolumeSetKind.Single;
    public IReadOnlyList<ProviderArchiveEntry> Entries => _entries;

    public static async ValueTask<IArchiveHandle> CreateAsync(
        string sourcePath,
        string outerFormat,
        ReaderOptions options,
        CancellationToken cancellationToken)
    {
        await using var reader = await ReaderFactory
            .OpenAsyncReader(sourcePath, options, cancellationToken)
            .ConfigureAwait(false);
        var entries = new List<ProviderArchiveEntry>();
        var metadataBudget = new ArchiveMetadataBudget();
        long decodedWorkBytes = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await reader.MoveToNextEntryAsync(cancellationToken).ConfigureAwait(false))
            {
                break;
            }

            var fallbackPath = reader.Type == ArchiveType.GZip
                ? StreamArchiveMetadata.GetSyntheticEntryPath(sourcePath, outerFormat, entries.Count)
                : null;
            var entry = StreamArchiveMetadata.ToProviderEntry(
                reader.Entry,
                entries.Count,
                fallbackPath,
                checksumAvailable: reader.Type == ArchiveType.GZip,
                zeroGzipMtimeMeansUnknown: reader.Type == ArchiveType.GZip);
            metadataBudget.Add(entry);
            StreamArchiveMetadata.ValidateSequentialEntry(entry);
            entries.Add(entry);
            var drained = await DrainCurrentEntryAsync(
                reader,
                entry,
                MaximumKnownEntryOutputBytes - decodedWorkBytes,
                cancellationToken).ConfigureAwait(false);
            decodedWorkBytes = ChargeDecodedWork(decodedWorkBytes, drained);
        }

        var format = reader.Type == ArchiveType.Tar
            ? outerFormat switch
            {
                "gzip" => "tar.gz",
                "bzip2" => "tar.bz2",
                "xz" => "tar.xz",
                _ => "tar",
            }
            : StreamArchiveMetadata.NormalizeFormat(reader.Type.ToString());
        return new SequentialReaderArchiveHandle(
            sourcePath,
            options,
            format,
            entries,
            outerCompressionFormat: reader.Type == ArchiveType.Tar
                && outerFormat is "gzip" or "bzip2" or "xz"
                    ? outerFormat
                    : null);
    }

    public async ValueTask<Stream> OpenEntryStreamAsync(
        int providerOrdinal,
        CancellationToken cancellationToken)
    {
        ValidateOrdinal(providerOrdinal);
        await _readerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            lock (_stateGate)
            {
                if (_streamOpen)
                {
                    throw new InvalidOperationException(
                        "A sequential archive entry stream is already open on this handle.");
                }
            }

            var reader = await OpenReaderAtAsync(providerOrdinal, cancellationToken).ConfigureAwait(false);
            if (reader.Entry.IsDirectory)
            {
                throw new InvalidOperationException("Directory entries do not have data streams.");
            }

            var entryStream = await reader
                .OpenEntryStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            lock (_stateGate)
            {
                _streamOpen = true;
                _streamOrdinal = providerOrdinal;
                _readerEntryConsumed = false;
            }

            return new OwnedReadStream(
                entryStream,
                owner: null,
                onAbort: reader.Cancel,
                onDisposed: fullyConsumed => OnStreamDisposed(providerOrdinal, fullyConsumed));
        }
        catch
        {
            await ResetReaderUnsafeAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _readerGate.Release();
        }
    }

    public async ValueTask CopyEntryToAsync(
        int providerOrdinal,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ValidateOrdinal(providerOrdinal);
        var succeeded = false;
        try
        {
            await EnsureOuterCompressionValidatedAsync(cancellationToken).ConfigureAwait(false);
            await using var source = await OpenEntryStreamAsync(providerOrdinal, cancellationToken)
                .ConfigureAwait(false);
            using var checksum = new Crc32Stream(Stream.Null);
            var buffer = new byte[64 * 1024];
            long bytesRead = 0;
            while (true)
            {
                var read = await source
                    .ReadAsync(buffer.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                try
                {
                    bytesRead = checked(bytesRead + read);
                }
                catch (OverflowException exception)
                {
                    throw StreamArchiveMetadata.ResourceLimit(
                        "resource.output_overflow",
                        "Decoded output accounting overflowed.",
                        exception);
                }

                var maximum = _entries[providerOrdinal].OriginalSize is not null
                    ? MaximumKnownEntryOutputBytes
                    : MaximumUnknownEntryOutputBytes;
                if (bytesRead > maximum)
                {
                    throw StreamArchiveMetadata.ResourceLimit(
                        _entries[providerOrdinal].OriginalSize is not null
                            ? "resource.output_limit"
                            : "resource.unknown_entry_limit",
                        "Decoded output exceeded the sequential provider limit.");
                }

                ChargeRetainedReaderWork(read);
                checksum.Write(buffer.AsSpan(0, read));
                await destination
                    .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
            }

            StreamArchiveMetadata.ValidateSequentialEntryResult(
                _entries[providerOrdinal],
                bytesRead,
                checksum.Crc);
            succeeded = true;
        }
        catch (ReaderCancelledException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "SharpCompress cancelled the sequential entry copy.",
                exception,
                cancellationToken);
        }
        catch (SharpCompressException exception)
        {
            throw StreamArchiveMetadata.CorruptEntry(exception);
        }
        catch (InvalidDataException exception)
        {
            throw StreamArchiveMetadata.CorruptEntry(exception);
        }
        finally
        {
            if (!succeeded)
            {
                await ResetReaderAsync().ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _readerGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_stateGate)
            {
                _disposed = true;
            }

            await ResetReaderUnsafeAsync().ConfigureAwait(false);
        }
        finally
        {
            _readerGate.Release();
        }
    }

    private async ValueTask<IAsyncReader> OpenReaderAtAsync(
        int providerOrdinal,
        CancellationToken cancellationToken)
    {
        if (CanContinueTo(providerOrdinal))
        {
            var retainedReader = _reader!;
            for (var ordinal = _readerOrdinal + 1; ordinal <= providerOrdinal; ordinal++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await retainedReader.MoveToNextEntryAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new ArchiveProviderException(
                        ArchiveErrorCategory.InvalidArchive,
                        "archive.entry_disappeared",
                        "The sequential archive ended before the selected entry was reached.");
                }

                StreamArchiveMetadata.ValidateSequentialEntry(_entries[ordinal]);
                SetReaderPosition(ordinal, consumed: false);
                if (ordinal < providerOrdinal)
                {
                    var drained = await DrainCurrentEntryAsync(
                        retainedReader,
                        _entries[ordinal],
                        GetRetainedReaderWorkRemaining(),
                        cancellationToken).ConfigureAwait(false);
                    ChargeRetainedReaderWork(drained);
                    SetReaderPosition(ordinal, consumed: true);
                }
            }

            return retainedReader;
        }

        await ResetReaderUnsafeAsync().ConfigureAwait(false);
        var reader = await ReaderFactory
            .OpenAsyncReader(_sourcePath, _options, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            for (var ordinal = 0; ordinal <= providerOrdinal; ordinal++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await reader.MoveToNextEntryAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new ArchiveProviderException(
                        ArchiveErrorCategory.InvalidArchive,
                        "archive.entry_disappeared",
                        "The sequential archive ended before the selected entry was reached.");
                }

                StreamArchiveMetadata.ValidateSequentialEntry(_entries[ordinal]);
                if (ordinal < providerOrdinal)
                {
                    var drained = await DrainCurrentEntryAsync(
                        reader,
                        _entries[ordinal],
                        GetRetainedReaderWorkRemaining(),
                        cancellationToken).ConfigureAwait(false);
                    ChargeRetainedReaderWork(drained);
                }
            }

            _reader = reader;
            SetReaderPosition(providerOrdinal, consumed: false);
            return reader;
        }
        catch
        {
            await reader.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private bool CanContinueTo(int providerOrdinal)
    {
        lock (_stateGate)
        {
            return !_disposed
                && !_readerNeedsReset
                && !_streamOpen
                && _reader is not null
                && providerOrdinal > _readerOrdinal
                && _readerEntryConsumed;
        }
    }

    private void SetReaderPosition(int providerOrdinal, bool consumed)
    {
        lock (_stateGate)
        {
            _readerOrdinal = providerOrdinal;
            _readerEntryConsumed = consumed;
            _readerNeedsReset = false;
        }
    }

    private void OnStreamDisposed(int providerOrdinal, bool fullyConsumed)
    {
        lock (_stateGate)
        {
            if (!_streamOpen || _streamOrdinal != providerOrdinal)
            {
                return;
            }

            _streamOpen = false;
            _streamOrdinal = -1;
            _readerEntryConsumed = fullyConsumed;
            _readerNeedsReset = !fullyConsumed;
        }
    }

    private void ThrowIfDisposed()
    {
        lock (_stateGate)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SequentialReaderArchiveHandle));
            }
        }
    }

    private async ValueTask ResetReaderAsync()
    {
        await _readerGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await ResetReaderUnsafeAsync().ConfigureAwait(false);
        }
        finally
        {
            _readerGate.Release();
        }
    }

    private async ValueTask ResetReaderUnsafeAsync()
    {
        IAsyncReader? reader;
        lock (_stateGate)
        {
            reader = _reader;
            _reader = null;
            _readerOrdinal = -1;
            _readerEntryConsumed = true;
            _readerNeedsReset = true;
            _readerDecodedWorkBytes = 0;
            _streamOpen = false;
            _streamOrdinal = -1;
        }

        if (reader is not null)
        {
            await reader.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask EnsureOuterCompressionValidatedAsync(CancellationToken cancellationToken)
    {
        if (_outerCompressionFormat is null || Volatile.Read(ref _outerCompressionValidated))
        {
            return;
        }

        await _outerValidationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_outerCompressionValidated)
            {
                return;
            }

            await using var source = new FileStream(
                _sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            try
            {
                Stream decoder = _outerCompressionFormat switch
                {
                    "gzip" => new GZipStream(source, CompressionMode.Decompress, leaveOpen: true),
                    "bzip2" => await CompressionProviderRegistry.Default
                        .CreateDecompressStreamAsync(CompressionType.BZip2, source, cancellationToken)
                        .ConfigureAwait(false),
                    "xz" => await CompressionProviderRegistry.Default
                        .CreateDecompressStreamAsync(CompressionType.Xz, source, cancellationToken)
                        .ConfigureAwait(false),
                    _ => throw new InvalidOperationException(
                        "The sequential archive requested an unknown outer compression validator."),
                };
                await using var decoded = decoder;
                var buffer = new byte[64 * 1024];
                long decodedBytes = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = await decoded
                        .ReadAsync(buffer.AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    decodedBytes = ChargeDecodedWork(decodedBytes, read);
                }
            }
            catch (ReaderCancelledException exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    "SharpCompress cancelled outer compression validation.",
                    exception,
                    cancellationToken);
            }
            catch (Exception exception) when (
                exception is SharpCompressException or InvalidDataException or EndOfStreamException)
            {
                throw StreamArchiveMetadata.CorruptEntry(exception);
            }

            Volatile.Write(ref _outerCompressionValidated, true);
        }
        finally
        {
            _outerValidationGate.Release();
        }
    }

    private static async ValueTask<long> DrainCurrentEntryAsync(
        IAsyncReader reader,
        ProviderArchiveEntry entry,
        long remainingDecodedWorkBytes,
        CancellationToken cancellationToken)
    {
        if (reader.Entry.IsDirectory)
        {
            return 0;
        }

        var entryMaximum = entry.OriginalSize is not null
            ? MaximumKnownEntryOutputBytes
            : MaximumUnknownEntryOutputBytes;
        var maximum = Math.Min(entryMaximum, Math.Max(0, remainingDecodedWorkBytes));
        await using var source = await reader
            .OpenEntryStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        var buffer = new byte[64 * 1024];
        long bytesRead = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await source
                    .ReadAsync(buffer.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    return bytesRead;
                }

                try
                {
                    bytesRead = checked(bytesRead + read);
                }
                catch (OverflowException exception)
                {
                    throw StreamArchiveMetadata.ResourceLimit(
                        "resource.output_overflow",
                        "Decoded output accounting overflowed while indexing the sequential archive.",
                        exception);
                }

                if (bytesRead > maximum)
                {
                    throw StreamArchiveMetadata.ResourceLimit(
                        bytesRead > entryMaximum
                            ? entry.OriginalSize is not null
                                ? "resource.output_limit"
                                : "resource.unknown_entry_limit"
                            : "resource.sequential_work_limit",
                        "A sequential archive entry exceeded the bounded indexing or decoded-work limit.");
                }
            }
        }
        catch
        {
            reader.Cancel();
            throw;
        }
    }

    private void ChargeRetainedReaderWork(long additionalBytes)
    {
        lock (_stateGate)
        {
            _readerDecodedWorkBytes = ChargeDecodedWork(
                _readerDecodedWorkBytes,
                additionalBytes);
        }
    }

    private long GetRetainedReaderWorkRemaining()
    {
        lock (_stateGate)
        {
            return MaximumKnownEntryOutputBytes - _readerDecodedWorkBytes;
        }
    }

    private static long ChargeDecodedWork(long currentBytes, long additionalBytes)
    {
        long next;
        try
        {
            next = checked(currentBytes + additionalBytes);
        }
        catch (OverflowException exception)
        {
            throw StreamArchiveMetadata.ResourceLimit(
                "resource.output_overflow",
                "Sequential decoded-work accounting overflowed.",
                exception);
        }

        if (next > MaximumKnownEntryOutputBytes)
        {
            throw StreamArchiveMetadata.ResourceLimit(
                "resource.sequential_work_limit",
                "Sequential archive scanning exceeded the bounded decoded-work limit.");
        }

        return next;
    }

    private void ValidateOrdinal(int providerOrdinal)
    {
        if ((uint)providerOrdinal >= (uint)_entries.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(providerOrdinal));
        }
    }
}

internal sealed class SingleCompressedStreamArchiveHandle : IArchiveHandle
{
    private readonly string _sourcePath;
    private readonly CompressionType _compressionType;
    private readonly IReadOnlyList<ProviderArchiveEntry> _entries;

    private SingleCompressedStreamArchiveHandle(
        string sourcePath,
        string format,
        CompressionType compressionType,
        ProviderArchiveEntry entry)
    {
        _sourcePath = sourcePath;
        Format = format;
        _compressionType = compressionType;
        _entries = [entry];
    }

    public string Format { get; }
    public string ProviderId => "sharpcompress/0.50.4";
    public ArchiveCapability Capabilities =>
        ArchiveCapability.List
        | ArchiveCapability.OpenEntryStream
        | ArchiveCapability.Test
        | ArchiveCapability.Extract;
    public bool IsSolid => false;
    public ArchiveVolumeSetKind VolumeSetKind => ArchiveVolumeSetKind.Single;
    public IReadOnlyList<ProviderArchiveEntry> Entries => _entries;

    public static IArchiveHandle Create(string sourcePath, string format)
    {
        var compressionType = format switch
        {
            "bzip2" => CompressionType.BZip2,
            "xz" => CompressionType.Xz,
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        var information = new FileInfo(sourcePath);
        var entry = new ProviderArchiveEntry(
            0,
            StreamArchiveMetadata.GetSyntheticEntryPath(sourcePath, format, 0),
            ArchiveEntryKind.File,
            OriginalSize: null,
            CompressedSize: information.Length,
            ModifiedTimeUtc: new DateTimeOffset(information.LastWriteTimeUtc),
            WindowsAttributes: null,
            CompressionMethod: compressionType.ToString(),
            IntegrityValue: null,
            IsEncrypted: false,
            IsSolid: false,
            LinkTarget: null);
        return new SingleCompressedStreamArchiveHandle(sourcePath, format, compressionType, entry);
    }

    public async ValueTask<Stream> OpenEntryStreamAsync(
        int providerOrdinal,
        CancellationToken cancellationToken)
    {
        ValidateOrdinal(providerOrdinal);
        var source = new FileStream(
            _sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var decoder = await CompressionProviderRegistry.Default
                .CreateDecompressStreamAsync(_compressionType, source, cancellationToken)
                .ConfigureAwait(false);
            return new OwnedReadStream(decoder, source);
        }
        catch
        {
            await source.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask CopyEntryToAsync(
        int providerOrdinal,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ValidateOrdinal(providerOrdinal);
        try
        {
            await using var source = await OpenEntryStreamAsync(providerOrdinal, cancellationToken)
                .ConfigureAwait(false);
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }
        catch (SharpCompressException exception)
        {
            throw StreamArchiveMetadata.CorruptEntry(exception);
        }
        catch (InvalidDataException exception)
        {
            throw StreamArchiveMetadata.CorruptEntry(exception);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static void ValidateOrdinal(int providerOrdinal)
    {
        if (providerOrdinal != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(providerOrdinal));
        }
    }
}

internal static class StreamArchiveMetadata
{
    public static ProviderArchiveEntry ToProviderEntry(
        IEntry entry,
        int ordinal,
        string? fallbackPath = null,
        bool checksumAvailable = false,
        bool zeroGzipMtimeMeansUnknown = false)
    {
        var rawPath = string.IsNullOrEmpty(entry.Key) ? fallbackPath : entry.Key;
        if (string.IsNullOrEmpty(rawPath))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidArchive,
                "archive.empty_entry_path",
                "The archive contains an entry with no usable path.");
        }

        var linkTarget = TryGet(() => entry.LinkTarget, fallback: null);
        var kind = entry.IsDirectory
            ? ArchiveEntryKind.Directory
            : linkTarget is not null
                ? ArchiveEntryKind.SymbolicLink
                : ArchiveEntryKind.File;
        var size = TryGet(() => entry.Size, -1L);
        var compressedSize = TryGet(() => entry.CompressedSize, -1L);
        var crc = TryGet(() => entry.Crc, -1L);
        if (size < -1 || compressedSize < -1 || crc < -1 || crc > uint.MaxValue)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidArchive,
                "archive.invalid_entry_size",
                "The sequential archive provider returned malformed entry metadata.");
        }

        if (checksumAvailable && (size < 0 || crc < 0))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidArchive,
                "archive.missing_entry_checksum",
                "The GZIP entry did not expose its required trailer checksum metadata.");
        }

        var modifiedTimeUtc = ToUtcTimestamp(TryGet(() => entry.LastModifiedTime, fallback: null));
        if (zeroGzipMtimeMeansUnknown && modifiedTimeUtc == DateTimeOffset.UnixEpoch)
        {
            // RFC 1952 assigns zero to an unavailable MTIME. Treating that
            // sentinel as a real 1970 timestamp breaks extraction to filesystems
            // such as exFAT whose timestamp range begins later.
            modifiedTimeUtc = null;
        }

        return new ProviderArchiveEntry(
            ordinal,
            rawPath,
            kind,
            size >= 0 ? size : null,
            compressedSize >= 0 ? compressedSize : null,
            modifiedTimeUtc,
            TryGet(() => entry.Attrib, fallback: null),
            TryGetCompressionMethod(entry),
            checksumAvailable && crc >= 0 ? crc.ToString("X8") : null,
            TryGet(() => entry.IsEncrypted, false),
            TryGet(() => entry.IsSolid, false),
            linkTarget);
    }

    public static void ValidateSequentialEntry(ProviderArchiveEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.OriginalSize is < 0 || entry.CompressedSize is < 0)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidArchive,
                "archive.negative_entry_size",
                "The sequential archive provider returned a negative entry size.");
        }

        if (entry.IntegrityValue is { } integrityValue
            && !uint.TryParse(
                integrityValue,
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out _))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidArchive,
                "archive.invalid_entry_checksum",
                "The sequential archive provider returned malformed checksum metadata.");
        }
    }

    public static void ValidateSequentialEntryResult(
        ProviderArchiveEntry entry,
        long actualSize,
        uint actualChecksum)
    {
        ValidateSequentialEntry(entry);
        if (entry.OriginalSize is { } expectedSize && actualSize != expectedSize)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.CorruptArchive,
                "archive.entry_size_mismatch",
                "The sequential entry produced a different number of bytes than its metadata declared.");
        }

        if (entry.IntegrityValue is not { } integrityValue)
        {
            return;
        }

        if (!uint.TryParse(
                integrityValue,
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out var expectedChecksum)
            || actualChecksum != expectedChecksum)
        {
            throw CorruptEntry(new InvalidDataException(
                "The sequential entry checksum did not match its metadata."));
        }
    }

    public static string GetSyntheticEntryPath(string sourcePath, string format, int ordinal)
    {
        var name = Path.GetFileName(sourcePath);
        var suffixes = format switch
        {
            "gzip" => new[] { ".gz", ".gzip" },
            "bzip2" => new[] { ".bz2", ".bzip2" },
            "xz" => new[] { ".xz" },
            _ => [],
        };
        foreach (var suffix in suffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^suffix.Length];
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = "content";
        }

        return ordinal == 0 ? name : $"{name}.{ordinal + 1}";
    }

    public static string NormalizeFormat(string format) => format.ToLowerInvariant() switch
    {
        "sevenzip" => "7z",
        "gzip" => "gzip",
        "bzip2" => "bzip2",
        _ => format.ToLowerInvariant(),
    };

    public static ArchiveProviderException CorruptEntry(Exception exception) =>
        new(
            ArchiveErrorCategory.CorruptArchive,
            "archive.entry_checksum_or_decode_failed",
            "The entry failed integrity validation or could not be decoded.",
            exception);

    public static ArchiveProviderException ResourceLimit(
        string code,
        string message,
        Exception? innerException = null) =>
        new(ArchiveErrorCategory.ResourceLimit, code, message, innerException);

    private static DateTimeOffset? ToUtcTimestamp(DateTime? value)
    {
        if (value is not { } timestamp)
        {
            return null;
        }

        return timestamp.Kind switch
        {
            DateTimeKind.Utc => new DateTimeOffset(timestamp),
            DateTimeKind.Local => new DateTimeOffset(timestamp).ToUniversalTime(),
            _ => new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Local)).ToUniversalTime(),
        };
    }

    private static T TryGet<T>(Func<T> getter, T fallback)
    {
        try
        {
            return getter();
        }
        catch (NotImplementedException)
        {
            return fallback;
        }
    }

    private static string? TryGetCompressionMethod(IEntry entry)
    {
        try
        {
            return entry.CompressionType.ToString();
        }
        catch (Exception exception) when (exception is NotImplementedException or InvalidFormatException)
        {
            return null;
        }
    }
}

internal sealed class OwnedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly IAsyncDisposable? _owner;
    private readonly Action? _onAbort;
    private readonly Func<Exception, Exception?>? _readExceptionMapper;
    private Action<bool>? _onDisposed;
    private int _reachedEnd;
    private int _disposed;

    public OwnedReadStream(
        Stream inner,
        IAsyncDisposable? owner,
        Action? onAbort = null,
        Action<bool>? onDisposed = null,
        Func<Exception, Exception?>? readExceptionMapper = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _owner = owner;
        _onAbort = onAbort;
        _onDisposed = onDisposed;
        _readExceptionMapper = readExceptionMapper;
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => _inner.CanWrite;
    public override long Length => _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) =>
        _inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count)
    {
        try
        {
            return TrackRead(_inner.Read(buffer, offset, count));
        }
        catch (Exception exception) when (_readExceptionMapper?.Invoke(exception) is { } mapped)
        {
            throw mapped;
        }
    }

    public override int Read(Span<byte> buffer)
    {
        try
        {
            return TrackRead(_inner.Read(buffer));
        }
        catch (Exception exception) when (_readExceptionMapper?.Invoke(exception) is { } mapped)
        {
            throw mapped;
        }
    }

    public override int ReadByte()
    {
        try
        {
            return TrackReadByte(_inner.ReadByte());
        }
        catch (Exception exception) when (_readExceptionMapper?.Invoke(exception) is { } mapped)
        {
            throw mapped;
        }
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return TrackRead(await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (_readExceptionMapper?.Invoke(exception) is { } mapped)
        {
            throw mapped;
        }
    }

    public override async Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        try
        {
            return TrackRead(
                await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (_readExceptionMapper?.Invoke(exception) is { } mapped)
        {
            throw mapped;
        }
    }
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
    public override void SetLength(long value) => _inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) =>
        _inner.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);
    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        _inner.WriteAsync(buffer, cancellationToken);
    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        _inner.WriteAsync(buffer, offset, count, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                try
                {
                    if (Volatile.Read(ref _reachedEnd) == 0)
                    {
                        _onAbort?.Invoke();
                    }
                }
                finally
                {
                    _inner.Dispose();
                }
            }
            finally
            {
                try
                {
                    _owner?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                finally
                {
                    NotifyDisposed();
                }
            }
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                try
                {
                    if (Volatile.Read(ref _reachedEnd) == 0)
                    {
                        _onAbort?.Invoke();
                    }
                }
                finally
                {
                    await _inner.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                try
                {
                    if (_owner is not null)
                    {
                        await _owner.DisposeAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    NotifyDisposed();
                }
            }
        }

        GC.SuppressFinalize(this);
    }

    private int TrackRead(int count)
    {
        if (count == 0)
        {
            Volatile.Write(ref _reachedEnd, 1);
        }

        return count;
    }

    private int TrackReadByte(int value)
    {
        if (value < 0)
        {
            Volatile.Write(ref _reachedEnd, 1);
        }

        return value;
    }

    private void NotifyDisposed()
    {
        var callback = Interlocked.Exchange(ref _onDisposed, null);
        callback?.Invoke(Volatile.Read(ref _reachedEnd) != 0);
    }
}
