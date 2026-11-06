using System.Runtime.ExceptionServices;
using RainArchives.Contracts;

namespace RainArchives.Client;

/// <summary>
/// Materializes bounded complete-entry leases into private delete-on-close files
/// and exposes only read-only seekable streams to presentation clients.
/// </summary>
public sealed class ArchivePreviewCache : IAsyncDisposable
{
    public const long DefaultMaximumActiveBytes = 512L * 1024 * 1024;
    public const int DefaultMaximumActiveLeases = ProtocolConstants.MaximumEntryStreamLeases;
    private const int CopyBufferBytes = 1024 * 1024;
    private const int MaximumCreateAttempts = 8;
    private readonly IArchiveClient _client;
    private readonly string _cacheDirectory;
    private readonly long _maximumActiveBytes;
    private readonly SemaphoreSlim _leaseSlots;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private readonly object _disposeGate = new();
    private readonly Dictionary<Guid, ArchivePreviewLease> _leases = [];
    private TaskCompletionSource? _inFlightDrained;
    private Task? _disposeTask;
    private long _reservedBytes;
    private int _inFlight;
    private bool _disposed;

    public ArchivePreviewCache(
        IArchiveClient client,
        string cacheDirectory,
        long maximumActiveBytes = DefaultMaximumActiveBytes,
        int maximumActiveLeases = DefaultMaximumActiveLeases)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumActiveBytes);
        if (maximumActiveLeases is <= 0 or > ProtocolConstants.MaximumEntryStreamLeases)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumActiveLeases),
                $"Preview cache leases must be between 1 and {ProtocolConstants.MaximumEntryStreamLeases}.");
        }

        var canonicalDirectory = Path.GetFullPath(cacheDirectory);
        EnsureCacheDirectoryIsUsable(canonicalDirectory, constructorValidation: true);
        _client = client;
        _cacheDirectory = canonicalDirectory;
        _maximumActiveBytes = maximumActiveBytes;
        _leaseSlots = new SemaphoreSlim(maximumActiveLeases, maximumActiveLeases);
    }

    public int ActiveLeaseCount
    {
        get
        {
            lock (_gate)
            {
                return _leases.Count;
            }
        }
    }

    public long ReservedBytes
    {
        get
        {
            lock (_gate)
            {
                return _reservedBytes;
            }
        }
    }

    public Task<ArchivePreviewLease> MaterializeAsync(
        ArchiveSessionId sessionId,
        EntryId entryId,
        long maximumBytes = ProtocolConstants.DefaultEntryStreamBytes,
        Guid? passwordSlotId = null,
        CancellationToken cancellationToken = default)
    {
        if (sessionId.Value == Guid.Empty)
        {
            throw new ArgumentException("Archive session identifier cannot be empty.", nameof(sessionId));
        }

        if (entryId.Value == Guid.Empty)
        {
            throw new ArgumentException("Archive entry identifier cannot be empty.", nameof(entryId));
        }

        if (maximumBytes is <= 0 or > ProtocolConstants.MaximumEntryStreamBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumBytes),
                $"Preview byte limits must be between 1 and {ProtocolConstants.MaximumEntryStreamBytes:N0} bytes.");
        }

        BeginMaterialization();
        return MaterializeTrackedAsync(
            sessionId,
            entryId,
            maximumBytes,
            passwordSlotId,
            cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task<ArchivePreviewLease> MaterializeTrackedAsync(
        ArchiveSessionId sessionId,
        EntryId entryId,
        long maximumBytes,
        Guid? passwordSlotId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await MaterializeCoreAsync(
                    sessionId,
                    entryId,
                    maximumBytes,
                    passwordSlotId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            EndMaterialization();
        }
    }

    private async Task<ArchivePreviewLease> MaterializeCoreAsync(
        ArchiveSessionId sessionId,
        EntryId entryId,
        long maximumBytes,
        Guid? passwordSlotId,
        CancellationToken cancellationToken)
    {
        if (!_leaseSlots.Wait(0))
        {
            throw CacheLimit(
                "preview.cache_lease_limit",
                "The seekable preview cache has reached its active lease limit.");
        }

        var slotHeld = true;
        var reservedBytes = 0L;
        var reservationHeld = false;
        FileStream? deleteOwner = null;
        ArchiveEntryStreamLease? sourceLease = null;
        ArchivePreviewLease? result = null;
        Exception? operationFailure = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token);
        try
        {
            EnsureCacheDirectoryIsUsable(_cacheDirectory, constructorValidation: false);
            deleteOwner = CreateDeleteOnCloseFile();
            sourceLease = await _client.OpenEntryStreamAsync(
                    sessionId,
                    entryId,
                    maximumBytes,
                    passwordSlotId,
                    linked.Token)
                .ConfigureAwait(false);
            reservedBytes = sourceLease.DeclaredLength ?? maximumBytes;
            ReserveBytes(reservedBytes);
            reservationHeld = true;

            await sourceLease.Content.CopyToAsync(
                    deleteOwner,
                    CopyBufferBytes,
                    linked.Token)
                .ConfigureAwait(false);
            await deleteOwner.FlushAsync(linked.Token).ConfigureAwait(false);
            var materializedBytes = deleteOwner.Length;
            if (sourceLease.DeclaredLength is { } declaredLength
                && materializedBytes != declaredLength)
            {
                throw new ArchiveClientException(new ArchiveError(
                    "preview.cache_size_mismatch",
                    ArchiveErrorCategory.InvalidArchive,
                    "The preview payload length did not match the archive entry metadata.",
                    EntryId: entryId));
            }

            if (materializedBytes > reservedBytes)
            {
                throw new ArchiveClientException(new ArchiveError(
                    "preview.cache_byte_limit",
                    ArchiveErrorCategory.ResourceLimit,
                    "The preview payload exceeded its reserved cache budget.",
                    EntryId: entryId));
            }

            await sourceLease.DisposeAsync().ConfigureAwait(false);
            sourceLease = null;
            deleteOwner.Position = 0;
            var leaseId = Guid.NewGuid();
            var lease = new ArchivePreviewLease(
                sessionId,
                entryId,
                materializedBytes,
                deleteOwner,
                () => ReleaseLease(leaseId, materializedBytes));
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _reservedBytes -= reservedBytes - materializedBytes;
                reservedBytes = materializedBytes;
                _leases.Add(leaseId, lease);
                deleteOwner = null;
                reservedBytes = 0;
                reservationHeld = false;
                slotHeld = false;
            }

            result = lease;
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested
            && _shutdown.IsCancellationRequested)
        {
            operationFailure = new ObjectDisposedException(nameof(ArchivePreviewCache));
        }
        catch (Exception exception)
        {
            operationFailure = exception;
        }

        Exception? cleanupFailure = null;
        try
        {
            if (sourceLease is not null)
            {
                await sourceLease.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }

        try
        {
            if (deleteOwner is not null)
            {
                await deleteOwner.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            cleanupFailure = CombineFailures(cleanupFailure, exception);
        }

        try
        {
            if (reservationHeld)
            {
                ReleaseReservedBytes(reservedBytes);
            }
        }
        catch (Exception exception)
        {
            cleanupFailure = CombineFailures(cleanupFailure, exception);
        }

        try
        {
            if (slotHeld)
            {
                _leaseSlots.Release();
            }
        }
        catch (Exception exception)
        {
            cleanupFailure = CombineFailures(cleanupFailure, exception);
        }

        if (operationFailure is not null)
        {
            if (cleanupFailure is not null)
            {
                throw new AggregateException(
                    "Preview materialization and cleanup both failed.",
                    operationFailure,
                    cleanupFailure);
            }

            ExceptionDispatchInfo.Capture(operationFailure).Throw();
        }

        if (cleanupFailure is not null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }

        return result
            ?? throw new InvalidOperationException("Preview materialization completed without a lease.");
    }

    private async Task DisposeCoreAsync()
    {
        Task inFlightDrained;
        lock (_gate)
        {
            _disposed = true;
            inFlightDrained = _inFlight == 0
                ? Task.CompletedTask
                : (_inFlightDrained ??= new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        await inFlightDrained.ConfigureAwait(false);

        ArchivePreviewLease[] leases;
        lock (_gate)
        {
            leases = _leases.Values.ToArray();
        }

        Exception? failure = null;
        foreach (var lease in leases)
        {
            try
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }

        _leaseSlots.Dispose();
        _shutdown.Dispose();
        if (failure is not null)
        {
            throw failure;
        }
    }

    private FileStream CreateDeleteOnCloseFile()
    {
        for (var attempt = 0; attempt < MaximumCreateAttempts; attempt++)
        {
            var path = Path.Combine(
                _cacheDirectory,
                $"rainarchives-preview-{Guid.NewGuid():N}.cache");
            try
            {
                return new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    CopyBufferBytes,
                    FileOptions.Asynchronous
                        | FileOptions.RandomAccess
                        | FileOptions.DeleteOnClose);
            }
            catch (IOException) when (File.Exists(path))
            {
                // A random collision is harmless; retry with another unguessable name.
            }
        }

        throw new IOException("Could not allocate a unique seekable preview cache file.");
    }

    private void BeginMaterialization()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            checked
            {
                _inFlight++;
            }
        }
    }

    private void EndMaterialization()
    {
        lock (_gate)
        {
            if (_inFlight <= 0)
            {
                throw new InvalidOperationException("The preview-cache materialization count is inconsistent.");
            }

            _inFlight--;
            if (_inFlight == 0)
            {
                _inFlightDrained?.TrySetResult();
            }
        }
    }

    private void ReserveBytes(long bytes)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (bytes > _maximumActiveBytes - _reservedBytes)
            {
                throw CacheLimit(
                    "preview.cache_byte_limit",
                    "The seekable preview cache has reached its active byte limit.");
            }

            _reservedBytes += bytes;
        }
    }

    private void ReleaseReservedBytes(long bytes)
    {
        lock (_gate)
        {
            if (bytes < 0 || bytes > _reservedBytes)
            {
                throw new InvalidOperationException("The preview-cache byte reservation is inconsistent.");
            }

            _reservedBytes -= bytes;
        }
    }

    private void ReleaseLease(Guid leaseId, long bytes)
    {
        var removed = false;
        lock (_gate)
        {
            if (_leases.Remove(leaseId))
            {
                if (bytes < 0 || bytes > _reservedBytes)
                {
                    throw new InvalidOperationException("The preview-cache lease budget is inconsistent.");
                }

                _reservedBytes -= bytes;
                removed = true;
            }
        }

        if (removed)
        {
            _leaseSlots.Release();
        }
    }

    private static void EnsureCacheDirectoryIsUsable(
        string cacheDirectory,
        bool constructorValidation)
    {
        if (!Directory.Exists(cacheDirectory))
        {
            if (constructorValidation)
            {
                throw new DirectoryNotFoundException("The seekable preview cache directory does not exist.");
            }

            throw new ArchiveClientException(new ArchiveError(
                "preview.cache_directory_changed",
                ArchiveErrorCategory.DestinationConflict,
                "The seekable preview cache directory is no longer available."));
        }

        if (File.GetAttributes(cacheDirectory).HasFlag(FileAttributes.ReparsePoint))
        {
            if (constructorValidation)
            {
                throw new ArgumentException(
                    "The seekable preview cache directory must not be a reparse point.",
                    nameof(cacheDirectory));
            }

            throw new ArchiveClientException(new ArchiveError(
                "preview.cache_directory_changed",
                ArchiveErrorCategory.DestinationConflict,
                "The seekable preview cache directory changed into a reparse point."));
        }
    }

    private static ArchiveClientException CacheLimit(string code, string message) =>
        new(new ArchiveError(
            code,
            ArchiveErrorCategory.ResourceLimit,
            message,
            IsRetryable: true));

    private static Exception CombineFailures(Exception? first, Exception next) =>
        first is null
            ? next
            : new AggregateException("Multiple preview-cache cleanup steps failed.", first, next);
}
