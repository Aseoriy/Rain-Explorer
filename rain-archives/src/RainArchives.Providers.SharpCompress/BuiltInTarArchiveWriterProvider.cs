using System.Buffers;
using System.Formats.Tar;
using System.Security.Cryptography;
using RainArchives.Contracts;
using RainArchives.Core;

namespace RainArchives.Providers.SharpCompress;

/// <summary>
/// Managed TAR writer backed by the .NET TAR implementation.  This provider
/// deliberately exposes only streams and already-validated archive metadata;
/// it has no filesystem-path authority and does not preserve platform-specific
/// POSIX or Windows attributes.
/// </summary>
public sealed class BuiltInTarArchiveWriterProvider : IArchiveWriterProvider, IArchiveStagedOutputValidator
{
    private static readonly byte[] EmptyTarEndMarkers = new byte[1024];
    private static readonly IReadOnlySet<ArchiveCompressionPreset> SupportedPresets =
        new HashSet<ArchiveCompressionPreset>
        {
            ArchiveCompressionPreset.Store,
        };

    public string Id => "dotnet/tar/net10";
    public string Format => "tar";
    public IReadOnlySet<ArchiveCompressionPreset> CompressionPresets => SupportedPresets;

    public ValueTask<IArchiveWriterHandle> CreateAsync(
        Stream destination,
        ArchiveCompressionPreset compressionPreset,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The TAR destination stream must be writable.", nameof(destination));
        }

