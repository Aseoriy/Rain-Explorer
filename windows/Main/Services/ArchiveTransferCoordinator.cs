using System.IO;
using RainExplorer.Models;

namespace RainExplorer.Services;

internal enum ArchiveTransferOutcome
{
    Ready,
    Cancelled,
    Busy,
    InvalidSelection,
    DestinationConflict,
    Unsupported,
    Stale,
    Offline,
    UnsafeOutput,
    Failed,
    Preserved,
    CleanupIncomplete,
}

internal enum ArchiveTransferReleaseReason
{
    DragCompleted,
    TargetRefused,
    TargetException,
    ClipboardReplaced,
    SelectionChanged,
    Navigation,
    SourceChanged,
    WorkerLost,
    TabClosed,
    ApplicationShutdown,
}

internal sealed record ArchiveTransferRenderResult(
    ArchiveTransferOutcome Outcome,
    IReadOnlyList<string> FileDropPaths,
    bool CopyOnly,
    string Status)
{
    public bool IsReady => Outcome == ArchiveTransferOutcome.Ready;

    public static ArchiveTransferRenderResult Fixed(ArchiveTransferOutcome outcome) =>
        new(outcome, Array.Empty<string>(), true, ArchiveTransferStatus.For(outcome));
}

internal delegate Task<ArchiveOperationResult> ArchiveTransferExtraction(
    ArchiveTransferAllocation allocation,
    ArchiveTransferStorage storage,
    ArchiveTransferExtractionProgress progress,
    char[]? password,
    CancellationToken cancellationToken);

internal enum ArchiveTransferOutputOwnership
{
    None,
    Uncertain,
    ConfirmedPublished,
    PreservationRequired,
}

internal sealed class ArchiveTransferExtractionProgress
{
    private readonly object _sync = new();
    private ArchiveTransferSelectionManifest? _selection;
    private ArchiveTransferManifest? _manifest;
    private ArchiveTransferOutputOwnership _outputOwnership;

    public void BindSelection(ArchiveTransferSelectionManifest selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var immutable = new ArchiveTransferSelectionManifest(
            selection.ExpectedObjects.ToArray(),
            selection.PublishedRelativePaths.ToArray());
        lock (_sync)
        {
            if (_selection is not null)
                throw new InvalidOperationException("Archive-transfer selection authority was already bound.");
            _selection = immutable;
        }
    }

    public void MarkStartAttempted()
    {
        lock (_sync)
        {
            if (_selection is null)
                throw new InvalidOperationException("Archive-transfer start preceded selection authority.");
            if (_outputOwnership != ArchiveTransferOutputOwnership.None)
                throw new InvalidOperationException("Archive-transfer start ownership was already recorded.");
            _outputOwnership = ArchiveTransferOutputOwnership.Uncertain;
        }
    }

    public void ConfirmPublished()
    {
        lock (_sync)
        {
            if (_selection is null
                || _outputOwnership != ArchiveTransferOutputOwnership.Uncertain)
                throw new InvalidOperationException("Archive-transfer publication lacked an attempted start.");
            _outputOwnership = ArchiveTransferOutputOwnership.ConfirmedPublished;
        }
    }

    public void SetReviewedManifest(ArchiveTransferManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        lock (_sync)
        {
            if (_outputOwnership != ArchiveTransferOutputOwnership.ConfirmedPublished
                || _selection is null)
                throw new InvalidOperationException("Archive-transfer output was reviewed before publication authority.");
            if (_manifest is not null)
                throw new InvalidOperationException("Archive-transfer output was reviewed more than once.");
            _manifest = manifest;
        }
    }

