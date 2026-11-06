using System.IO;
using System.Text;
using RainArchives.Client;
using RainArchives.Contracts;
using RainExplorer.Models;

namespace RainExplorer.Services;

public enum ArchiveLocationState
{
    None,
    Loading,
    Ready,
    Empty,
    Unsupported,
    PasswordRequired,
    Offline,
    Stale,
    Error,
}

public sealed record ArchiveBreadcrumb(string Label, string? NodeId, int ArchiveDepth = 0);

public enum ArchiveNestedOpenOutcome
{
    Opened,
    PasswordRequired,
    Refused,
    Stale,
    Offline,
    Cancelled,
    Error,
}

/// <summary>Rain-owned, read-only virtual location model over the Contracts/Client boundary.</summary>
public sealed class ArchiveLocationAdapter : IAsyncDisposable
{
    public const int MaximumIndexedEntries = 500_000;
    public const int MaximumVisibleItems = 512;
    public const int MaximumHistoryEntries = 256;

    private readonly ArchiveClientRuntime _runtime;
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);
    private readonly List<Node> _roots = [];
    private readonly List<ArchiveCursor> _history = [];
    private readonly List<SessionFrame> _sessionChain = [];
    private readonly SemaphoreSlim _openGate = new(1, 1);
    private readonly object _operationSync = new();
    private readonly object _archiveOperationSync = new();
    private readonly object _previewSync = new();
    private readonly object _clientInvalidationSync = new();
    private readonly ArchiveEntryPreviewLeaseFactory _previewLeaseFactory;
    private readonly Func<Task>? _previewBeforeCompletion;
    private readonly IArchiveActivitySink _activitySink;
    private readonly ArchiveTransferCoordinator _archiveTransferCoordinator;
    private readonly Func<Task>? _transferBeforeTerminalCompletion;
    private readonly Func<Task>? _rebuildBeforeStart;
    private CancellationTokenSource? _operationCancellation;
    private long _operationVersion;
    private long _previewAuthorityGeneration;
    private PreviewRequest? _activePreview;
    private PreviewRequest? _pendingPreview;
    private bool _previewPumpRunning;
    private Task _previewPump = Task.CompletedTask;
    private Task _clientInvalidationTask = Task.CompletedTask;
    private IArchiveClient? _invalidatingClient;
    private IArchiveClient? _client;
    private ArchiveDescriptor? _archive;
    private ActiveArchiveOperation? _activeArchiveOperation;
    private readonly List<string> _selectedArchiveNodeIds = [];
    private string? _sourcePath;
    private string _rootTitle = "Archive";
    private string? _currentNodeId;
    private int _currentDepth;
    private int _historyIndex = -1;
    private int _pageIndex;
    private string _searchText = string.Empty;
    private bool _recursiveSearch;
    private bool _persistentKeysEnabled;
    private bool _disposed;

    public ArchiveLocationAdapter(ArchiveClientRuntime runtime)
        : this(runtime, OpenPreviewLeaseAsync)
    {
    }

    internal ArchiveLocationAdapter(
        ArchiveClientRuntime runtime,
        IArchiveActivitySink activitySink)
        : this(runtime, OpenPreviewLeaseAsync, activitySink: activitySink)
    {
    }

    internal ArchiveLocationAdapter(
        ArchiveClientRuntime runtime,
        IArchiveActivitySink activitySink,
        ArchiveTransferStorage transferStorage)
        : this(
            runtime,
            OpenPreviewLeaseAsync,
            activitySink: activitySink,
            transferStorage: transferStorage)
    {
    }

    internal ArchiveLocationAdapter(
        ArchiveClientRuntime runtime,
        IArchiveActivitySink activitySink,
        ArchiveTransferStorage transferStorage,
        Func<Task> transferBeforeTerminalCompletion)
        : this(
            runtime,
            OpenPreviewLeaseAsync,
            activitySink: activitySink,
            transferStorage: transferStorage,
            transferBeforeTerminalCompletion: transferBeforeTerminalCompletion)
    {
    }

    internal ArchiveLocationAdapter(
        ArchiveClientRuntime runtime,
        IArchiveActivitySink activitySink,
        ArchiveTransferStorage transferStorage,
        TimeSpan transferRenderDrainTimeout,
        Func<Task> transferBeforeOwnerAdmission)
        : this(
            runtime,
            OpenPreviewLeaseAsync,
            activitySink: activitySink,
            transferStorage: transferStorage,
            transferRenderDrainTimeout: transferRenderDrainTimeout,
            transferBeforeOwnerAdmission: transferBeforeOwnerAdmission)
    {
    }

    internal ArchiveLocationAdapter(
        ArchiveClientRuntime runtime,
        ArchiveEntryPreviewLeaseFactory previewLeaseFactory,
        Func<Task>? previewBeforeCompletion = null,
        IArchiveActivitySink? activitySink = null,
        ArchiveTransferStorage? transferStorage = null,
        Func<Task>? transferBeforeTerminalCompletion = null,
        TimeSpan? transferRenderDrainTimeout = null,
        Func<Task>? transferBeforeOwnerAdmission = null,
        Func<Task>? rebuildBeforeStart = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _previewLeaseFactory = previewLeaseFactory
            ?? throw new ArgumentNullException(nameof(previewLeaseFactory));
        _previewBeforeCompletion = previewBeforeCompletion;
        _activitySink = activitySink ?? ArchiveActivitySink.Instance;
        _transferBeforeTerminalCompletion = transferBeforeTerminalCompletion;
        _rebuildBeforeStart = rebuildBeforeStart;
        _archiveTransferCoordinator = new ArchiveTransferCoordinator(
            ExecuteArchiveTransferAsync,
            transferStorage,
            transferRenderDrainTimeout,
            transferBeforeOwnerAdmission);
        _runtime.ClientInvalidated += OnClientInvalidated;
    }

    public event EventHandler? Changed;

    public ArchiveLocationState State { get; private set; }
    public string Status { get; private set; } = string.Empty;
    public string Title { get; private set; } = "Archive";
    public IReadOnlyList<FileItem> Items { get; private set; } = Array.Empty<FileItem>();
    public IReadOnlyList<ArchiveBreadcrumb> Breadcrumbs { get; private set; } = Array.Empty<ArchiveBreadcrumb>();
    public bool IsOpen => _archive is not null;
    public bool CanGoBack => IsBrowsable && _historyIndex > 0;
    public bool CanGoForward => IsBrowsable && _historyIndex >= 0 && _historyIndex < _history.Count - 1;
    public bool CanGoUp => IsBrowsable && (_currentNodeId is not null || _currentDepth > 0);
    public bool CanGoToPreviousPage => IsBrowsable && _pageIndex > 0;
    public bool CanGoToNextPage => IsBrowsable && (_pageIndex + 1) * MaximumVisibleItems < MatchingNodes().Count;
    public bool CanPersistLocation => TryGetPersistentTarget(out _);
    public string SourcePath => _sourcePath ?? string.Empty;
    public int PageIndex => _pageIndex;
    public bool RequiresOperationPassword => _archive?.HasEncryptedEntries == true;
    public bool CanTestIntegrity => CanStartArchiveOperation(
        ArchiveCapability.Test, requireSelection: false);
    public bool CanExtractSelection => CanStartArchiveOperation(
        ArchiveCapability.Extract, requireSelection: true);
    internal bool CanExtractEntireArchive => CanStartArchiveOperation(
        ArchiveCapability.Extract, requireSelection: false);
    public bool CanRebuildCopy => CanStartRebuildCopy();
    public bool CanCopyArchiveSelection => false;
    public string ArchiveCopyDisabledReason => ArchiveOleTransferGate.DisabledReason;
    public int NestedDepth => _currentDepth;

    private bool IsBrowsable => State is ArchiveLocationState.Ready or ArchiveLocationState.Empty;

    public Task<ArchiveEntryPreviewResult> LoadPreviewAsync(
        FileItem item,
        CancellationToken cancellationToken = default)
    {
        if (item is not { IsArchiveEntry: true, IsDirectory: false, ArchiveNodeId: not null })
            return Task.FromResult(ArchiveEntryPreviewResult.Fixed(ArchiveEntryPreviewState.Unsupported));
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(ArchiveEntryPreviewResult.Fixed(ArchiveEntryPreviewState.Cancelled));

        PreviewRequest request;
        lock (_previewSync)
        {
            if (_disposed)
                return Task.FromResult(ArchiveEntryPreviewResult.Fixed(ArchiveEntryPreviewState.Cancelled));

            request = new PreviewRequest(
                item.ArchiveNodeId,
                _previewAuthorityGeneration,
                cancellationToken);

            if (_pendingPreview is not null)
                CompletePreview(_pendingPreview, ArchiveEntryPreviewState.Cancelled);
            _pendingPreview = request;
            if (_activePreview is not null)
            {
                _activePreview.Force(ArchiveEntryPreviewState.Cancelled);
                CancelPreview(_activePreview.Cancellation);
            }
            if (!_previewPumpRunning)
            {
                _previewPumpRunning = true;
                _previewPump = Task.Run(RunPreviewPumpAsync);
            }
        }
        return request.Completion.Task;
    }

    private async Task RunPreviewPumpAsync()
    {
        while (true)
        {
            PreviewRequest request;
            lock (_previewSync)
            {
                if (_pendingPreview is null)
                {
                    _previewPumpRunning = false;
                    return;
                }
                request = _pendingPreview;
                _pendingPreview = null;
                request.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    request.CallerCancellation);
                _activePreview = request;
            }

            ArchiveEntryPreviewResult result;
            try
            {
                result = await ExecutePreviewAsync(request, request.Cancellation.Token)
                    .ConfigureAwait(false);
            }
            catch
            {
                result = ArchiveEntryPreviewResult.Fixed(ArchiveEntryPreviewState.Error);
            }

            if (_previewBeforeCompletion is not null)
            {
                try { await _previewBeforeCompletion().ConfigureAwait(false); }
                catch { result = ArchiveEntryPreviewResult.Fixed(ArchiveEntryPreviewState.Error); }
            }

            CancellationTokenSource? requestCancellation = request.Cancellation;
            lock (_previewSync)
            {
                if (request.ForcedState is ArchiveEntryPreviewState forced)
                    result = ArchiveEntryPreviewResult.Fixed(forced);
                else if (request.CallerCancellation.IsCancellationRequested)
                    result = ArchiveEntryPreviewResult.Fixed(ArchiveEntryPreviewState.Cancelled);
                else if (request.AuthorityGeneration != _previewAuthorityGeneration)
                    result = ArchiveEntryPreviewResult.Fixed(ArchiveEntryPreviewState.Stale);

                if (ReferenceEquals(_activePreview, request)) _activePreview = null;
                request.Cancellation = null;
                request.Completion.TrySetResult(result);
            }
            requestCancellation?.Dispose();
        }
    }

    private async Task<ArchiveEntryPreviewResult> ExecutePreviewAsync(
        PreviewRequest request,
        CancellationToken cancellationToken)
    {
        IArchiveEntryPreviewLease? lease = null;
        IArchiveClient? expectedClient = null;
        ArchiveSessionId expectedSession = default;
        ArchiveFingerprint? expectedFingerprint = null;
        EntryId expectedEntry = default;
        try
        {
            await _openGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (request.AuthorityGeneration != Volatile.Read(ref _previewAuthorityGeneration))
                    return ArchiveEntryPreviewResult.Fixed(ArchiveEntryPreviewState.Stale);
                if (State == ArchiveLocationState.Offline || _client is null)
                    return ArchiveEntryPreviewResult.Fixed(ArchiveEntryPreviewState.Offline);
                if (ReferenceEquals(Volatile.Read(ref _invalidatingClient), _client))
                    return ArchiveEntryPreviewResult.Fixed(ArchiveEntryPreviewState.Offline);
                if (State == ArchiveLocationState.Stale || _archive is null)
                    return ArchiveEntryPreviewResult.Fixed(ArchiveEntryPreviewState.Stale);
                if (!IsBrowsable
                    || !_archive.Capabilities.HasFlag(ArchiveCapability.OpenEntryStream)
                    || !_client.SupportsFeature(ProtocolFeatures.EntryStreamLeases))
                    return ArchiveEntryPreviewResult.Fixed(ArchiveEntryPreviewState.Unsupported);
                if (!_nodes.TryGetValue(request.NodeId, out Node? node)
                    || node.IsImplicit
                    || node.Descriptor is not { Kind: ArchiveEntryKind.File } descriptor
                    || descriptor.ProviderOrdinal < 0
                    || !Items.Any(item => item.IsArchiveEntry && !item.IsDirectory
                        && string.Equals(item.ArchiveNodeId, request.NodeId, StringComparison.Ordinal)))
                    return ArchiveEntryPreviewResult.Fixed(ArchiveEntryPreviewState.Stale);
                if (descriptor.OriginalSize is > ArchiveEntryPreviewReader.MaximumTextBytes)
                    return ArchiveEntryPreviewResult.Fixed(ArchiveEntryPreviewState.Oversize);

                cancellationToken.ThrowIfCancellationRequested();
                expectedClient = _client;
                expectedSession = _archive.SessionId;
                expectedFingerprint = _archive.Fingerprint;
                expectedEntry = descriptor.EntryId;
                lease = await _previewLeaseFactory(
                    expectedClient,
                    expectedSession,
                    expectedEntry,
                    ArchiveEntryPreviewReader.StreamLimitBytes,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _openGate.Release();
            }

            ArchiveEntryPreviewResult result;
            if (lease.SessionId != expectedSession || lease.EntryId != expectedEntry)
            {
                result = ArchiveEntryPreviewResult.Fixed(ArchiveEntryPreviewState.Stale);
            }
            else
            {
                result = await ArchiveEntryPreviewReader.ReadAsync(
                    lease, cancellationToken).ConfigureAwait(false);
            }

            IArchiveEntryPreviewLease leaseToDispose = lease;
            lease = null;
            await leaseToDispose.DisposeAsync().ConfigureAwait(false);

            await _openGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (request.AuthorityGeneration != Volatile.Read(ref _previewAuthorityGeneration)
                    || _archive is null
                    || !ReferenceEquals(_client, expectedClient)
                    || _archive.SessionId != expectedSession
                    || expectedFingerprint is null
                    || !FingerprintsEqual(_archive.Fingerprint, expectedFingerprint)
                    || !_nodes.TryGetValue(request.NodeId, out Node? currentNode)
                    || currentNode.Descriptor?.EntryId != expectedEntry)
                    return ArchiveEntryPreviewResult.Fixed(ArchiveEntryPreviewState.Stale);
            }
            finally
            {
                _openGate.Release();
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            return ArchiveEntryPreviewResult.Fixed(
                request.ForcedState ?? ArchiveEntryPreviewState.Cancelled);
        }
        catch (ArchiveClientException exception)
        {
            IArchiveEntryPreviewLease? failedLease = lease;
            lease = null;
            await DisposeFailedPreviewLeaseAsync(failedLease).ConfigureAwait(false);
            await ApplyPreviewAuthorityFailureAsync(
                exception.Error.Category,
                expectedClient,
                expectedSession,
                expectedFingerprint).ConfigureAwait(false);
            return MapPreviewFailure(exception);
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or IOException
                                              or InvalidDataException
                                              or ObjectDisposedException
                                              or UnauthorizedAccessException)
        {
            IArchiveEntryPreviewLease? failedLease = lease;
            lease = null;
            await DisposeFailedPreviewLeaseAsync(failedLease).ConfigureAwait(false);
            if (exception is IOException or ObjectDisposedException)
            {
                await InvalidatePreviewClientAsync(expectedClient).ConfigureAwait(false);
                return ArchiveEntryPreviewResult.Fixed(ArchiveEntryPreviewState.Offline);
            }
            return ArchiveEntryPreviewResult.Fixed(ArchiveEntryPreviewState.Error);
        }
        finally
        {
            if (lease is not null)
            {
                try { await lease.DisposeAsync().ConfigureAwait(false); }
                catch { }
            }
        }
    }

    private static async Task DisposeFailedPreviewLeaseAsync(IArchiveEntryPreviewLease? lease)
    {
        if (lease is null) return;
        try { await lease.DisposeAsync().ConfigureAwait(false); }
        catch { }
    }

    private async Task ApplyPreviewAuthorityFailureAsync(
        ArchiveErrorCategory category,
        IArchiveClient? expectedClient,
        ArchiveSessionId expectedSession,
        ArchiveFingerprint? expectedFingerprint)
    {
        if (category == ArchiveErrorCategory.SourceChanged)
        {
            await TransitionPreviewSourceChangedAsync(
                expectedClient, expectedSession, expectedFingerprint).ConfigureAwait(false);
        }
        else if (category is ArchiveErrorCategory.WorkerFailure or ArchiveErrorCategory.ProtocolMismatch)
        {
            await InvalidatePreviewClientAsync(expectedClient).ConfigureAwait(false);
        }
    }

    private async Task TransitionPreviewSourceChangedAsync(
        IArchiveClient? expectedClient,
        ArchiveSessionId expectedSession,
        ArchiveFingerprint? expectedFingerprint,
        bool drainArchiveOperation = true)
    {
        if (expectedClient is null || expectedFingerprint is null) return;
        _ = _archiveTransferCoordinator.InvalidateAsync(ArchiveTransferReleaseReason.SourceChanged);
        if (drainArchiveOperation)
            await CancelAndDrainArchiveOperationAsync().ConfigureAwait(false);
        await _openGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed
                || !ReferenceEquals(_client, expectedClient)
                || _archive is null
                || _archive.SessionId != expectedSession
                || !FingerprintsEqual(_archive.Fingerprint, expectedFingerprint))
                return;

            _ = InvalidatePreview(ArchiveEntryPreviewState.Stale);
            bool closed = await CloseCurrentSessionAsync().ConfigureAwait(false);
            ClearSessionData();
            if (closed)
            {
                SetFailure(ArchiveLocationState.Stale,
                    "The archive source changed. Reopen it before continuing.");
            }
            else
            {
                _ = InvalidatePreview(ArchiveEntryPreviewState.Offline);
                SetFailure(ArchiveLocationState.Offline,
                    "The Rain Archives Worker is unavailable. Reopen the archive after reconnecting.");
            }
        }
        finally
        {
            _openGate.Release();
        }
    }

    private async Task InvalidatePreviewClientAsync(IArchiveClient? expectedClient)
    {
        if (expectedClient is null) return;
        try { await _runtime.InvalidateAsync(expectedClient).ConfigureAwait(false); }
        catch (Exception exception) when (exception is ArchiveClientException
                                              or OperationCanceledException
                                              or IOException
                                              or InvalidDataException
                                              or ObjectDisposedException)
        {
        }
        BeginClientInvalidation(expectedClient);
    }

    private Task InvalidatePreview(ArchiveEntryPreviewState state)
    {
        Task drain;
        lock (_previewSync)
        {
            Interlocked.Increment(ref _previewAuthorityGeneration);
            if (_pendingPreview is not null)
            {
                CompletePreview(_pendingPreview, state);
                _pendingPreview = null;
            }
            if (_activePreview is not null)
            {
                _activePreview.Force(state);
                CancelPreview(_activePreview.Cancellation);
            }
            drain = _previewPump;
        }
        return drain;
    }

    private static void CancelPreview(CancellationTokenSource? cancellation)
    {
        if (cancellation is null) return;
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private static void CompletePreview(PreviewRequest request, ArchiveEntryPreviewState state)
    {
        request.Force(state);
        request.Completion.TrySetResult(ArchiveEntryPreviewResult.Fixed(state));
    }

    private static ArchiveEntryPreviewResult MapPreviewFailure(ArchiveClientException exception)
    {
        if (exception.Error.Code is "entry.stream_declared_size_limit"
            or "entry.stream_byte_limit"
            or "entry.stream_byte_overflow")
            return ArchiveEntryPreviewResult.Fixed(ArchiveEntryPreviewState.Oversize);
        return ArchiveEntryPreviewResult.Fixed(exception.Error.Category switch
        {
            ArchiveErrorCategory.PasswordRequired or ArchiveErrorCategory.IncorrectPassword =>
                ArchiveEntryPreviewState.PasswordRequired,
            ArchiveErrorCategory.SourceChanged => ArchiveEntryPreviewState.Stale,
            ArchiveErrorCategory.WorkerFailure or ArchiveErrorCategory.ProtocolMismatch =>
                ArchiveEntryPreviewState.Offline,
            ArchiveErrorCategory.UnsupportedCapability or ArchiveErrorCategory.UnsupportedFormat =>
                ArchiveEntryPreviewState.Unsupported,
            _ => ArchiveEntryPreviewState.Error,
        });
    }

    private static async Task<IArchiveEntryPreviewLease> OpenPreviewLeaseAsync(
        IArchiveClient client,
        ArchiveSessionId sessionId,
        EntryId entryId,
        long maximumBytes,
        CancellationToken cancellationToken) =>
        new ClientArchiveEntryPreviewLease(await client.OpenEntryStreamAsync(
            sessionId, entryId, maximumBytes, cancellationToken: cancellationToken)
            .ConfigureAwait(false));

    public async Task OpenAsync(string sourceOrTarget, CancellationToken cancellationToken = default)
    {
        if (ArchiveLocationSerializer.IsArchiveTarget(sourceOrTarget))
        {
            if (!ArchiveLocationSerializer.TryDeserialize(sourceOrTarget, out var location))
            {
                await RejectInvalidTargetAsync().ConfigureAwait(false);
                return;
            }
            await OpenCoreAsync(location.SourcePath, location, requireSameFingerprint: true, cancellationToken)
                .ConfigureAwait(false);
        }
        else
            await OpenCoreAsync(sourceOrTarget, null, requireSameFingerprint: false, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// Takes ownership of <paramref name="password"/> and performs one password-bearing
    /// root-open attempt. This is reserved for archives whose headers cannot be listed
    /// until the Worker receives a secret slot.
    /// </summary>
    internal async Task OpenWithPasswordAsync(
        string sourcePath,
        char[] password,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(password);
        var passwordOwner = new OneShotOperationPassword(password);
        try
        {
            if (ArchiveLocationSerializer.IsArchiveTarget(sourcePath))
            {
                await RejectInvalidTargetAsync().ConfigureAwait(false);
                return;
            }

            await OpenCoreAsync(
                    sourcePath,
                    restore: null,
                    requireSameFingerprint: false,
                    cancellationToken,
                    passwordOwner)
                .ConfigureAwait(false);
        }
        finally
        {
            passwordOwner.Clear();
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_sourcePath is null || _archive is null || !IsBrowsable) return;
        if (!_persistentKeysEnabled && _currentNodeId is not null)
        {
            Status = "Refresh is unavailable inside this archive because stable entry keys were not negotiated.";
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }
        var location = CreateLocationForCurrentFolder();
        await OpenCoreAsync(_sourcePath, location, requireSameFingerprint: true, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RejectInvalidTargetAsync()
    {
        long operationVersion = Interlocked.Increment(ref _operationVersion);
        CancelActiveOperation();
        Task transferInvalidation = _archiveTransferCoordinator.InvalidateAsync(
            ArchiveTransferReleaseReason.Navigation);
        await CancelAndDrainArchiveOperationAsync().ConfigureAwait(false);
        await transferInvalidation.ConfigureAwait(false);
        await _archiveTransferCoordinator.DrainAsync().ConfigureAwait(false);
        await InvalidatePreview(ArchiveEntryPreviewState.Cancelled).ConfigureAwait(false);
        await DrainClientInvalidationAsync().ConfigureAwait(false);
        await _openGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsCurrentOperation(operationVersion)) return;
            if (_archive is not null)
            {
                Status = "Rain Explorer refused an invalid archive location.";
                Changed?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                SetFailure(ArchiveLocationState.Error,
                    "Rain Explorer refused an invalid archive location.");
            }
        }
        finally
        {
            _openGate.Release();
        }
    }

    public async Task CloseAsync()
    {
        Interlocked.Increment(ref _operationVersion);
        CancelActiveOperation();
        Task transferInvalidation = _archiveTransferCoordinator.InvalidateAsync(
            ArchiveTransferReleaseReason.TabClosed);
        await CancelAndDrainArchiveOperationAsync().ConfigureAwait(false);
        await transferInvalidation.ConfigureAwait(false);
        await _archiveTransferCoordinator.DrainAsync().ConfigureAwait(false);
        await InvalidatePreview(ArchiveEntryPreviewState.Cancelled).ConfigureAwait(false);
        await DrainClientInvalidationAsync().ConfigureAwait(false);
        await _openGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseCurrentSessionAsync().ConfigureAwait(false);
            ClearSessionData();
            State = ArchiveLocationState.None;
            Status = string.Empty;
        }
        finally
        {
            _openGate.Release();
        }
    }

    public bool Navigate(string? nodeId, bool pushHistory = true) =>
        Navigate(_currentDepth, nodeId, pushHistory);

    public bool Navigate(int archiveDepth, string? nodeId, bool pushHistory = true)
    {
        if (!IsBrowsable || archiveDepth < 0 || archiveDepth >= _sessionChain.Count)
            return false;
        SessionFrame frame = _sessionChain[archiveDepth];
        if (nodeId is not null
            && (frame.Index is null
                || !frame.Index.Nodes.TryGetValue(nodeId, out Node? node)
                || !node.IsDirectory))
            return false;

        BeginArchiveTransferInvalidation(ArchiveTransferReleaseReason.Navigation);
        _ = InvalidatePreview(ArchiveEntryPreviewState.Cancelled);
        var cursor = new ArchiveCursor(archiveDepth, nodeId);
        if (pushHistory) PushHistory(cursor);
        ApplyCursor(cursor);
        return true;
    }

    public bool Open(FileItem? item) =>
        item is { IsArchiveEntry: true, IsDirectory: true, ArchiveNodeId: not null }
        && Navigate(item.ArchiveNodeId);

    public bool CanOpenNestedArchive(FileItem? item) =>
        TryResolveNestedSource(item, out _, out _, out _, out _);

    /// <summary>Takes ownership of <paramref name="password"/> and clears it on every path.</summary>
    public async Task<ArchiveNestedOpenOutcome> OpenNestedArchiveAsync(
        FileItem? item,
        char[]? password = null,
        CancellationToken cancellationToken = default)
    {
        var passwordOwner = new OneShotOperationPassword(password);
        long operationVersion = Interlocked.Increment(ref _operationVersion);
        CancelActiveOperation();
        try
        {
            Task transferInvalidation = _archiveTransferCoordinator.InvalidateAsync(
                ArchiveTransferReleaseReason.Navigation);
            await CancelAndDrainArchiveOperationAsync().ConfigureAwait(false);
            await transferInvalidation.ConfigureAwait(false);
            await _archiveTransferCoordinator.DrainAsync().ConfigureAwait(false);
            await InvalidatePreview(ArchiveEntryPreviewState.Cancelled).ConfigureAwait(false);
            await DrainClientInvalidationAsync().ConfigureAwait(false);
            await _openGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!IsCurrentOperation(operationVersion)) return ArchiveNestedOpenOutcome.Cancelled;
                return await OpenNestedArchiveSerialAsync(
                        item, passwordOwner, operationVersion, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                _openGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            if (!_disposed && IsCurrentOperation(operationVersion))
            {
                Status = "Nested archive opening was cancelled.";
                Changed?.Invoke(this, EventArgs.Empty);
            }
            return ArchiveNestedOpenOutcome.Cancelled;
        }
        finally
        {
            passwordOwner.Clear();
        }
    }

    private async Task<ArchiveNestedOpenOutcome> OpenNestedArchiveSerialAsync(
        FileItem? item,
        OneShotOperationPassword password,
        long operationVersion,
        CancellationToken callerCancellation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!TryResolveNestedSource(
                item, out SessionFrame parentFrame, out Node sourceNode,
                out long maximumBytes, out string refusal))
        {
            Status = refusal;
            Changed?.Invoke(this, EventArgs.Empty);
            return ArchiveNestedOpenOutcome.Refused;
        }

        if (!await CloseDescendantsAfterAsync(_currentDepth).ConfigureAwait(false))
        {
            ClearSessionData();
            SetFailure(ArchiveLocationState.Offline,
                "The Rain Archives Worker is unavailable. Reopen the archive after reconnecting.");
            return ArchiveNestedOpenOutcome.Offline;
        }

        if (!IsCurrentOperation(operationVersion)
            || !TryResolveNestedSource(
                item, out parentFrame, out sourceNode, out maximumBytes, out refusal))
        {
            Status = "The selected archive entry is no longer current.";
            Changed?.Invoke(this, EventArgs.Empty);
            return ArchiveNestedOpenOutcome.Stale;
        }

        if (!_runtime.TryReserveNestedArchiveBytes(maximumBytes, out var byteReservation))
        {
            Status = "The active nested archives have reached their bounded byte limit.";
            Changed?.Invoke(this, EventArgs.Empty);
            return ArchiveNestedOpenOutcome.Refused;
        }

        CancellationTokenSource operationCancellation;
        lock (_operationSync)
        {
            _operationCancellation?.Dispose();
            operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(callerCancellation);
            _operationCancellation = operationCancellation;
        }

        OpenNestedArchiveResponse? opened = null;
        Guid? nestedPasswordSlot = null;
        bool slotsTransferred = false;
        bool childOwned = false;
        try
        {
            CancellationToken token = operationCancellation.Token;
            IArchiveClient client = _client!;
            ArchiveEntryDescriptor descriptor = sourceNode.Descriptor!;
            char[]? suppliedPassword = password.Value;
            if (suppliedPassword is not null)
            {
                if (suppliedPassword.Length == 0 || !IsPasswordWithinProtocolLimit(suppliedPassword)
                    || !client.SupportsFeature(ProtocolFeatures.SecretSlots)
                    || !client.SupportsFeature(ProtocolFeatures.SecretSlotDiscard))
                {
                    Status = "Rain Explorer refused an invalid nested-archive password request.";
                    Changed?.Invoke(this, EventArgs.Empty);
                    return ArchiveNestedOpenOutcome.Refused;
                }

                try
                {
                    nestedPasswordSlot = await client.CreateSecretAsync(
                        suppliedPassword.AsMemory(), token).ConfigureAwait(false);
                }
                finally
                {
                    password?.Clear();
                }
            }

            token.ThrowIfCancellationRequested();
            if (!IsCurrentNestedAuthority(parentFrame, sourceNode, item))
            {
                Status = "The selected archive entry is no longer current.";
                Changed?.Invoke(this, EventArgs.Empty);
                return ArchiveNestedOpenOutcome.Stale;
            }

            slotsTransferred = true;
            opened = await client.OpenNestedArchiveAsync(
                    parentFrame.Archive.SessionId,
                    descriptor.EntryId,
                    maximumBytes,
                    parentPasswordSlotId: null,
                    nestedPasswordSlotId: nestedPasswordSlot,
                    cancellationToken: token)
                .ConfigureAwait(false);
            childOwned = true;
            ValidateNestedResponse(opened, parentFrame, descriptor, maximumBytes);
            PreparedIndex childIndex = await PrepareIndexAsync(client, opened.Archive, token)
                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!IsCurrentOperation(operationVersion)
                || !IsCurrentNestedAuthority(parentFrame, sourceNode, item))
                throw new OperationCanceledException(token);

            byteReservation.Commit(opened.MaterializedBytes);
            var persistentSegment = new ArchiveNestedSegment(
                parentFrame.Archive.Fingerprint,
                sourceNode.NormalizedPath!,
                descriptor.DuplicateOrdinal,
                opened.Archive.Fingerprint);
            var childFrame = new SessionFrame(
                opened.Archive,
                childIndex,
                PersistentKeysEnabled: true,
                sourceNode.Name,
                sourceNode.Id,
                persistentSegment,
                opened.MaterializedBytes,
                opened.ChainMaterializedBytes,
                byteReservation,
                opened.Depth);
            byteReservation = null!;
            childOwned = false;
            _sessionChain.Add(childFrame);
            var childCursor = new ArchiveCursor(_sessionChain.Count - 1, null);
            PushHistory(childCursor);
            ApplyCursor(childCursor);
            return ArchiveNestedOpenOutcome.Opened;
        }
        catch (ArchiveClientException exception)
        {
            if (exception.Error.Category == ArchiveErrorCategory.SourceChanged)
            {
                await CloseCurrentSessionAsync().ConfigureAwait(false);
                ClearSessionData();
                SetFailure(ArchiveLocationState.Stale,
                    "The archive source changed. Reopen it before continuing.");
                return ArchiveNestedOpenOutcome.Stale;
            }

            ArchiveLocationState mapped = MapState(exception.Error.Category);
            Status = exception.Error.Category switch
            {
                ArchiveErrorCategory.PasswordRequired or ArchiveErrorCategory.IncorrectPassword =>
                    "A password is required to open the selected nested archive.",
                ArchiveErrorCategory.WorkerFailure or ArchiveErrorCategory.ProtocolMismatch =>
                    "The Rain Archives Worker is unavailable. Reopen the archive after reconnecting.",
                ArchiveErrorCategory.ResourceLimit =>
                    "The nested archive exceeds the bounded depth or byte limits.",
                ArchiveErrorCategory.UnsupportedCapability =>
                    "Nested archive opening is unavailable for this parent archive.",
                ArchiveErrorCategory.UnsupportedFormat or ArchiveErrorCategory.InvalidArchive
                    or ArchiveErrorCategory.CorruptArchive =>
                    "The selected entry is not a supported archive.",
                _ => "Rain Archives could not open the selected nested archive safely.",
            };
            if (mapped == ArchiveLocationState.Offline && _client is { } failedClient)
            {
                await _runtime.InvalidateAsync(failedClient).ConfigureAwait(false);
                ClearSessionData();
                SetFailure(ArchiveLocationState.Offline, Status);
                return ArchiveNestedOpenOutcome.Offline;
            }
            Changed?.Invoke(this, EventArgs.Empty);
            return mapped switch
            {
                ArchiveLocationState.PasswordRequired => ArchiveNestedOpenOutcome.PasswordRequired,
                ArchiveLocationState.Offline => ArchiveNestedOpenOutcome.Offline,
                ArchiveLocationState.Stale => ArchiveNestedOpenOutcome.Stale,
                ArchiveLocationState.Unsupported => ArchiveNestedOpenOutcome.Refused,
                _ => ArchiveNestedOpenOutcome.Error,
            };
        }
        catch (OperationCanceledException)
        {
            if (!_disposed && IsCurrentOperation(operationVersion))
            {
                Status = "Nested archive opening was cancelled.";
                Changed?.Invoke(this, EventArgs.Empty);
            }
            return ArchiveNestedOpenOutcome.Cancelled;
        }
        catch (Exception exception) when (exception is IOException
                                              or InvalidDataException
                                              or ObjectDisposedException)
        {
            Status = exception is IOException or ObjectDisposedException
                ? "The Rain Archives Worker is unavailable. Reopen the archive after reconnecting."
                : "Rain Archives refused an invalid nested archive response.";
            if (exception is IOException or ObjectDisposedException && _client is { } failedClient)
            {
                await _runtime.InvalidateAsync(failedClient).ConfigureAwait(false);
                ClearSessionData();
                SetFailure(ArchiveLocationState.Offline, Status);
                return ArchiveNestedOpenOutcome.Offline;
            }
            Changed?.Invoke(this, EventArgs.Empty);
            return exception is IOException or ObjectDisposedException
                ? ArchiveNestedOpenOutcome.Offline
                : ArchiveNestedOpenOutcome.Error;
        }
        finally
        {
            if (childOwned && opened is not null && _client is { } client)
                await _runtime.CloseSessionAsync(client, opened.Archive.SessionId).ConfigureAwait(false);
            byteReservation?.Dispose();
            if (!slotsTransferred && _client is { } slotClient)
            {
                if (nestedPasswordSlot is Guid nestedSlot)
                    await SettleUntransferredSecretAsync(slotClient, nestedSlot).ConfigureAwait(false);
            }
            lock (_operationSync)
            {
                if (ReferenceEquals(_operationCancellation, operationCancellation))
                    _operationCancellation = null;
            }
            operationCancellation.Dispose();
        }
    }

    private bool TryResolveNestedSource(
        FileItem? item,
        out SessionFrame parentFrame,
        out Node sourceNode,
        out long maximumBytes,
        out string refusal)
    {
        parentFrame = null!;
        sourceNode = null!;
        maximumBytes = 0;
        refusal = "Only one current material file can be opened as a nested archive.";

        IArchiveClient? client = _client;
        ArchiveDescriptor? archive = _archive;
        if (_disposed || !IsBrowsable || client is null || archive is null
            || _currentDepth < 0 || _currentDepth >= _sessionChain.Count
            || ReferenceEquals(Volatile.Read(ref _invalidatingClient), client))
            return false;
        lock (_archiveOperationSync)
        {
            if (_activeArchiveOperation is not null)
            {
                refusal = "Wait for the current archive operation to finish before opening a nested archive.";
                return false;
            }
        }

        parentFrame = _sessionChain[_currentDepth];
        if (parentFrame.Archive.SessionId != archive.SessionId
            || !FingerprintsEqual(parentFrame.Archive.Fingerprint, archive.Fingerprint)
            || !parentFrame.PersistentKeysEnabled
            || !client.SupportsFeature(ProtocolFeatures.PersistentEntryKeys)
            || !client.SupportsFeature(ProtocolFeatures.NestedArchiveOpen)
            || !client.SupportsFeature(ProtocolFeatures.RequestCancellation)
            || !client.SupportsFeature(ProtocolFeatures.SourceFingerprintValidation)
            || !client.SupportsFeature(ProtocolFeatures.VolumeSetIdentity))
        {
            refusal = "Nested archive opening is unavailable with the connected Worker.";
            return false;
        }
        if (_currentDepth >= ProtocolConstants.MaximumNestedArchiveDepth)
        {
            refusal = "The nested archive depth limit has been reached.";
            return false;
        }
        if (archive.IsMultipart || archive.VolumeSetKind != ArchiveVolumeSetKind.Single)
        {
            refusal = "Nested archive opening is unavailable for multipart or uncertain-volume archives.";
            return false;
        }
        if (archive.IsSolid
            || !archive.Capabilities.HasFlag(ArchiveCapability.Extract)
            || !archive.Capabilities.HasFlag(ArchiveCapability.OpenEntryStream))
        {
            refusal = "Nested archive opening requires a non-solid random-access parent archive.";
            return false;
        }
        if (item is not { IsArchiveEntry: true, IsDirectory: false, ArchiveNodeId: not null }
            || !_nodes.TryGetValue(item.ArchiveNodeId, out Node? resolvedSource)
            || resolvedSource.IsImplicit
            || resolvedSource.Descriptor is not { Kind: ArchiveEntryKind.File } descriptor
            || descriptor.ProviderOrdinal < 0
            || descriptor.SafetyStatus != EntrySafetyStatus.Safe
            || descriptor.IsSolid
            || resolvedSource.NormalizedPath is null
            || !ArchiveLocationSerializer.IsValidNormalizedArchivePath(resolvedSource.NormalizedPath)
            || descriptor.DuplicateOrdinal < 0)
            return false;
        sourceNode = resolvedSource;
        if (descriptor.IsEncrypted)
        {
            refusal = "Nested archive opening does not support encrypted parent entries in this prompt.";
            return false;
        }
        string sourceNodeId = sourceNode.Id;
        if (!Items.Any(visible => ReferenceEquals(visible, item)
                && string.Equals(visible.ArchiveNodeId, sourceNodeId, StringComparison.Ordinal)))
            return false;

        if (descriptor.OriginalSize is not long declaredBytes
            || declaredBytes <= 0
            || declaredBytes > ProtocolConstants.MaximumNestedArchiveBytes)
        {
            refusal = "The selected entry does not have a safe bounded nested-archive size.";
            return false;
        }
        if (parentFrame.ChainMaterializedBytes < 0
            || parentFrame.ChainMaterializedBytes
                > ProtocolConstants.MaximumNestedArchiveChainBytes - declaredBytes)
        {
            refusal = "The nested archive chain has reached its bounded byte limit.";
            return false;
        }

        maximumBytes = declaredBytes;
        return true;
    }

    private bool IsCurrentNestedAuthority(
        SessionFrame expectedParent,
        Node expectedSource,
        FileItem? expectedItem) =>
        _client is not null
        && _archive is not null
        && _currentDepth >= 0
        && _currentDepth < _sessionChain.Count
        && ReferenceEquals(_sessionChain[_currentDepth], expectedParent)
        && _archive.SessionId == expectedParent.Archive.SessionId
        && FingerprintsEqual(_archive.Fingerprint, expectedParent.Archive.Fingerprint)
        && _nodes.TryGetValue(expectedSource.Id, out Node? currentSource)
        && ReferenceEquals(currentSource, expectedSource)
        && Items.Any(item => ReferenceEquals(item, expectedItem)
            && string.Equals(item.ArchiveNodeId, expectedSource.Id, StringComparison.Ordinal));

    private void ValidateNestedResponse(
        OpenNestedArchiveResponse response,
        SessionFrame parent,
        ArchiveEntryDescriptor source,
        long requestedMaximumBytes,
        IReadOnlyList<SessionFrame>? authorityChain = null)
    {
        authorityChain ??= _sessionChain;
        ArchiveDescriptor child = response.Archive;
        if (child is null
            || child.SessionId.Value == Guid.Empty
            || response.ParentSessionId != parent.Archive.SessionId
            || response.SourceEntryId != source.EntryId
            || response.Depth != parent.Depth + 1
            || response.Depth > ProtocolConstants.MaximumNestedArchiveDepth
            || response.MaterializedBytes <= 0
            || response.MaterializedBytes > requestedMaximumBytes
            || response.ChainMaterializedBytes
                != parent.ChainMaterializedBytes + response.MaterializedBytes
            || response.ChainMaterializedBytes > ProtocolConstants.MaximumNestedArchiveChainBytes
            || child.Fingerprint is null
            || child.Fingerprint.Length != response.MaterializedBytes
            || !IsValidArchiveFingerprint(child.Fingerprint)
            || string.IsNullOrWhiteSpace(child.Format)
            || string.IsNullOrWhiteSpace(child.ProviderId)
            || child.EntryCount is < 0 or > MaximumIndexedEntries
            || !child.Capabilities.HasFlag(ArchiveCapability.List)
            || child.IsMultipart
            || child.VolumeSetKind != ArchiveVolumeSetKind.Single
            || authorityChain.Any(frame => frame.Archive.SessionId == child.SessionId))
            throw new InvalidDataException("The Worker returned an invalid nested archive response.");
    }

    private static bool IsValidArchiveFingerprint(ArchiveFingerprint fingerprint) =>
        fingerprint.Length > 0
        && fingerprint.LastWriteTimeUtc.Offset == TimeSpan.Zero
        && (fingerprint.FileIdentity is null || fingerprint.FileIdentity.Length <= 1024)
        && fingerprint.PrefixSha256.Length == 64
        && fingerprint.PrefixSha256.All(character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private async Task<bool> CloseDescendantsAfterAsync(int parentDepth)
    {
        if (_client is null) return _sessionChain.Count == 0;
        IArchiveClient client = _client;
        bool acknowledged = true;
        for (int index = _sessionChain.Count - 1; index > parentDepth; index--)
        {
            SessionFrame frame = _sessionChain[index];
            bool closed = await _runtime.CloseSessionAsync(client, frame.Archive.SessionId)
                .ConfigureAwait(false);
            frame.ByteReservation?.Dispose();
            _sessionChain.RemoveAt(index);
            if (!closed)
            {
                acknowledged = false;
                break;
            }
        }

        if (!acknowledged)
        {
            foreach (SessionFrame frame in _sessionChain)
                frame.ByteReservation?.Dispose();
            _sessionChain.Clear();
        }
        PruneHistoryBeyondDepth(parentDepth);
        return acknowledged;
    }

    private void PruneHistoryBeyondDepth(int maximumDepth)
    {
        for (int index = _history.Count - 1; index >= 0; index--)
        {
            if (_history[index].ArchiveDepth <= maximumDepth) continue;
            _history.RemoveAt(index);
            if (index <= _historyIndex) _historyIndex--;
        }
        _historyIndex = Math.Min(_historyIndex, _history.Count - 1);
    }

    public bool GoBack()
    {
        if (!CanGoBack) return false;
        _ = InvalidatePreview(ArchiveEntryPreviewState.Cancelled);
        _historyIndex--;
        ApplyCursor(_history[_historyIndex]);
        return true;
    }

    public bool GoForward()
    {
        if (!CanGoForward) return false;
        _ = InvalidatePreview(ArchiveEntryPreviewState.Cancelled);
        _historyIndex++;
        ApplyCursor(_history[_historyIndex]);
        return true;
    }

    public bool GoUp()
    {
        if (!CanGoUp) return false;
        if (_currentNodeId is not null && _nodes.TryGetValue(_currentNodeId, out Node? current))
            return Navigate(_currentDepth, current.ParentId);
        if (_currentDepth <= 0) return false;

        SessionFrame child = _sessionChain[_currentDepth];
        SessionFrame parent = _sessionChain[_currentDepth - 1];
        string? parentFolder = child.SourceNodeId is not null
            && parent.Index?.Nodes.TryGetValue(child.SourceNodeId, out Node? source) == true
                ? source.ParentId
                : null;
        return Navigate(_currentDepth - 1, parentFolder);
    }

    private void PushHistory(ArchiveCursor cursor)
    {
        if (_historyIndex + 1 < _history.Count)
            _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
        _history.Add(cursor);
        if (_history.Count > MaximumHistoryEntries) _history.RemoveAt(0);
        _historyIndex = _history.Count - 1;
    }

    private void ApplyCursor(ArchiveCursor cursor)
    {
        SessionFrame frame = _sessionChain[cursor.ArchiveDepth];
        _currentDepth = cursor.ArchiveDepth;
        _archive = frame.Archive;
        _persistentKeysEnabled = frame.PersistentKeysEnabled;
        _nodes.Clear();
        _roots.Clear();
        if (frame.Index is not null)
        {
            foreach (var pair in frame.Index.Nodes) _nodes.Add(pair.Key, pair.Value);
            _roots.AddRange(frame.Index.Roots);
        }
        _currentNodeId = cursor.NodeId;
        _pageIndex = 0;
        _searchText = string.Empty;
        _recursiveSearch = false;
        Title = frame.Title;
        lock (_archiveOperationSync) _selectedArchiveNodeIds.Clear();

        if (!frame.Archive.Capabilities.HasFlag(ArchiveCapability.List))
        {
            State = ArchiveLocationState.Unsupported;
            Status = "This archive session does not allow entry listing.";
            Items = Array.Empty<FileItem>();
            Breadcrumbs = BuildBreadcrumbs();
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }
        RebuildPresentation();
    }

    public void SetSearch(string text, bool recursive)
    {
        if (!IsBrowsable) return;
        BeginArchiveTransferInvalidation(ArchiveTransferReleaseReason.SelectionChanged);
        _ = InvalidatePreview(ArchiveEntryPreviewState.Cancelled);
        _searchText = text ?? string.Empty;
        _recursiveSearch = recursive;
        _pageIndex = 0;
        RebuildPresentation();
    }

    public bool MovePage(int direction)
    {
        int requested = _pageIndex + Math.Sign(direction);
        int count = MatchingNodes().Count;
        if (requested < 0 || (long)requested * MaximumVisibleItems >= count) return false;
        BeginArchiveTransferInvalidation(ArchiveTransferReleaseReason.SelectionChanged);
        _ = InvalidatePreview(ArchiveEntryPreviewState.Cancelled);
        _pageIndex = requested;
        RebuildPresentation();
        return true;
    }

    public bool TryGetPersistentTarget(out string target)
    {
        target = string.Empty;
        if (_archive is null || _sourcePath is null || !_persistentKeysEnabled || !IsBrowsable)
            return false;
        return ArchiveLocationSerializer.TrySerialize(CreateLocationForCurrentFolder(), out target);
    }

    public bool TryGetPersistentTarget(FileItem item, out string target)
    {
        target = string.Empty;
        if (item is not { IsArchiveEntry: true, IsDirectory: true, ArchiveNodeId: not null }
            || !_nodes.TryGetValue(item.ArchiveNodeId, out var node)
            || _archive is null || _sourcePath is null || !_persistentKeysEnabled)
            return false;
        return ArchiveLocationSerializer.TrySerialize(CreateLocation(node), out target);
    }

    /// <summary>
    /// Records only the newest visible archive selection. Worker entry identities are resolved
    /// again from the current descriptor index immediately before an operation is admitted.
    /// </summary>
    public void UpdateSelection(IReadOnlyList<FileItem> selectedItems)
    {
        ArgumentNullException.ThrowIfNull(selectedItems);
        var requested = selectedItems.Count <= MaximumVisibleItems
            ? selectedItems
                .Where(item => item is { IsArchiveEntry: true, ArchiveNodeId: not null })
                .Select(item => item.ArchiveNodeId!)
                .ToHashSet(StringComparer.Ordinal)
            : [];

        lock (_archiveOperationSync)
        {
            _selectedArchiveNodeIds.Clear();
            foreach (FileItem visible in Items)
            {
                if (visible.ArchiveNodeId is { } nodeId && requested.Remove(nodeId))
                    _selectedArchiveNodeIds.Add(nodeId);
            }
        }
        BeginArchiveTransferInvalidation(ArchiveTransferReleaseReason.SelectionChanged);
    }

    /// <summary>Takes ownership of <paramref name="password"/> and clears it after slot transfer.</summary>
    public Task<ArchiveOperationResult> TestIntegrityAsync(
        char[]? password = null,
        CancellationToken cancellationToken = default) =>
        RunArchiveOperationAsync(
            ActivityOperationKind.IntegrityTest,
            destinationPath: null,
            password,
            cancellationToken);

    /// <summary>Takes ownership of <paramref name="password"/> and clears it after slot transfer.</summary>
    public Task<ArchiveOperationResult> ExtractSelectionAsync(
        string destinationPath,
        char[]? password = null,
        CancellationToken cancellationToken = default) =>
        RunArchiveOperationAsync(
            ActivityOperationKind.Extraction,
            destinationPath,
            password,
            cancellationToken);

    /// <summary>
    /// Takes ownership of <paramref name="password"/> and asks the Worker to extract the
    /// complete current archive into one distinct absent destination.
    /// </summary>
    internal Task<ArchiveOperationResult> ExtractEntireArchiveAsync(
        string destinationPath,
        char[]? password = null,
        CancellationToken cancellationToken = default) =>
        RunArchiveOperationAsync(
            ActivityOperationKind.Extraction,
            destinationPath,
            password,
            cancellationToken,
            extractEntireArchive: true);

    public async Task<ArchiveRebuildCapabilitySnapshot> LoadRebuildCapabilitiesAsync(
        CancellationToken cancellationToken = default)
    {
        IArchiveClient? failedClient = null;
        bool invalidateClient = false;
        ArchiveSessionId staleSession = default;
        ArchiveFingerprint? staleFingerprint = null;
        await _openGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed || State == ArchiveLocationState.Stale)
                return ArchiveRebuildCapabilitySnapshot.Fixed(ArchiveWriteCapabilityState.Stale);
            if (_client is null || State == ArchiveLocationState.Offline
                || ReferenceEquals(Volatile.Read(ref _invalidatingClient), _client))
                return ArchiveRebuildCapabilitySnapshot.Fixed(ArchiveWriteCapabilityState.Offline);
            if (!TryGetRebuildAuthority(
                    _client,
                    out ArchiveDescriptor? archive,
                    out string sourcePath))
                return ArchiveRebuildCapabilitySnapshot.Fixed(ArchiveWriteCapabilityState.Unavailable);

            failedClient = _client;
            GetZipWriteCapabilitiesResponse capabilities = await _client
                .GetZipWriteCapabilitiesAsync(archive.SessionId, cancellationToken)
                .ConfigureAwait(false);
            if (!IsSafeRebuildCapability(capabilities))
                return ArchiveRebuildCapabilitySnapshot.Fixed(ArchiveWriteCapabilityState.Unavailable);
            return new ArchiveRebuildCapabilitySnapshot(
                ArchiveWriteCapabilityState.Ready,
                archive.SessionId,
                archive.Fingerprint,
                sourcePath,
                capabilities.CompressionPresets.ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ArchiveClientException exception)
        {
            if (exception.Error.Category is ArchiveErrorCategory.WorkerFailure
                or ArchiveErrorCategory.ProtocolMismatch)
            {
                invalidateClient = true;
                return ArchiveRebuildCapabilitySnapshot.Fixed(ArchiveWriteCapabilityState.Offline);
            }
            if (exception.Error.Category == ArchiveErrorCategory.SourceChanged
                && _archive is { } staleArchive)
            {
                staleSession = staleArchive.SessionId;
                staleFingerprint = staleArchive.Fingerprint;
            }
            return ArchiveRebuildCapabilitySnapshot.Fixed(
                exception.Error.Category == ArchiveErrorCategory.SourceChanged
                    ? ArchiveWriteCapabilityState.Stale
                    : exception.Error.Category is ArchiveErrorCategory.UnsupportedCapability
                        or ArchiveErrorCategory.UnsupportedFormat
                        or ArchiveErrorCategory.InvalidRequest
                        ? ArchiveWriteCapabilityState.Unavailable
                        : ArchiveWriteCapabilityState.Error);
        }
        catch (Exception exception) when (exception is IOException
                                              or InvalidDataException
                                              or ObjectDisposedException)
        {
            invalidateClient = true;
            return ArchiveRebuildCapabilitySnapshot.Fixed(ArchiveWriteCapabilityState.Offline);
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or NotSupportedException
                                              or UnauthorizedAccessException)
        {
            return ArchiveRebuildCapabilitySnapshot.Fixed(ArchiveWriteCapabilityState.Unavailable);
        }
        finally
        {
            _openGate.Release();
            if (invalidateClient && failedClient is not null)
                await InvalidatePreviewClientAsync(failedClient).ConfigureAwait(false);
            else if (failedClient is not null && staleFingerprint is not null)
                await TransitionPreviewSourceChangedAsync(
                        failedClient,
                        staleSession,
                        staleFingerprint)
                    .ConfigureAwait(false);
        }
    }

    public Task<ArchiveOperationResult> RebuildCopyAsync(
        ArchiveRebuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunArchiveOperationAsync(
            ActivityOperationKind.ArchiveRebuild,
            request.DestinationPath,
            password: null,
            cancellationToken,
            rebuildRequest: request);
    }

    internal async Task<ArchiveTransferOwner?> BeginArchiveTransferCoordinatorAsync(
        CancellationToken cancellationToken = default)
    {
        if (!CanStartArchiveTransfer()) return null;
        return await _archiveTransferCoordinator.BeginAsync(cancellationToken).ConfigureAwait(false);
    }

    private bool CanStartArchiveTransfer()
    {
        if (!ArchiveOleTransferGate.HasWorkerPublicationIdentity) return false;
        if (!CanStartArchiveOperation(ArchiveCapability.Extract, requireSelection: true)) return false;
        lock (_archiveOperationSync)
            return TryResolveSelectedEntryIds(
                requireExactTransferManifest: true,
                out _,
                out _);
    }

    private Task<ArchiveOperationResult> ExecuteArchiveTransferAsync(
        ArchiveTransferAllocation allocation,
        ArchiveTransferStorage storage,
        ArchiveTransferExtractionProgress progress,
        char[]? password,
        CancellationToken cancellationToken) =>
        ArchiveOleTransferGate.HasWorkerPublicationIdentity
            ? RunArchiveOperationAsync(
            ActivityOperationKind.TransferOut,
            allocation.DestinationPath,
            password,
            cancellationToken,
            progress,
            storage,
            allocation)
            : Task.FromResult(new ArchiveOperationResult(ArchiveOperationOutcome.Unsupported));

    private bool CanStartArchiveOperation(ArchiveCapability capability, bool requireSelection)
    {
        ArchiveDescriptor? archive = _archive;
        IArchiveClient? client = _client;
        if (_disposed || archive is null || client is null || !IsBrowsable
            || ReferenceEquals(Volatile.Read(ref _invalidatingClient), client)
            || archive.VolumeSetKind != ArchiveVolumeSetKind.Single
            || archive.IsMultipart
            || !archive.Capabilities.HasFlag(capability)
            || !SupportsArchiveOperationProtocol(client, capability))
            return false;

        lock (_archiveOperationSync)
        {
            if (_activeArchiveOperation is not null) return false;
            return !requireSelection || TryResolveSelectedEntryIds(out _);
        }
    }

    private bool CanStartRebuildCopy()
    {
        IArchiveClient? client = _client;
        if (client is null || !TryGetRebuildAuthority(client, out _, out _)) return false;
        lock (_archiveOperationSync) return _activeArchiveOperation is null;
    }

    private bool TryGetRebuildAuthority(
        IArchiveClient client,
        out ArchiveDescriptor archive,
        out string sourcePath)
    {
        archive = null!;
        sourcePath = string.Empty;
        if (_disposed || !IsBrowsable || State == ArchiveLocationState.Stale
            || !ReferenceEquals(_client, client)
            || ReferenceEquals(Volatile.Read(ref _invalidatingClient), client)
            || _archive is not { } current
            || _sourcePath is not { Length: > 0 } currentSource
            || _currentDepth != 0
            || _sessionChain.Count != 1
            || !string.Equals(current.Format, "zip", StringComparison.OrdinalIgnoreCase)
            || current.VolumeSetKind != ArchiveVolumeSetKind.Single
            || current.IsMultipart
            || current.HasEncryptedEntries
            || !current.Capabilities.HasFlag(ArchiveCapability.UpdateByRewrite)
            || !SupportsRebuildProtocol(client))
            return false;
        archive = current;
        sourcePath = currentSource;
        return true;
    }

    private static bool SupportsRebuildProtocol(IArchiveClient client) =>
        client.SupportsFeature(ProtocolFeatures.OperationEvents)
        && client.SupportsFeature(ProtocolFeatures.RequestCancellation)
        && client.SupportsFeature(ProtocolFeatures.SourceFingerprintValidation)
        && client.SupportsFeature(ProtocolFeatures.VolumeSetIdentity)
        && client.SupportsFeature(ProtocolFeatures.OperationActivity)
        && client.SupportsFeature(ProtocolFeatures.ZipRebuildCopy)
        && client.SupportsFeature(ProtocolFeatures.ZipWriteCapabilities);

    private static bool IsSafeRebuildCapability(GetZipWriteCapabilitiesResponse capability) =>
        capability.CanRebuild
        && capability.CompressionPresets.Count > 0
        && capability.MaximumEntries is > 0 and <= ProtocolConstants.MaximumZipCreationEntries
        && capability.MaximumSourceBytes is > 0 and <= ProtocolConstants.MaximumZipCreationSourceBytes
        && !capability.SupportsEncryption
        && !capability.SupportsFilenameEncryption
        && !capability.SupportsSplitOutput
        && !capability.SupportsExistingDestinationReplacement
        && !capability.SupportsInPlaceUpdate;

    private static bool RebuildAuthorityMatches(
        ArchiveRebuildCapabilitySnapshot expected,
        ArchiveDescriptor current,
        string currentSource) =>
        expected.IsReady
        && expected.SessionId == current.SessionId
        && expected.Fingerprint is not null
        && FingerprintsEqual(expected.Fingerprint, current.Fingerprint)
        && string.Equals(expected.SourcePath, currentSource, StringComparison.OrdinalIgnoreCase);

    private async Task<ArchiveOperationResult> RunArchiveOperationAsync(
        ActivityOperationKind kind,
        string? destinationPath,
        char[]? password,
        CancellationToken cancellationToken,
        ArchiveTransferExtractionProgress? transferProgress = null,
        ArchiveTransferStorage? transferStorage = null,
        ArchiveTransferAllocation? transferAllocation = null,
        ArchiveRebuildRequest? rebuildRequest = null,
        bool extractEntireArchive = false)
    {
        var passwordOwner = new OneShotOperationPassword(password);
        ActiveArchiveOperation? operation = null;
        try
        {
            lock (_archiveOperationSync)
            {
                if (_activeArchiveOperation is not null)
                    return new ArchiveOperationResult(ArchiveOperationOutcome.Busy);
                operation = new ActiveArchiveOperation(kind, _activitySink.Begin(kind));
                _activeArchiveOperation = operation;
            }

            try
            {
                operation.Activity.AttachCancel(() => RequestArchiveOperationCancellation(operation));
                using CancellationTokenRegistration callerCancellation = cancellationToken.Register(
                    static state =>
                    {
                        var tuple = ((ArchiveLocationAdapter Adapter, ActiveArchiveOperation Operation))state!;
                        tuple.Adapter.RequestArchiveOperationCancellation(tuple.Operation);
                    },
                    (this, operation));
                return await ExecuteArchiveOperationAsync(
                    operation,
                    destinationPath,
                    passwordOwner,
                    transferProgress,
                    transferStorage,
                    transferAllocation,
                    rebuildRequest,
                    extractEntireArchive).ConfigureAwait(false);
            }
            finally
            {
                lock (_archiveOperationSync)
                {
                    if (ReferenceEquals(_activeArchiveOperation, operation))
                        _activeArchiveOperation = null;
                }
                operation.AdmissionCancellation.Dispose();
                operation.TerminalWaitCancellation.Dispose();
            }
        }
        finally
        {
            passwordOwner.Clear();
            operation?.Drained.TrySetResult();
        }
    }

    private async Task<ArchiveOperationResult> ExecuteArchiveOperationAsync(
        ActiveArchiveOperation operation,
        string? requestedDestination,
        OneShotOperationPassword password,
        ArchiveTransferExtractionProgress? transferProgress,
        ArchiveTransferStorage? transferStorage,
        ArchiveTransferAllocation? transferAllocation,
        ArchiveRebuildRequest? rebuildRequest,
        bool extractEntireArchive)
    {
        operation.Activity.Report(new ActivityOperationUpdate(
            operation.Kind, ActivityOperationPhase.Validating));

        string? destination = null;
        ArchiveOperationOutcome destinationFailure = ArchiveOperationOutcome.InvalidDestination;
        if (IsExtractionKind(operation.Kind)
            && !TryValidateExtractionDestination(
                requestedDestination, out destination, out destinationFailure))
        {
            return FinishRefusal(operation, destinationFailure,
                destinationFailure == ArchiveOperationOutcome.DestinationConflict
                    ? ActivityOperationFailure.DestinationConflict
                    : ActivityOperationFailure.InvalidRequest);
        }
        if (operation.Kind == ActivityOperationKind.ArchiveRebuild)
        {
            if (rebuildRequest is null
                || !TryValidateRebuildDestination(
                    requestedDestination,
                    rebuildRequest.Authority.SourcePath,
                    out destination,
                    out destinationFailure))
            {
                return FinishRefusal(operation,
                    rebuildRequest is null
                        ? ArchiveOperationOutcome.InvalidRequest
                        : destinationFailure,
                    rebuildRequest is not null
                        && destinationFailure == ArchiveOperationOutcome.DestinationConflict
                        ? ActivityOperationFailure.DestinationConflict
                        : ActivityOperationFailure.InvalidRequest);
            }
        }

        OperationAuthority? initialAuthority = null;
        Guid? secretSlot = null;
        bool secretTransferred = false;
        EventHandler<OperationEvent>? eventHandler = null;
        try
        {
            initialAuthority = await CaptureOperationAuthorityAsync(
                    operation.Kind,
                    rebuildRequest,
                    extractEntireArchive,
                    operation.AdmissionCancellation.Token)
                .ConfigureAwait(false);
            if (initialAuthority.Failure is ArchiveOperationOutcome initialFailure)
                return FinishRefusal(operation, initialFailure, FailureForOutcome(initialFailure));

            operation.SetClient(initialAuthority.Client!);
            if (operation.IsCancellationRequested)
                return FinishCancellation(operation);

            char[]? suppliedPassword = password.Value;
            bool hasPassword = suppliedPassword is not null;
            if (suppliedPassword is not null && !IsPasswordWithinProtocolLimit(suppliedPassword))
            {
                return FinishRefusal(operation, ArchiveOperationOutcome.InvalidRequest,
                    ActivityOperationFailure.InvalidRequest);
            }
            if (!FitsOperationStartFrame(
                    operation.Kind,
                    initialAuthority,
                    destination,
                    hasPassword ? Guid.Empty : null,
                    rebuildRequest?.CompressionPreset))
            {
                return FinishRefusal(operation, ArchiveOperationOutcome.InvalidSelection,
                    ActivityOperationFailure.InvalidRequest);
            }

            if (suppliedPassword is not null)
            {
                try
                {
                    if (!initialAuthority.Client!.SupportsFeature(ProtocolFeatures.SecretSlots)
                        || !initialAuthority.Client.SupportsFeature(ProtocolFeatures.SecretSlotDiscard))
                    {
                        return FinishRefusal(operation, ArchiveOperationOutcome.Unsupported,
                            ActivityOperationFailure.Unsupported);
                    }

                    secretSlot = await initialAuthority.Client.CreateSecretAsync(
                        suppliedPassword.AsMemory(), operation.AdmissionCancellation.Token).ConfigureAwait(false);
                }
                finally
                {
                    password.Clear();
                }
            }

            if (IsExtractionKind(operation.Kind)
                && !TryValidateExtractionDestination(
                    destination, out destination, out destinationFailure))
            {
                return FinishRefusal(operation, destinationFailure,
                    destinationFailure == ArchiveOperationOutcome.DestinationConflict
                        ? ActivityOperationFailure.DestinationConflict
                        : ActivityOperationFailure.InvalidRequest);
            }
            if (operation.Kind == ActivityOperationKind.ArchiveRebuild
                && (!TryValidateRebuildDestination(
                        destination,
                        rebuildRequest!.Authority.SourcePath,
                        out destination,
                        out destinationFailure)))
            {
                return FinishRefusal(operation, destinationFailure,
                    destinationFailure == ArchiveOperationOutcome.DestinationConflict
                        ? ActivityOperationFailure.DestinationConflict
                        : ActivityOperationFailure.InvalidRequest);
            }

            OperationAuthority finalAuthority = await CaptureOperationAuthorityAsync(
                    operation.Kind,
                    rebuildRequest,
                    extractEntireArchive,
                    operation.AdmissionCancellation.Token)
                .ConfigureAwait(false);
            if (finalAuthority.Failure is ArchiveOperationOutcome finalFailure)
                return FinishRefusal(operation, finalFailure, FailureForOutcome(finalFailure));
            if (!ReferenceEquals(initialAuthority.Client, finalAuthority.Client)
                || initialAuthority.SessionId != finalAuthority.SessionId
                || initialAuthority.Fingerprint is null
                || finalAuthority.Fingerprint is null
                || !FingerprintsEqual(initialAuthority.Fingerprint, finalAuthority.Fingerprint)
                || !initialAuthority.EntryIds.SequenceEqual(finalAuthority.EntryIds)
                || operation.Kind == ActivityOperationKind.ArchiveRebuild
                && (!string.Equals(
                        initialAuthority.SourcePath,
                        finalAuthority.SourcePath,
                        StringComparison.OrdinalIgnoreCase)
                    || initialAuthority.ZipWriteCapabilities is null
                    || finalAuthority.ZipWriteCapabilities is null)
                || operation.Kind == ActivityOperationKind.TransferOut
                && !TransferSelectionsEqual(
                    initialAuthority.TransferSelection,
                    finalAuthority.TransferSelection))
            {
                return FinishRefusal(operation, ArchiveOperationOutcome.Stale,
                    ActivityOperationFailure.SourceChanged);
            }
            if (!FitsOperationStartFrame(
                    operation.Kind,
                    finalAuthority,
                    destination,
                    secretSlot,
                    rebuildRequest?.CompressionPreset))
            {
                return FinishRefusal(operation, ArchiveOperationOutcome.InvalidSelection,
                    ActivityOperationFailure.InvalidRequest);
            }

            operation.SetAuthority(finalAuthority);
            if (operation.Kind == ActivityOperationKind.TransferOut)
            {
                if (transferProgress is null
                    || transferStorage is null
                    || transferAllocation is null
                    || finalAuthority.TransferSelection is null)
                {
                    return FinishRefusal(operation, ArchiveOperationOutcome.InvalidSelection,
                        ActivityOperationFailure.InvalidRequest);
                }
                transferStorage.ValidateSelectionForAdmission(finalAuthority.TransferSelection);
                transferProgress.BindSelection(finalAuthority.TransferSelection);
            }
            eventHandler = (_, operationEvent) =>
                ReceiveOperationEvent(operation, operationEvent);
            finalAuthority.Client!.OperationEventReceived += eventHandler;

            if (operation.IsCancellationRequested)
                return FinishCancellation(operation);

            if (operation.Kind == ActivityOperationKind.ArchiveRebuild)
            {
                if (!TryValidateRebuildDestination(
                        destination,
                        finalAuthority.SourcePath!,
                        out destination,
                        out destinationFailure))
                {
                    return FinishRefusal(operation, destinationFailure,
                        destinationFailure == ArchiveOperationOutcome.DestinationConflict
                            ? ActivityOperationFailure.DestinationConflict
                            : ActivityOperationFailure.InvalidRequest);
                }
                if (_rebuildBeforeStart is not null)
                    await _rebuildBeforeStart().ConfigureAwait(false);
                if (operation.IsCancellationRequested)
                    return FinishCancellation(operation);
            }

            secretTransferred = secretSlot is not null;
            if (operation.Kind == ActivityOperationKind.TransferOut)
                transferProgress!.MarkStartAttempted();
            StartOperationResponse started;
            if (IsExtractionKind(operation.Kind))
            {
                started = await finalAuthority.Client.StartExtractionAsync(
                        finalAuthority.SessionId,
                        finalAuthority.EntryIds,
                        destination!,
                        secretSlot,
                        operation.AdmissionCancellation.Token)
                    .ConfigureAwait(false);
            }
            else if (operation.Kind == ActivityOperationKind.ArchiveRebuild)
            {
                started = await finalAuthority.Client.StartZipRebuildCopyAsync(
                        finalAuthority.SessionId,
                        destination!,
                        [],
                        [],
                        rebuildRequest!.CompressionPreset,
                        operation.TerminalWaitCancellation.Token)
                    .ConfigureAwait(false);
            }
            else
            {
                started = await finalAuthority.Client.StartIntegrityTestAsync(
                        finalAuthority.SessionId,
                        [],
                        secretSlot,
                        operation.AdmissionCancellation.Token)
                    .ConfigureAwait(false);
            }

            operation.SetOperationId(started.OperationId,
                RequestArchiveOperationCancellation);
            operation.Activity.Report(operation.CreateUpdate(ActivityOperationPhase.Queued));

            OperationEvent terminal = await finalAuthority.Client.WaitForTerminalAsync(
                started.OperationId, operation.TerminalWaitCancellation.Token).ConfigureAwait(false);
            if (terminal.OperationId != started.OperationId
                || terminal.Kind != OperationEventKind.Terminal
                || terminal.Phase is not (OperationPhase.Completed
                    or OperationPhase.CompletedWithWarnings
                    or OperationPhase.Cancelled
                    or OperationPhase.Failed))
            {
                operation.ReportTerminal(
                    ActivityOperationPhase.Failed,
                    ActivityOperationFailure.ProtocolMismatch);
                await InvalidatePreviewClientAsync(finalAuthority.Client).ConfigureAwait(false);
                return new ArchiveOperationResult(ArchiveOperationOutcome.Offline);
            }

            await ApplyFinalActivitySnapshotAsync(
                finalAuthority.Client, operation, started.OperationId).ConfigureAwait(false);
            if (operation.Kind == ActivityOperationKind.TransferOut
                && terminal.Phase is OperationPhase.Completed or OperationPhase.CompletedWithWarnings)
            {
                transferProgress!.ConfirmPublished();
                try
                {
                    _ = await transferProgress.ReviewAndBindAsync(
                            transferStorage!,
                            transferAllocation!,
                            operation.AdmissionCancellation.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (!IsFatalArchiveTransferException(exception))
                {
                    operation.ReportTerminal(
                        ActivityOperationPhase.Failed,
                        ActivityOperationFailure.UnsafeOutput);
                    return new ArchiveOperationResult(ArchiveOperationOutcome.UnsafeOutput);
                }
                try
                {
                    if (_transferBeforeTerminalCompletion is not null)
                        await _transferBeforeTerminalCompletion().ConfigureAwait(false);
                    if (operation.IsCancellationRequested)
                        return FinishCancellation(operation);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (!IsFatalArchiveTransferException(exception))
                {
                    operation.ReportTerminal(
                        ActivityOperationPhase.Failed,
                        ActivityOperationFailure.UnsafeOutput);
                    return new ArchiveOperationResult(ArchiveOperationOutcome.UnsafeOutput);
                }
            }
            ArchiveOperationResult result = operation.Kind == ActivityOperationKind.TransferOut
                ? FinishTransferTerminal(operation, terminal)
                : FinishTerminal(operation, terminal);
            if (terminal.Error?.Category == ArchiveErrorCategory.SourceChanged)
            {
                await TransitionPreviewSourceChangedAsync(
                    finalAuthority.Client,
                    finalAuthority.SessionId,
                    finalAuthority.Fingerprint,
                    drainArchiveOperation: false).ConfigureAwait(false);
            }
            else if (terminal.Error?.Category is ArchiveErrorCategory.WorkerFailure
                     or ArchiveErrorCategory.ProtocolMismatch)
            {
                await InvalidatePreviewClientAsync(finalAuthority.Client).ConfigureAwait(false);
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            if (secretTransferred && operation.OperationId is null
                && initialAuthority?.Client is { } uncertainClient)
                await InvalidatePreviewClientAsync(uncertainClient).ConfigureAwait(false);
            return FinishCancellation(operation);
        }
        catch (ArchiveClientException exception)
        {
            ArchiveOperationResult result = FinishClientFailure(operation, exception.Error);
            if (exception.Error.Category == ArchiveErrorCategory.SourceChanged
                && initialAuthority is { Client: not null, Fingerprint: not null })
            {
                await TransitionPreviewSourceChangedAsync(
                    initialAuthority.Client,
                    initialAuthority.SessionId,
                    initialAuthority.Fingerprint,
                    drainArchiveOperation: false).ConfigureAwait(false);
            }
            else if (exception.Error.Category is (ArchiveErrorCategory.WorkerFailure
                         or ArchiveErrorCategory.ProtocolMismatch)
                     && initialAuthority?.Client is { } failedClient)
            {
                await InvalidatePreviewClientAsync(failedClient).ConfigureAwait(false);
            }
            return result;
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or IOException
                                              or InvalidDataException
                                              or NotSupportedException
                                              or ObjectDisposedException
                                              or UnauthorizedAccessException)
        {
            operation.ReportTerminal(
                ActivityOperationPhase.Failed,
                exception is IOException or ObjectDisposedException
                    ? ActivityOperationFailure.WorkerUnavailable
                    : exception is NotSupportedException
                        ? ActivityOperationFailure.Unsupported
                        : ActivityOperationFailure.InvalidRequest);
            if (exception is IOException or ObjectDisposedException
                && initialAuthority?.Client is { } failedClient)
                await InvalidatePreviewClientAsync(failedClient).ConfigureAwait(false);
            return new ArchiveOperationResult(
                exception is IOException or ObjectDisposedException
                    ? ArchiveOperationOutcome.Offline
                    : exception is NotSupportedException
                        ? ArchiveOperationOutcome.Unsupported
                        : ArchiveOperationOutcome.InvalidRequest);
        }
        finally
        {
            if (eventHandler is not null && initialAuthority?.Client is { } subscribedClient)
                subscribedClient.OperationEventReceived -= eventHandler;
            if (secretSlot is Guid untransferredSlot && !secretTransferred
                && initialAuthority?.Client is { } secretClient)
                await SettleUntransferredSecretAsync(secretClient, untransferredSlot)
                    .ConfigureAwait(false);
        }
    }

    private async Task<OperationAuthority> CaptureOperationAuthorityAsync(
        ActivityOperationKind kind,
        ArchiveRebuildRequest? rebuildRequest,
        bool extractEntireArchive,
        CancellationToken cancellationToken)
    {
        await _openGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed || State == ArchiveLocationState.Stale)
                return OperationAuthority.Refused(ArchiveOperationOutcome.Stale);
            if (_client is null || State == ArchiveLocationState.Offline
                || ReferenceEquals(Volatile.Read(ref _invalidatingClient), _client))
                return OperationAuthority.Refused(ArchiveOperationOutcome.Offline);
            if (_archive is null || !IsBrowsable)
                return OperationAuthority.Refused(ArchiveOperationOutcome.Unsupported);

            GetZipWriteCapabilitiesResponse? zipWriteCapabilities = null;
            string? sourcePath = null;
            if (kind == ActivityOperationKind.ArchiveRebuild)
            {
                if (rebuildRequest is null
                    || !TryGetRebuildAuthority(_client, out ArchiveDescriptor? rebuildArchive,
                        out string rebuildSource)
                    || !RebuildAuthorityMatches(rebuildRequest.Authority, rebuildArchive, rebuildSource))
                    return OperationAuthority.Refused(ArchiveOperationOutcome.Stale);
                zipWriteCapabilities = await _client.GetZipWriteCapabilitiesAsync(
                        rebuildArchive.SessionId,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!IsSafeRebuildCapability(zipWriteCapabilities)
                    || !zipWriteCapabilities.CompressionPresets.Contains(
                        rebuildRequest.CompressionPreset))
                    return OperationAuthority.Refused(ArchiveOperationOutcome.Unsupported);
                sourcePath = rebuildSource;
            }
            else
            {
                ArchiveCapability capability = IsExtractionKind(kind)
                    ? ArchiveCapability.Extract
                    : ArchiveCapability.Test;
                if (_archive.VolumeSetKind != ArchiveVolumeSetKind.Single
                    || _archive.IsMultipart
                    || !_archive.Capabilities.HasFlag(capability)
                    || !SupportsArchiveOperationProtocol(_client, capability))
                    return OperationAuthority.Refused(ArchiveOperationOutcome.Unsupported);
            }

            EntryId[] entryIds = [];
            ArchiveTransferSelectionManifest? transferSelection = null;
            lock (_archiveOperationSync)
            {
                if (extractEntireArchive && kind != ActivityOperationKind.Extraction)
                    return OperationAuthority.Refused(ArchiveOperationOutcome.InvalidRequest);
                if (IsExtractionKind(kind)
                    && !extractEntireArchive
                    && !TryResolveSelectedEntryIds(
                        requireExactTransferManifest: kind == ActivityOperationKind.TransferOut,
                        out entryIds,
                        out transferSelection))
                    return OperationAuthority.Refused(ArchiveOperationOutcome.InvalidSelection);
            }

            var names = new Dictionary<EntryId, string>();
            foreach (Node node in _nodes.Values)
            {
                if (node.Descriptor is { } descriptor)
                    names.TryAdd(descriptor.EntryId, node.Name);
            }
            return new OperationAuthority(
                _client,
                _archive.SessionId,
                _archive.Fingerprint,
                entryIds,
                names,
                transferSelection,
                sourcePath,
                zipWriteCapabilities,
                Failure: null);
        }
        finally
        {
            _openGate.Release();
        }
    }

    private bool TryResolveSelectedEntryIds(out EntryId[] entryIds) =>
        TryResolveSelectedEntryIds(
            requireExactTransferManifest: false,
            out entryIds,
            out _);

    private bool TryResolveSelectedEntryIds(
        bool requireExactTransferManifest,
        out EntryId[] entryIds,
        out ArchiveTransferSelectionManifest? transferSelection)
    {
        entryIds = [];
        transferSelection = null;
        if (_selectedArchiveNodeIds.Count == 0
            || _selectedArchiveNodeIds.Count > MaximumVisibleItems)
            return false;

        var visibleIds = Items
            .Where(item => item.ArchiveNodeId is not null)
            .Select(item => item.ArchiveNodeId!)
            .ToHashSet(StringComparer.Ordinal);
        var resolved = new List<EntryId>();
        var seen = new HashSet<EntryId>();
        var selectedNodes = new List<Node>();
        foreach (string nodeId in _selectedArchiveNodeIds)
        {
            if (!visibleIds.Contains(nodeId) || !_nodes.TryGetValue(nodeId, out Node? node))
                return false;
            if (!AddSelectedNode(node, resolved, seen)) return false;
            selectedNodes.Add(node);
        }

        if (resolved.Count == 0 || resolved.Count > MaximumIndexedEntries) return false;
        if (requireExactTransferManifest
            && !TryBuildTransferSelectionManifest(selectedNodes, out transferSelection))
            return false;
        entryIds = resolved.ToArray();
        return true;
    }

    private static bool AddSelectedNode(
        Node selected,
        List<EntryId> resolved,
        HashSet<EntryId> seen)
    {
        var stack = new Stack<Node>();
        stack.Push(selected);
        while (stack.Count > 0)
        {
            Node node = stack.Pop();
            if (node.Descriptor is { } descriptor)
            {
                if (descriptor.Kind is not (ArchiveEntryKind.File or ArchiveEntryKind.Directory))
                    return false;
                if (seen.Add(descriptor.EntryId)) resolved.Add(descriptor.EntryId);
                // The Worker authoritatively expands a material directory. Stop at that
                // identity so large subtrees do not become oversized control frames.
                continue;
            }
            if (!node.IsImplicit || !node.IsDirectory)
            {
                return false;
            }

            for (int index = node.Children.Count - 1; index >= 0; index--)
                stack.Push(node.Children[index]);
        }
        return true;
    }

    private static bool TryBuildTransferSelectionManifest(
        IReadOnlyList<Node> selectedNodes,
        out ArchiveTransferSelectionManifest? manifest)
    {
        manifest = null;
        var expected = new Dictionary<string, TransferExpectedBuilder>(
            StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var published = new List<string>(selectedNodes.Count);
        var pathBudget = new ArchiveTransferPathBudget();
        foreach (Node selected in selectedNodes)
        {
            if (selected.NormalizedPath is not { } selectedPath
                || !ArchiveLocationSerializer.IsValidNormalizedArchivePath(selectedPath)
                || published.Any(path => PathsOverlap(path, selectedPath))
                || !pathBudget.TryAdd(selectedPath))
                return false;
            published.Add(selectedPath);

            var pending = new Stack<Node>();
            pending.Push(selected);
            while (pending.Count > 0)
            {
                Node node = pending.Pop();
                if (!visited.Add(node.Id)) continue;
                if (node.NormalizedPath is not { } path
                    || !ArchiveLocationSerializer.IsValidNormalizedArchivePath(path))
                    return false;

                bool isDirectory = node.IsDirectory;
                long length = 0;
                if (node.Descriptor is { } descriptor)
                {
                    if (descriptor.Kind is not (ArchiveEntryKind.File or ArchiveEntryKind.Directory)
                        || descriptor.SafetyStatus != EntrySafetyStatus.Safe)
                        return false;
                    if (!isDirectory)
                    {
                        if (descriptor.OriginalSize is not long declaredLength) return false;
                        length = declaredLength;
                    }
                }
                else if (!isDirectory)
                {
                    return false;
                }

                if (!TryAddExpectedPath(
                        expected, pathBudget, path, isDirectory, length, concrete: true)
                    || expected.Count > MaximumIndexedEntries)
                    return false;
                if (!isDirectory) continue;
                for (int index = node.Children.Count - 1; index >= 0; index--)
                    pending.Push(node.Children[index]);
            }
        }

        if (expected.Count == 0 || published.Count == 0) return false;
        ArchiveTransferExpectedObject[] expectedObjects = expected.Values
                .Select(value => new ArchiveTransferExpectedObject(
                    value.Path,
                    value.IsDirectory,
                    value.Length))
                .OrderBy(value => value.RelativePath, StringComparer.Ordinal)
                .ToArray();
        manifest = new ArchiveTransferSelectionManifest(expectedObjects, published.ToArray());
        return true;
    }

    private static bool TryAddExpectedPath(
        Dictionary<string, TransferExpectedBuilder> expected,
        ArchiveTransferPathBudget pathBudget,
        string path,
        bool isDirectory,
        long length,
        bool concrete)
    {
        string[] components = path.Split('/');
        string current = string.Empty;
        for (int index = 0; index < components.Length - 1; index++)
        {
            current = current.Length == 0 ? components[index] : current + "/" + components[index];
            if (!TryAddExpectedObject(
                    expected, pathBudget, current, isDirectory: true, length: 0, concrete: false))
                return false;
        }
        return TryAddExpectedObject(expected, pathBudget, path, isDirectory, length, concrete);
    }

    private static bool TryAddExpectedObject(
        Dictionary<string, TransferExpectedBuilder> expected,
        ArchiveTransferPathBudget pathBudget,
        string path,
        bool isDirectory,
        long length,
        bool concrete)
    {
        if (!expected.TryGetValue(path, out TransferExpectedBuilder? existing))
        {
            if (!pathBudget.TryAdd(path)) return false;
            expected.Add(path, new TransferExpectedBuilder(path, isDirectory, length, concrete));
            return true;
        }
        if (!string.Equals(existing.Path, path, StringComparison.Ordinal)
            || existing.IsDirectory != isDirectory
            || existing.Length != length
            || concrete && existing.IsConcrete)
            return false;
        if (concrete) existing.IsConcrete = true;
        return true;
    }

    private static bool PathsOverlap(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
        || left.StartsWith(right + "/", StringComparison.OrdinalIgnoreCase)
        || right.StartsWith(left + "/", StringComparison.OrdinalIgnoreCase);

    private static bool TransferSelectionsEqual(
        ArchiveTransferSelectionManifest? left,
        ArchiveTransferSelectionManifest? right) =>
        left is not null
        && right is not null
        && left.ExpectedObjects.SequenceEqual(right.ExpectedObjects)
        && left.PublishedRelativePaths.SequenceEqual(
            right.PublishedRelativePaths,
            StringComparer.Ordinal);

    private static bool IsExtractionKind(ActivityOperationKind kind) =>
        kind is ActivityOperationKind.Extraction or ActivityOperationKind.TransferOut;

    private static OperationKind WorkerOperationKind(ActivityOperationKind kind) => kind switch
    {
        ActivityOperationKind.Extraction or ActivityOperationKind.TransferOut =>
            OperationKind.Extract,
        ActivityOperationKind.ArchiveCreation => OperationKind.CreateArchive,
        ActivityOperationKind.ArchiveRebuild => OperationKind.RebuildArchive,
        _ => OperationKind.IntegrityTest,
    };

    private static bool IsFatalArchiveTransferException(Exception exception) => exception is
        OutOfMemoryException or AccessViolationException;

    private static bool SupportsArchiveOperationProtocol(
        IArchiveClient client,
        ArchiveCapability capability) =>
        client.SupportsFeature(ProtocolFeatures.OperationEvents)
        && client.SupportsFeature(ProtocolFeatures.RequestCancellation)
        && client.SupportsFeature(ProtocolFeatures.SourceFingerprintValidation)
        && client.SupportsFeature(ProtocolFeatures.VolumeSetIdentity)
        && client.SupportsFeature(ProtocolFeatures.OperationActivity)
        && (capability != ArchiveCapability.Extract
            || client.SupportsFeature(ProtocolFeatures.NewDestinationExtraction));

    private static bool IsPasswordWithinProtocolLimit(char[] password)
    {
        if (password.Length > ProtocolConstants.MaximumSecretBytes) return false;
        try
        {
            return Encoding.UTF8.GetByteCount(password.AsSpan())
                <= ProtocolConstants.MaximumSecretBytes;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool FitsOperationStartFrame(
        ActivityOperationKind kind,
        OperationAuthority authority,
        string? destination,
        Guid? passwordSlot,
        ArchiveCompressionPreset? rebuildPreset)
    {
        try
        {
            if (IsExtractionKind(kind))
            {
                ProtocolStream.ValidateJsonFrameSize(
                    ProtocolMessageKinds.StartExtraction,
                    new StartExtractionRequest(
                        authority.SessionId,
                        authority.EntryIds,
                        destination!,
                        passwordSlot));
            }
            else if (kind == ActivityOperationKind.ArchiveRebuild)
            {
                if (rebuildPreset is not ArchiveCompressionPreset preset) return false;
                ProtocolStream.ValidateJsonFrameSize(
                    ProtocolMessageKinds.StartZipRebuildCopy,
                    new StartZipRebuildCopyRequest(
                        authority.SessionId,
                        destination!,
                        [],
                        [],
                        preset));
            }
            else
            {
                ProtocolStream.ValidateJsonFrameSize(
                    ProtocolMessageKinds.StartIntegrityTest,
                    new StartIntegrityTestRequest(
                        authority.SessionId,
                        authority.EntryIds,
                        passwordSlot));
            }
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            return false;
        }
    }

    private static bool TryValidateExtractionDestination(
        string? requested,
        out string? canonical,
        out ArchiveOperationOutcome failure)
    {
        canonical = null;
        failure = ArchiveOperationOutcome.InvalidDestination;
        if (string.IsNullOrWhiteSpace(requested)
            || requested.Length > ProtocolConstants.MaximumPathCharacters
            || !Path.IsPathFullyQualified(requested))
            return false;

        try
        {
            canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requested));
            string? root = Path.GetPathRoot(canonical);
            if (string.IsNullOrEmpty(root)
                || string.Equals(canonical, Path.TrimEndingDirectorySeparator(root),
                    StringComparison.OrdinalIgnoreCase))
                return false;
            if (Directory.Exists(canonical) || File.Exists(canonical))
            {
                failure = ArchiveOperationOutcome.DestinationConflict;
                return false;
            }

            DirectoryInfo? parent = Directory.GetParent(canonical);
            if (parent is null || !parent.Exists) return false;
            for (DirectoryInfo? current = parent; current is not null; current = current.Parent)
            {
                FileAttributes attributes = current.Attributes;
                if (!attributes.HasFlag(FileAttributes.Directory)
                    || attributes.HasFlag(FileAttributes.ReparsePoint))
                    return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or IOException
                                              or NotSupportedException
                                              or PathTooLongException
                                              or UnauthorizedAccessException)
        {
            canonical = null;
            return false;
        }
    }

    private static bool TryValidateRebuildDestination(
        string? requested,
        string sourcePath,
        out string? canonical,
        out ArchiveOperationOutcome failure)
    {
        canonical = null;
        failure = ArchiveOperationOutcome.InvalidDestination;
        if (string.IsNullOrWhiteSpace(sourcePath)
            || string.IsNullOrWhiteSpace(requested))
            return false;
        var source = new ArchiveCreationSource(
            ArchiveCreationSourceKind.File,
            sourcePath,
            Path.GetFileName(sourcePath));
        if (ArchiveWriteValidation.TryValidateOutputDestination(
                requested,
                ".zip",
                [source],
                out string validated,
                out bool destinationExists))
        {
            canonical = validated;
            return true;
        }
        failure = destinationExists
            ? ArchiveOperationOutcome.DestinationConflict
            : ArchiveOperationOutcome.InvalidDestination;
        return false;
    }

    private static ArchiveOperationResult FinishRefusal(
        ActiveArchiveOperation operation,
        ArchiveOperationOutcome outcome,
        ActivityOperationFailure failure)
    {
        ArchiveDestinationConflictReason conflictReason =
            outcome == ArchiveOperationOutcome.DestinationConflict
                && IsExtractionKind(operation.Kind)
                ? ArchiveDestinationConflictReason.ExistingDestination
                : ArchiveDestinationConflictReason.None;
        operation.ReportTerminal(ActivityOperationPhase.Failed, failure, conflictReason);
        return new ArchiveOperationResult(outcome, DestinationConflictReason: conflictReason);
    }

    private static ArchiveOperationResult FinishCancellation(ActiveArchiveOperation operation)
    {
        if (operation.UnexpectedWaiting)
        {
            operation.ReportTerminal(
                ActivityOperationPhase.Failed,
                ActivityOperationFailure.Unsupported);
            return new ArchiveOperationResult(ArchiveOperationOutcome.Unsupported);
        }
        operation.ReportTerminal(ActivityOperationPhase.Cancelled, ActivityOperationFailure.None);
        return new ArchiveOperationResult(ArchiveOperationOutcome.Cancelled);
    }

    private static ArchiveOperationResult FinishTerminal(
        ActiveArchiveOperation operation,
        OperationEvent terminal)
    {
        if (terminal.Progress is { } progress) operation.ApplyProgress(progress);
        if (operation.UnexpectedWaiting)
        {
            operation.ReportTerminal(
                ActivityOperationPhase.Failed,
                ActivityOperationFailure.Unsupported);
            return new ArchiveOperationResult(ArchiveOperationOutcome.Unsupported);
        }
        return terminal.Phase switch
        {
            OperationPhase.Completed => FinishSuccess(
                operation, ActivityOperationPhase.Completed, ArchiveOperationOutcome.Completed),
            OperationPhase.CompletedWithWarnings => FinishSuccess(
                operation.EnsureWarningCount(),
                ActivityOperationPhase.CompletedWithWarnings,
                ArchiveOperationOutcome.CompletedWithWarnings),
            OperationPhase.Cancelled => FinishCancellation(operation),
            _ => FinishTerminalFailure(operation, terminal.Error),
        };
    }

    private static ArchiveOperationResult FinishTransferTerminal(
        ActiveArchiveOperation operation,
        OperationEvent terminal)
    {
        if (terminal.Progress is { } progress) operation.ApplyProgress(progress);
        ActivityOperationPhase phase;
        ArchiveOperationOutcome outcome;
        switch (terminal.Phase)
        {
            case OperationPhase.Completed:
                phase = ActivityOperationPhase.Completed;
                outcome = ArchiveOperationOutcome.Completed;
                break;
            case OperationPhase.CompletedWithWarnings:
                operation.EnsureWarningCount();
                phase = ActivityOperationPhase.CompletedWithWarnings;
                outcome = ArchiveOperationOutcome.CompletedWithWarnings;
                break;
            case OperationPhase.Cancelled:
                return FinishCancellation(operation);
            default:
                return FinishTerminalFailure(operation, terminal.Error);
        }

        return operation.TryReportTransferSuccess(phase)
            ? new ArchiveOperationResult(outcome)
            : FinishCancellation(operation);
    }

    private static ArchiveOperationResult FinishSuccess(
        ActiveArchiveOperation operation,
        ActivityOperationPhase phase,
        ArchiveOperationOutcome outcome)
    {
        operation.ReportTerminal(phase, ActivityOperationFailure.None);
        return new ArchiveOperationResult(outcome);
    }

    private static ArchiveOperationResult FinishTerminalFailure(
        ActiveArchiveOperation operation,
        ArchiveError? error)
    {
        ActivityOperationFailure failure = MapOperationFailure(error?.Category);
        ArchiveDestinationConflictReason conflictReason =
            MapDestinationConflictReason(operation.Kind, error);
        operation.ReportTerminal(ActivityOperationPhase.Failed, failure, conflictReason);
        return new ArchiveOperationResult(
            MapOperationOutcome(error?.Category),
            DestinationConflictReason: conflictReason);
    }

    private static ArchiveOperationResult FinishClientFailure(
        ActiveArchiveOperation operation,
        ArchiveError error) =>
        error.Category == ArchiveErrorCategory.Cancelled
            ? FinishCancellation(operation)
            : FinishTerminalFailure(operation, error);

    private static ActivityOperationFailure FailureForOutcome(ArchiveOperationOutcome outcome) =>
        outcome switch
        {
            ArchiveOperationOutcome.InvalidRequest or ArchiveOperationOutcome.InvalidSelection
                or ArchiveOperationOutcome.InvalidDestination =>
                ActivityOperationFailure.InvalidRequest,
            ArchiveOperationOutcome.DestinationConflict => ActivityOperationFailure.DestinationConflict,
            ArchiveOperationOutcome.Unsupported => ActivityOperationFailure.Unsupported,
            ArchiveOperationOutcome.Stale => ActivityOperationFailure.SourceChanged,
            ArchiveOperationOutcome.Offline => ActivityOperationFailure.WorkerUnavailable,
            ArchiveOperationOutcome.UnsafeOutput => ActivityOperationFailure.UnsafeOutput,
            _ => ActivityOperationFailure.Unknown,
        };

    private static ArchiveOperationOutcome MapOperationOutcome(ArchiveErrorCategory? category) =>
        category switch
        {
            ArchiveErrorCategory.Cancelled => ArchiveOperationOutcome.Cancelled,
            ArchiveErrorCategory.SourceChanged => ArchiveOperationOutcome.Stale,
            ArchiveErrorCategory.WorkerFailure or ArchiveErrorCategory.ProtocolMismatch =>
                ArchiveOperationOutcome.Offline,
            ArchiveErrorCategory.UnsupportedFormat or ArchiveErrorCategory.UnsupportedCapability =>
                ArchiveOperationOutcome.Unsupported,
            ArchiveErrorCategory.DestinationConflict => ArchiveOperationOutcome.DestinationConflict,
            ArchiveErrorCategory.InvalidRequest => ArchiveOperationOutcome.InvalidRequest,
            _ => ArchiveOperationOutcome.Failed,
        };

    private static ActivityOperationFailure MapOperationFailure(ArchiveErrorCategory? category) =>
        category switch
        {
            ArchiveErrorCategory.InvalidRequest => ActivityOperationFailure.InvalidRequest,
            ArchiveErrorCategory.UnsupportedFormat or ArchiveErrorCategory.UnsupportedCapability =>
                ActivityOperationFailure.Unsupported,
            ArchiveErrorCategory.InvalidArchive => ActivityOperationFailure.InvalidArchive,
            ArchiveErrorCategory.CorruptArchive => ActivityOperationFailure.CorruptArchive,
            ArchiveErrorCategory.PasswordRequired => ActivityOperationFailure.PasswordRequired,
            ArchiveErrorCategory.IncorrectPassword => ActivityOperationFailure.IncorrectPassword,
            ArchiveErrorCategory.UnsafePath => ActivityOperationFailure.UnsafePath,
            ArchiveErrorCategory.PathCollision => ActivityOperationFailure.PathCollision,
            ArchiveErrorCategory.ResourceLimit => ActivityOperationFailure.ResourceLimit,
            ArchiveErrorCategory.SourceChanged => ActivityOperationFailure.SourceChanged,
            ArchiveErrorCategory.DestinationConflict => ActivityOperationFailure.DestinationConflict,
            ArchiveErrorCategory.PermissionDenied => ActivityOperationFailure.PermissionDenied,
            ArchiveErrorCategory.InsufficientSpace => ActivityOperationFailure.InsufficientSpace,
            ArchiveErrorCategory.ProviderFailure => ActivityOperationFailure.ProviderFailure,
            ArchiveErrorCategory.WorkerFailure => ActivityOperationFailure.WorkerUnavailable,
            ArchiveErrorCategory.ProtocolMismatch => ActivityOperationFailure.ProtocolMismatch,
            _ => ActivityOperationFailure.Unknown,
        };

    private static ArchiveDestinationConflictReason MapDestinationConflictReason(
        ActivityOperationKind kind,
        ArchiveError? error)
    {
        if (!IsExtractionKind(kind)
            || error?.Category != ArchiveErrorCategory.DestinationConflict)
            return ArchiveDestinationConflictReason.None;

        return error.Code switch
        {
            "destination.must_not_exist" =>
                ArchiveDestinationConflictReason.ExistingDestination,
            "destination.appeared_before_commit" =>
                ArchiveDestinationConflictReason.AppearedBeforeCommit,
            "destination.identity_changed"
                or "destination.path_appeared"
                or "destination.path_replaced"
                or "destination.reparse_point_appeared"
                or "destination.reparse_point_encountered"
                or "destination.unexpected_path_appeared"
                or "destination.staging_shape_changed" =>
                ArchiveDestinationConflictReason.StagingValidationConflict,
            "destination.hard_link_appeared" =>
                ArchiveDestinationConflictReason.TemporaryFileHardLinkConflict,
            _ => ArchiveDestinationConflictReason.Unknown,
        };
    }

    private static ActivityOperationPhase MapOperationPhase(OperationPhase phase) => phase switch
    {
        OperationPhase.Queued => ActivityOperationPhase.Queued,
        OperationPhase.Validating => ActivityOperationPhase.Validating,
        OperationPhase.AwaitingPassword or OperationPhase.AwaitingConflict
            or OperationPhase.AwaitingRiskApproval => ActivityOperationPhase.WaitingForInput,
        OperationPhase.Running => ActivityOperationPhase.Running,
        OperationPhase.Finalizing => ActivityOperationPhase.Finalizing,
        OperationPhase.Completed => ActivityOperationPhase.Completed,
        OperationPhase.CompletedWithWarnings => ActivityOperationPhase.CompletedWithWarnings,
        OperationPhase.Cancelled => ActivityOperationPhase.Cancelled,
        OperationPhase.Failed => ActivityOperationPhase.Failed,
        _ => ActivityOperationPhase.Failed,
    };

    private void ReceiveOperationEvent(
        ActiveArchiveOperation operation,
        OperationEvent operationEvent)
    {
        if (!operation.TryAcceptEvent(operationEvent, out ActivityOperationUpdate? update)) return;
        if (update is not null) operation.Activity.Report(update);
        if (operation.Kind == ActivityOperationKind.ArchiveRebuild
            && IsUnexpectedInteractiveEvent(operationEvent))
        {
            operation.MarkUnexpectedWaiting();
            RequestArchiveOperationCancellation(operation);
        }
    }

    private static bool IsUnexpectedInteractiveEvent(OperationEvent operationEvent) =>
        operationEvent.Kind != OperationEventKind.Terminal
        && operationEvent.Phase is OperationPhase.AwaitingPassword
            or OperationPhase.AwaitingConflict
            or OperationPhase.AwaitingRiskApproval;

    private void RequestArchiveOperationCancellation(ActiveArchiveOperation operation)
    {
        if (!operation.MarkCancellationPending()) return;
        if (!operation.TryBeginCancelRequest(out IArchiveClient? client, out OperationId operationId))
            return;
        _ = SendArchiveOperationCancellationAsync(operation, client, operationId);
    }

    private async Task SendArchiveOperationCancellationAsync(
        ActiveArchiveOperation operation,
        IArchiveClient client,
        OperationId operationId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            _ = await client.CancelOperationAsync(operationId, timeout.Token).ConfigureAwait(false);
        }
        catch (ArchiveClientException exception)
            when (exception.Error.Category is ArchiveErrorCategory.WorkerFailure
                or ArchiveErrorCategory.ProtocolMismatch)
        {
            await InvalidatePreviewClientAsync(client).ConfigureAwait(false);
        }
        catch (ArchiveClientException)
        {
            // A nonfatal cancellation race belongs only to this exact operation.
            // Its terminal event remains authoritative and the shared client stays valid.
        }
        catch (Exception exception) when (exception is OperationCanceledException
                                              or IOException
                                              or InvalidDataException
                                              or ObjectDisposedException)
        {
            await InvalidatePreviewClientAsync(client).ConfigureAwait(false);
        }
    }

    private async Task SettleUntransferredSecretAsync(IArchiveClient client, Guid slotId)
    {
        bool settled = false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            settled = await client.DiscardSecretAsync(slotId, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArchiveClientException
                                              or OperationCanceledException
                                              or IOException
                                              or InvalidDataException
                                              or NotSupportedException
                                              or ObjectDisposedException)
        {
            settled = false;
        }

        if (!settled) await InvalidatePreviewClientAsync(client).ConfigureAwait(false);
    }

    private void BeginArchiveTransferInvalidation(ArchiveTransferReleaseReason reason)
    {
        Task invalidation = _archiveTransferCoordinator.InvalidateAsync(reason);
        if (invalidation.IsCompletedSuccessfully) return;
        _ = InvalidateArchiveTransferAndDrainAsync(invalidation);
    }

    private async Task InvalidateArchiveTransferAndDrainAsync(
        Task invalidation)
    {
        await CancelAndDrainArchiveOperationAsync(ActivityOperationKind.TransferOut)
            .ConfigureAwait(false);
        await invalidation.ConfigureAwait(false);
        await _archiveTransferCoordinator.DrainAsync().ConfigureAwait(false);
    }

    private async Task CancelAndDrainArchiveOperationAsync(
        ActivityOperationKind? requiredKind = null)
    {
        ActiveArchiveOperation? operation;
        lock (_archiveOperationSync) operation = _activeArchiveOperation;
        if (operation is null || requiredKind is ActivityOperationKind kind && operation.Kind != kind)
            return;

        RequestArchiveOperationCancellation(operation);
        Task drained = operation.Drained.Task;
        if (await Task.WhenAny(drained, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false)
            == drained)
        {
            await drained.ConfigureAwait(false);
            return;
        }

        if (operation.Client is { } client)
        {
            await InvalidatePreviewClientAsync(client).ConfigureAwait(false);
            operation.EndTerminalWaitAfterClientTeardown();
        }

        // ArchiveClient teardown is the bounded Worker-containment boundary. Once it
        // returns, the exact operation owner must settle before session or adapter state
        // can be disposed; proceeding with callbacks still live would violate ownership.
        await drained.ConfigureAwait(false);
    }

    private static async Task ApplyFinalActivitySnapshotAsync(
        IArchiveClient client,
        ActiveArchiveOperation operation,
        OperationId operationId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            GetOperationActivityResponse response = await client.GetOperationActivityAsync(timeout.Token)
                .ConfigureAwait(false);
            OperationActivitySnapshot? snapshot = response.Operations.SingleOrDefault(
                item => item.OperationId == operationId);
            if (snapshot is not null) operation.ApplySnapshot(snapshot);
        }
        catch (Exception exception) when (exception is ArchiveClientException
                                              or OperationCanceledException
                                              or IOException
                                              or InvalidDataException
                                              or NotSupportedException
                                              or ObjectDisposedException)
        {
            // The terminal event remains authoritative. Snapshot failure cannot create a retry.
        }
    }

    private async Task OpenCoreAsync(
        string sourcePath,
        ArchiveLocation? restore,
        bool requireSameFingerprint,
        CancellationToken cancellationToken,
        OneShotOperationPassword? password = null)
    {
        long operationVersion = Interlocked.Increment(ref _operationVersion);
        CancelActiveOperation();
        Task transferInvalidation = _archiveTransferCoordinator.InvalidateAsync(
            ArchiveTransferReleaseReason.Navigation);
        await CancelAndDrainArchiveOperationAsync().ConfigureAwait(false);
        await transferInvalidation.ConfigureAwait(false);
        await _archiveTransferCoordinator.DrainAsync().ConfigureAwait(false);
        await InvalidatePreview(ArchiveEntryPreviewState.Cancelled).ConfigureAwait(false);
        await DrainClientInvalidationAsync().ConfigureAwait(false);
        await _openGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsCurrentOperation(operationVersion)) return;
            await OpenCoreSerialAsync(sourcePath, restore, requireSameFingerprint,
                operationVersion, cancellationToken, password).ConfigureAwait(false);
        }
        finally
        {
            _openGate.Release();
        }
    }

    private async Task OpenCoreSerialAsync(
        string sourcePath,
        ArchiveLocation? restore,
        bool requireSameFingerprint,
        long operationVersion,
        CancellationToken cancellationToken,
        OneShotOperationPassword? password)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string canonicalSource;
        try { canonicalSource = Path.GetFullPath(sourcePath); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            SetFailure(ArchiveLocationState.Error, "Rain Archives could not open this location safely.");
            return;
        }

        bool hadPriorSession = _archive is not null;
        bool openingSameSource = _sourcePath is not null
            && string.Equals(_sourcePath, canonicalSource, StringComparison.OrdinalIgnoreCase);
        ArchiveLocationState priorState = State;
        string priorStatus = Status;

        // A failed first open must still identify the file the user selected. Keep the
        // prior presentation only when a live archive session actually exists.
        if (!hadPriorSession)
        {
            _sourcePath = canonicalSource;
            _rootTitle = SafeTitle(canonicalSource);
            Title = _rootTitle;
        }

        CancellationTokenSource operationCancellation;
        lock (_operationSync)
        {
            _operationCancellation?.Dispose();
            operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _operationCancellation = operationCancellation;
        }
        CancellationToken token = operationCancellation.Token;
        State = ArchiveLocationState.Loading;
        Status = "Opening archive…";
        Changed?.Invoke(this, EventArgs.Empty);

        PreparedChain? prepared = null;
        try
        {
            prepared = await PrepareSessionAsync(canonicalSource, restore, token, password)
                .ConfigureAwait(false);
            if (!IsCurrentOperation(operationVersion)) throw new OperationCanceledException(token);
            if (restore is not null
                && !FingerprintsEqual(restore.Fingerprint, prepared.Root.Archive.Fingerprint))
            {
                await ClosePreparedAsync(prepared).ConfigureAwait(false);
                prepared = null;
                await CloseCurrentSessionAsync().ConfigureAwait(false);
                ClearSessionData();
                _sourcePath = canonicalSource;
                Title = SafeTitle(canonicalSource);
                SetFailure(ArchiveLocationState.Stale,
                    "The archive source changed. Reopen it before continuing.");
                return;
            }

            if (requireSameFingerprint && _sessionChain.Count > 0
                && !FingerprintsEqual(
                    _sessionChain[0].Archive.Fingerprint, prepared.Root.Archive.Fingerprint))
            {
                await ClosePreparedAsync(prepared).ConfigureAwait(false);
                prepared = null;
                await CloseCurrentSessionAsync().ConfigureAwait(false);
                ClearSessionData();
                _sourcePath = canonicalSource;
                Title = SafeTitle(canonicalSource);
                SetFailure(ArchiveLocationState.Stale,
                    "The archive source changed. Reopen it before continuing.");
                return;
            }

            if (_archive is not null && !await CloseCurrentSessionAsync().ConfigureAwait(false))
            {
                await ClosePreparedAsync(prepared).ConfigureAwait(false);
                prepared = null;
                ClearSessionData();
                SetFailure(ArchiveLocationState.Error,
                    "Rain Archives could not replace the prior archive session safely.");
                return;
            }

            if (!IsCurrentOperation(operationVersion)) throw new OperationCanceledException(token);
            Adopt(prepared, canonicalSource, restore);
            prepared = null;
        }
        catch (StaleArchiveLocationException)
        {
            if (prepared is not null) await ClosePreparedAsync(prepared).ConfigureAwait(false);
            if (!IsCurrentOperation(operationVersion)) return;
            await CloseCurrentSessionAsync().ConfigureAwait(false);
            ClearSessionData();
            _sourcePath = canonicalSource;
            _rootTitle = SafeTitle(canonicalSource);
            Title = _rootTitle;
            SetFailure(ArchiveLocationState.Stale,
                "The archive source changed. Reopen it before continuing.");
        }
        catch (OperationCanceledException)
        {
            if (prepared is not null) await ClosePreparedAsync(prepared).ConfigureAwait(false);
            if (!_disposed && IsCurrentOperation(operationVersion))
            {
                if (hadPriorSession && _archive is not null) RestorePriorPresentation(priorState, priorStatus);
                else SetFailure(ArchiveLocationState.Error, "Archive opening was cancelled.");
            }
        }
        catch (ArchiveClientException exception)
        {
            if (prepared is not null) await ClosePreparedAsync(prepared).ConfigureAwait(false);
            if (!IsCurrentOperation(operationVersion)) return;
            if (hadPriorSession && _archive is not null
                && !(openingSameSource && exception.Error.Category == ArchiveErrorCategory.SourceChanged))
            {
                RestorePriorPresentation(priorState, priorStatus);
            }
            else
            {
                if (openingSameSource && exception.Error.Category == ArchiveErrorCategory.SourceChanged)
                {
                    await CloseCurrentSessionAsync().ConfigureAwait(false);
                    ClearSessionData();
                    _sourcePath = canonicalSource;
                    Title = SafeTitle(canonicalSource);
                }
                SetFailure(MapState(exception.Error.Category), SafeStatus(exception.Error.Category));
            }
        }
        catch (Exception exception) when (exception is IOException
                                              or InvalidDataException
                                              or ObjectDisposedException
                                              or UnauthorizedAccessException)
        {
            if (prepared is not null) await ClosePreparedAsync(prepared).ConfigureAwait(false);
            if (!IsCurrentOperation(operationVersion)) return;
            if (hadPriorSession && _archive is not null)
            {
                RestorePriorPresentation(priorState, priorStatus);
            }
            else
            {
                SetFailure(exception is ObjectDisposedException or IOException
                        ? ArchiveLocationState.Offline
                        : ArchiveLocationState.Error,
                    exception is ObjectDisposedException or IOException
                        ? "The Rain Archives Worker is unavailable. Reopen the archive after reconnecting."
                        : "Rain Archives refused an invalid archive listing.");
            }
        }
    }

    private async Task<PreparedChain> PrepareSessionAsync(
        string sourcePath,
        ArchiveLocation? restore,
        CancellationToken token,
        OneShotOperationPassword? password)
    {
        IArchiveClient client = await _runtime.GetClientAsync(token).ConfigureAwait(false);
        Guid? passwordSlot = null;
        bool slotTransferred = false;
        OpenArchiveResponse opened;
        try
        {
            char[]? suppliedPassword = password?.Value;
            if (suppliedPassword is not null)
            {
                if (suppliedPassword.Length == 0
                    || !IsPasswordWithinProtocolLimit(suppliedPassword)
                    || !client.SupportsFeature(ProtocolFeatures.SecretSlots)
                    || !client.SupportsFeature(ProtocolFeatures.SecretSlotDiscard))
                {
                    throw new ArchiveClientException(new ArchiveError(
                        "open.password_request_invalid",
                        ArchiveErrorCategory.InvalidRequest,
                        "The root-open password request was invalid."));
                }

                try
                {
                    passwordSlot = await client.CreateSecretAsync(
                        suppliedPassword.AsMemory(), token).ConfigureAwait(false);
                }
                finally
                {
                    password?.Clear();
                }
            }

            token.ThrowIfCancellationRequested();
            slotTransferred = true;
            opened = await client.OpenArchiveAsync(
                sourcePath, passwordSlot, token).ConfigureAwait(false);
        }
        finally
        {
            if (!slotTransferred && passwordSlot is Guid untransferredSlot)
                await SettleUntransferredSecretAsync(client, untransferredSlot)
                    .ConfigureAwait(false);
        }
        var frames = new List<SessionFrame>();
        OpenNestedArchiveResponse? untrackedChild = null;
        ArchiveClientRuntime.NestedArchiveByteReservation? untrackedReservation = null;
        try
        {
            bool persistentKeys = client.SupportsFeature(ProtocolFeatures.PersistentEntryKeys);
            frames.Add(new SessionFrame(
                opened.Archive,
                Index: null,
                persistentKeys,
                SafeTitle(sourcePath),
                SourceNodeId: null,
                PersistentSegment: null,
                MaterializedBytes: 0,
                ChainMaterializedBytes: 0,
                ByteReservation: null,
                Depth: 0));
            PreparedIndex? rootIndex = opened.Archive.Capabilities.HasFlag(ArchiveCapability.List)
                ? await PrepareIndexAsync(client, opened.Archive, token).ConfigureAwait(false)
                : null;
            frames[0] = new SessionFrame(
                opened.Archive,
                rootIndex,
                persistentKeys,
                SafeTitle(sourcePath),
                SourceNodeId: null,
                PersistentSegment: null,
                MaterializedBytes: 0,
                ChainMaterializedBytes: 0,
                ByteReservation: null,
                Depth: 0);

            if (restore is null) return new PreparedChain(client, frames);
            if (!FingerprintsEqual(restore.Fingerprint, opened.Archive.Fingerprint))
                throw new StaleArchiveLocationException();
            if (restore.NestedSegments.Count == 0) return new PreparedChain(client, frames);
            if (!persistentKeys
                || !client.SupportsFeature(ProtocolFeatures.NestedArchiveOpen)
                || !client.SupportsFeature(ProtocolFeatures.RequestCancellation)
                || !client.SupportsFeature(ProtocolFeatures.SourceFingerprintValidation)
                || !client.SupportsFeature(ProtocolFeatures.VolumeSetIdentity))
            {
                throw new ArchiveClientException(new ArchiveError(
                    "nested.feature_not_negotiated",
                    ArchiveErrorCategory.UnsupportedCapability,
                    "Nested archive restoration is unavailable."));
            }

            foreach (ArchiveNestedSegment segment in restore.NestedSegments)
            {
                SessionFrame parent = frames[^1];
                if (!(parent.Depth == 0
                        ? FingerprintsEqual(segment.ParentFingerprint, parent.Archive.Fingerprint)
                        : NestedContentFingerprintsEqual(
                            segment.ParentFingerprint, parent.Archive.Fingerprint))
                    || parent.Index is null)
                    throw new StaleArchiveLocationException();
                Node sourceNode = parent.Index.Nodes.Values.SingleOrDefault(node =>
                    !node.IsImplicit
                    && node.Descriptor is { Kind: ArchiveEntryKind.File }
                    && string.Equals(node.NormalizedPath, segment.SourceEntryPath, StringComparison.Ordinal)
                    && node.DuplicateOrdinal == segment.SourceEntryDuplicateOrdinal)
                    ?? throw new StaleArchiveLocationException();
                long maximumBytes = ValidateRestoredNestedAdmission(parent, sourceNode);
                if (!_runtime.TryReserveNestedArchiveBytes(maximumBytes, out untrackedReservation))
                {
                    throw new ArchiveClientException(new ArchiveError(
                        "nested.active_byte_limit",
                        ArchiveErrorCategory.ResourceLimit,
                        "The active nested archive byte limit was reached."));
                }

                untrackedChild = await client.OpenNestedArchiveAsync(
                        parent.Archive.SessionId,
                        sourceNode.Descriptor!.EntryId,
                        maximumBytes,
                        cancellationToken: token)
                    .ConfigureAwait(false);
                ValidateNestedResponse(
                    untrackedChild, parent, sourceNode.Descriptor, maximumBytes, frames);
                PreparedIndex childIndex = await PrepareIndexAsync(
                        client, untrackedChild.Archive, token)
                    .ConfigureAwait(false);
                if (!NestedContentFingerprintsEqual(
                    segment.ChildFingerprint, untrackedChild.Archive.Fingerprint))
                    throw new StaleArchiveLocationException();

                untrackedReservation.Commit(untrackedChild.MaterializedBytes);
                frames.Add(new SessionFrame(
                    untrackedChild.Archive,
                    childIndex,
                    PersistentKeysEnabled: true,
                    sourceNode.Name,
                    sourceNode.Id,
                    segment,
                    untrackedChild.MaterializedBytes,
                    untrackedChild.ChainMaterializedBytes,
                    untrackedReservation,
                    untrackedChild.Depth));
                untrackedChild = null;
                untrackedReservation = null;
            }

            return new PreparedChain(client, frames);
        }
        catch
        {
            if (untrackedChild is not null)
                await _runtime.CloseSessionAsync(client, untrackedChild.Archive.SessionId)
                    .ConfigureAwait(false);
            untrackedReservation?.Dispose();
            await CloseFramesAsync(client, frames).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<PreparedIndex> PrepareIndexAsync(
        IArchiveClient client,
        ArchiveDescriptor archive,
        CancellationToken token)
    {
        if (!archive.Capabilities.HasFlag(ArchiveCapability.List)
            || archive.EntryCount is < 0 or > MaximumIndexedEntries)
            throw new InvalidDataException("The archive exceeds the bounded descriptor index.");

        bool persistentKeys = client.SupportsFeature(ProtocolFeatures.PersistentEntryKeys);
        var entries = new List<ArchiveEntryDescriptor>(archive.EntryCount);
        var seenIds = new HashSet<EntryId>();
        int offset = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            ListEntriesResponse page = await client.ListEntriesAsync(
                archive.SessionId,
                offset,
                Math.Min(512, ProtocolConstants.MaximumPageSize),
                token).ConfigureAwait(false);
            if (page.Offset != offset || page.TotalCount != archive.EntryCount
                || page.Entries.Count > ProtocolConstants.MaximumPageSize
                || (long)entries.Count + page.Entries.Count > archive.EntryCount)
                throw new InvalidDataException("The archive page sequence was invalid.");

            foreach (ArchiveEntryDescriptor? entry in page.Entries)
            {
                if (entry is null || !seenIds.Add(entry.EntryId))
                    throw new InvalidDataException("The archive returned duplicate entry identities.");
                entries.Add(entry);
            }

            if (page.NextOffset is not int next)
            {
                if (entries.Count != archive.EntryCount)
                    throw new InvalidDataException("The archive listing ended early.");
                break;
            }
            if (page.Entries.Count == 0 || next <= offset || next != entries.Count)
                throw new InvalidDataException("The archive page did not advance authoritatively.");
            offset = next;
        }

        return BuildIndex(archive, entries, persistentKeys);
    }

    private static long ValidateRestoredNestedAdmission(SessionFrame parent, Node sourceNode)
    {
        ArchiveDescriptor archive = parent.Archive;
        ArchiveEntryDescriptor descriptor = sourceNode.Descriptor!;
        if (descriptor.IsEncrypted)
        {
            throw new ArchiveClientException(new ArchiveError(
                "nested.parent_password_role_unavailable",
                ArchiveErrorCategory.UnsupportedCapability,
                "Encrypted parent entries require a separate password role."));
        }
        if (!parent.PersistentKeysEnabled
            || parent.Depth >= ProtocolConstants.MaximumNestedArchiveDepth
            || archive.IsSolid || archive.IsMultipart
            || archive.VolumeSetKind != ArchiveVolumeSetKind.Single
            || !archive.Capabilities.HasFlag(ArchiveCapability.Extract)
            || !archive.Capabilities.HasFlag(ArchiveCapability.OpenEntryStream)
            || sourceNode.IsImplicit
            || descriptor.Kind != ArchiveEntryKind.File
            || descriptor.ProviderOrdinal < 0
            || descriptor.SafetyStatus != EntrySafetyStatus.Safe
            || descriptor.IsSolid
            || sourceNode.NormalizedPath is null
            || descriptor.OriginalSize is not long declaredBytes
            || declaredBytes <= 0
            || declaredBytes > ProtocolConstants.MaximumNestedArchiveBytes
            || parent.ChainMaterializedBytes < 0
            || parent.ChainMaterializedBytes
                > ProtocolConstants.MaximumNestedArchiveChainBytes - declaredBytes)
            throw new StaleArchiveLocationException();
        return declaredBytes;
    }

    private static PreparedIndex BuildIndex(
        ArchiveDescriptor archive,
        IReadOnlyList<ArchiveEntryDescriptor> entries,
        bool persistentKeysEnabled)
    {
        if (entries.Count != archive.EntryCount || entries.Count > MaximumIndexedEntries)
            throw new InvalidDataException("The archive descriptor count changed.");

        var descriptors = new Dictionary<EntryId, ArchiveEntryDescriptor>(entries.Count);
        var materialNodes = new Dictionary<EntryId, Node>(entries.Count);
        var directoryPaths = new Dictionary<string, List<Node>>(StringComparer.Ordinal);
        var persistentKeys = new HashSet<ArchiveEntryKey>();

        foreach (var entry in entries)
        {
            if (entry.EntryId.Value == Guid.Empty || entry.ParentEntryId?.Value == Guid.Empty
                || string.IsNullOrEmpty(entry.RawPath) || string.IsNullOrEmpty(entry.DisplayName)
                || !Enum.IsDefined(entry.Kind) || !Enum.IsDefined(entry.SafetyStatus)
                || entry.ProviderOrdinal < -1
                || entry.ProviderOrdinal == -1 && entry.Kind != ArchiveEntryKind.Directory
                || entry.OriginalSize is < 0 || entry.CompressedSize is < 0
                || entry.DuplicateOrdinal < 0 || !descriptors.TryAdd(entry.EntryId, entry))
                throw new InvalidDataException("The archive contains invalid entry metadata.");

            string? path = entry.NormalizedPath;
            if (persistentKeysEnabled)
            {
                if (string.IsNullOrEmpty(path)
                    || !ArchiveLocationSerializer.IsValidNormalizedArchivePath(path)
                    || !persistentKeys.Add(new ArchiveEntryKey(path, entry.DuplicateOrdinal)))
                    throw new InvalidDataException("The archive contains invalid persistent entry keys.");
            }
            else
            {
                if (path is not null || entry.DuplicateOrdinal != 0)
                    throw new InvalidDataException("Persistent entry keys were returned without negotiation.");
                string normalized = entry.RawPath.Replace('\\', '/').TrimEnd('/');
                path = ArchiveLocationSerializer.IsValidNormalizedArchivePath(normalized)
                    ? normalized : null;
            }

            var node = Node.Material(entry, path);
            materialNodes.Add(entry.EntryId, node);
            if (node.IsDirectory && path is not null)
            {
                if (!directoryPaths.TryGetValue(path, out var matches))
                    directoryPaths.Add(path, matches = []);
                matches.Add(node);
            }
        }

        ValidateMaterialHierarchy(descriptors);
        var allNodes = materialNodes.Values.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var roots = new List<Node>();

        Node EnsureImplicit(string path)
        {
            string id = "implicit:" + path;
            if (allNodes.TryGetValue(id, out var existing)) return existing;
            if (!CanAddVirtualNode(allNodes.Count))
                throw new InvalidDataException("The archive exceeds the bounded virtual index.");
            int separator = path.LastIndexOf('/');
            string parentPath = separator < 0 ? string.Empty : path[..separator];
            string name = separator < 0 ? path : path[(separator + 1)..];
            var node = Node.Implicit(id, UntrustedDisplayText.Escape(name), path);
            allNodes.Add(id, node);
            Node? parent = ResolvePathParent(parentPath);
            node.ParentId = parent?.Id;
            if (parent is null) roots.Add(node); else parent.Children.Add(node);
            return node;
        }

        Node? ResolvePathParent(string path)
        {
            if (path.Length == 0) return null;
            if (directoryPaths.TryGetValue(path, out var matches) && matches.Count == 1)
                return matches[0];
            return EnsureImplicit(path);
        }

        foreach (var node in materialNodes.Values)
        {
            Node? parent = null;
            if (node.Descriptor!.ParentEntryId is EntryId parentId)
                parent = materialNodes[parentId];
            else if (node.NormalizedPath is { } path)
            {
                int separator = path.LastIndexOf('/');
                parent = ResolvePathParent(separator < 0 ? string.Empty : path[..separator]);
            }
            node.ParentId = parent?.Id;
            if (parent is null) roots.Add(node); else parent.Children.Add(node);
        }

        foreach (var node in allNodes.Values)
            node.Children.Sort(NodeComparer.Instance);
        roots.Sort(NodeComparer.Instance);
        return new PreparedIndex(allNodes, roots);
    }

    private static void ValidateMaterialHierarchy(
        IReadOnlyDictionary<EntryId, ArchiveEntryDescriptor> entries)
    {
        foreach (var entry in entries.Values)
        {
            if (entry.ParentEntryId is not EntryId parentId) continue;
            if (!entries.TryGetValue(parentId, out var parent)
                || parent.Kind != ArchiveEntryKind.Directory || parentId == entry.EntryId)
                throw new InvalidDataException("The archive contains an impossible parent relationship.");
        }

        var depths = new Dictionary<EntryId, int>(entries.Count);
        var visiting = new HashSet<EntryId>();
        foreach (var entryId in entries.Keys)
        {
            if (depths.ContainsKey(entryId)) continue;
            var path = new List<EntryId>();
            EntryId current = entryId;
            int resolvedDepth = 0;
            while (!depths.TryGetValue(current, out resolvedDepth))
            {
                if (!visiting.Add(current))
                    throw new InvalidDataException("The archive hierarchy contains a cycle.");
                path.Add(current);
                if (entries[current].ParentEntryId is not EntryId parent) break;
                current = parent;
            }
            for (int index = path.Count - 1; index >= 0; index--)
            {
                if (resolvedDepth >= ArchivePathRules.MaximumPathDepth)
                    throw new InvalidDataException("The archive hierarchy exceeds the depth limit.");
                resolvedDepth++;
                depths[path[index]] = resolvedDepth;
                visiting.Remove(path[index]);
            }
        }
    }

    private void Adopt(PreparedChain prepared, string sourcePath, ArchiveLocation? restore)
    {
        _ = InvalidatePreview(ArchiveEntryPreviewState.Stale);
        lock (_archiveOperationSync) _selectedArchiveNodeIds.Clear();
        string? restoreNodeId = ResolveRestoreNode(prepared.Current.Index, restore);
        _client = prepared.Client;
        _sourcePath = sourcePath;
        _rootTitle = SafeTitle(sourcePath);
        _sessionChain.Clear();
        _sessionChain.AddRange(prepared.Frames);

        _history.Clear();
        var cursor = new ArchiveCursor(_sessionChain.Count - 1, restoreNodeId);
        _history.Add(cursor);
        _historyIndex = 0;
        State = ArchiveLocationState.Ready;
        ApplyCursor(cursor);
    }

    private static string? ResolveRestoreNode(PreparedIndex? index, ArchiveLocation? restore)
    {
        if (restore is null || restore.FolderPath.Length == 0) return null;
        if (index is null)
            throw new InvalidDataException("The persisted archive folder is unavailable.");
        var nodes = index.Nodes.Values;
        if (restore.FolderDuplicateOrdinal is int duplicate)
            return nodes.FirstOrDefault(node => node.IsDirectory && !node.IsImplicit
                && string.Equals(node.NormalizedPath, restore.FolderPath, StringComparison.Ordinal)
                && node.DuplicateOrdinal == duplicate)?.Id
                ?? throw new InvalidDataException("The persisted archive folder is unavailable.");
        return nodes.FirstOrDefault(node => node.IsDirectory && node.IsImplicit
            && string.Equals(node.NormalizedPath, restore.FolderPath, StringComparison.Ordinal))?.Id
            ?? throw new InvalidDataException("The persisted archive folder is unavailable.");
    }

    private void RebuildPresentation()
    {
        var matches = MatchingNodes();
        int pageCount = matches.Count == 0 ? 1 : (matches.Count + MaximumVisibleItems - 1) / MaximumVisibleItems;
        _pageIndex = Math.Clamp(_pageIndex, 0, pageCount - 1);
        Items = matches.Skip(_pageIndex * MaximumVisibleItems).Take(MaximumVisibleItems)
            .Select(ToFileItem).ToArray();
        Breadcrumbs = BuildBreadcrumbs();
        State = matches.Count == 0 ? ArchiveLocationState.Empty : ArchiveLocationState.Ready;
        Status = matches.Count == 0
            ? "This archive location is empty."
            : pageCount > 1
                ? $"{matches.Count:N0} items · page {_pageIndex + 1} of {pageCount}"
                : $"{matches.Count:N0} item{(matches.Count == 1 ? string.Empty : "s")}";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private List<Node> MatchingNodes()
    {
        IEnumerable<Node> candidates = CurrentChildren();
        if (_recursiveSearch && _searchText.Length > 0)
        {
            var descendants = new List<Node>();
            var stack = new Stack<Node>(candidates.Reverse());
            while (stack.Count > 0 && descendants.Count <= MaximumIndexedEntries)
            {
                var node = stack.Pop();
                descendants.Add(node);
                for (int index = node.Children.Count - 1; index >= 0; index--)
                    stack.Push(node.Children[index]);
            }
            candidates = descendants;
        }
        if (_searchText.Length > 0)
            candidates = candidates.Where(node => node.Name.Contains(
                _searchText, StringComparison.OrdinalIgnoreCase));
        return candidates.ToList();
    }

    private IReadOnlyList<Node> CurrentChildren() =>
        _currentNodeId is null ? _roots
        : _nodes.TryGetValue(_currentNodeId, out var node) ? node.Children
        : Array.Empty<Node>();

    private IReadOnlyList<ArchiveBreadcrumb> BuildBreadcrumbs()
    {
        var result = new List<ArchiveBreadcrumb> { new(_rootTitle, null, 0) };
        for (int archiveDepth = 1; archiveDepth <= _currentDepth; archiveDepth++)
        {
            SessionFrame child = _sessionChain[archiveDepth];
            SessionFrame parent = _sessionChain[archiveDepth - 1];
            if (child.SourceNodeId is null || parent.Index is null
                || !parent.Index.Nodes.TryGetValue(child.SourceNodeId, out Node? source))
                throw new InvalidDataException("The nested archive breadcrumb authority was unavailable.");
            AppendDirectoryBreadcrumbs(result, parent, source.ParentId, archiveDepth - 1);
            result.Add(new ArchiveBreadcrumb(child.Title, null, archiveDepth));
        }
        AppendDirectoryBreadcrumbs(
            result, _sessionChain[_currentDepth], _currentNodeId, _currentDepth);
        return result;
    }

    private static void AppendDirectoryBreadcrumbs(
        List<ArchiveBreadcrumb> result,
        SessionFrame frame,
        string? nodeId,
        int archiveDepth)
    {
        if (nodeId is null || frame.Index is null) return;
        var chain = new Stack<Node>();
        string? current = nodeId;
        int pathDepth = 0;
        while (current is not null && frame.Index.Nodes.TryGetValue(current, out Node? node))
        {
            if (++pathDepth > ArchivePathRules.MaximumPathDepth)
                throw new InvalidDataException("The archive breadcrumb chain exceeded the depth limit.");
            chain.Push(node);
            current = node.ParentId;
        }
        while (chain.Count > 0)
        {
            Node node = chain.Pop();
            result.Add(new ArchiveBreadcrumb(node.Name, node.Id, archiveDepth));
        }
    }

    private static FileItem ToFileItem(Node node) => new()
    {
        Name = node.Name,
        FullPath = string.Empty,
        IsDirectory = node.IsDirectory,
        Size = node.Descriptor?.OriginalSize ?? 0,
        Modified = node.Descriptor?.ModifiedTimeUtc?.LocalDateTime ?? default,
        IsArchiveEntry = true,
        ArchiveNodeId = node.Id,
    };

    private ArchiveLocation CreateLocationForCurrentFolder() =>
        _currentNodeId is null || !_nodes.TryGetValue(_currentNodeId, out Node? node)
            ? CreateLocation(folderPath: string.Empty, folderDuplicateOrdinal: null)
            : CreateLocation(node.NormalizedPath ?? string.Empty,
                node.IsImplicit ? null : node.DuplicateOrdinal);

    private ArchiveLocation CreateLocation(Node node) =>
        CreateLocation(node.NormalizedPath ?? string.Empty,
            node.IsImplicit ? null : node.DuplicateOrdinal);

    private ArchiveLocation CreateLocation(string folderPath, int? folderDuplicateOrdinal) => new(
        _sourcePath!,
        _sessionChain[0].Archive.Fingerprint,
        folderPath,
        folderDuplicateOrdinal)
    {
        NestedSegments = _sessionChain
            .Skip(1)
            .Take(_currentDepth)
            .Select(frame => frame.PersistentSegment
                ?? throw new InvalidDataException("The nested archive persistence chain was incomplete."))
            .ToArray(),
    };

    private async Task<bool> CloseCurrentSessionAsync()
    {
        if (_client is null || _sessionChain.Count == 0) return true;
        IArchiveClient client = _client;
        _client = null;
        _archive = null;
        return await CloseFramesAsync(client, _sessionChain).ConfigureAwait(false);
    }

    private static void ReleaseFrameReservations(IEnumerable<SessionFrame> frames)
    {
        foreach (SessionFrame frame in frames) frame.ByteReservation?.Dispose();
    }

    private async Task<bool> CloseFramesAsync(IArchiveClient client, List<SessionFrame> frames)
    {
        bool acknowledged = true;
        for (int index = frames.Count - 1; index >= 0; index--)
        {
            if (acknowledged)
            {
                acknowledged = await _runtime.CloseSessionAsync(
                        client, frames[index].Archive.SessionId)
                    .ConfigureAwait(false);
            }
            frames[index].ByteReservation?.Dispose();
        }
        frames.Clear();
        return acknowledged;
    }

    private async Task ClosePreparedAsync(PreparedChain prepared) =>
        _ = await CloseFramesAsync(prepared.Client, prepared.Frames).ConfigureAwait(false);

    private void ClearSessionData()
    {
        ReleaseFrameReservations(_sessionChain);
        _sessionChain.Clear();
        _client = null;
        _archive = null;
        _nodes.Clear();
        _roots.Clear();
        _history.Clear();
        _historyIndex = -1;
        _currentNodeId = null;
        _currentDepth = 0;
        _persistentKeysEnabled = false;
        Items = Array.Empty<FileItem>();
        Breadcrumbs = Array.Empty<ArchiveBreadcrumb>();
        lock (_archiveOperationSync) _selectedArchiveNodeIds.Clear();
    }

    private void SetFailure(ArchiveLocationState state, string status)
    {
        State = state;
        Status = status;
        Items = Array.Empty<FileItem>();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void RestorePriorPresentation(ArchiveLocationState state, string status)
    {
        State = state;
        Status = status;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void CancelActiveOperation()
    {
        lock (_operationSync) _operationCancellation?.Cancel();
    }

    private bool IsCurrentOperation(long operationVersion) =>
        operationVersion == Volatile.Read(ref _operationVersion);

    private void OnClientInvalidated(object? sender, ArchiveClientInvalidatedEventArgs e)
        => BeginClientInvalidation(e.Client);

    private void BeginClientInvalidation(IArchiveClient invalidatedClient)
    {
        _ = _archiveTransferCoordinator.InvalidateAsync(ArchiveTransferReleaseReason.WorkerLost);
        ActiveArchiveOperation? archiveOperation;
        lock (_archiveOperationSync) archiveOperation = _activeArchiveOperation;
        if (ReferenceEquals(archiveOperation?.Client, invalidatedClient))
            archiveOperation.MarkCancellationPending();
        lock (_clientInvalidationSync)
        {
            if (_disposed
                || !ReferenceEquals(_client, invalidatedClient)
                || ReferenceEquals(_invalidatingClient, invalidatedClient))
                return;
            Volatile.Write(ref _invalidatingClient, invalidatedClient);
            Task previewDrain = InvalidatePreview(ArchiveEntryPreviewState.Offline);
            _clientInvalidationTask = CompleteClientInvalidationAsync(
                _clientInvalidationTask, previewDrain, invalidatedClient);
        }
    }

    private async Task CompleteClientInvalidationAsync(
        Task priorInvalidation,
        Task previewDrain,
        IArchiveClient invalidatedClient)
    {
        await priorInvalidation.ConfigureAwait(false);
        await previewDrain.ConfigureAwait(false);
        await _openGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_clientInvalidationSync)
            {
                if (ReferenceEquals(_client, invalidatedClient))
                {
                    ClearSessionData();
                    if (!_disposed)
                    {
                        SetFailure(ArchiveLocationState.Offline,
                            "The Rain Archives Worker is unavailable. Reopen the archive after reconnecting.");
                    }
                }
                if (ReferenceEquals(_invalidatingClient, invalidatedClient))
                    Volatile.Write(ref _invalidatingClient, null);
            }
        }
        finally
        {
            _openGate.Release();
        }
    }

    private async Task DrainClientInvalidationAsync()
    {
        while (true)
        {
            Task pending;
            lock (_clientInvalidationSync) pending = _clientInvalidationTask;
            await pending.ConfigureAwait(false);
            lock (_clientInvalidationSync)
            {
                if (ReferenceEquals(pending, _clientInvalidationTask)) return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task clientInvalidation;
        lock (_clientInvalidationSync)
        {
            if (_disposed) return;
            _disposed = true;
            _runtime.ClientInvalidated -= OnClientInvalidated;
            clientInvalidation = _clientInvalidationTask;
        }
        Interlocked.Increment(ref _operationVersion);
        CancelActiveOperation();
        Task transferInvalidation = _archiveTransferCoordinator.InvalidateAsync(
            ArchiveTransferReleaseReason.ApplicationShutdown);
        await CancelAndDrainArchiveOperationAsync().ConfigureAwait(false);
        await transferInvalidation.ConfigureAwait(false);
        await _archiveTransferCoordinator.DrainAsync().ConfigureAwait(false);
        await _archiveTransferCoordinator.DisposeAsync().ConfigureAwait(false);
        await InvalidatePreview(ArchiveEntryPreviewState.Cancelled).ConfigureAwait(false);
        await clientInvalidation.ConfigureAwait(false);
        await _openGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_operationSync)
            {
                _operationCancellation?.Dispose();
                _operationCancellation = null;
            }
            await CloseCurrentSessionAsync().ConfigureAwait(false);
            ClearSessionData();
        }
        finally
        {
            _openGate.Release();
        }
    }

    private static ArchiveLocationState MapState(ArchiveErrorCategory category) => category switch
    {
        ArchiveErrorCategory.UnsupportedFormat or ArchiveErrorCategory.UnsupportedCapability => ArchiveLocationState.Unsupported,
        ArchiveErrorCategory.PasswordRequired or ArchiveErrorCategory.IncorrectPassword => ArchiveLocationState.PasswordRequired,
        ArchiveErrorCategory.SourceChanged => ArchiveLocationState.Stale,
        ArchiveErrorCategory.WorkerFailure or ArchiveErrorCategory.ProtocolMismatch => ArchiveLocationState.Offline,
        _ => ArchiveLocationState.Error,
    };

    private static string SafeStatus(ArchiveErrorCategory category) => category switch
    {
        ArchiveErrorCategory.UnsupportedFormat => "This archive format is not supported.",
        ArchiveErrorCategory.UnsupportedCapability => "This archive cannot be browsed with the connected Worker.",
        ArchiveErrorCategory.PasswordRequired => "This archive requires a password and cannot be opened in this read-only integration yet.",
        ArchiveErrorCategory.IncorrectPassword => "The archive password was not accepted.",
        ArchiveErrorCategory.SourceChanged => "The archive source changed. Reopen it before continuing.",
        ArchiveErrorCategory.WorkerFailure or ArchiveErrorCategory.ProtocolMismatch =>
            "The Rain Archives Worker is unavailable. Reopen the archive after reconnecting.",
        ArchiveErrorCategory.InvalidArchive or ArchiveErrorCategory.CorruptArchive =>
            "This file is not a complete, valid archive. It may be damaged, truncated, or only named like an archive.",
        ArchiveErrorCategory.ResourceLimit => "The archive exceeds the bounded browser limits.",
        ArchiveErrorCategory.PermissionDenied => "Windows denied access to this archive.",
        ArchiveErrorCategory.ProviderFailure =>
            "Rain Archives could not decode this archive. It may be damaged, incomplete, or use unsupported archive features.",
        ArchiveErrorCategory.InvalidRequest =>
            "Rain Archives could not accept the validated archive-open request.",
        ArchiveErrorCategory.UnsafePath or ArchiveErrorCategory.PathCollision =>
            "The archive contains unsafe or conflicting entry paths and was not opened.",
        ArchiveErrorCategory.Cancelled => "Archive opening was cancelled.",
        _ => "Rain Archives could not open this archive safely.",
    };

    private static bool FingerprintsEqual(ArchiveFingerprint left, ArchiveFingerprint right) =>
        left.Length == right.Length
        && left.LastWriteTimeUtc == right.LastWriteTimeUtc
        && string.Equals(left.FileIdentity, right.FileIdentity, StringComparison.Ordinal)
        && string.Equals(left.PrefixSha256, right.PrefixSha256, StringComparison.Ordinal);

    // Nested sources are rematerialized into a fresh Worker-owned file on every restore,
    // so filesystem identity and timestamp are intentionally ephemeral. The immutable
    // parent fingerprint plus persistent entry key bind the source; length and prefix
    // hash are the stable child-fingerprint components that can be compared across opens.
    private static bool NestedContentFingerprintsEqual(
        ArchiveFingerprint left,
        ArchiveFingerprint right) =>
        left.Length == right.Length
        && string.Equals(left.PrefixSha256, right.PrefixSha256, StringComparison.Ordinal);

    internal static bool CanAddVirtualNode(int currentCount) =>
        currentCount >= 0 && currentCount < MaximumIndexedEntries;

    private static string SafeTitle(string sourcePath)
    {
        string title = Path.GetFileName(sourcePath);
        return string.IsNullOrWhiteSpace(title) ? "Archive" : UntrustedDisplayText.Escape(title);
    }

    private static string BoundActivityCurrentItem(string value)
    {
        int maximum = ActivityOperationUpdate.MaximumCurrentItemCharacters;
        if (value.Length <= maximum) return value;

        int prefixLength = maximum - 1;
        if (prefixLength > 0
            && char.IsHighSurrogate(value[prefixLength - 1])
            && char.IsLowSurrogate(value[prefixLength]))
        {
            prefixLength--;
        }
        return value[..prefixLength] + "…";
    }

    private sealed class PreparedChain(
        IArchiveClient client,
        List<SessionFrame> frames)
    {
        public IArchiveClient Client { get; } = client;
        public List<SessionFrame> Frames { get; } = frames;
        public SessionFrame Root => Frames[0];
        public SessionFrame Current => Frames[^1];
    }

    private sealed record SessionFrame(
        ArchiveDescriptor Archive,
        PreparedIndex? Index,
        bool PersistentKeysEnabled,
        string Title,
        string? SourceNodeId,
        ArchiveNestedSegment? PersistentSegment,
        long MaterializedBytes,
        long ChainMaterializedBytes,
        ArchiveClientRuntime.NestedArchiveByteReservation? ByteReservation,
        int Depth);

    private sealed record ArchiveCursor(int ArchiveDepth, string? NodeId);

    private sealed class StaleArchiveLocationException : Exception;

    private sealed record OperationAuthority(
        IArchiveClient? Client,
        ArchiveSessionId SessionId,
        ArchiveFingerprint? Fingerprint,
        EntryId[] EntryIds,
        IReadOnlyDictionary<EntryId, string> EntryNames,
        ArchiveTransferSelectionManifest? TransferSelection,
        string? SourcePath,
        GetZipWriteCapabilitiesResponse? ZipWriteCapabilities,
        ArchiveOperationOutcome? Failure)
    {
        public static OperationAuthority Refused(ArchiveOperationOutcome failure) =>
            new(
                null,
                default,
                null,
                [],
                new Dictionary<EntryId, string>(),
                null,
                null,
                null,
                failure);
    }

    /// <summary>Owns caller-supplied plaintext and makes every cleanup path idempotent.</summary>
    private sealed class OneShotOperationPassword(char[]? value)
    {
        private char[]? _value = value;

        public char[]? Value => Volatile.Read(ref _value);

        public void Clear()
        {
            char[]? captured = Interlocked.Exchange(ref _value, null);
            if (captured is not null) Array.Clear(captured);
        }
    }

    private sealed class ActiveArchiveOperation(
        ActivityOperationKind kind,
        IArchiveActivityHandle activity)
    {
        private readonly object _sync = new();
        private readonly List<OperationEvent> _pendingEvents = [];
        private IReadOnlyDictionary<EntryId, string> _entryNames =
            new Dictionary<EntryId, string>();
        private IArchiveClient? _client;
        private OperationId? _operationId;
        private long _lastEventSequence;
        private long _bytesProcessed;
        private long? _totalBytes;
        private int _entriesProcessed;
        private int? _totalEntries;
        private TimeSpan? _elapsed;
        private double? _bytesPerSecond;
        private double? _compressionRatio;
        private string? _currentItem;
        private int _warningCount;
        private int _cancelRequestStarted;
        private int _terminalReported;
        private int _unexpectedWaiting;

        public ActivityOperationKind Kind { get; } = kind;
        public IArchiveActivityHandle Activity { get; } = activity;
        public CancellationTokenSource AdmissionCancellation { get; } = new();
        public CancellationTokenSource TerminalWaitCancellation { get; } = new();
        public TaskCompletionSource Drained { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IArchiveClient? Client
        {
            get { lock (_sync) return _client; }
        }

        public OperationId? OperationId
        {
            get { lock (_sync) return _operationId; }
        }

        public bool IsCancellationRequested => AdmissionCancellation.IsCancellationRequested;
        public bool UnexpectedWaiting => Volatile.Read(ref _unexpectedWaiting) != 0;

        public void SetClient(IArchiveClient client)
        {
            lock (_sync) _client = client;
        }

        public void SetAuthority(OperationAuthority authority)
        {
            lock (_sync)
            {
                _client = authority.Client;
                _entryNames = authority.EntryNames;
            }
        }

        public void SetOperationId(
            OperationId operationId,
            Action<ActiveArchiveOperation> requestCancellation)
        {
            List<OperationEvent> pending;
            bool cancellationRequested;
            lock (_sync)
            {
                _operationId = operationId;
                pending = _pendingEvents
                    .Where(item => item.OperationId == operationId)
                    .OrderBy(item => item.Sequence)
                    .ToList();
                _pendingEvents.Clear();
                cancellationRequested = AdmissionCancellation.IsCancellationRequested;
            }

            foreach (OperationEvent operationEvent in pending)
            {
                if (!TryAcceptEvent(operationEvent, out ActivityOperationUpdate? update)) continue;
                if (update is not null) Activity.Report(update);
                if (Kind == ActivityOperationKind.ArchiveRebuild
                    && IsUnexpectedInteractiveEvent(operationEvent))
                {
                    MarkUnexpectedWaiting();
                    requestCancellation(this);
                }
            }
            if (cancellationRequested) requestCancellation(this);
        }

        public bool TryAcceptEvent(
            OperationEvent operationEvent,
            out ActivityOperationUpdate? update)
        {
            lock (_sync)
            {
                update = null;
                if (Volatile.Read(ref _terminalReported) != 0) return false;
                if (_operationId is null)
                {
                    if (_pendingEvents.Count < ProtocolConstants.MaximumPendingRequests)
                        _pendingEvents.Add(operationEvent);
                    return false;
                }
                if (operationEvent.OperationId != _operationId
                    || operationEvent.Sequence <= _lastEventSequence)
                    return false;

                _lastEventSequence = operationEvent.Sequence;
                if (operationEvent.Kind == OperationEventKind.Warning) _warningCount++;
                if (operationEvent.Progress is { } progress) ApplyProgressUnsafe(progress);
                update = operationEvent.Kind == OperationEventKind.Terminal
                    ? null
                    : CreateUpdateUnsafe(MapOperationPhase(operationEvent.Phase));
                return true;
            }
        }

        public void ApplyProgress(OperationProgress progress)
        {
            lock (_sync) ApplyProgressUnsafe(progress);
        }

        public void ApplySnapshot(OperationActivitySnapshot snapshot)
        {
            lock (_sync)
            {
                if (_operationId != snapshot.OperationId
                    || snapshot.Kind != WorkerOperationKind(Kind))
                    return;
                _lastEventSequence = Math.Max(_lastEventSequence, snapshot.LastEventSequence);
                _warningCount = Math.Max(_warningCount, snapshot.WarningCount);
                if (snapshot.Progress is { } progress)
                {
                    _bytesProcessed = progress.BytesProcessed;
                    _totalBytes = progress.TotalBytes;
                    _entriesProcessed = progress.EntriesProcessed;
                    _totalEntries = progress.TotalEntries;
                    _elapsed = progress.Elapsed;
                    _bytesPerSecond = progress.BytesPerSecond;
                    _compressionRatio = progress.CompressionRatio;
                }
            }
        }

        public ActiveArchiveOperation EnsureWarningCount()
        {
            lock (_sync) _warningCount = Math.Max(1, _warningCount);
            return this;
        }

        public void MarkUnexpectedWaiting() =>
            Interlocked.Exchange(ref _unexpectedWaiting, 1);

        public ActivityOperationUpdate CreateUpdate(ActivityOperationPhase phase)
        {
            lock (_sync) return CreateUpdateUnsafe(phase);
        }

        public void ReportTerminal(
            ActivityOperationPhase phase,
            ActivityOperationFailure failure,
            ArchiveDestinationConflictReason conflictReason =
                ArchiveDestinationConflictReason.None)
        {
            if (Interlocked.Exchange(ref _terminalReported, 1) != 0) return;
            ActivityOperationUpdate update;
            lock (_sync)
            {
                _currentItem = null;
                update = CreateUpdateUnsafe(phase, failure, conflictReason);
            }
            Activity.Report(update);
        }

        public bool MarkCancellationPending()
        {
            lock (_sync)
            {
                if (Volatile.Read(ref _terminalReported) != 0) return false;
                try { AdmissionCancellation.Cancel(); }
                catch (ObjectDisposedException) { return false; }
            }
            Activity.MarkCancellationPending();
            return true;
        }

        public bool TryReportTransferSuccess(ActivityOperationPhase phase)
        {
            ActivityOperationUpdate update;
            lock (_sync)
            {
                if (AdmissionCancellation.IsCancellationRequested
                    || Volatile.Read(ref _terminalReported) != 0)
                    return false;
                Volatile.Write(ref _terminalReported, 1);
                _currentItem = null;
                update = CreateUpdateUnsafe(phase, ActivityOperationFailure.None);
            }
            Activity.Report(update);
            return true;
        }

        public void EndTerminalWaitAfterClientTeardown()
        {
            try { TerminalWaitCancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        public bool TryBeginCancelRequest(
            out IArchiveClient client,
            out OperationId operationId)
        {
            lock (_sync)
            {
                if (_client is null || _operationId is null
                    || Interlocked.Exchange(ref _cancelRequestStarted, 1) != 0)
                {
                    client = null!;
                    operationId = default;
                    return false;
                }
                client = _client;
                operationId = _operationId.Value;
                return true;
            }
        }

        private void ApplyProgressUnsafe(OperationProgress progress)
        {
            _bytesProcessed = progress.BytesProcessed;
            _totalBytes = progress.TotalBytes;
            _entriesProcessed = progress.EntriesProcessed;
            _totalEntries = progress.TotalEntries;
            _elapsed = progress.Elapsed;
            _bytesPerSecond = progress.BytesPerSecond;
            _compressionRatio = progress.CompressionRatio;
            _currentItem = progress.CurrentEntryId is EntryId entryId
                && _entryNames.TryGetValue(entryId, out string? name)
                    ? BoundActivityCurrentItem(name)
                    : null;
        }

        private ActivityOperationUpdate CreateUpdateUnsafe(
            ActivityOperationPhase phase,
            ActivityOperationFailure failure = ActivityOperationFailure.None,
            ArchiveDestinationConflictReason conflictReason =
                ArchiveDestinationConflictReason.None) =>
            new(
                Kind,
                phase,
                _bytesProcessed,
                _totalBytes,
                _entriesProcessed,
                _totalEntries,
                _elapsed,
                _bytesPerSecond,
                _compressionRatio,
                _currentItem,
                _warningCount,
                failure,
                FailureLimit: null,
                DestinationConflictReason: conflictReason);
    }

    private sealed record PreparedIndex(
        IReadOnlyDictionary<string, Node> Nodes,
        IReadOnlyList<Node> Roots);

    private sealed class PreviewRequest(
        string nodeId,
        long authorityGeneration,
        CancellationToken callerCancellation)
    {
        private int _forcedState;

        public string NodeId { get; } = nodeId;
        public long AuthorityGeneration { get; } = authorityGeneration;
        public CancellationToken CallerCancellation { get; } = callerCancellation;
        public CancellationTokenSource? Cancellation { get; set; }
        public TaskCompletionSource<ArchiveEntryPreviewResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ArchiveEntryPreviewState? ForcedState
        {
            get
            {
                int value = Volatile.Read(ref _forcedState);
                return value == 0 ? null : (ArchiveEntryPreviewState)(value - 1);
            }
        }

        public void Force(ArchiveEntryPreviewState state) =>
            Interlocked.Exchange(ref _forcedState, (int)state + 1);
    }

    private sealed class Node
    {
        private Node(string id, string name, string? normalizedPath, ArchiveEntryDescriptor? descriptor)
        {
            Id = id;
            Name = name;
            NormalizedPath = normalizedPath;
            Descriptor = descriptor;
        }

        public string Id { get; }
        public string Name { get; }
        public string? NormalizedPath { get; }
        public ArchiveEntryDescriptor? Descriptor { get; }
        public string? ParentId { get; set; }
        public List<Node> Children { get; } = [];
        public bool IsImplicit => Descriptor is null
            || Descriptor.ProviderOrdinal < 0 && Descriptor.Kind == ArchiveEntryKind.Directory;
        public bool IsDirectory => IsImplicit || Descriptor!.Kind == ArchiveEntryKind.Directory;
        public int DuplicateOrdinal => Descriptor?.DuplicateOrdinal ?? 0;

        public static Node Material(ArchiveEntryDescriptor descriptor, string? normalizedPath) =>
            new("material:" + descriptor.EntryId.Value.ToString("N"),
                UntrustedDisplayText.Escape(descriptor.DisplayName), normalizedPath, descriptor);

        public static Node Implicit(string id, string name, string normalizedPath) =>
            new(id, name, normalizedPath, null);
    }

    private sealed class TransferExpectedBuilder(
        string path,
        bool isDirectory,
        long length,
        bool isConcrete)
    {
        public string Path { get; } = path;
        public bool IsDirectory { get; } = isDirectory;
        public long Length { get; } = length;
        public bool IsConcrete { get; set; } = isConcrete;
    }

    private sealed class NodeComparer : IComparer<Node>
    {
        public static NodeComparer Instance { get; } = new();

        public int Compare(Node? left, Node? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            int result = right.IsDirectory.CompareTo(left.IsDirectory);
            if (result != 0) return result;
            result = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
            if (result != 0) return result;
            result = StringComparer.Ordinal.Compare(left.Name, right.Name);
            if (result != 0) return result;
            result = StringComparer.Ordinal.Compare(left.NormalizedPath, right.NormalizedPath);
            if (result != 0) return result;
            result = left.DuplicateOrdinal.CompareTo(right.DuplicateOrdinal);
            return result != 0 ? result : StringComparer.Ordinal.Compare(left.Id, right.Id);
        }
    }
}
