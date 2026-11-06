using RainArchives.Contracts;
using RainArchives.Core;

namespace RainArchives.Security;

public sealed class ArchiveCreationResourceGuard
{
    private const long DiskCheckIntervalBytes = 256L * 1024 * 1024;
    private const long MinimumDiskReserveBytes = 512L * 1024 * 1024;
    private const double MinimumDiskReserveFraction = 0.05;
    private readonly IAvailableDiskSpace _diskSpace;

    public ArchiveCreationResourceGuard()
        : this(SystemAvailableDiskSpace.Instance)
    {
    }

    internal ArchiveCreationResourceGuard(IAvailableDiskSpace diskSpace)
    {
        _diskSpace = diskSpace ?? throw new ArgumentNullException(nameof(diskSpace));
    }

    public ArchiveCreationOutputGuard CreateOutputGuard(
        string destinationParent,
        long maximumExpectedOutputBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationParent);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumExpectedOutputBytes);
        var driveRoot = Path.GetPathRoot(Path.GetFullPath(destinationParent));
        if (string.IsNullOrEmpty(driveRoot))
        {
            throw new IOException("The archive destination parent has no filesystem root.");
        }

        ValidatePreflight(driveRoot, maximumExpectedOutputBytes);
        return new ArchiveCreationOutputGuard(
            _diskSpace,
            driveRoot,
            maximumExpectedOutputBytes,
            DiskCheckIntervalBytes,
            MinimumDiskReserveBytes,
            MinimumDiskReserveFraction);
    }

    private void ValidatePreflight(string driveRoot, long requiredBytes)
    {
        var available = _diskSpace.GetAvailableFreeSpace(driveRoot);
        var reserve = Math.Max(
            MinimumDiskReserveBytes,
            (long)(available * MinimumDiskReserveFraction));
        if (requiredBytes > available - reserve)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InsufficientSpace,
                "creation.insufficient_space",
                "The archive destination does not have enough free space while preserving the safety reserve.");
        }
    }
}

public sealed class ArchiveCreationOutputGuard
{
    private readonly IAvailableDiskSpace _diskSpace;
    private readonly string _driveRoot;
    private readonly long _maximumBytes;
    private readonly long _checkIntervalBytes;
    private readonly long _minimumReserveBytes;
    private readonly double _minimumReserveFraction;
    private long _lastSpaceCheckBytes;
    private long _nextSpaceCheckBytes;

    internal ArchiveCreationOutputGuard(
        IAvailableDiskSpace diskSpace,
        string driveRoot,
        long maximumBytes,
        long checkIntervalBytes,
        long minimumReserveBytes,
        double minimumReserveFraction)
    {
        _diskSpace = diskSpace;
        _driveRoot = driveRoot;
        _maximumBytes = maximumBytes;
        _checkIntervalBytes = checkIntervalBytes;
        _minimumReserveBytes = minimumReserveBytes;
        _minimumReserveFraction = minimumReserveFraction;
    }

    public void ValidateNextChunk(long bytesWritten, int nextChunkBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytesWritten);
        ArgumentOutOfRangeException.ThrowIfNegative(nextChunkBytes);
        long next;
        try
        {
            next = checked(bytesWritten + nextChunkBytes);
        }
        catch (OverflowException exception)
        {
            throw ResourceLimit("creation.output_overflow", "Archive output accounting overflowed.", exception);
        }

        if (next > _maximumBytes)
        {
            throw ResourceLimit(
                "creation.output_limit",
                "The archive writer exceeded its conservative bounded output estimate.");
        }

        if (next < _nextSpaceCheckBytes)
        {
            return;
        }

        var available = _diskSpace.GetAvailableFreeSpace(_driveRoot);
        var reserve = Math.Max(
            _minimumReserveBytes,
            (long)(available * _minimumReserveFraction));
        var pendingBytes = checked(next - _lastSpaceCheckBytes);
        if (pendingBytes > available - reserve)
        {
            throw new ArchiveProviderException(
                ArchiveErrorCategory.InsufficientSpace,
                "creation.insufficient_space_during_write",
                "The archive destination ran below the configured free-space reserve during creation.");
        }

        var safeHeadroom = available - reserve - pendingBytes;
        var interval = Math.Max(1, Math.Min(_checkIntervalBytes, safeHeadroom));
        _lastSpaceCheckBytes = next;
        _nextSpaceCheckBytes = next > long.MaxValue - interval
            ? long.MaxValue
            : next + interval;
    }

    private static ArchiveProviderException ResourceLimit(
        string code,
        string message,
        Exception? innerException = null) =>
        new(ArchiveErrorCategory.ResourceLimit, code, message, innerException);
}