    public async Task<ArchiveTransferManifest> ReviewAndBindAsync(
        ArchiveTransferStorage storage,
        ArchiveTransferAllocation allocation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(allocation);
        ArchiveTransferSelectionManifest selection;
        lock (_sync)
        {
            if (_outputOwnership != ArchiveTransferOutputOwnership.ConfirmedPublished
                || _selection is null
                || _manifest is not null)
                throw new InvalidOperationException("Archive-transfer review lacked confirmed publication authority.");
            selection = _selection;
        }

        ArchiveTransferManifest reviewed;
        try
        {
            reviewed = await storage.ReviewCompletedAsync(
                    allocation, selection, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            RequirePreservation();
            throw;
        }
        SetReviewedManifest(reviewed);
        return reviewed;
    }

    public void RequirePreservation()
    {
        lock (_sync)
        {
            if (_outputOwnership != ArchiveTransferOutputOwnership.ConfirmedPublished
                || _manifest is not null)
                throw new InvalidOperationException("Archive-transfer preservation did not follow a failed first review.");
            _outputOwnership = ArchiveTransferOutputOwnership.PreservationRequired;
        }
    }


    private static bool IsFatal(Exception exception) => exception is
        OutOfMemoryException or AccessViolationException;

    public ArchiveTransferExtractionSnapshot Snapshot()
    {
        lock (_sync)
            return new ArchiveTransferExtractionSnapshot(_selection, _outputOwnership, _manifest);
    }
}

internal sealed record ArchiveTransferExtractionSnapshot(
    ArchiveTransferSelectionManifest? Selection,
    ArchiveTransferOutputOwnership OutputOwnership,
    ArchiveTransferManifest? Manifest);

internal static class ArchiveTransferStatus
{
    public static string For(ArchiveTransferOutcome outcome) => outcome switch
    {
        ArchiveTransferOutcome.Ready => "The selected archive items are ready to copy.",
        ArchiveTransferOutcome.Cancelled => "Archive copy cancelled; no retry was started.",
        ArchiveTransferOutcome.Busy => "Another archive operation already owns this tab.",
        ArchiveTransferOutcome.InvalidSelection => "The current visible archive selection is no longer valid.",
        ArchiveTransferOutcome.DestinationConflict => "The private archive-copy destination was not absent. Nothing was overwritten.",
        ArchiveTransferOutcome.Unsupported => "Archive copy is unavailable for the current session.",
        ArchiveTransferOutcome.Stale => "The archive source changed. Reopen it before copying.",
        ArchiveTransferOutcome.Offline => "The Rain Archives Worker is unavailable. Reopen the archive after reconnecting.",
        ArchiveTransferOutcome.UnsafeOutput => "Archive copy output did not match the exact bounded selection manifest.",
        ArchiveTransferOutcome.Preserved => "Archive copy evidence was preserved because exact safe cleanup could not be proven.",
        ArchiveTransferOutcome.CleanupIncomplete => "Archive copy cleanup stopped after removing one or more exact reviewed objects.",
        _ => "Rain Explorer could not prepare the selected archive items safely.",
    };
}

/// <summary>
/// Serializes delayed archive-transfer owners. Creating or probing an owner
/// performs no extraction. The first RenderAsync call owns cancellation and the
/// password buffer for the one shared render; all re-entrant calls share it.
/// </summary>
internal sealed class ArchiveTransferCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan CleanupReviewTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RenderDrainTimeout = TimeSpan.FromSeconds(10);
    private readonly object _sync = new();
    private readonly SemaphoreSlim _admissionGate = new(1, 1);
    private readonly ArchiveTransferStorage _storage;
    private readonly ArchiveTransferExtraction _extract;
    private readonly TimeSpan _renderDrainTimeout;
    private readonly Func<Task>? _beforeOwnerAdmission;
    private readonly HashSet<Task> _retired = [];
    private ArchiveTransferOwner? _active;
    private long _invalidationGeneration;
    private bool _disposed;

    internal bool HasOwnedWork
    {
        get
        {
            lock (_sync) return _active is not null || _retired.Count != 0;
        }
    }

    public ArchiveTransferCoordinator(
        ArchiveTransferExtraction extract,
        ArchiveTransferStorage? storage = null,
        TimeSpan? renderDrainTimeout = null,
        Func<Task>? beforeOwnerAdmission = null)
    {
        _extract = extract ?? throw new ArgumentNullException(nameof(extract));
        _storage = storage ?? new ArchiveTransferStorage();
        _renderDrainTimeout = renderDrainTimeout ?? RenderDrainTimeout;
        _beforeOwnerAdmission = beforeOwnerAdmission;
        if (_renderDrainTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(renderDrainTimeout));
    }

