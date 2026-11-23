using System.Buffers;
using System.Security.Cryptography;
using RainArchives.Contracts;
using RainArchives.Core;
using RainArchives.Providers.SharpCompress;
using SharpSevenZip;
using SharpSevenZip.Exceptions;
using SevenZipLevel = SharpSevenZip.CompressionLevel;

namespace RainArchives.Providers.SevenZip;

// Protocol 1.25 writers backed by the bundled official 7-Zip engine (see SevenZipNativeLibrary).
// Like every Rain writer, each one re-opens its staged output and checks it against the exact
// expected manifest (paths, kinds, lengths, SHA-256) before the Worker may publish it.

/// <summary>7z (LZMA2, multithreaded). Entries are materialized into a private temporary workspace
/// laid out exactly like the archive, then 7-Zip compresses that tree in one solid pass.</summary>
public sealed class SevenZipArchiveWriterProvider : IArchiveWriterProvider, IArchiveStagedOutputValidator
{
    public string Id => $"7zip/7z/{SevenZipNativeLibrary.Version}";
    public string Format => "7z";
    public IReadOnlySet<ArchiveCompressionPreset> CompressionPresets => SevenZipWriterSupport.AllPresets;

    public ValueTask<IArchiveWriterHandle> CreateAsync(
        Stream destination,
        ArchiveCompressionPreset compressionPreset,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();
        SevenZipNativeLibrary.EnsureInitialized();
        SevenZipWriterSupport.ValidatePreset(Format, compressionPreset);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The 7z destination stream must be writable.", nameof(destination));
        }

