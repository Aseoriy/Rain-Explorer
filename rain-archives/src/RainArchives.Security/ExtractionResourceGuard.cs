using System.ComponentModel;
using System.Runtime.InteropServices;
using RainArchives.Contracts;
using RainArchives.Core;

namespace RainArchives.Security;

public enum ExtractionObjectKind
{
    Directory,
    File,
    ProvenanceStream,
}

public sealed class ExtractionResourceGuard
{
    private readonly ExtractionSecurityPolicy _policy;
    private readonly IAvailableDiskSpace _diskSpace;

    public ExtractionResourceGuard(ExtractionSecurityPolicy? policy = null)
        : this(policy, SystemAvailableDiskSpace.Instance)
    {
    }

    internal ExtractionResourceGuard(
        ExtractionSecurityPolicy? policy,
        IAvailableDiskSpace diskSpace)
    {
        _policy = policy ?? new ExtractionSecurityPolicy();
        _diskSpace = diskSpace ?? throw new ArgumentNullException(nameof(diskSpace));
    }

    public void Validate(
        ExtractionPathPlan plan,
        string destinationParent,
        bool includeProvenance = false)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationParent);
        ExtractionResourceGuard.ValidateObjectCharges(_policy);

        if (plan.Entries.Count > _policy.DefaultMaximumEntries)
        {
            throw ResourceLimit(
                "resource.entry_count",
                $"The operation contains more than {_policy.DefaultMaximumEntries:N0} entries and needs explicit high-entry-count approval.");
        }

        long metadataEstimate = 0;
        long totalOutput = 0;
        long totalCompressed = 0;
        var directoryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            string.Empty,
        };
        long fileCount = 0;
        long requiredDiskBytes;
        var allOutputSizesKnown = true;
        var allCompressedSizesKnown = true;
        try
        {
            foreach (var entry in plan.Entries)
            {
                metadataEstimate = checked(metadataEstimate + 256 + (entry.RelativePath.Length * sizeof(char)));
                AddDirectoryPaths(
                    directoryPaths,
                    entry.CollisionKey,
                    includeLeaf: entry.IndexedEntry.Descriptor.Kind == ArchiveEntryKind.Directory);
                if (entry.IndexedEntry.Descriptor.Kind != ArchiveEntryKind.Directory)
                {
                    fileCount = checked(fileCount + 1);
                }

                if (entry.IndexedEntry.Descriptor.OriginalSize is { } outputSize and >= 0)
                {
                    totalOutput = checked(totalOutput + outputSize);
                }
                else
                {
                    allOutputSizesKnown = false;
                }

                if (entry.IndexedEntry.Descriptor.CompressedSize is { } compressedSize and >= 0)
                {
                    totalCompressed = checked(totalCompressed + compressedSize);
                }
                else
                {
                    allCompressedSizesKnown = false;
                }
            }

            var directoryCharge = checked((long)directoryPaths.Count * _policy.DirectoryObjectChargeBytes);
            var fileCharge = checked(fileCount * _policy.FileObjectChargeBytes);
            var provenanceCharge = includeProvenance
                ? checked(fileCount * _policy.ProvenanceStreamChargeBytes)
                : 0;
            var objectCharge = checked(directoryCharge + fileCharge + provenanceCharge);
            requiredDiskBytes = allOutputSizesKnown
                ? checked(totalOutput + objectCharge)
                : objectCharge;
        }
        catch (OverflowException)
        {
            throw ResourceLimit("resource.metadata_overflow", "Archive metadata overflowed safe accounting limits.");
        }

        if (metadataEstimate > _policy.MetadataBudgetBytes)
        {
            throw ResourceLimit("resource.metadata_budget", "Archive metadata exceeds the configured memory budget.");
        }

        if (allOutputSizesKnown && totalOutput > _policy.DefaultMaximumOutputBytes)
        {
            throw ResourceLimit(
                "resource.output_limit",
                "The declared output exceeds the default extraction limit and requires explicit risk approval.");
        }

        if (allOutputSizesKnown
            && allCompressedSizesKnown
            && totalOutput >= _policy.ExpansionRatioMinimumOutputBytes
            && (totalCompressed == 0 || (double)totalOutput / totalCompressed > _policy.ExpansionRatioWarning))
        {
            throw ResourceLimit(
                "resource.expansion_ratio",
                "The archive exceeds the default expansion-ratio limit and requires explicit risk approval.");
        }

        if (requiredDiskBytes >= 0)
        {
            var root = Path.GetPathRoot(Path.GetFullPath(destinationParent));
            if (!string.IsNullOrEmpty(root))
            {
                var available = _diskSpace.GetAvailableFreeSpace(root);
                var reserve = Math.Max(
                    _policy.MinimumDiskReserveBytes,
                    (long)(available * _policy.MinimumDiskReserveFraction));
                if (requiredDiskBytes > available - reserve)
                {
                    throw new ArchiveProviderException(
                        ArchiveErrorCategory.InsufficientSpace,
                        "resource.insufficient_space",
                        "The destination does not have enough available space while preserving the configured safety reserve.");
                }
            }
        }
    }

    public void ValidateRead(IReadOnlyCollection<IndexedArchiveEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count > _policy.DefaultMaximumEntries)
        {
            throw ResourceLimit(
                "resource.entry_count",
                $"The operation contains more than {_policy.DefaultMaximumEntries:N0} entries and needs explicit high-entry-count approval.");
        }

        long metadataEstimate = 0;
        long totalOutput = 0;
        long totalCompressed = 0;
        var allOutputSizesKnown = true;
        var allCompressedSizesKnown = true;
        try
        {
            foreach (var entry in entries)
            {
                var descriptor = entry.Descriptor;
                metadataEstimate = checked(metadataEstimate + 256 + (descriptor.RawPath.Length * sizeof(char)));
                if (descriptor.OriginalSize is { } outputSize and >= 0)
                {
                    totalOutput = checked(totalOutput + outputSize);
                }
                else
                {
                    allOutputSizesKnown = false;
                }

                if (descriptor.CompressedSize is { } compressedSize and >= 0)
                {
                    totalCompressed = checked(totalCompressed + compressedSize);
                }
                else
                {
                    allCompressedSizesKnown = false;
                }
            }
        }
        catch (OverflowException)
        {
            throw ResourceLimit("resource.metadata_overflow", "Archive metadata overflowed safe accounting limits.");
        }

        if (metadataEstimate > _policy.MetadataBudgetBytes)
        {
            throw ResourceLimit("resource.metadata_budget", "Archive metadata exceeds the configured memory budget.");
        }

        if (allOutputSizesKnown && totalOutput > _policy.DefaultMaximumOutputBytes)
        {
            throw ResourceLimit(
                "resource.output_limit",
                "The declared decoded output exceeds the default read limit and requires explicit risk approval.");
        }

        if (allOutputSizesKnown
            && allCompressedSizesKnown
            && totalOutput >= _policy.ExpansionRatioMinimumOutputBytes
            && (totalCompressed == 0 || (double)totalOutput / totalCompressed > _policy.ExpansionRatioWarning))
        {
            throw ResourceLimit(
                "resource.expansion_ratio",
                "The archive exceeds the default expansion-ratio limit and requires explicit risk approval.");
        }
    }

    public ExtractionRuntimeGuard CreateRuntimeGuard(
        string? destinationParent = null,
        long? declaredCompressedBytes = null) =>
        new(_policy, _diskSpace, destinationParent, declaredCompressedBytes);

    private static void AddDirectoryPaths(
        HashSet<string> directoryPaths,
        string relativePath,
        bool includeLeaf)
    {
        var componentStart = 0;
        while (true)
        {
            var separator = relativePath.IndexOf('/', componentStart);
            if (separator < 0)
            {
                break;
            }

            directoryPaths.Add(relativePath[..separator]);
            componentStart = separator + 1;
        }

        if (includeLeaf && relativePath.Length > 0)
        {
            directoryPaths.Add(relativePath);
        }
    }

    internal static void ValidateObjectCharges(ExtractionSecurityPolicy policy)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(policy.FileObjectChargeBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(policy.DirectoryObjectChargeBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(policy.ProvenanceStreamChargeBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(policy.DiskSpaceCheckIntervalObjects, 1);
    }

    private static ArchiveProviderException ResourceLimit(string code, string message) =>
        new(ArchiveErrorCategory.ResourceLimit, code, message);
}

public sealed class ExtractionRuntimeGuard
{
    private readonly ExtractionSecurityPolicy _policy;
    private readonly IAvailableDiskSpace _diskSpace;
    private readonly string? _driveRoot;
    private readonly long? _declaredCompressedBytes;
    private readonly HashSet<string> _chargedDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _chargedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _chargedProvenanceStreams = new(StringComparer.OrdinalIgnoreCase);
    private long _filesystemObjectBytes;
    private int _filesystemObjectsSinceSpaceCheck;
    private long _lastSpaceCheckAtBytes;
    private long _nextSpaceCheckAtBytes;

    internal ExtractionRuntimeGuard(
        ExtractionSecurityPolicy policy,
        IAvailableDiskSpace diskSpace,
        string? destinationParent,
        long? declaredCompressedBytes)
    {
        _policy = policy;
        _diskSpace = diskSpace;
        ExtractionResourceGuard.ValidateObjectCharges(_policy);
        _declaredCompressedBytes = declaredCompressedBytes is >= 0
            ? declaredCompressedBytes
            : null;
        if (!string.IsNullOrWhiteSpace(destinationParent))
        {
            var root = Path.GetPathRoot(Path.GetFullPath(destinationParent));
            if (!string.IsNullOrEmpty(root))
            {
                _driveRoot = root;
            }
        }
    }

    public void ValidateObjectCreation(
        long totalBytesProcessed,
        ExtractionObjectKind objectKind,
        string relativePath = "")
    {
        if (totalBytesProcessed < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalBytesProcessed));
        }

        ArgumentNullException.ThrowIfNull(relativePath);
        var normalizedPath = relativePath.Replace('\\', '/');
        var newDirectories = new List<string>();
        var candidateDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newFiles = 0;
        var newProvenanceStreams = 0;

        switch (objectKind)
        {
            case ExtractionObjectKind.Directory:
                if (normalizedPath.Length == 0)
                {
                    AddNewDirectory(string.Empty, candidateDirectories, newDirectories);
                    break;
                }

                AddNewDirectoryPaths(
                    normalizedPath,
                    includeLeaf: true,
                    candidateDirectories,
                    newDirectories);
                break;
            case ExtractionObjectKind.File:
                if (normalizedPath.Length == 0)
                {
                    throw new ArgumentException("A file object requires a relative path.", nameof(relativePath));
                }

                AddNewDirectoryPaths(
                    normalizedPath,
                    includeLeaf: false,
                    candidateDirectories,
                    newDirectories);
                if (_chargedFiles.Contains(normalizedPath) == false)
                {
                    newFiles = 1;
                }

                break;
            case ExtractionObjectKind.ProvenanceStream:
                if (normalizedPath.Length == 0)
                {
                    throw new ArgumentException(
                        "A provenance stream requires the relative path of its file.",
                        nameof(relativePath));
                }

                if (_chargedProvenanceStreams.Contains(normalizedPath) == false)
                {
                    newProvenanceStreams = 1;
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(objectKind));
        }

        try
        {
            var directoryCharge = checked(
                (long)newDirectories.Count * _policy.DirectoryObjectChargeBytes);
            var fileCharge = checked((long)newFiles * _policy.FileObjectChargeBytes);
            var provenanceCharge = checked(
                (long)newProvenanceStreams * _policy.ProvenanceStreamChargeBytes);
            var nextObjectBytes = checked(
                _filesystemObjectBytes + directoryCharge + fileCharge + provenanceCharge);
            var nextAccountedBytes = checked(totalBytesProcessed + nextObjectBytes);
            var newObjectCount = checked(newDirectories.Count + newFiles + newProvenanceStreams);
            var nextObjectsSinceSpaceCheck = checked(
                _filesystemObjectsSinceSpaceCheck + newObjectCount);
            if (nextObjectBytes != _filesystemObjectBytes)
            {
                var checkedSpace = ValidateAvailableSpace(
                    nextAccountedBytes,
                    nextObjectsSinceSpaceCheck >= _policy.DiskSpaceCheckIntervalObjects);
                _filesystemObjectsSinceSpaceCheck = checkedSpace
                    ? 0
                    : nextObjectsSinceSpaceCheck;
            }

            _filesystemObjectBytes = nextObjectBytes;
            foreach (var directory in newDirectories)
            {
                _chargedDirectories.Add(directory);
            }

            if (newFiles != 0)
            {
                _chargedFiles.Add(normalizedPath);
            }

            if (newProvenanceStreams != 0)
            {
                _chargedProvenanceStreams.Add(normalizedPath);
            }
        }
        catch (OverflowException)
        {
            throw ResourceLimit("resource.output_overflow", "Filesystem object accounting overflowed.");
        }
    }

    public void ValidateNextChunk(
        long totalBytesProcessed,
        long entryBytesProcessed,
        int nextChunkBytes,
        long? declaredEntrySize)
    {
        if (totalBytesProcessed < 0 || entryBytesProcessed < 0 || nextChunkBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalBytesProcessed));
        }

        long nextTotal;
        long nextEntry;
        long nextAccounted;
        try
        {
            nextTotal = checked(totalBytesProcessed + nextChunkBytes);
            nextEntry = checked(entryBytesProcessed + nextChunkBytes);
            nextAccounted = checked(nextTotal + _filesystemObjectBytes);
        }
        catch (OverflowException)
        {
            throw ResourceLimit("resource.output_overflow", "Decoded byte accounting overflowed.");
        }

        if (nextTotal > _policy.DefaultMaximumOutputBytes)
        {
            throw ResourceLimit(
                "resource.output_limit",
                "Decoded output exceeded the default operation limit and requires explicit risk approval.");
        }

        if (_declaredCompressedBytes is { } compressed
            && nextTotal >= _policy.ExpansionRatioMinimumOutputBytes
            && (compressed == 0 || (double)nextTotal / compressed > _policy.ExpansionRatioWarning))
        {
            throw ResourceLimit(
                "resource.expansion_ratio",
                "Actual decoded output exceeded the default expansion-ratio limit and requires explicit risk approval.");
        }

        if (declaredEntrySize is null && nextEntry > _policy.MaximumUnknownEntryOutputBytes)
        {
            throw ResourceLimit(
                "resource.unknown_entry_limit",
                "An entry with no trustworthy size exceeded the default decoded-output limit.");
        }

        if (declaredEntrySize is { } declared && nextEntry > declared)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.CorruptArchive,
                "archive.entry_exceeded_declared_size",
                "An entry produced more data than its declared uncompressed size.");
        }

        if (ValidateAvailableSpace(nextAccounted))
        {
            _filesystemObjectsSinceSpaceCheck = 0;
        }
    }

    public void ValidateCompletedEntry(long actualEntryBytes, long? declaredEntrySize)
    {
        if (actualEntryBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(actualEntryBytes));
        }

        if (declaredEntrySize is { } declared
            && declared >= 0
            && actualEntryBytes != declared)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.CorruptArchive,
                "archive.entry_size_mismatch",
                "An entry produced a different number of bytes than its declared uncompressed size.");
        }
    }

    private static ArchiveProviderException ResourceLimit(string code, string message) =>
        new(ArchiveErrorCategory.ResourceLimit, code, message);

    private void AddNewDirectoryPaths(
        string relativePath,
        bool includeLeaf,
        HashSet<string> candidateDirectories,
        List<string> newDirectories)
    {
        var componentStart = 0;
        while (true)
        {
            var separator = relativePath.IndexOf('/', componentStart);
            if (separator < 0)
            {
                break;
            }

            AddNewDirectory(relativePath[..separator], candidateDirectories, newDirectories);
            componentStart = separator + 1;
        }

        if (includeLeaf && relativePath.Length > 0)
        {
            AddNewDirectory(relativePath, candidateDirectories, newDirectories);
        }
    }

    private void AddNewDirectory(
        string path,
        HashSet<string> candidateDirectories,
        List<string> newDirectories)
    {
        if (!_chargedDirectories.Contains(path) && candidateDirectories.Add(path))
        {
            newDirectories.Add(path);
        }
    }

    private bool ValidateAvailableSpace(long nextAccountedBytes, bool force = false)
    {
        if (_driveRoot is null || (!force && nextAccountedBytes < _nextSpaceCheckAtBytes))
        {
            return false;
        }

        var available = _diskSpace.GetAvailableFreeSpace(_driveRoot);
        var reserve = Math.Max(
            _policy.MinimumDiskReserveBytes,
            (long)(available * _policy.MinimumDiskReserveFraction));
        var writableBeforeReserve = available - reserve;
        var pendingBytes = checked(nextAccountedBytes - _lastSpaceCheckAtBytes);
        if (pendingBytes > writableBeforeReserve)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InsufficientSpace,
                "resource.insufficient_space_during_write",
                "The destination ran below the configured free-space reserve during extraction.");
        }

        var safeHeadroomAfterWrite = writableBeforeReserve - pendingBytes;
        var nextInterval = Math.Max(
            1,
            Math.Min(_policy.DiskSpaceCheckIntervalBytes, safeHeadroomAfterWrite));
        _lastSpaceCheckAtBytes = nextAccountedBytes;
        _nextSpaceCheckAtBytes = nextAccountedBytes > long.MaxValue - nextInterval
            ? long.MaxValue
            : nextAccountedBytes + nextInterval;
        return true;
    }
}

internal interface IAvailableDiskSpace
{
    long GetAvailableFreeSpace(string driveRoot);
}

internal sealed partial class SystemAvailableDiskSpace : IAvailableDiskSpace
{
    public static SystemAvailableDiskSpace Instance { get; } = new();

    private SystemAvailableDiskSpace()
    {
    }

    public long GetAvailableFreeSpace(string driveRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driveRoot);
        if (!OperatingSystem.IsWindows())
        {
            return new DriveInfo(driveRoot).AvailableFreeSpace;
        }

        var queryPath = NormalizeQueryPath(driveRoot);
        if (!GetDiskFreeSpaceEx(
                queryPath,
                out var availableBytes,
                out _,
                out _))
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "Could not query available destination space.");
        }

        return availableBytes > long.MaxValue
            ? long.MaxValue
            : checked((long)availableBytes);
    }

    internal static string NormalizeQueryPath(string driveRoot) =>
        Path.EndsInDirectorySeparator(driveRoot)
            ? driveRoot
            : driveRoot + Path.DirectorySeparatorChar;

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "GetDiskFreeSpaceExW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceEx(
        string directoryName,
        out ulong freeBytesAvailableToCaller,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);
}