    public async Task<ArchiveTransferOwner?> BeginAsync(CancellationToken cancellationToken = default)
    {
        await _admissionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ArchiveTransferOwner? prior;
            long generation;
            lock (_sync)
            {
                if (_disposed) return null;
                generation = _invalidationGeneration;
                prior = _active;
                _active = null;
            }

            if (prior is not null)
                await RetireAsync(prior, ArchiveTransferReleaseReason.ClipboardReplaced, cancellationToken)
                    .ConfigureAwait(false);
            await DrainAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            if (_beforeOwnerAdmission is not null)
                await _beforeOwnerAdmission().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            lock (_sync)
            {
                if (_disposed || generation != _invalidationGeneration) return null;
                return _active = new ArchiveTransferOwner(this, _storage, _extract);
            }
        }
        finally
        {
            _admissionGate.Release();
        }
    }

    public Task InvalidateAsync(
        ArchiveTransferReleaseReason reason,
        CancellationToken cancellationToken = default)
    {
        ArchiveTransferOwner? owner;
        lock (_sync)
        {
            _invalidationGeneration++;
            owner = _active;
            _active = null;
        }
        return owner is null
            ? Task.CompletedTask
            : RetireAsync(owner, reason, cancellationToken);
    }

    public async Task DrainAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_sync) pending = _retired.ToArray();
            if (pending.Length == 0) return;
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }

    private Task RetireAsync(
        ArchiveTransferOwner owner,
        ArchiveTransferReleaseReason reason,
        CancellationToken cancellationToken)
    {
        Task<ArchiveTransferCleanupOutcome> core = owner.StartRelease(reason);
        return cancellationToken.CanBeCanceled
            ? core.WaitAsync(cancellationToken)
            : core;
    }

    internal void TrackRelease(Task release)
    {
        lock (_sync) _retired.Add(release);
        _ = release.ContinueWith(
            completed =>
            {
                lock (_sync) _retired.Remove(completed);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    internal void OwnerReleased(ArchiveTransferOwner owner)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_active, owner)) _active = null;
        }
    }

    internal static CancellationTokenSource CreateCleanupTimeout() =>
        new(CleanupReviewTimeout);

    internal CancellationTokenSource CreateRenderDrainTimeout() =>
        new(_renderDrainTimeout);

    public async ValueTask DisposeAsync()
    {
        await _admissionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ArchiveTransferOwner? owner;
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                _invalidationGeneration++;
                owner = _active;
                _active = null;
            }
            if (owner is not null)
                await RetireAsync(owner, ArchiveTransferReleaseReason.ApplicationShutdown, CancellationToken.None)
                    .ConfigureAwait(false);
            await DrainAsync().ConfigureAwait(false);
        }
        finally
        {
            _admissionGate.Release();
        }
    }
}

internal sealed class ArchiveTransferOwner
{
    private readonly object _sync = new();
    private readonly ArchiveTransferCoordinator _coordinator;
    private readonly ArchiveTransferStorage _storage;
    private readonly ArchiveTransferExtraction _extract;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private Task<ArchiveTransferRenderResult>? _renderTask;
    private Task<ArchiveTransferCleanupOutcome>? _releaseTask;
    private ArchiveTransferManifest? _manifest;
    private ArchiveTransferCleanupOutcome _cleanupOutcome = ArchiveTransferCleanupOutcome.NotNeeded;
    private ArchiveTransferReleaseReason? _releaseReason;
    private int _coordinatorOwnershipReleased;
    private int _lifetimeDisposed;
    private bool _released;

    public ArchiveTransferOwner(
        ArchiveTransferCoordinator coordinator,
        ArchiveTransferStorage storage,
        ArchiveTransferExtraction extract)
    {
        _coordinator = coordinator;
        _storage = storage;
        _extract = extract;
    }

    public bool HasStartedRendering
    {
        get { lock (_sync) return _renderTask is not null; }
    }

    public ArchiveTransferCleanupOutcome CleanupOutcome
    {
        get { lock (_sync) return _cleanupOutcome; }
    }

    public ArchiveTransferReleaseReason? ReleaseReason
    {
        get { lock (_sync) return _releaseReason; }
    }