        return ValueTask.FromResult<IArchiveWriterHandle>(new WorkspaceHandle(destination, compressionPreset));
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
            throw new ArgumentException("7z staged-output validation requires a readable, seekable stream.", nameof(archive));
        }

        SevenZipNativeLibrary.EnsureInitialized();
        long originalPosition = archive.Position;
        try
        {
            // 1. 7-Zip's own integrity test (CRCs of every packed stream and the headers).
            archive.Position = 0;
            bool nativeOk = await Task.Run(() =>
            {
                using var extractor = new SharpSevenZipExtractor(archive, true);
                return extractor.Check();
            }, cancellationToken).ConfigureAwait(false);
            if (!nativeOk) throw SevenZipWriterSupport.ManifestMismatch(Format);

            // 2. An independent managed reader must see exactly the expected entries and bytes.
            archive.Position = 0;
            await SevenZipWriterSupport.ValidateSevenZipManifestAsync(archive, expected, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArchiveProviderException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is SharpSevenZipException
                                              or InvalidDataException
                                              or IOException
                                              or NotSupportedException
                                              or ArgumentException
                                              or InvalidOperationException
                                              or global::SharpCompress.Common.SharpCompressException)
        {
            throw SevenZipWriterSupport.InvalidOutput(Format, exception);
        }
        finally
        {
            archive.Position = originalPosition;
        }
    }

    private sealed class WorkspaceHandle : IArchiveWriterHandle
    {
        private readonly Stream _destination;
        private readonly ArchiveCompressionPreset _preset;
        private readonly SevenZipWorkspace _workspace = new();
        private int _finalizationStarted;

        public WorkspaceHandle(Stream destination, ArchiveCompressionPreset preset)
        {
            _destination = destination;
            _preset = preset;
        }

        public ValueTask AddDirectoryAsync(ArchiveWriteDirectory entry, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(entry);
            EnsureActive();
            cancellationToken.ThrowIfCancellationRequested();
            _workspace.AddDirectory(entry);
            return ValueTask.CompletedTask;
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
            await _workspace.AddFileAsync(entry, source, reportEntryBytes, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask CompleteAsync(CancellationToken cancellationToken)
        {
            EnsureActive();
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref _finalizationStarted, 1) != 0)
            {
                throw new ObjectDisposedException(nameof(WorkspaceHandle));
            }

            try
            {
                if (_workspace.FileCount == 0)
                {
                    // Only empty folders (e.g. compressing an empty folder). 7-Zip's folder mode refuses an
                    // archive without files, and there is no data to compress, so the managed 7z writer
                    // records the folders. The same 7-Zip check + manifest read-back validates it.
                    await SevenZipWriterSupport.WriteFoldersOnlyAsync(_workspace.Directories, _destination, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                _workspace.ApplyDirectoryTimestamps();
                var compressor = SevenZipWriterSupport.CreateCompressor(OutArchiveFormat.SevenZip, _preset);
                compressor.IncludeEmptyDirectories = true;
                compressor.PreserveDirectoryRoot = false;
                compressor.DirectoryStructure = true;
                // 7-Zip can only stop between files; a cancel during one huge file lands when it ends.
                compressor.FileCompressionStarted += (_, args) =>
                {
                    if (cancellationToken.IsCancellationRequested) args.Cancel = true;
                };
                await SevenZipWriterSupport.EncodeThroughTempAsync(
                    "7z",
                    temp => compressor.CompressDirectory(_workspace.Root, temp, null!, "*", true),
                    _destination,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _workspace.Dispose();
            }
        }

        public ValueTask DisposeAsync()
        {
            // An unfinished archive is abandoned: the staged output is discarded by the caller.
            Interlocked.Exchange(ref _finalizationStarted, 1);
            _workspace.Dispose();
            return ValueTask.CompletedTask;
        }

        private void EnsureActive()
        {
            if (Volatile.Read(ref _finalizationStarted) != 0)
            {
                throw new ObjectDisposedException(nameof(WorkspaceHandle));
            }
        }
    }
}

/// <summary>Raw XZ of exactly one file, streamed straight into 7-Zip's LZMA2 encoder.</summary>
public sealed class SevenZipXzArchiveWriterProvider : IArchiveWriterProvider, IArchiveStagedOutputValidator
{
    public string Id => $"7zip/xz/{SevenZipNativeLibrary.Version}";
    public string Format => "xz";
    public IReadOnlySet<ArchiveCompressionPreset> CompressionPresets => SevenZipWriterSupport.AllPresets;

    public ValueTask<IArchiveWriterHandle> CreateAsync(
        Stream destination,
        ArchiveCompressionPreset compressionPreset,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();
        SevenZipNativeLibrary.EnsureInitialized();
        SevenZipWriterSupport.ValidatePreset(Format, compressionPreset);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The xz destination stream must be writable.", nameof(destination));
        }

        return ValueTask.FromResult<IArchiveWriterHandle>(new RawHandle(destination, compressionPreset));
    }

    public async ValueTask ValidateAsync(
        Stream archive,
        IReadOnlyList<ArchiveStagedOutputManifestEntry> expected,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(expected);
        if (expected.Count != 1 || expected[0] is not { Kind: ArchiveEntryKind.File, Length: >= 0 } file
            || !SevenZipWriterSupport.IsSha256(file.ContentSha256))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ProviderFailure,
                "raw.output_manifest_invalid",
                "Raw staged-output validation requires exactly one regular file with an exact SHA-256 digest.");
        }

        long originalPosition = archive.Position;
        try
        {
            archive.Position = 0;
            using var decoded = new global::SharpCompress.Compressors.Xz.XZStream(archive);
            var (length, digest) = await SevenZipWriterSupport.HashToEndAsync(decoded, file.Length!.Value, Format, cancellationToken)
                .ConfigureAwait(false);
            if (length != file.Length) throw SevenZipWriterSupport.ManifestMismatch(Format);
            SevenZipWriterSupport.ValidateDigest(digest, file.ContentSha256!, Format);
        }
        catch (ArchiveProviderException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException
                                              or IOException
                                              or NotSupportedException
                                              or ArgumentException
                                              or InvalidOperationException
                                              or global::SharpCompress.Common.SharpCompressException)
        {
            throw SevenZipWriterSupport.InvalidOutput(Format, exception);
        }
        finally
        {
            archive.Position = originalPosition;
        }
    }

    private sealed class RawHandle : IArchiveWriterHandle
    {
        private readonly Stream _destination;
        private readonly ArchiveCompressionPreset _preset;
        private bool _fileWritten;
        private int _finalizationStarted;

        public RawHandle(Stream destination, ArchiveCompressionPreset preset)
        {
            _destination = destination;
            _preset = preset;
        }

        public ValueTask AddDirectoryAsync(ArchiveWriteDirectory entry, CancellationToken cancellationToken)
        {
            EnsureActive();
            throw new ArchiveProviderException(
                ArchiveErrorCategory.UnsupportedCapability,
                "xz.directory_unsupported",
                "Raw xz creation accepts exactly one regular file and no directories.");
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
            if (_fileWritten)
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.UnsupportedCapability,
                    "xz.multiple_files_unsupported",
                    "Raw xz creation accepts exactly one regular file.");
            }

            if (string.IsNullOrWhiteSpace(entry.ArchivePath) || entry.Length < 0
                || entry.WindowsAttributes is not null || !source.CanRead)
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.UnsupportedCapability,
                    "xz.entry_metadata_unsupported",
                    "Raw xz creation accepts one validated logical regular-file path and does not preserve platform metadata.");
            }

            // 7-Zip's single-stream encoder needs a seekable input, so take an exact-length private copy first.
            await using var copy = SevenZipWriterSupport.CreatePrivateTempFile("RainArchives.xz-source.");
            await SevenZipWriterSupport.CopyExactAsync(source, copy, entry.Length, reportEntryBytes, cancellationToken)
                .ConfigureAwait(false);
            await copy.FlushAsync(cancellationToken).ConfigureAwait(false);
            copy.Position = 0;
            var compressor = SevenZipWriterSupport.CreateCompressor(OutArchiveFormat.XZ, _preset);
            await SevenZipWriterSupport.EncodeThroughTempAsync(
                "xz",
                temp => compressor.CompressStream(copy, temp, null!),
                _destination,
                cancellationToken).ConfigureAwait(false);
            _fileWritten = true;
        }

        public async ValueTask CompleteAsync(CancellationToken cancellationToken)
        {
            EnsureActive();
            if (!_fileWritten)
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.InvalidRequest,
                    "xz.file_required",
                    "Raw xz creation requires exactly one regular file.");
            }

            if (Interlocked.Exchange(ref _finalizationStarted, 1) != 0)
            {
                throw new ObjectDisposedException(nameof(RawHandle));
            }

            await _destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _finalizationStarted, 1);
            return ValueTask.CompletedTask;
        }

        private void EnsureActive()
        {
            if (Volatile.Read(ref _finalizationStarted) != 0)
            {
                throw new ObjectDisposedException(nameof(RawHandle));
            }
        }
    }
}

