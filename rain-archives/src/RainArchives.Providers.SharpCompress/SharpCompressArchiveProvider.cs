using System.Buffers.Binary;
using System.Text;
using RainArchives.Contracts;
using RainArchives.Core;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace RainArchives.Providers.SharpCompress;

public sealed class SharpCompressArchiveProvider : IArchiveProvider
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> SupportedFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "zip",
        "7z",
        "rar",
        "tar",
        "gzip",
        "bzip2",
        "xz",
    };

    public string Id => "sharpcompress/0.50.4";

    public ArchiveProbeResult Probe(ReadOnlySpan<byte> prefix, string sourcePath)
    {
        var detected = ArchiveSignatures.Detect(prefix);
        if (detected is not null && SupportedFormats.Contains(detected))
        {
            return new ArchiveProbeResult(
                detected,
                detected is "7z" or "rar" ? 70 : 90,
                "Content signature matched.");
        }

        return string.Equals(Path.GetExtension(sourcePath), ".zip", StringComparison.OrdinalIgnoreCase)
            ? new ArchiveProbeResult(
                "zip",
                10,
                "The path is a possible classic split-ZIP terminal volume; content validation is required during open.")
            : ArchiveProbeResult.NoMatch("No supported SharpCompress signature matched.");
    }

    public async ValueTask<IArchiveHandle> OpenAsync(
        string sourcePath,
        ReadOnlyMemory<byte> passwordUtf8,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        string? password = null;
        string? detectedFormat = null;
        try
        {
            if (!passwordUtf8.IsEmpty)
            {
                password = StrictUtf8.GetString(passwordUtf8.Span);
            }

            var options = CreateReaderOptions(password);
            var archiveOpenPath = ZipArchivePreflight.ResolveArchiveOpenPath(sourcePath);
            detectedFormat = await DetectSourceFormatAsync(archiveOpenPath, cancellationToken)
                .ConfigureAwait(false);
            if (detectedFormat is null)
            {
                throw new ArchiveProviderException(
                    ZipArchivePreflight.IsClassicVolumePath(sourcePath)
                        ? ArchiveErrorCategory.InvalidArchive
                        : ArchiveErrorCategory.UnsupportedFormat,
                    ZipArchivePreflight.IsClassicVolumePath(sourcePath)
                        ? "archive.invalid"
                        : "format.not_detected",
                    ZipArchivePreflight.IsClassicVolumePath(sourcePath)
                        ? "The classic split ZIP terminal volume is malformed or incomplete."
                        : "The file content did not match a supported archive format.");
            }

            if (detectedFormat == "zip")
            {
                ZipArchivePreflight.ValidateSingleVolume(archiveOpenPath, cancellationToken);
            }

            if (detectedFormat is "gzip" or "bzip2" or "xz")
            {
                try
                {
                    return await SequentialReaderArchiveHandle
                        .CreateAsync(archiveOpenPath, detectedFormat, options, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (InvalidFormatException) when (detectedFormat is "bzip2" or "xz")
                {
                    return SingleCompressedStreamArchiveHandle.Create(archiveOpenPath, detectedFormat);
                }
            }

            var archive = ArchiveFactory.OpenArchive(archiveOpenPath, options);
            try
            {
                return new SharpCompressArchiveHandle(
                    archive,
                    sourcePath,
                    password is not null,
                    cancellationToken);
            }
            catch
            {
                archive.Dispose();
                throw;
            }
        }
        catch (CryptographicException exception)
        {
            var suppliedPassword = password is not null;
            throw new ArchiveProviderException(
                suppliedPassword
                    ? ArchiveErrorCategory.IncorrectPassword
                    : ArchiveErrorCategory.PasswordRequired,
                suppliedPassword
                    ? "archive.incorrect_password"
                    : "archive.password_required",
                suppliedPassword
                    ? "The supplied password could not decrypt or authenticate the archive."
                    : "The archive requires a password.",
                exception);
        }
        catch (SharpCompressException exception) when (
            password is not null && detectedFormat is "zip" or "7z" or "rar")
        {
            throw IncorrectPasswordOrEncryptedData(exception);
        }
        catch (SharpCompressException exception) when (
            exception is ArchiveException or ArchiveOperationException or InvalidFormatException)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidArchive,
                "archive.invalid",
                "The archive is malformed, incomplete, or unsupported by the managed provider.",
                exception);
        }
        catch (SharpCompressException exception)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ProviderFailure,
                "provider.sharpcompress_failure",
                "SharpCompress could not open the archive.",
                exception);
        }
        catch (IndexOutOfRangeException exception) when (detectedFormat == "zip")
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidArchive,
                "archive.invalid",
                "The ZIP archive contains an invalid volume or entry reference.",
                exception);
        }
        catch (OverflowException exception) when (detectedFormat == "rar")
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidArchive,
                "archive.invalid",
                "The RAR archive contains malformed variable-length metadata.",
                exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new ArchiveProviderException(
                exception is UnauthorizedAccessException
                    ? ArchiveErrorCategory.PermissionDenied
                    : ArchiveErrorCategory.ProviderFailure,
                "provider.sharpcompress_open_failed",
                "The managed archive provider could not open the source.",
                exception);
        }
        catch (ArgumentException exception)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ProviderFailure,
                "provider.sharpcompress_metadata_failed",
                "The managed archive provider could not read optional archive metadata.",
                exception);
        }
    }

    private static ReaderOptions CreateReaderOptions(string? password) =>
        password is null
            ? ReaderOptions.ForFilePath
            : ReaderOptions.ForEncryptedArchive(password);

    private static async ValueTask<string?> DetectSourceFormatAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: ArchiveSignatures.PrefixBytesRequired,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var prefix = new byte[ArchiveSignatures.PrefixBytesRequired];
        var bytesRead = await stream
            .ReadAtLeastAsync(prefix, prefix.Length, throwOnEndOfStream: false, cancellationToken)
            .ConfigureAwait(false);
        var detected = ArchiveSignatures.Detect(prefix.AsSpan(0, bytesRead));
        if (detected is not null
            || !string.Equals(Path.GetExtension(sourcePath), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            return detected;
        }

        return await ZipArchivePreflight
            .HasTerminalEndRecordAsync(sourcePath, cancellationToken)
            .ConfigureAwait(false)
            ? "zip"
            : null;
    }

    internal static ArchiveVolumeSetKind ClassifyZipVolumeSetForDiagnostics(
        string sourcePath,
        string format) =>
        SharpCompressArchiveHandle.ClassifyZipVolumeSet(sourcePath, format);

    internal static bool IsZipMultipartSourceForDiagnostics(string sourcePath, string format) =>
        format == "zip"
        && ClassifyZipVolumeSetForDiagnostics(sourcePath, format) != ArchiveVolumeSetKind.Single;

    private sealed class SharpCompressArchiveHandle : IArchiveHandle
    {
        private readonly IArchive _archive;
        private readonly bool _hasPassword;
        private readonly string _format;
        private readonly ArchiveVolumeSetKind _volumeSetKind;
        private readonly IReadOnlyList<IArchiveEntry> _nativeEntries;
        private readonly IReadOnlyList<ProviderArchiveEntry> _entries;

        public SharpCompressArchiveHandle(
            IArchive archive,
            string sourcePath,
            bool hasPassword,
            CancellationToken cancellationToken)
        {
            _archive = archive;
            _hasPassword = hasPassword;
            _format = NormalizeFormat(archive.Type.ToString());
            var nativeEntries = new List<IArchiveEntry>();
            var entries = new List<ProviderArchiveEntry>();
            var metadataBudget = new ArchiveMetadataBudget();
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var providerEntry = ToProviderEntry(entry, entries.Count);
                metadataBudget.Add(providerEntry);
                nativeEntries.Add(entry);
                entries.Add(providerEntry);
            }

            _nativeEntries = nativeEntries;
            _entries = entries;
            _volumeSetKind = ClassifyVolumeSet(archive, sourcePath, _format);
        }

        public string Format => _format;
        public string ProviderId => "sharpcompress/0.50.4";
        public ArchiveCapability Capabilities
        {
            get
            {
                var capabilities = ArchiveCapability.List
                    | (_format is "zip" or "7z" or "rar" ? ArchiveCapability.ReadEncrypted : ArchiveCapability.None)
                    | (_volumeSetKind == ArchiveVolumeSetKind.ConfirmedMultipart
                        ? ArchiveCapability.ReadVolumes
                        : ArchiveCapability.None);

                if (_volumeSetKind == ArchiveVolumeSetKind.Single)
                {
                    capabilities |= ArchiveCapability.Test | ArchiveCapability.Extract;
                    if (!(_format == "rar" && _archive.IsSolid))
                    {
                        capabilities |= ArchiveCapability.OpenEntryStream;
                    }
                }

                return capabilities;
            }
        }
        public bool IsSolid => _archive.IsSolid;
        public ArchiveVolumeSetKind VolumeSetKind => _volumeSetKind;
        public IReadOnlyList<ProviderArchiveEntry> Entries => _entries;

        public async ValueTask<Stream> OpenEntryStreamAsync(
            int providerOrdinal,
            CancellationToken cancellationToken)
        {
            if ((uint)providerOrdinal >= (uint)_nativeEntries.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(providerOrdinal));
            }

            var entry = _nativeEntries[providerOrdinal];
            if (entry.IsDirectory)
            {
                throw new InvalidOperationException("Directory entries do not have data streams.");
            }

            if (_format == "rar" && _archive.IsSolid)
            {
                throw new ArchiveProviderException(
                    ArchiveErrorCategory.UnsupportedCapability,
                    "entry.stream_solid_rar_deferred",
                    "Preview streaming for solid RAR archives is deferred until bounded sequential decoding is available.");
            }

            try
            {
                var stream = await entry.OpenEntryStreamAsync(cancellationToken).ConfigureAwait(false);
                return new OwnedReadStream(
                    stream,
                    owner: null,
                    readExceptionMapper: exception => MapDeferredEntryReadException(
                        exception,
                        _entries[providerOrdinal].IsEncrypted,
                        cancellationToken));
            }
            catch (CryptographicException exception)
            {
                throw new ArchiveProviderException(
                    _hasPassword
                        ? ArchiveErrorCategory.IncorrectPassword
                        : ArchiveErrorCategory.PasswordRequired,
                    _hasPassword
                        ? "archive.incorrect_password"
                        : "archive.password_required",
                    _hasPassword
                        ? "The supplied password could not decrypt or authenticate the selected entry."
                        : "The selected entry requires a password.",
                    exception);
            }
            catch (ReaderCancelledException exception)
            {
                throw new OperationCanceledException(
                    "SharpCompress cancelled the entry read.",
                    exception,
                    cancellationToken);
            }
            catch (SharpCompressException exception)
            {
                if (_hasPassword && TryGet(() => entry.IsEncrypted, false))
                {
                    throw IncorrectPasswordOrEncryptedData(exception);
                }

                throw new ArchiveProviderException(
                    ArchiveErrorCategory.CorruptArchive,
                    "archive.entry_stream_failed",
                    "The entry could not be decoded or authenticated.",
                    exception);
            }
        }

        private Exception? MapDeferredEntryReadException(
            Exception exception,
            bool isEncrypted,
            CancellationToken cancellationToken) =>
            exception switch
            {
                ArchiveProviderException or OperationCanceledException => null,
                CryptographicException cryptographic => new ArchiveProviderException(
                    _hasPassword
                        ? ArchiveErrorCategory.IncorrectPassword
                        : ArchiveErrorCategory.PasswordRequired,
                    _hasPassword
                        ? "archive.incorrect_password"
                        : "archive.password_required",
                    _hasPassword
                        ? "The supplied password could not decrypt or authenticate the selected entry."
                        : "The selected entry requires a password.",
                    cryptographic),
                ReaderCancelledException cancelled => new OperationCanceledException(
                    "SharpCompress cancelled the entry read.",
                    cancelled,
                    cancellationToken),
                SharpCompressException sharpCompress when _hasPassword && isEncrypted =>
                    IncorrectPasswordOrEncryptedData(sharpCompress),
                SharpCompressException sharpCompress when isEncrypted => new ArchiveProviderException(
                    ArchiveErrorCategory.PasswordRequired,
                    "archive.password_required",
                    "The selected entry requires a password.",
                    sharpCompress),
                SharpCompressException sharpCompress => new ArchiveProviderException(
                    ArchiveErrorCategory.CorruptArchive,
                    "archive.entry_stream_failed",
                    "The entry could not be decoded or authenticated.",
                    sharpCompress),
                _ => null,
            };

        public async ValueTask CopyEntryToAsync(
            int providerOrdinal,
            Stream destination,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(destination);
            if ((uint)providerOrdinal >= (uint)_nativeEntries.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(providerOrdinal));
            }

            var entry = _nativeEntries[providerOrdinal];
            if (entry.IsDirectory)
            {
                throw new InvalidOperationException("Directory entries do not have data streams.");
            }

            try
            {
                await entry.WriteToAsync(
                    destination,
                    new ExtractionOptions
                    {
                        CheckCrc = true,
                        ExtractFullPath = false,
                        Overwrite = false,
                        PreserveAttributes = false,
                        PreserveFileTime = false,
                    },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (CryptographicException exception)
            {
                throw new ArchiveProviderException(
                    _hasPassword
                        ? ArchiveErrorCategory.IncorrectPassword
                        : ArchiveErrorCategory.PasswordRequired,
                    _hasPassword
                        ? "archive.incorrect_password"
                        : "archive.password_required",
                    _hasPassword
                        ? "The supplied password could not decrypt or authenticate the selected entry."
                        : "The selected entry requires a password.",
                    exception);
            }
            catch (ReaderCancelledException exception)
            {
                throw new OperationCanceledException(
                    "SharpCompress cancelled the entry copy.",
                    exception,
                    cancellationToken);
            }
            catch (SharpCompressException exception)
            {
                if (_hasPassword && TryGet(() => entry.IsEncrypted, false))
                {
                    throw IncorrectPasswordOrEncryptedData(exception);
                }

                throw new ArchiveProviderException(
                    ArchiveErrorCategory.CorruptArchive,
                    "archive.entry_checksum_or_decode_failed",
                    "The entry failed checksum validation or could not be decoded.",
                    exception);
            }
        }

        public ValueTask DisposeAsync()
        {
            _archive.Dispose();
            return ValueTask.CompletedTask;
        }

        private static ProviderArchiveEntry ToProviderEntry(IArchiveEntry entry, int ordinal)
        {
            if (string.IsNullOrEmpty(entry.Key))
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
            var crc = TryGetCrc(entry);

            return new ProviderArchiveEntry(
                ordinal,
                entry.Key,
                kind,
                size >= 0 ? size : null,
                compressedSize >= 0 ? compressedSize : null,
                ToUtcTimestamp(TryGet(() => entry.LastModifiedTime, fallback: null)),
                TryGet(() => entry.Attrib, fallback: null),
                TryGetCompressionMethod(entry),
                crc >= 0 ? crc.ToString("X8") : null,
                TryGet(() => entry.IsEncrypted, false),
                TryGet(() => entry.IsSolid, false),
                linkTarget);
        }

        internal static DateTimeOffset? ToUtcTimestamp(DateTime? value)
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

        internal static string NormalizeFormat(string format) => format.ToLowerInvariant() switch
        {
            "sevenzip" => "7z",
            "gzip" => "gzip",
            "bzip2" => "bzip2",
            _ => format.ToLowerInvariant(),
        };

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

        private static long TryGetCrc(IArchiveEntry entry)
        {
            if (entry.IsDirectory)
            {
                return -1L;
            }

            try
            {
                return entry.Crc;
            }
            catch (Exception exception) when (
                exception is NotImplementedException or ArgumentNullException)
            {
                // RAR5 permits records without a CRC32 (including valid directory
                // records and files using another integrity field). SharpCompress
                // exposes the missing optional value by throwing ArgumentNullException.
                return -1L;
            }
        }

        private static string? TryGetCompressionMethod(IArchiveEntry entry)
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

        private static ArchiveVolumeSetKind ClassifyVolumeSet(
            IArchive archive,
            string sourcePath,
            string format)
        {
            if (archive.Volumes.Skip(1).Any())
            {
                return ArchiveVolumeSetKind.ConfirmedMultipart;
            }

            return format switch
            {
                "zip" => ClassifyZipVolumeSet(sourcePath, format),
                "7z" => ClassifySevenZipVolumeSet(sourcePath, format),
                "rar" when archive is IRarArchive rarArchive => rarArchive.IsMultipartVolume()
                    ? ArchiveVolumeSetKind.ConfirmedMultipart
                    : ArchiveVolumeSetKind.Single,
                "rar" => ArchiveVolumeSetKind.Unknown,
                _ => ArchiveVolumeSetKind.Single,
            };
        }

        private static ArchiveVolumeSetKind ClassifySevenZipVolumeSet(
            string sourcePath,
            string format)
        {
            if (format != "7z")
            {
                return ArchiveVolumeSetKind.Unknown;
            }

            Span<byte> header = stackalloc byte[32];
            ReadOnlySpan<byte> signature = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
            using var stream = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: header.Length,
                FileOptions.SequentialScan);
            if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) != header.Length
                || !header[..6].SequenceEqual(signature))
            {
                return ArchiveVolumeSetKind.Unknown;
            }

            var nextHeaderOffset = BinaryPrimitives.ReadUInt64LittleEndian(header[12..20]);
            var nextHeaderSize = BinaryPrimitives.ReadUInt64LittleEndian(header[20..28]);
            try
            {
                var requiredLength = checked(32UL + nextHeaderOffset + nextHeaderSize);
                return requiredLength > checked((ulong)stream.Length)
                    ? ArchiveVolumeSetKind.ConfirmedMultipart
                    : ArchiveVolumeSetKind.Single;
            }
            catch (OverflowException)
            {
                return ArchiveVolumeSetKind.Unknown;
            }
        }

        internal static ArchiveVolumeSetKind ClassifyZipVolumeSet(string sourcePath, string format)
        {
            if (format != "zip")
            {
                return ArchiveVolumeSetKind.Unknown;
            }

            if (ZipArchivePreflight.IsExplicitVolumePath(sourcePath))
            {
                return ArchiveVolumeSetKind.ConfirmedMultipart;
            }

            const uint spanningMarker = 0x08074B50;
            const uint endOfCentralDirectorySignature = 0x06054B50;
            const uint zip64LocatorSignature = 0x07064B50;
            const int maximumTailBytes = ushort.MaxValue + 22 + 20 + 56;
            using var stream = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: maximumTailBytes,
                FileOptions.SequentialScan);
            Span<byte> signature = stackalloc byte[sizeof(uint)];
            if (stream.Read(signature) == signature.Length
                && BinaryPrimitives.ReadUInt32LittleEndian(signature) == spanningMarker)
            {
                return ArchiveVolumeSetKind.ConfirmedMultipart;
            }

            var tailLength = checked((int)Math.Min(stream.Length, maximumTailBytes));
            if (tailLength < 22)
            {
                return ArchiveVolumeSetKind.Unknown;
            }

            var tail = new byte[tailLength];
            stream.Position = stream.Length - tailLength;
            stream.ReadExactly(tail);
            var foundSingleDiskEndRecord = false;
            for (var offset = tail.Length - 22; offset >= 0; offset--)
            {
                var candidate = tail.AsSpan(offset);
                if (BinaryPrimitives.ReadUInt32LittleEndian(candidate) != endOfCentralDirectorySignature)
                {
                    continue;
                }

                var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(candidate[20..22]);
                if (offset + 22 + commentLength != tail.Length)
                {
                    continue;
                }

                var diskNumber = BinaryPrimitives.ReadUInt16LittleEndian(candidate[4..6]);
                var centralDirectoryDisk = BinaryPrimitives.ReadUInt16LittleEndian(candidate[6..8]);
                var entriesOnDisk = BinaryPrimitives.ReadUInt16LittleEndian(candidate[8..10]);
                var totalEntries = BinaryPrimitives.ReadUInt16LittleEndian(candidate[10..12]);
                var centralDirectorySize = BinaryPrimitives.ReadUInt32LittleEndian(candidate[12..16]);
                var centralDirectoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(candidate[16..20]);
                if (diskNumber != 0
                    || centralDirectoryDisk != 0
                    || entriesOnDisk != totalEntries)
                {
                    return ArchiveVolumeSetKind.ConfirmedMultipart;
                }

                const int zip64LocatorBytes = 20;
                var locatorOffset = offset - zip64LocatorBytes;
                var hasZip64Locator = false;
                if (locatorOffset >= 0)
                {
                    var locator = tail.AsSpan(locatorOffset, zip64LocatorBytes);
                    if (BinaryPrimitives.ReadUInt32LittleEndian(locator) == zip64LocatorSignature)
                    {
                        hasZip64Locator = true;
                        var zip64EndDisk = BinaryPrimitives.ReadUInt32LittleEndian(locator[4..8]);
                        var totalDisks = BinaryPrimitives.ReadUInt32LittleEndian(locator[16..20]);
                        if (zip64EndDisk != 0 || totalDisks != 1)
                        {
                            return ArchiveVolumeSetKind.ConfirmedMultipart;
                        }
                    }
                }

                var requiresZip64Locator = entriesOnDisk == ushort.MaxValue
                    || totalEntries == ushort.MaxValue
                    || centralDirectorySize == uint.MaxValue
                    || centralDirectoryOffset == uint.MaxValue;
                if (requiresZip64Locator && !hasZip64Locator)
                {
                    return ArchiveVolumeSetKind.Unknown;
                }

                foundSingleDiskEndRecord = true;
            }

            return foundSingleDiskEndRecord
                ? ArchiveVolumeSetKind.Single
                : ArchiveVolumeSetKind.Unknown;
        }
    }

    private static ArchiveProviderException IncorrectPasswordOrEncryptedData(Exception exception) =>
        new(
            ArchiveErrorCategory.IncorrectPassword,
            "archive.incorrect_password_or_encrypted_data",
            "The supplied password was incorrect or the encrypted archive data could not be authenticated.",
            exception);
}
