using System.Buffers.Binary;
using RainArchives.Contracts;
using RainArchives.Core;

namespace RainArchives.Providers.SharpCompress;

internal static class ZipArchivePreflight
{
    private const uint EndOfCentralDirectorySignature = 0x06054B50;
    private const uint Zip64EndOfCentralDirectorySignature = 0x06064B50;
    private const uint Zip64LocatorSignature = 0x07064B50;
    private const uint CentralDirectoryEntrySignature = 0x02014B50;
    private const uint CentralDirectoryDigitalSignature = 0x05054B50;
    private const ushort Zip64ExtraFieldId = 0x0001;
    private const int EndOfCentralDirectoryBytes = 22;
    private const int Zip64LocatorBytes = 20;
    private const int Zip64EndOfCentralDirectoryMinimumBytes = 56;
    private const int CentralDirectoryEntryBytes = 46;
    private const int MaximumTailBytes = ushort.MaxValue
        + EndOfCentralDirectoryBytes
        + Zip64LocatorBytes
        + Zip64EndOfCentralDirectoryMinimumBytes;
    private static readonly int MaximumEntries = new ArchiveMetadataLimits().MaximumEntries;

    public static void ValidateSingleVolume(string sourcePath, CancellationToken cancellationToken)
    {
        if (IsExplicitVolumePath(sourcePath))
        {
            return;
        }

        using var stream = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.RandomAccess);
        var endRecord = FindEndRecord(stream);
        if (endRecord is null)
        {
            throw InvalidCentralDirectory(
                "The ZIP end-of-central-directory record is missing or malformed.");
        }

        var layout = ResolveSingleVolumeLayout(stream, endRecord.Value);
        if (layout is null)
        {
            return;
        }