/// <summary>TAR.XZ: Rain's own TAR writer fills a private temporary file, which 7-Zip then
/// compresses to XZ. Validation decodes the XZ and hands the TAR to the TAR validator.</summary>
public sealed class SevenZipTarXzArchiveWriterProvider : IArchiveWriterProvider, IArchiveStagedOutputValidator
{
    public string Id => $"7zip/tar.xz/{SevenZipNativeLibrary.Version}";
    public string Format => "tar.xz";
    public IReadOnlySet<ArchiveCompressionPreset> CompressionPresets => SevenZipWriterSupport.AllPresets;

    public async ValueTask<IArchiveWriterHandle> CreateAsync(
        Stream destination,
        ArchiveCompressionPreset compressionPreset,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();
        SevenZipNativeLibrary.EnsureInitialized();
        SevenZipWriterSupport.ValidatePreset(Format, compressionPreset);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The tar.xz destination stream must be writable.", nameof(destination));
        }

        var tarFile = SevenZipWriterSupport.CreatePrivateTempFile("RainArchives.tarxz.");
        try
        {
            var tar = await new BuiltInTarArchiveWriterProvider()
                .CreateAsync(tarFile, ArchiveCompressionPreset.Store, cancellationToken)
                .ConfigureAwait(false);
            return new TarXzHandle(destination, compressionPreset, tarFile, tar);
        }
        catch
        {
            await tarFile.DisposeAsync().ConfigureAwait(false);
            throw;
        }
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
            throw new ArgumentException("tar.xz staged-output validation requires a readable, seekable stream.", nameof(archive));
        }

        long originalPosition = archive.Position;
        try
        {
            archive.Position = 0;
            await using var tar = SevenZipWriterSupport.CreatePrivateTempFile("RainArchives.tarxz-check.");
            using (var decoded = new global::SharpCompress.Compressors.Xz.XZStream(archive))
            {
                await decoded.CopyToAsync(tar, 80 * 1024, cancellationToken).ConfigureAwait(false);
            }

            tar.Position = 0;
            await new BuiltInTarArchiveWriterProvider().ValidateAsync(tar, expected, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArchiveProviderException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException
                                              or IOException
                                              or NotSupportedException
                                              or ArgumentException
                                              or InvalidOperationException
                                              or global::SharpCompress.Common.SharpCompressException)
        {
            throw SevenZipWriterSupport.InvalidOutput(Format, exception);
        }
        finally
        {
            archive.Position = originalPosition;
        }
    }

    private sealed class TarXzHandle : IArchiveWriterHandle
    {
        private readonly Stream _destination;
        private readonly ArchiveCompressionPreset _preset;
        private readonly FileStream _tarFile;
        private readonly IArchiveWriterHandle _tar;
        private int _finalizationStarted;

        public TarXzHandle(Stream destination, ArchiveCompressionPreset preset, FileStream tarFile, IArchiveWriterHandle tar)
        {
            _destination = destination;
            _preset = preset;
            _tarFile = tarFile;
            _tar = tar;
        }

        public ValueTask AddDirectoryAsync(ArchiveWriteDirectory entry, CancellationToken cancellationToken)
        {
            EnsureActive();
            return _tar.AddDirectoryAsync(entry, cancellationToken);
        }

        public ValueTask AddFileAsync(
            ArchiveWriteEntry entry,
            Stream source,
            Func<long, ValueTask> reportEntryBytes,
            CancellationToken cancellationToken)
        {
            EnsureActive();
            return _tar.AddFileAsync(entry, source, reportEntryBytes, cancellationToken);
        }

        public async ValueTask CompleteAsync(CancellationToken cancellationToken)
        {
            EnsureActive();
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref _finalizationStarted, 1) != 0)
            {
                throw new ObjectDisposedException(nameof(TarXzHandle));
            }

            try
            {
                await _tar.CompleteAsync(cancellationToken).ConfigureAwait(false);
                await _tarFile.FlushAsync(cancellationToken).ConfigureAwait(false);
                _tarFile.Position = 0;
                var compressor = SevenZipWriterSupport.CreateCompressor(OutArchiveFormat.XZ, _preset);
                await SevenZipWriterSupport.EncodeThroughTempAsync(
                    "tar.xz",
                    temp => compressor.CompressStream(_tarFile, temp, null!),
                    _destination,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await _tarFile.DisposeAsync().ConfigureAwait(false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _finalizationStarted, 1) != 0) return;
            try { await _tar.DisposeAsync().ConfigureAwait(false); }
            finally { await _tarFile.DisposeAsync().ConfigureAwait(false); }
        }

        private void EnsureActive()
        {
            if (Volatile.Read(ref _finalizationStarted) != 0)
            {
                throw new ObjectDisposedException(nameof(TarXzHandle));
            }
        }
    }
}