        if (!SupportedPresets.Contains(compressionPreset))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.UnsupportedCapability,
                "tar.compression_preset_unsupported",
                "The selected TAR compression preset is not supported by the managed writer.");
        }

        // Pax is selected for a stable Unicode-capable archive format. An
        // uncompressed TAR has no compression-level setting, so Store is the
        // only truthful preset.
        return ValueTask.FromResult<IArchiveWriterHandle>(
            new WriterHandle(
                new TarWriter(destination, TarEntryFormat.Pax, leaveOpen: true),
                destination));
    }

    public async ValueTask ValidateAsync(
        Stream archive,
        IReadOnlyList<ArchiveStagedOutputManifestEntry> expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(expected);
        if (!archive.CanRead || !archive.CanSeek)
        {
            throw new ArgumentException(
                "TAR staged-output validation requires a readable, seekable stream.",
                nameof(archive));
        }

        var originalPosition = archive.Position;
        try
        {
            ValidateExpectedManifest(expected);
            archive.Position = 0;
            await ValidateTarPayloadAsync(archive, expected, cancellationToken).ConfigureAwait(false);
        }
        catch (ArchiveProviderException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or IOException
                or NotSupportedException
                or ArgumentException)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.CorruptArchive,
                "tar.output_invalid",
                "The staged TAR output could not be reopened and validated.",
                exception);
        }
        finally
        {
            archive.Position = originalPosition;
        }
    }

    internal static async ValueTask ValidateTarPayloadAsync(
        Stream tarPayload,
        IReadOnlyList<ArchiveStagedOutputManifestEntry> expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tarPayload);
        ArgumentNullException.ThrowIfNull(expected);
        if (!tarPayload.CanRead)
        {
            throw new ArgumentException("The TAR payload stream must be readable.", nameof(tarPayload));
        }

        var tracked = new TarTailTrackingReadStream(tarPayload);
        await using var reader = new TarReader(tracked, leaveOpen: true);
        for (var index = 0; index < expected.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var actual = await reader
                .GetNextEntryAsync(copyData: false, cancellationToken)
                .ConfigureAwait(false);
            if (actual is null || !Matches(actual, expected[index]))
            {
                throw ManifestMismatch();
            }

            await ConsumeEntryDataAsync(actual, expected[index], cancellationToken)
                .ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw ManifestMismatch();
        }

        var drainBuffer = ArrayPool<byte>.Shared.Rent(80 * 1024);
        try
        {
            while (await tracked
                .ReadAsync(drainBuffer.AsMemory(0, drainBuffer.Length), cancellationToken)
                .ConfigureAwait(false) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(drainBuffer, clearArray: true);
        }

        if (!tracked.HasCanonicalEndMarkers)
        {
            throw ManifestMismatch();
        }
    }

    private static async ValueTask ConsumeEntryDataAsync(
        TarEntry actual,
        ArchiveStagedOutputManifestEntry expected,
        CancellationToken cancellationToken)
    {
        if (expected.Kind != ArchiveEntryKind.File)
        {
            return;
        }

        var data = actual.DataStream;
        if (data is null)
        {
            if (expected.Length != 0)
            {
                throw ManifestMismatch();
            }

            ValidateContentDigest(SHA256.HashData(ReadOnlySpan<byte>.Empty), expected.ContentSha256!);
            return;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(80 * 1024);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            long totalRead = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await data
                    .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                totalRead = checked(totalRead + read);
                if (totalRead > expected.Length)
                {
                    throw ManifestMismatch();
                }

                digest.AppendData(buffer.AsSpan(0, read));
            }

            if (totalRead != expected.Length)
            {
                throw ManifestMismatch();
            }

            ValidateContentDigest(digest.GetHashAndReset(), expected.ContentSha256!);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static void ValidateContentDigest(byte[] actualDigest, string expectedSha256)
    {
        var expectedDigest = Convert.FromHexString(expectedSha256);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(actualDigest, expectedDigest))
            {
                throw ManifestMismatch();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actualDigest);
            CryptographicOperations.ZeroMemory(expectedDigest);
        }
    }

    internal static void ValidateExpectedManifest(
        IReadOnlyList<ArchiveStagedOutputManifestEntry> expected)
    {
        foreach (var entry in expected)
        {
            if (entry is null
                || entry.Kind is not ArchiveEntryKind.File and not ArchiveEntryKind.Directory
                || (entry.Kind == ArchiveEntryKind.File
                    && (entry.Length is null or < 0 || !IsSha256(entry.ContentSha256)))
                || (entry.Kind == ArchiveEntryKind.Directory
                    && (entry.Length is not null || entry.ContentSha256 is not null)))
            {
                throw InvalidManifest();
            }

            try
            {
                _ = NormalizeArchivePath(
                    entry.ArchivePath,
                    directory: entry.Kind == ArchiveEntryKind.Directory);
                _ = ValidateTimestamp(entry.LastWriteTime);
            }
            catch (Exception exception) when (
                exception is ArgumentException or ArchiveProviderException)
            {
                throw InvalidManifest(exception);
            }
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character =>
            character is >= '0' and <= '9'
                or >= 'a' and <= 'f'
                or >= 'A' and <= 'F');

    private static bool Matches(
        TarEntry actual,
        ArchiveStagedOutputManifestEntry expected)
    {
        var expectedPath = NormalizeArchivePath(expected.ArchivePath, expected.Kind == ArchiveEntryKind.Directory);
        var actualKind = actual.EntryType == TarEntryType.Directory
            ? ArchiveEntryKind.Directory
            : actual.EntryType == TarEntryType.RegularFile
                ? ArchiveEntryKind.File
                : ArchiveEntryKind.Unknown;
        if (!string.Equals(actual.Name, expectedPath, StringComparison.Ordinal)
            || actualKind != expected.Kind
            || (expected.Kind == ArchiveEntryKind.File && actual.Length != expected.Length)
            || (expected.Kind == ArchiveEntryKind.Directory && actual.Length != 0)
            || ValidateTimestamp(actual.ModificationTime) != ValidateTimestamp(expected.LastWriteTime))
        {
            return false;
        }

        return true;
    }

    private static string NormalizeArchivePath(string path, bool directory)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.IndexOf('\0') >= 0
            || path.StartsWith("/", StringComparison.Ordinal)
            || path.IndexOf('\\') >= 0)
        {
            throw new ArgumentException("TAR archive paths must be relative and use '/'.", nameof(path));
        }

        var normalized = directory ? path.TrimEnd('/') : path;
        if (normalized.Length == 0
            || normalized.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new ArgumentException("TAR archive paths contain an invalid segment.", nameof(path));
        }

        return directory ? $"{normalized}/" : normalized;
    }

    private static DateTimeOffset ValidateTimestamp(DateTimeOffset timestamp)
    {
        var utc = timestamp.ToUniversalTime();
        if (utc < DateTimeOffset.UnixEpoch)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "tar.timestamp_before_unix_epoch",
                "TAR creation does not support timestamps before the Unix epoch.");
        }

        return utc;
    }

    private static ArchiveProviderException SourceChanged() =>
        new(
            ArchiveErrorCategory.SourceChanged,
            "source.changed_during_creation",
            "A source stream changed while it was being added to the TAR archive.");

    private static ArchiveProviderException ManifestMismatch() =>
        new(
            ArchiveErrorCategory.ProviderFailure,
            "tar.output_manifest_mismatch",
            "The staged TAR output did not match the exact validated output manifest.");

    private static ArchiveProviderException InvalidManifest(Exception? inner = null) =>
        new(
            ArchiveErrorCategory.ProviderFailure,
            "tar.output_manifest_invalid",
            "TAR staged-output validation requires only files and directories, with an exact SHA-256 digest for every file.",
            inner);

    private sealed class WriterHandle : IArchiveWriterHandle
    {
        private readonly TarWriter _writer;
        private readonly Stream _destination;
        private bool _entryOpen;
        private int _entryCount;
        private int _finalizationStarted;

        public WriterHandle(TarWriter writer, Stream destination)
        {
            _writer = writer;
            _destination = destination;
        }

        public async ValueTask AddDirectoryAsync(
            ArchiveWriteDirectory entry,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(entry);
            EnsureActive();
            cancellationToken.ThrowIfCancellationRequested();
            ValidatePlatformMetadata(entry.WindowsAttributes);
            var path = NormalizeArchivePath(entry.ArchivePath, directory: true);
            var tarEntry = new PaxTarEntry(TarEntryType.Directory, path)
            {
                ModificationTime = ValidateTimestamp(entry.LastWriteTime),
            };
            await _writer.WriteEntryAsync(tarEntry, cancellationToken).ConfigureAwait(false);
            _entryCount++;
        }

        public async ValueTask AddFileAsync(
            ArchiveWriteEntry entry,
            Stream source,
            Func<long, ValueTask> reportEntryBytes,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(reportEntryBytes);
            EnsureActive();
            cancellationToken.ThrowIfCancellationRequested();
            if (!source.CanRead)
            {
                throw new ArgumentException("The TAR entry source must be readable.", nameof(source));
            }

            ValidateFileMetadata(entry);
            ValidatePlatformMetadata(entry.WindowsAttributes);
            _entryOpen = true;
            var readSource = new ExactLengthReadStream(source, entry.Length, reportEntryBytes);
            var tarEntry = new PaxTarEntry(
                TarEntryType.RegularFile,
                NormalizeArchivePath(entry.ArchivePath, directory: false))
            {
                ModificationTime = ValidateTimestamp(entry.LastWriteTime),
                DataStream = readSource,
            };
            try
            {
                await _writer.WriteEntryAsync(tarEntry, cancellationToken).ConfigureAwait(false);
                await readSource.ValidateCompleteAsync(cancellationToken).ConfigureAwait(false);
                _entryCount++;
            }
            finally
            {
                tarEntry.DataStream = null;
                _entryOpen = false;
            }
        }

        public async ValueTask CompleteAsync(CancellationToken cancellationToken)
        {
            EnsureActive();
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref _finalizationStarted, 1) != 0)
            {
                throw new ObjectDisposedException(nameof(WriterHandle));
            }

            await FinalizeAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _finalizationStarted, 1) != 0)
            {
                return;
            }

            await FinalizeAsync(CancellationToken.None).ConfigureAwait(false);
        }

        private async ValueTask FinalizeAsync(CancellationToken cancellationToken)
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
            if (_entryCount == 0)
            {
                await _destination
                    .WriteAsync(EmptyTarEndMarkers, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        private void EnsureActive()
        {
            if (Volatile.Read(ref _finalizationStarted) != 0)
            {
                throw new ObjectDisposedException(nameof(WriterHandle));
            }

            if (_entryOpen)
            {
                throw new InvalidOperationException("TAR entries must be written sequentially.");
            }
        }

        private static void ValidateFileMetadata(ArchiveWriteEntry entry)
        {
            if (entry.Length < 0)
            {
                throw new ArgumentException("TAR entry length cannot be negative.", nameof(entry));
            }

            _ = NormalizeArchivePath(entry.ArchivePath, directory: false);
            _ = ValidateTimestamp(entry.LastWriteTime);
        }

        private static void ValidatePlatformMetadata(int? windowsAttributes)
        {
            if (windowsAttributes is not null)
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.UnsupportedCapability,
                    "tar.platform_metadata_unsupported",
                    "TAR creation does not preserve Windows attributes or POSIX ownership and mode metadata.");
            }
        }
    }

    private sealed class ExactLengthReadStream : Stream
    {
        private readonly Stream _source;
        private readonly long _expectedLength;
        private readonly Func<long, ValueTask> _report;
        private readonly long _sourceStart;
        private long _read;

        public ExactLengthReadStream(Stream source, long expectedLength, Func<long, ValueTask> report)
        {
            _source = source;
            _expectedLength = expectedLength;
            _report = report;
            _sourceStart = source.CanSeek ? source.Position : 0;
        }

        public async ValueTask ValidateCompleteAsync(CancellationToken cancellationToken)
        {
            if (_read != _expectedLength)
            {
                throw SourceChanged();
            }

            var probe = ArrayPool<byte>.Shared.Rent(1);
            try
            {
                if (await _source.ReadAsync(probe.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0)
                {
                    throw SourceChanged();
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(probe, clearArray: true);
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateArguments(buffer, offset, count);
            return ReadCore(buffer.AsSpan(offset, count));
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, _expectedLength - _read);
            if (count == 0)
            {
                return 0;
            }

            var read = await _source.ReadAsync(buffer[..count], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return 0;
            }

            _read = checked(_read + read);
            await _report(_read).ConfigureAwait(false);
            return read;
        }

        private int ReadCore(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, _expectedLength - _read);
            if (count == 0)
            {
                return 0;
            }

            var read = _source.Read(buffer[..count]);
            if (read == 0)
            {
                return 0;
            }

            _read = checked(_read + read);
            _report(_read).AsTask().GetAwaiter().GetResult();
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => _source.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _expectedLength;
        public override long Position
        {
            get => _read;
            set => _ = Seek(value, SeekOrigin.Begin);
        }
        public override void Flush() => throw new NotSupportedException();
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            Task.FromException(new NotSupportedException());
        public override long Seek(long offset, SeekOrigin origin)
        {
            if (!CanSeek)
            {
                throw new NotSupportedException();
            }

            long position;
            try
            {
                position = origin switch
                {
                    SeekOrigin.Begin => offset,
                    SeekOrigin.Current => checked(_read + offset),
                    SeekOrigin.End => checked(_expectedLength + offset),
                    _ => throw new ArgumentOutOfRangeException(nameof(origin)),
                };
            }
            catch (OverflowException exception)
            {
                throw new IOException("The TAR source seek overflowed its bounded entry length.", exception);
            }

            if (position < 0 || position > _expectedLength)
            {
                throw new IOException("The TAR source seek exceeded its bounded entry length.");
            }

            _source.Position = checked(_sourceStart + position);
            _read = position;
            return position;
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { }
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static void ValidateArguments(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (buffer.Length - offset < count)
            {
                throw new ArgumentException("The buffer range is invalid.");
            }
        }
    }

    private sealed class TarTailTrackingReadStream : Stream
    {
        private const int TarBlockSize = 512;
        private const int EndMarkerSize = TarBlockSize * 2;
        private readonly Stream _source;
        private readonly byte[] _tail = new byte[EndMarkerSize];
        private long _totalRead;
        private int _tailLength;
        private int _blockOffset;
        private bool _currentBlockHasNonZero;
        private int _consecutiveZeroBlocks;
        private bool _nonZeroAfterEndMarkers;

        public TarTailTrackingReadStream(Stream source) => _source = source;

        public bool HasCanonicalEndMarkers =>
            _totalRead >= EndMarkerSize
            && _totalRead % TarBlockSize == 0
            && _tail.AsSpan().ContainsAnyExcept((byte)0) is false
            && _consecutiveZeroBlocks >= 2
            && !_nonZeroAfterEndMarkers;

        public override int Read(byte[] buffer, int offset, int count)
        {
            ValidateArguments(buffer, offset, count);
            var read = _source.Read(buffer, offset, count);
            Track(buffer.AsSpan(offset, read));
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = _source.Read(buffer);
            Track(buffer[..read]);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await _source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Track(buffer.Span[..read]);
            return read;
        }

        private void Track(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty)
            {
                return;
            }

            TrackBlocks(bytes);

            _totalRead = checked(_totalRead + bytes.Length);
            if (bytes.Length >= EndMarkerSize)
            {
                bytes[^EndMarkerSize..].CopyTo(_tail);
                _tailLength = EndMarkerSize;
                return;
            }

            var keep = Math.Min(_tailLength, EndMarkerSize - bytes.Length);
            if (keep > 0)
            {
                _tail.AsSpan(_tailLength - keep, keep).CopyTo(_tail);
            }

            bytes.CopyTo(_tail.AsSpan(keep));
            _tailLength = keep + bytes.Length;
        }

        private void TrackBlocks(ReadOnlySpan<byte> bytes)
        {
            while (!bytes.IsEmpty)
            {
                var take = Math.Min(TarBlockSize - _blockOffset, bytes.Length);
                if (bytes[..take].ContainsAnyExcept((byte)0))
                {
                    _currentBlockHasNonZero = true;
                }

                _blockOffset += take;
                bytes = bytes[take..];
                if (_blockOffset != TarBlockSize)
                {
                    continue;
                }

                if (_currentBlockHasNonZero)
                {
                    if (_consecutiveZeroBlocks >= 2)
                    {
                        _nonZeroAfterEndMarkers = true;
                    }

                    _consecutiveZeroBlocks = 0;
                }
                else
                {
                    _consecutiveZeroBlocks++;
                }

                _blockOffset = 0;
                _currentBlockHasNonZero = false;
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _totalRead;
            set => throw new NotSupportedException();
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { }
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static void ValidateArguments(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (buffer.Length - offset < count)
            {
                throw new ArgumentException("The buffer range is invalid.");
            }
        }
    }

}