    /// <summary>
    /// The first caller owns the shared operation: cancelling its token cancels
    /// the render for every re-entrant caller. Later tokens cannot fork or retry it.
    /// </summary>
    public Task<ArchiveTransferRenderResult> RenderAsync(
        char[]? password = null,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_released)
            {
                Clear(password);
                return Task.FromResult(ArchiveTransferRenderResult.Fixed(ArchiveTransferOutcome.Cancelled));
            }
            if (_renderTask is not null)
            {
                Clear(password);
                return _renderTask;
            }

            var completion = new TaskCompletionSource<ArchiveTransferRenderResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _renderTask = completion.Task;
            _ = RunRenderAsync(completion, password, cancellationToken);
            return _renderTask;
        }
    }

    public Task<ArchiveTransferCleanupOutcome> ReleaseAsync(
        ArchiveTransferReleaseReason reason,
        CancellationToken cancellationToken = default)
    {
        Task<ArchiveTransferCleanupOutcome> core = StartRelease(reason);
        return cancellationToken.CanBeCanceled ? core.WaitAsync(cancellationToken) : core;
    }

    internal Task<ArchiveTransferCleanupOutcome> StartRelease(ArchiveTransferReleaseReason reason)
    {
        Task<ArchiveTransferCleanupOutcome> release;
        lock (_sync)
        {
            if (_releaseTask is not null) return _releaseTask;
            _released = true;
            _releaseReason = reason;
            release = _releaseTask = ReleaseCoreAsync();
        }
        _coordinator.TrackRelease(release);
        return release;
    }

    private async Task<ArchiveTransferCleanupOutcome> ReleaseCoreAsync()
    {
        bool lateSettlementTracked = false;
        try
        {
            try { _lifetimeCancellation.Cancel(); }
            catch (ObjectDisposedException) { }

            Task<ArchiveTransferRenderResult>? render;
            lock (_sync) render = _renderTask;
            if (render is not null)
            {
                using CancellationTokenSource drain = _coordinator.CreateRenderDrainTimeout();
                try { _ = await render.WaitAsync(drain.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (drain.IsCancellationRequested)
                {
                    lock (_sync) _cleanupOutcome = ArchiveTransferCleanupOutcome.Preserved;
                    TrackReleaseAfterLateRender(render);
                    lateSettlementTracked = true;
                    return ArchiveTransferCleanupOutcome.Preserved;
                }
                catch (Exception exception) when (!IsFatal(exception)) { }
            }

            ArchiveTransferManifest? manifest;
            ArchiveTransferCleanupOutcome existing;
            lock (_sync)
            {
                manifest = _manifest;
                existing = _cleanupOutcome;
            }

            ArchiveTransferCleanupOutcome cleanup = manifest is null
                ? existing
                : await CleanupManifestSafelyAsync(manifest).ConfigureAwait(false);

            lock (_sync) _cleanupOutcome = cleanup;
            return cleanup;
        }
        finally
        {
            ReleaseCoordinatorOwnership();
            if (!lateSettlementTracked) DisposeLifetime();
        }
    }

    private void TrackReleaseAfterLateRender(Task<ArchiveTransferRenderResult> render)
    {
        _coordinator.TrackRelease(SettleLateRenderAsync(render));
    }

    private async Task SettleLateRenderAsync(Task<ArchiveTransferRenderResult> render)
    {
        try
        {
            try { _ = await render.ConfigureAwait(false); }
            catch (Exception exception) when (!IsFatal(exception)) { }

            ArchiveTransferManifest? manifest;
            lock (_sync) manifest = _manifest;
            if (manifest is not null)
            {
                ArchiveTransferCleanupOutcome cleanup = await CleanupManifestSafelyAsync(manifest)
                    .ConfigureAwait(false);
                lock (_sync) _cleanupOutcome = cleanup;
            }
        }
        finally
        {
            ReleaseCoordinatorOwnership();
            DisposeLifetime();
        }
    }

    private async Task<ArchiveTransferCleanupOutcome> CleanupManifestSafelyAsync(
        ArchiveTransferManifest manifest)
    {
        try
        {
            using CancellationTokenSource timeout = ArchiveTransferCoordinator.CreateCleanupTimeout();
            return await _storage.CleanupAsync(manifest, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return ArchiveTransferCleanupOutcome.Preserved;
        }
    }

    private void ReleaseCoordinatorOwnership()
    {
        if (Interlocked.Exchange(ref _coordinatorOwnershipReleased, 1) == 0)
            _coordinator.OwnerReleased(this);
    }

    private void DisposeLifetime()
    {
        if (Interlocked.Exchange(ref _lifetimeDisposed, 1) == 0)
            _lifetimeCancellation.Dispose();
    }

    private async Task RunRenderAsync(
        TaskCompletionSource<ArchiveTransferRenderResult> completion,
        char[]? password,
        CancellationToken firstRequestCancellation)
    {
        ArchiveTransferAllocation? allocation = null;
        var progress = new ArchiveTransferExtractionProgress();
        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCancellation.Token, firstRequestCancellation);
            linked.Token.ThrowIfCancellationRequested();
            allocation = _storage.Allocate();
            linked.Token.ThrowIfCancellationRequested();
            ArchiveOperationResult operation = await _extract(
                    allocation, _storage, progress, password, linked.Token)
                .ConfigureAwait(false);
            password = null;
            ArchiveTransferExtractionSnapshot snapshot = progress.Snapshot();

            if (operation.Succeeded
                && snapshot.OutputOwnership == ArchiveTransferOutputOwnership.ConfirmedPublished
                && snapshot.Manifest is not null)
            {
                bool stillExact;
                try
                {
                    using CancellationTokenSource timeout = ArchiveTransferCoordinator.CreateCleanupTimeout();
                    using CancellationTokenSource revalidation =
                        CancellationTokenSource.CreateLinkedTokenSource(linked.Token, timeout.Token);
                    linked.Token.ThrowIfCancellationRequested();
                    stillExact = await _storage.RevalidateReadyAsync(
                            snapshot.Manifest, revalidation.Token)
                        .ConfigureAwait(false);
                    linked.Token.ThrowIfCancellationRequested();
                }
                catch (Exception exception) when (!IsFatal(exception))
                {
                    if (exception is OperationCanceledException && linked.IsCancellationRequested)
                        throw;
                    stillExact = false;
                }
                if (!stillExact)
                {
                    ArchiveTransferCleanupOutcome cleanup = await TryCleanupAsync(snapshot, allocation)
                        .ConfigureAwait(false);
                    CompleteCleanupFailure(completion, cleanup, ArchiveTransferOutcome.UnsafeOutput);
                    return;
                }

                bool released;
                lock (_sync)
                {
                    released = _released;
                    if (!released)
                    {
                        _manifest = snapshot.Manifest;
                        completion.TrySetResult(new ArchiveTransferRenderResult(
                            ArchiveTransferOutcome.Ready,
                            snapshot.Manifest.PublishedPaths,
                            CopyOnly: true,
                            ArchiveTransferStatus.For(ArchiveTransferOutcome.Ready)));
                    }
                }
                if (released)
                {
                    ArchiveTransferCleanupOutcome cleanup = await TryCleanupAsync(snapshot, allocation)
                        .ConfigureAwait(false);
                    CompleteCleanupFailure(completion, cleanup, ArchiveTransferOutcome.Cancelled);
                }
                return;
            }

            if (snapshot.OutputOwnership != ArchiveTransferOutputOwnership.None)
            {
                ArchiveTransferCleanupOutcome cleanup = await TryCleanupAsync(snapshot, allocation).ConfigureAwait(false);
                if (CompleteCleanupFailure(completion, cleanup, MapOutcome(operation.Outcome))) return;
            }

            completion.TrySetResult(ArchiveTransferRenderResult.Fixed(MapOutcome(operation.Outcome)));
        }
        catch (OperationCanceledException)
        {
            ArchiveTransferExtractionSnapshot snapshot = progress.Snapshot();
            if (snapshot.OutputOwnership != ArchiveTransferOutputOwnership.None)
            {
                ArchiveTransferCleanupOutcome cleanup = await TryCleanupAsync(snapshot, allocation).ConfigureAwait(false);
                if (CompleteCleanupFailure(completion, cleanup, ArchiveTransferOutcome.Cancelled)) return;
            }
            completion.TrySetResult(ArchiveTransferRenderResult.Fixed(ArchiveTransferOutcome.Cancelled));
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            ArchiveTransferExtractionSnapshot snapshot = progress.Snapshot();
            if (snapshot.OutputOwnership != ArchiveTransferOutputOwnership.None)
            {
                ArchiveTransferCleanupOutcome cleanup = await TryCleanupAsync(snapshot, allocation).ConfigureAwait(false);
                if (CompleteCleanupFailure(completion, cleanup, ArchiveTransferOutcome.Failed)) return;
            }
            completion.TrySetResult(ArchiveTransferRenderResult.Fixed(ArchiveTransferOutcome.Failed));
        }
        finally
        {
            Clear(password);
            completion.TrySetResult(ArchiveTransferRenderResult.Fixed(ArchiveTransferOutcome.Failed));
        }
    }

    private bool CompleteCleanupFailure(
        TaskCompletionSource<ArchiveTransferRenderResult> completion,
        ArchiveTransferCleanupOutcome cleanup,
        ArchiveTransferOutcome otherwise)
    {
        lock (_sync) _cleanupOutcome = cleanup;
        if (cleanup == ArchiveTransferCleanupOutcome.Preserved)
        {
            completion.TrySetResult(ArchiveTransferRenderResult.Fixed(ArchiveTransferOutcome.Preserved));
            return true;
        }
        if (cleanup == ArchiveTransferCleanupOutcome.PartiallyRemoved)
        {
            completion.TrySetResult(ArchiveTransferRenderResult.Fixed(ArchiveTransferOutcome.CleanupIncomplete));
            return true;
        }
        completion.TrySetResult(ArchiveTransferRenderResult.Fixed(otherwise));
        return true;
    }

    private async Task<ArchiveTransferCleanupOutcome> TryCleanupAsync(
        ArchiveTransferExtractionSnapshot snapshot,
        ArchiveTransferAllocation? allocation)
    {
        try
        {
            using CancellationTokenSource timeout = ArchiveTransferCoordinator.CreateCleanupTimeout();
            if (snapshot.OutputOwnership == ArchiveTransferOutputOwnership.Uncertain)
                return allocation is null
                    ? ArchiveTransferCleanupOutcome.Preserved
                    : _storage.InspectUncertainOutput(allocation);
            if (snapshot.OutputOwnership == ArchiveTransferOutputOwnership.PreservationRequired)
                return ArchiveTransferCleanupOutcome.Preserved;
            if (snapshot.OutputOwnership != ArchiveTransferOutputOwnership.ConfirmedPublished)
                return ArchiveTransferCleanupOutcome.NotNeeded;
            ArchiveTransferManifest? manifest = snapshot.Manifest;
            if (manifest is null)
            {
                if (snapshot.Selection is null || allocation is null)
                    return ArchiveTransferCleanupOutcome.Preserved;
                return await _storage.CleanupConfirmedOutputAsync(
                        allocation, snapshot.Selection, timeout.Token)
                    .ConfigureAwait(false);
            }
            return await _storage.CleanupAsync(manifest, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return ArchiveTransferCleanupOutcome.Preserved;
        }
    }

    private static ArchiveTransferOutcome MapOutcome(ArchiveOperationOutcome outcome) => outcome switch
    {
        ArchiveOperationOutcome.Cancelled => ArchiveTransferOutcome.Cancelled,
        ArchiveOperationOutcome.Busy => ArchiveTransferOutcome.Busy,
        ArchiveOperationOutcome.InvalidSelection or ArchiveOperationOutcome.InvalidRequest
            or ArchiveOperationOutcome.InvalidDestination => ArchiveTransferOutcome.InvalidSelection,
        ArchiveOperationOutcome.DestinationConflict => ArchiveTransferOutcome.DestinationConflict,
        ArchiveOperationOutcome.Unsupported => ArchiveTransferOutcome.Unsupported,
        ArchiveOperationOutcome.Stale => ArchiveTransferOutcome.Stale,
        ArchiveOperationOutcome.Offline => ArchiveTransferOutcome.Offline,
        ArchiveOperationOutcome.UnsafeOutput => ArchiveTransferOutcome.UnsafeOutput,
        _ => ArchiveTransferOutcome.Failed,
    };

    private static void Clear(char[]? value)
    {
        if (value is not null) Array.Clear(value);
    }

    private static bool IsFatal(Exception exception) => exception is
        OutOfMemoryException or AccessViolationException;
}