internal static class SevenZipWriterSupport
{
    internal static readonly IReadOnlySet<ArchiveCompressionPreset> AllPresets =
        new HashSet<ArchiveCompressionPreset>(Enum.GetValues<ArchiveCompressionPreset>());

    internal static void ValidatePreset(string format, ArchiveCompressionPreset preset)
    {
        if (!AllPresets.Contains(preset))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.UnsupportedCapability,
                $"{format}.compression_preset_unsupported",
                $"The selected {format} compression preset is not supported by the 7-Zip writer.");
        }
    }

    internal static SharpSevenZipCompressor CreateCompressor(OutArchiveFormat format, ArchiveCompressionPreset preset)
    {
        var compressor = new SharpSevenZipCompressor
        {
            ArchiveFormat = format,
            CompressionMethod = CompressionMethod.Lzma2,
            CompressionLevel = preset switch
            {
                ArchiveCompressionPreset.Store => SevenZipLevel.None,
                ArchiveCompressionPreset.Fast => SevenZipLevel.Fast,
                ArchiveCompressionPreset.Balanced => SevenZipLevel.Normal,
                ArchiveCompressionPreset.Maximum => SevenZipLevel.Ultra,
                _ => throw new ArgumentOutOfRangeException(nameof(preset)),
            },
            EventSynchronization = EventSynchronizationStrategy.AlwaysSynchronous,
        };
        compressor.CustomParameters["mt"] = "on";
        return compressor;
    }

    /// <summary>Runs a blocking 7-Zip call off the caller's thread and maps its failures.</summary>
    internal static async Task RunEncoderAsync(
        string format,
        Action encode,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Run(encode, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SharpSevenZipException
                                              or IOException
                                              or InvalidOperationException
                                              or ArgumentException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ProviderFailure,
                $"{format}.encoder_failed",
                $"The bundled 7-Zip engine could not write the {format} archive.",
                exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Copies exactly <paramref name="expectedLength"/> bytes and fails if the source is
    /// shorter or longer (it changed while being read).</summary>
    internal static async ValueTask CopyExactAsync(
        Stream source,
        Stream target,
        long expectedLength,
        Func<long, ValueTask> reportBytes,
        CancellationToken cancellationToken)
    {
        if (expectedLength < 0 || !source.CanRead)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "creation.entry_invalid",
                "Archive creation requires a readable source with a known length.");
        }

        var buffer = ArrayPool<byte>.Shared.Rent(80 * 1024);
        try
        {
            long copied = 0;
            while (copied < expectedLength)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int wanted = (int)Math.Min(buffer.Length, expectedLength - copied);
                int read = await source.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
                if (read == 0) throw SourceChanged();
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                copied += read;
                await reportBytes(copied).ConfigureAwait(false);
            }

            if (await source.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0)
            {
                throw SourceChanged();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    /// <summary>
    /// 7-Zip seeks while it writes, but the Worker hands writers a forward-only, size-guarded
    /// stream. Encode into a private temporary file first, then copy the finished bytes through
    /// the guarded stream so its output accounting stays exact.
    /// </summary>
    internal static async Task EncodeThroughTempAsync(
        string format,
        Action<Stream> encode,
        Stream destination,
        CancellationToken cancellationToken)
    {
        await using var temp = CreatePrivateTempFile($"RainArchives.{format}.");
        await RunEncoderAsync(format, () => encode(temp), cancellationToken).ConfigureAwait(false);
        await temp.FlushAsync(cancellationToken).ConfigureAwait(false);
        temp.Position = 0;
        await temp.CopyToAsync(destination, 80 * 1024, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A 7z that holds only folder entries and no data, laid out the way 7-Zip itself records empty
    /// folders (7zFormat.txt): signature header, then one plain header with FilesInfo = all entries
    /// are empty streams (and not empty files, i.e. directories), names, modification times and the
    /// directory attribute. No packed streams exist, so there is nothing to compress.
    /// </summary>
    internal static async Task WriteFoldersOnlyAsync(
        IReadOnlyList<ArchiveWriteDirectory> directories,
        Stream destination,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (directories.Count == 0)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "7z.entries_required",
                "A 7z archive needs at least one file or folder.");
        }

        int count = directories.Count;
        var properties = new MemoryStream();

        // kEmptyStream: every entry has no data.
        var emptyBits = new byte[(count + 7) / 8];
        for (int index = 0; index < count; index++) emptyBits[index / 8] |= (byte)(0x80 >> (index % 8));
        WriteProperty(properties, 0x0E, emptyBits);

        // kName: not external, UTF-16LE names each ending in a 0 character.
        var names = new MemoryStream();
        names.WriteByte(0);
        foreach (var directory in directories)
        {
            byte[] name = System.Text.Encoding.Unicode.GetBytes(Normalize(directory.ArchivePath).Replace('/', '\\'));
            names.Write(name);
            names.Write([0, 0]);
        }
        WriteProperty(properties, 0x11, names.ToArray());

        // kMTime: all defined, not external, FILETIME per entry.
        var times = new MemoryStream();
        times.WriteByte(1);
        times.WriteByte(0);
        foreach (var directory in directories)
            times.Write(BitConverter.GetBytes(directory.LastWriteTime.UtcDateTime.ToFileTimeUtc()));
        WriteProperty(properties, 0x14, times.ToArray());

        // kWinAttributes: all defined, not external, FILE_ATTRIBUTE_DIRECTORY.
        var attributes = new MemoryStream();
        attributes.WriteByte(1);
        attributes.WriteByte(0);
        for (int index = 0; index < count; index++) attributes.Write(BitConverter.GetBytes(0x10u));
        WriteProperty(properties, 0x15, attributes.ToArray());

        var header = new MemoryStream();
        header.WriteByte(0x01);                // kHeader
        header.WriteByte(0x05);                // kFilesInfo
        WriteNumber(header, (ulong)count);
        properties.Position = 0;
        properties.CopyTo(header);
        header.WriteByte(0x00);                // kEnd (FilesInfo)
        header.WriteByte(0x00);                // kEnd (Header)
        byte[] headerBytes = header.ToArray();

        var startHeader = new byte[20];
        BitConverter.GetBytes(0UL).CopyTo(startHeader, 0);                          // NextHeaderOffset
        BitConverter.GetBytes((ulong)headerBytes.Length).CopyTo(startHeader, 8);    // NextHeaderSize
        BitConverter.GetBytes(Crc32(headerBytes)).CopyTo(startHeader, 16);          // NextHeaderCRC

        var archive = new MemoryStream();
        archive.Write([0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04]);            // signature, version 0.4
        archive.Write(BitConverter.GetBytes(Crc32(startHeader)));                    // StartHeaderCRC
        archive.Write(startHeader);
        archive.Write(headerBytes);

        archive.Position = 0;
        await archive.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);

        static void WriteProperty(Stream target, byte id, byte[] data)
        {
            target.WriteByte(id);
            WriteNumber(target, (ulong)data.Length);
            target.Write(data);
        }
    }

    /// <summary>7z's variable-length NUMBER encoding.</summary>
    private static void WriteNumber(Stream target, ulong value)
    {
        byte first = 0;
        byte mask = 0x80;
        int extra;
        for (extra = 0; extra < 8; extra++)
        {
            if (value < (1UL << (7 * (extra + 1))))
            {
                first |= (byte)(value >> (8 * extra));
                break;
            }

            first |= mask;
            mask >>= 1;
        }

        target.WriteByte(first);
        for (int index = 0; index < extra; index++) target.WriteByte((byte)(value >> (8 * index)));
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }

        return ~crc;
    }

    internal static FileStream CreatePrivateTempFile(string prefix)
    {
        string path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N") + ".tmp");
        return new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 80 * 1024,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);
    }

    internal static async ValueTask ValidateSevenZipManifestAsync(
        Stream archive,
        IReadOnlyList<ArchiveStagedOutputManifestEntry> expected,
        CancellationToken cancellationToken)
    {
        var expectedByPath = new Dictionary<string, ArchiveStagedOutputManifestEntry>(StringComparer.Ordinal);
        var impliedDirectories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in expected)
        {
            if (entry is null || string.IsNullOrEmpty(entry.ArchivePath)
                || entry.Kind is not (ArchiveEntryKind.File or ArchiveEntryKind.Directory)
                || (entry.Kind == ArchiveEntryKind.File && (entry.Length is null or < 0 || !IsSha256(entry.ContentSha256)))
                || !expectedByPath.TryAdd(Normalize(entry.ArchivePath), entry))
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.ProviderFailure,
                    "7z.output_manifest_invalid",
                    "7z staged-output validation requires unique file and directory entries with exact file digests.");
            }

            // 7-Zip records every parent folder of a stored path as its own entry.
            string path = Normalize(entry.ArchivePath);
            for (int slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1))
            {
                impliedDirectories.Add(path[..slash]);
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var options = new global::SharpCompress.Readers.ReaderOptions { LeaveStreamOpen = true };
        using var reader = global::SharpCompress.Archives.SevenZip.SevenZipArchive.OpenArchive(archive, options);
        foreach (var item in reader.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = Normalize(item.Key ?? string.Empty);
            if (!seen.Add(path)) throw ManifestMismatch("7z");
            if (!expectedByPath.TryGetValue(path, out var wanted))
            {
                if (item.IsDirectory && impliedDirectories.Contains(path)) continue;
                throw ManifestMismatch("7z");
            }

            if (wanted.Kind == ArchiveEntryKind.Directory)
            {
                if (!item.IsDirectory) throw ManifestMismatch("7z");
                continue;
            }

            if (item.IsDirectory || item.Size != wanted.Length) throw ManifestMismatch("7z");
            using var content = item.OpenEntryStream();
            var (length, digest) = await HashToEndAsync(content, wanted.Length!.Value, "7z", cancellationToken)
                .ConfigureAwait(false);
            if (length != wanted.Length) throw ManifestMismatch("7z");
            ValidateDigest(digest, wanted.ContentSha256!, "7z");
        }

        if (expectedByPath.Keys.Any(path => !seen.Contains(path))) throw ManifestMismatch("7z");
    }

    internal static async ValueTask<(long Length, byte[] Digest)> HashToEndAsync(
        Stream stream,
        long expectedLength,
        string format,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(80 * 1024);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            long total = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                total = checked(total + read);
                if (total > expectedLength) throw ManifestMismatch(format);
                hash.AppendData(buffer.AsSpan(0, read));
            }

            return (total, hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    internal static void ValidateDigest(byte[] actual, string expectedHex, string format)
    {
        var expected = Convert.FromHexString(expectedHex);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(actual, expected)) throw ManifestMismatch(format);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(actual);
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    internal static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    internal static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

    internal static ArchiveProviderException ManifestMismatch(string format) =>
        new(
            ArchiveErrorCategory.ProviderFailure,
            $"{format}.output_manifest_mismatch",
            $"The staged {format} output did not match the exact validated output manifest.");

    internal static ArchiveProviderException InvalidOutput(string format, Exception inner) =>
        new(
            ArchiveErrorCategory.CorruptArchive,
            $"{format}.output_invalid",
            $"The staged {format} output could not be reopened and validated.",
            inner);

    internal static ArchiveProviderException SourceChanged() =>
        new(
            ArchiveErrorCategory.SourceChanged,
            "source.changed_during_creation",
            "A source stream changed while it was being added to the archive.");
}