        if (layout.Value.EntryCount > (ulong)MaximumEntries)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.ResourceLimit,
                "resource.entry_count",
                $"The archive contains more than {MaximumEntries:N0} entries.");
        }

        ValidateCentralDirectoryEntries(stream, layout.Value, cancellationToken);
    }

    internal static async ValueTask<bool> HasTerminalEndRecordAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (stream.Length < EndOfCentralDirectoryBytes)
        {
            return false;
        }

        var tailLength = checked((int)Math.Min(stream.Length, MaximumTailBytes));
        var tail = new byte[tailLength];
        var tailOffset = stream.Length - tailLength;
        stream.Position = tailOffset;
        await stream.ReadExactlyAsync(tail, cancellationToken).ConfigureAwait(false);
        return FindEndRecord(tail, tailOffset) is not null;
    }

    internal static string ResolveArchiveOpenPath(string sourcePath)
    {
        if (!IsClassicVolumePath(sourcePath))
        {
            return sourcePath;
        }

        var terminalPath = Path.ChangeExtension(sourcePath, ".zip");
        if (!File.Exists(terminalPath))
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InvalidArchive,
                "archive.missing_zip_terminal_volume",
                "The classic split ZIP terminal .zip volume is missing.");
        }

        return terminalPath;
    }

    internal static bool IsExplicitVolumePath(string sourcePath)
    {
        var fileName = Path.GetFileName(sourcePath);
        var numericSuffixStart = fileName.LastIndexOf('.');
        if (numericSuffixStart > 0
            && fileName.AsSpan(numericSuffixStart + 1).Length > 0
            && fileName.AsSpan(numericSuffixStart + 1).IndexOfAnyExceptInRange('0', '9') < 0
            && fileName.AsSpan(0, numericSuffixStart).EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsClassicVolumePath(sourcePath);
    }

    internal static bool IsClassicVolumePath(string sourcePath)
    {
        var extension = Path.GetExtension(Path.GetFileName(sourcePath));
        return extension.Length > 2
            && extension[1] is 'z' or 'Z'
            && extension.AsSpan(2).IndexOfAnyExceptInRange('0', '9') < 0;
    }

    private static EndRecord? FindEndRecord(FileStream stream)
    {
        if (stream.Length < EndOfCentralDirectoryBytes)
        {
            return null;
        }

        var tailLength = checked((int)Math.Min(stream.Length, MaximumTailBytes));
        var tail = new byte[tailLength];
        var tailOffset = stream.Length - tailLength;
        stream.Position = tailOffset;
        stream.ReadExactly(tail);

        return FindEndRecord(tail, tailOffset);
    }

    private static EndRecord? FindEndRecord(ReadOnlySpan<byte> tail, long tailOffset)
    {
        for (var offset = tail.Length - EndOfCentralDirectoryBytes; offset >= 0; offset--)
        {
            var candidate = tail[offset..];
            if (BinaryPrimitives.ReadUInt32LittleEndian(candidate) != EndOfCentralDirectorySignature)
            {
                continue;
            }

            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(candidate[20..22]);
            if (offset + EndOfCentralDirectoryBytes + commentLength != tail.Length)
            {
                continue;
            }

            return new EndRecord(
                tailOffset + offset,
                BinaryPrimitives.ReadUInt16LittleEndian(candidate[4..6]),
                BinaryPrimitives.ReadUInt16LittleEndian(candidate[6..8]),
                BinaryPrimitives.ReadUInt16LittleEndian(candidate[8..10]),
                BinaryPrimitives.ReadUInt16LittleEndian(candidate[10..12]),
                BinaryPrimitives.ReadUInt32LittleEndian(candidate[12..16]),
                BinaryPrimitives.ReadUInt32LittleEndian(candidate[16..20]));
        }

        return null;
    }

    private static CentralDirectoryLayout? ResolveSingleVolumeLayout(
        FileStream stream,
        EndRecord endRecord)
    {
        if (endRecord.DiskNumber != 0
            || endRecord.CentralDirectoryDisk != 0
            || endRecord.EntriesOnDisk != endRecord.TotalEntries)
        {
            return null;
        }

        var requiresZip64 = endRecord.EntriesOnDisk == ushort.MaxValue
            || endRecord.TotalEntries == ushort.MaxValue
            || endRecord.CentralDirectorySize == uint.MaxValue
            || endRecord.CentralDirectoryOffset == uint.MaxValue;
        if (!requiresZip64)
        {
            return CreateLayout(
                endRecord.TotalEntries,
                endRecord.CentralDirectoryOffset,
                endRecord.CentralDirectorySize,
                endRecord.Offset);
        }

        var locatorOffset = endRecord.Offset - Zip64LocatorBytes;
        if (locatorOffset < 0)
        {
            throw InvalidCentralDirectory("The ZIP64 locator is missing.");
        }

        Span<byte> locator = stackalloc byte[Zip64LocatorBytes];
        stream.Position = locatorOffset;
        stream.ReadExactly(locator);
        if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != Zip64LocatorSignature)
        {
            throw InvalidCentralDirectory("The ZIP64 locator is missing or malformed.");
        }

        var zip64EndDisk = BinaryPrimitives.ReadUInt32LittleEndian(locator[4..8]);
        var zip64EndOffset = BinaryPrimitives.ReadUInt64LittleEndian(locator[8..16]);
        var totalDisks = BinaryPrimitives.ReadUInt32LittleEndian(locator[16..20]);
        if (zip64EndDisk != 0 || totalDisks != 1)
        {
            return null;
        }

        var zip64EndPosition = ToPosition(zip64EndOffset);
        if (zip64EndPosition > locatorOffset - Zip64EndOfCentralDirectoryMinimumBytes)
        {
            throw InvalidCentralDirectory("The ZIP64 end record points outside the archive layout.");
        }

        Span<byte> zip64End = stackalloc byte[Zip64EndOfCentralDirectoryMinimumBytes];
        stream.Position = zip64EndPosition;
        stream.ReadExactly(zip64End);
        if (BinaryPrimitives.ReadUInt32LittleEndian(zip64End) != Zip64EndOfCentralDirectorySignature)
        {
            throw InvalidCentralDirectory("The ZIP64 end record is missing or malformed.");
        }

        var recordPayloadSize = BinaryPrimitives.ReadUInt64LittleEndian(zip64End[4..12]);
        if (recordPayloadSize < 44
            || recordPayloadSize > long.MaxValue - 12
            || zip64EndPosition + checked((long)recordPayloadSize + 12) != locatorOffset)
        {
            throw InvalidCentralDirectory("The ZIP64 end record length is invalid.");
        }

        var diskNumber = BinaryPrimitives.ReadUInt32LittleEndian(zip64End[16..20]);
        var centralDirectoryDisk = BinaryPrimitives.ReadUInt32LittleEndian(zip64End[20..24]);
        var entriesOnDisk = BinaryPrimitives.ReadUInt64LittleEndian(zip64End[24..32]);
        var totalEntries = BinaryPrimitives.ReadUInt64LittleEndian(zip64End[32..40]);
        if (diskNumber != 0 || centralDirectoryDisk != 0 || entriesOnDisk != totalEntries)
        {
            return null;
        }

        return CreateLayout(
            totalEntries,
            BinaryPrimitives.ReadUInt64LittleEndian(zip64End[48..56]),
            BinaryPrimitives.ReadUInt64LittleEndian(zip64End[40..48]),
            zip64EndPosition);
    }

    private static CentralDirectoryLayout CreateLayout(
        ulong entryCount,
        ulong centralDirectoryOffset,
        ulong centralDirectorySize,
        long followingRecordOffset)
    {
        var offset = ToPosition(centralDirectoryOffset);
        var size = ToPosition(centralDirectorySize);
        long end;
        try
        {
            end = checked(offset + size);
        }
        catch (OverflowException)
        {
            throw InvalidCentralDirectory("The ZIP central-directory bounds overflow.");
        }

        if (end > followingRecordOffset)
        {
            throw InvalidCentralDirectory("The ZIP central directory extends beyond its end record.");
        }

        return new CentralDirectoryLayout(entryCount, offset, end);
    }

    private static void ValidateCentralDirectoryEntries(
        FileStream stream,
        CentralDirectoryLayout layout,
        CancellationToken cancellationToken)
    {
        stream.Position = layout.Offset;
        Span<byte> header = stackalloc byte[CentralDirectoryEntryBytes];
        for (ulong index = 0; index < layout.EntryCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recordOffset = stream.Position;
            if (recordOffset > layout.End - CentralDirectoryEntryBytes)
            {
                throw InvalidCentralDirectory("The ZIP central directory ended before all entries were present.");
            }

            stream.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != CentralDirectoryEntrySignature)
            {
                throw InvalidCentralDirectory("A ZIP central-directory entry signature is invalid.");
            }

            var fileNameLength = BinaryPrimitives.ReadUInt16LittleEndian(header[28..30]);
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header[30..32]);
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(header[32..34]);
            long recordEnd;
            try
            {
                recordEnd = checked(
                    recordOffset
                    + CentralDirectoryEntryBytes
                    + fileNameLength
                    + extraLength
                    + commentLength);
            }
            catch (OverflowException)
            {
                throw InvalidCentralDirectory("A ZIP central-directory entry length overflowed.");
            }

            if (recordEnd > layout.End)
            {
                throw InvalidCentralDirectory("A ZIP central-directory entry extends beyond the directory bounds.");
            }

            var diskNumberStart = BinaryPrimitives.ReadUInt16LittleEndian(header[34..36]);
            if (diskNumberStart == ushort.MaxValue)
            {
                stream.Position = recordOffset + CentralDirectoryEntryBytes + fileNameLength;
                var extra = new byte[extraLength];
                stream.ReadExactly(extra);
                if (ReadZip64DiskNumber(header, extra) != 0)
                {
                    throw InvalidVolumeReference();
                }

                throw UnsupportedZip64DiskStartEncoding();
            }
            else if (diskNumberStart != 0)
            {
                throw InvalidVolumeReference();
            }

            stream.Position = recordEnd;
        }

        ValidateOptionalDigitalSignature(stream, layout.End);
    }

    private static uint ReadZip64DiskNumber(ReadOnlySpan<byte> header, ReadOnlySpan<byte> extra)
    {
        var offset = 0;
        while (offset <= extra.Length - 4)
        {
            var fieldId = BinaryPrimitives.ReadUInt16LittleEndian(extra[offset..(offset + 2)]);
            var fieldLength = BinaryPrimitives.ReadUInt16LittleEndian(extra[(offset + 2)..(offset + 4)]);
            offset += 4;
            if (fieldLength > extra.Length - offset)
            {
                throw InvalidCentralDirectory("A ZIP extra field extends beyond its entry.");
            }

            if (fieldId == Zip64ExtraFieldId)
            {
                var field = extra.Slice(offset, fieldLength);
                var valueOffset = 0;
                if (BinaryPrimitives.ReadUInt32LittleEndian(header[24..28]) == uint.MaxValue)
                {
                    valueOffset = AdvanceZip64Value(field, valueOffset, sizeof(ulong));
                }

                if (BinaryPrimitives.ReadUInt32LittleEndian(header[20..24]) == uint.MaxValue)
                {
                    valueOffset = AdvanceZip64Value(field, valueOffset, sizeof(ulong));
                }

                if (BinaryPrimitives.ReadUInt32LittleEndian(header[42..46]) == uint.MaxValue)
                {
                    valueOffset = AdvanceZip64Value(field, valueOffset, sizeof(ulong));
                }

                if (valueOffset > field.Length - sizeof(uint))
                {
                    throw InvalidCentralDirectory("The ZIP64 disk-number field is missing.");
                }

                return BinaryPrimitives.ReadUInt32LittleEndian(field[valueOffset..]);
            }

            offset += fieldLength;
        }

        throw InvalidCentralDirectory("The ZIP64 disk-number field is missing.");
    }

    private static int AdvanceZip64Value(ReadOnlySpan<byte> field, int offset, int length)
    {
        if (offset > field.Length - length)
        {
            throw InvalidCentralDirectory("A ZIP64 extra field is truncated.");
        }

        return offset + length;
    }

    private static void ValidateOptionalDigitalSignature(FileStream stream, long centralDirectoryEnd)
    {
        var remaining = centralDirectoryEnd - stream.Position;
        if (remaining == 0)
        {
            return;
        }

        if (remaining < 6 || remaining > ushort.MaxValue + 6L)
        {
            throw InvalidCentralDirectory("The ZIP central directory contains unexpected trailing data.");
        }

        Span<byte> signatureHeader = stackalloc byte[6];
        stream.ReadExactly(signatureHeader);
        var signatureLength = BinaryPrimitives.ReadUInt16LittleEndian(signatureHeader[4..6]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(signatureHeader) != CentralDirectoryDigitalSignature
            || signatureLength + 6L != remaining)
        {
            throw InvalidCentralDirectory("The ZIP central-directory digital signature is malformed.");
        }

        stream.Position = centralDirectoryEnd;
    }

    private static long ToPosition(ulong value)
    {
        if (value > long.MaxValue)
        {
            throw InvalidCentralDirectory("A ZIP file offset exceeds the supported range.");
        }

        return checked((long)value);
    }

    private static ArchiveProviderException InvalidCentralDirectory(string message) =>
        new(
            ArchiveErrorCategory.InvalidArchive,
            "archive.invalid_zip_central_directory",
            message);

    private static ArchiveProviderException InvalidVolumeReference() =>
        new(
            ArchiveErrorCategory.InvalidArchive,
            "archive.invalid_zip_volume_reference",
            "A single-volume ZIP entry refers to a different disk.");

    private static ArchiveProviderException UnsupportedZip64DiskStartEncoding() =>
        new(
            ArchiveErrorCategory.UnsupportedFormat,
            "archive.unsupported_zip64_disk_start",
            "This ZIP64 disk-start representation is not safely supported by the managed provider.");

    private readonly record struct EndRecord(
        long Offset,
        ushort DiskNumber,
        ushort CentralDirectoryDisk,
        ushort EntriesOnDisk,
        ushort TotalEntries,
        uint CentralDirectorySize,
        uint CentralDirectoryOffset);

    private readonly record struct CentralDirectoryLayout(
        ulong EntryCount,
        long Offset,
        long End);
}