/// <summary>Private temporary folder that mirrors the archive layout for the 7z encoder.</summary>
internal sealed class SevenZipWorkspace : IDisposable
{
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Path, DateTimeOffset Time)> _directories = [];
    private int _disposed;

    public SevenZipWorkspace()
    {
        Root = Directory.CreateTempSubdirectory("RainArchives.7z.").FullName;
    }

    public string Root { get; }

    public int FileCount { get; private set; }

    /// <summary>The folder entries in the order they were added (archive path, time).</summary>
    public List<ArchiveWriteDirectory> Directories { get; } = [];

    public void AddDirectory(ArchiveWriteDirectory entry)
    {
        string path = Map(entry.ArchivePath);
        Directory.CreateDirectory(path);
        _directories.Add((path, entry.LastWriteTime));
        Directories.Add(entry);
    }

    public async ValueTask AddFileAsync(
        ArchiveWriteEntry entry,
        Stream source,
        Func<long, ValueTask> reportEntryBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = Map(entry.ArchivePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        await using (var target = new FileStream(
                         path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 80 * 1024, FileOptions.Asynchronous))
        {
            await SevenZipWriterSupport.CopyExactAsync(source, target, entry.Length, reportEntryBytes, cancellationToken)
                .ConfigureAwait(false);
        }

        File.SetLastWriteTimeUtc(path, entry.LastWriteTime.UtcDateTime);
        FileCount++;
    }

    /// <summary>Folder times are set last (deepest first) because adding files changes them.</summary>
    public void ApplyDirectoryTimestamps()
    {
        foreach (var (path, time) in _directories.OrderByDescending(item => item.Path.Length))
        {
            Directory.SetLastWriteTimeUtc(path, time.UtcDateTime);
        }
    }

    private string Map(string archivePath)
    {
        string[] parts = SevenZipWriterSupport.Normalize(archivePath ?? string.Empty).Split('/');
        if (parts.Length == 0 || parts.Any(part => part.Length == 0 || part is "." or ".."))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "7z.entry_path_invalid",
                "A 7z entry path was empty or not a plain relative path.");
        }

        string full = System.IO.Path.GetFullPath(System.IO.Path.Combine([Root, .. parts]));
        if (!full.StartsWith(Root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidRequest,
                "7z.entry_path_invalid",
                "A 7z entry path left the private workspace.");
        }

        if (!_paths.Add(full))
        {
            // Windows folders can't hold two names that differ only in letter case.
            throw new ArchiveProviderException(
                ArchiveErrorCategory.UnsupportedCapability,
                "7z.entry_path_collision",
                "Two 7z entries would have the same name on Windows.");
        }

        return full;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort: the folder is in the user's private temp area and holds only copies.
        }
    }
}
