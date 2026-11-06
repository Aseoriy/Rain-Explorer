using System.Collections.ObjectModel;
using System.IO;
using RainArchives.App.Infrastructure;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.App.ViewModels;

public sealed class ActivityCenterViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CancelTimeout = TimeSpan.FromSeconds(2);
    private readonly IArchiveClient _client;
    private readonly Action<Action> _postToUi;
    private readonly TimeSpan _refreshInterval;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _refreshSignal = new(0, 1);
    private readonly object _stateGate = new();
    private readonly Dictionary<OperationId, MutableActivityState> _states = [];
    private readonly Dictionary<OperationId, ActivityOperationViewModel> _rowsById = [];
    private Task _refreshLoop = Task.CompletedTask;
    private bool _started;
    private bool _disposed;
    private bool _uiUpdateScheduled;
    private TaskCompletionSource? _uiDrainCompletion;
    private int _pendingUiPosts;
    private TaskCompletionSource? _allUiPostsDrained;
    private long _generation;
    private long _modelRevision;
    private long _lastAppliedUiRevision;
    private long _snapshotRevision;
    private long _lastNotifiedSnapshotRevision;
    private GetOperationActivityResponse? _latestActivity;
    private GetSupportDiagnosticsResponse? _latestDiagnostics;
    private ActivityCenterConnectionState _connectionState = ActivityCenterConnectionState.Loading;
    private ActivityCenterConnectionState _uiConnectionState = ActivityCenterConnectionState.Loading;
    private bool _isRefreshing;
    private string _statusText = "Loading connection-lifetime activity…";
    private string _lastRefreshedText = "Not refreshed yet";
    private string _policySummary =
        "At most 32 operations are admitted, one runs, queue order is unspecified, and the newest 64 terminal records are retained.";
    private int _activeAndQueuedCount;
    private int _retainedTerminalCount;
    private bool _historyTruncated;

    public ActivityCenterViewModel(
        IArchiveClient client,
        Action<Action>? postToUi = null,
        TimeSpan? refreshInterval = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _postToUi = postToUi ?? (action => action());
        _refreshInterval = refreshInterval ?? DefaultRefreshInterval;
        if (_refreshInterval < TimeSpan.FromMilliseconds(250)
            || _refreshInterval > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(
                nameof(refreshInterval),
                "The Activity Center refresh interval must be between 250 milliseconds and 30 seconds.");
        }

        RefreshCommand = new AsyncRelayCommand(RefreshNowAsync, () => CanRefresh);
    }

    public event Action<GetOperationActivityResponse>? ActivitySnapshotApplied;

    public ObservableCollection<ActivityOperationViewModel> Operations { get; } = [];
    public SupportDiagnosticsViewModel SupportDiagnostics { get; } = new();
    public AsyncRelayCommand RefreshCommand { get; }
    public bool IsLoading => _uiConnectionState == ActivityCenterConnectionState.Loading;
    public bool IsOffline => _uiConnectionState == ActivityCenterConnectionState.Offline;
    public bool IsUnavailable => _uiConnectionState == ActivityCenterConnectionState.Unsupported;
    public bool IsReady => _uiConnectionState == ActivityCenterConnectionState.Ready;
    public bool HasOperations => Operations.Count > 0;
    public bool HasNoOperations => IsReady && !HasOperations;
    public bool CanRefresh => _started && !_disposed && !_isRefreshing && !IsOffline;
    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (SetProperty(ref _isRefreshing, value))
            {
                OnPropertyChanged(nameof(CanRefresh));
                RefreshCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string LastRefreshedText
    {
        get => _lastRefreshedText;
        private set => SetProperty(ref _lastRefreshedText, value);
    }

    public string PolicySummary
    {
        get => _policySummary;
        private set => SetProperty(ref _policySummary, value);
    }

    public string UnsupportedControlsText =>
        "Unavailable in protocol 1.22: pause, retry, queue reordering, restart persistence, and password/conflict/risk decision continuations. Rain never simulates these controls.";

    public int ActiveAndQueuedCount
    {
        get => _activeAndQueuedCount;
        private set => SetProperty(ref _activeAndQueuedCount, value);
    }

    public int RetainedTerminalCount
    {
        get => _retainedTerminalCount;
        private set => SetProperty(ref _retainedTerminalCount, value);
    }

    public bool HistoryTruncated
    {
        get => _historyTruncated;
        private set => SetProperty(ref _historyTruncated, value);
    }

    public string InventorySummaryText =>
        $"{ActiveAndQueuedCount:N0} active or queued · {RetainedTerminalCount:N0} retained terminal"
        + (HistoryTruncated ? " · older history truncated" : string.Empty);

    public void Start()
    {
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return;
            }

            _started = true;
            _client.OperationEventReceived += OnOperationEventReceived;
            _refreshLoop = RunRefreshLoopAsync();
        }

        PostUi(() =>
        {
            OnPropertyChanged(nameof(CanRefresh));
            RefreshCommand.NotifyCanExecuteChanged();
        });
    }

    public void TrackStarted(
        OperationId operationId,
        OperationKind kind,
        string operationName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        if (operationId.Value == Guid.Empty || !Enum.IsDefined(kind))
        {
            throw new ArgumentException("The started operation identity or kind is invalid.", nameof(operationId));
        }

        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var state = TryGetOrCreateState(operationId);
            if (state is null)
            {
                return;
            }
            state.OperationName = operationName;
            state.Kind = kind;
            state.Phase = OperationPhase.Queued;
            state.CreatedAtUtc = state.CreatedAtUtc == default ? now : state.CreatedAtUtc;
            state.UpdatedAtUtc = now;
            state.SnapshotCanCancel = false;
            state.SeenInSnapshot = false;
            SignalModelChangedLocked();
        }

        RequestRefresh();
    }

    public Task RefreshNowAsync() => RefreshCoreAsync(_lifetime.Token);

    public void RequestRefresh()
    {
        lock (_stateGate)
        {
            if (_disposed || !_started || _connectionState == ActivityCenterConnectionState.Offline)
            {
                return;
            }
        }

        try
        {
            if (_refreshSignal.CurrentCount == 0)
            {
                _refreshSignal.Release();
            }
        }
        catch (Exception exception) when (
            exception is SemaphoreFullException or ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task refreshLoop;
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _generation++;
            _client.OperationEventReceived -= OnOperationEventReceived;
            refreshLoop = _refreshLoop;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await refreshLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }

        await _refreshGate.WaitAsync().ConfigureAwait(false);
        _refreshGate.Release();
        PostUi(() =>
        {
            OnPropertyChanged(nameof(CanRefresh));
            RefreshCommand.NotifyCanExecuteChanged();
        });

        Task uiDrain;
        lock (_stateGate)
        {
            uiDrain = _uiUpdateScheduled && _uiDrainCompletion is not null
                ? _uiDrainCompletion.Task
                : Task.CompletedTask;
        }

        try
        {
            await uiDrain.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A dispatcher that is already shutting down cannot be drained. Any delayed
            // callback observes _disposed and performs no presentation mutation.
        }

        Task allUiPostsDrained;
        lock (_stateGate)
        {
            allUiPostsDrained = _pendingUiPosts > 0 && _allUiPostsDrained is not null
                ? _allUiPostsDrained.Task
                : Task.CompletedTask;
        }

        try
        {
            await allUiPostsDrained.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A closing dispatcher may refuse to execute a final status notification.
        }

        _refreshSignal.Dispose();
        _refreshGate.Dispose();
        _lifetime.Dispose();
    }

    private async Task RunRefreshLoopAsync()
    {
        try
        {
            await RefreshCoreAsync(_lifetime.Token).ConfigureAwait(false);
            while (!_lifetime.IsCancellationRequested)
            {
                bool signalled;
                try
                {
                    signalled = await _refreshSignal
                        .WaitAsync(_refreshInterval, _lifetime.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    break;
                }

                _ = signalled;
                lock (_stateGate)
                {
                    if (_connectionState is ActivityCenterConnectionState.Offline
                        or ActivityCenterConnectionState.Unsupported)
                    {
                        break;
                    }
                }

                await RefreshCoreAsync(_lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        long generation;
        try
        {
            lock (_stateGate)
            {
                if (_disposed)
                {
                    return;
                }

                generation = _generation;
            }

            SetRefreshing(true);
            if (!_client.SupportsFeature(ProtocolFeatures.OperationActivity))
            {
                SetUnavailable(requiresProtocolUpgrade: true);
                return;
            }

            var activity = await _client
                .GetOperationActivityAsync(cancellationToken)
                .ConfigureAwait(false);
            ValidateActivity(activity);

            GetSupportDiagnosticsResponse? diagnostics = null;
            var diagnosticsRequiresUpgrade = !_client.SupportsFeature(ProtocolFeatures.SupportDiagnostics);
            if (!diagnosticsRequiresUpgrade)
            {
                diagnostics = await _client
                    .GetSupportDiagnosticsAsync(cancellationToken)
                    .ConfigureAwait(false);
                ValidateDiagnostics(diagnostics);
            }

            lock (_stateGate)
            {
                if (_disposed || generation != _generation)
                {
                    return;
                }

                ApplyActivityLocked(activity);
                _latestActivity = activity;
                _latestDiagnostics = diagnostics;
                _snapshotRevision++;
                _connectionState = ActivityCenterConnectionState.Ready;
                _statusText = "Activity is current for this Worker connection.";
                SignalModelChangedLocked();
            }

            if (diagnosticsRequiresUpgrade)
            {
                PostUi(() => SupportDiagnostics.MarkUnavailable(requiresProtocolUpgrade: true));
            }
        }
        catch (NotSupportedException)
        {
            SetUnavailable(requiresProtocolUpgrade: true);
        }
        catch (InvalidDataException)
        {
            SetOffline("Activity data was rejected because it was malformed or inconsistent.");
        }
        catch (ArchiveClientException)
        {
            SetOffline("Activity is offline because the authenticated Worker connection is unavailable.");
        }
        catch (ObjectDisposedException)
        {
            SetOffline("Activity is offline because the Client connection has closed.");
        }
        catch (IOException)
        {
            SetOffline("Activity is offline because the authenticated Worker connection is unavailable.");
        }
        catch (Exception) when (!_lifetime.IsCancellationRequested)
        {
            SetOffline("Activity is offline because the bounded refresh could not be completed safely.");
        }
        finally
        {
            SetRefreshing(false);
            _refreshGate.Release();
        }
    }

    private void OnOperationEventReceived(object? sender, OperationEvent operationEvent)
    {
        if (operationEvent.OperationId.Value == Guid.Empty
            || operationEvent.Sequence <= 0
            || !Enum.IsDefined(operationEvent.Kind)
            || !Enum.IsDefined(operationEvent.Phase))
        {
            return;
        }

        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            var state = TryGetOrCreateState(operationEvent.OperationId);
            if (state is null)
            {
                return;
            }
            if (operationEvent.Sequence <= state.LastEventSequence)
            {
                return;
            }

            state.LastEventSequence = operationEvent.Sequence;
            state.Phase = operationEvent.Phase;
            state.UpdatedAtUtc = DateTimeOffset.UtcNow;
            if (state.CreatedAtUtc == default)
            {
                state.CreatedAtUtc = state.UpdatedAtUtc;
            }

            if (operationEvent.Progress is { } progress)
            {
                state.Progress = ToActivityProgress(progress);
                state.CurrentItem = string.IsNullOrWhiteSpace(progress.CurrentEntryName)
                    ? "Not reported"
                    : UntrustedDisplayText.Escape(progress.CurrentEntryName);
            }

            if (operationEvent.Kind == OperationEventKind.Warning)
            {
                state.WarningCount = checked(state.WarningCount + 1);
            }

            if (operationEvent.Kind == OperationEventKind.Terminal)
            {
                state.CompletedAtUtc = state.UpdatedAtUtc;
                state.SnapshotCanCancel = false;
                state.ErrorCategory = operationEvent.Error?.Category;
            }

            SignalModelChangedLocked();
        }

        if (operationEvent.Kind == OperationEventKind.Terminal)
        {
            RequestRefresh();
        }
    }

    private async Task CancelOperationAsync(ActivityOperationViewModel operation)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(CancelTimeout);
            var response = await _client
                .CancelOperationAsync(operation.OperationId, timeout.Token)
                .ConfigureAwait(true);
            if (!response.CancellationRequested)
            {
                operation.ClearCancellationPending();
                lock (_stateGate)
                {
                    if (_states.TryGetValue(operation.OperationId, out var state))
                    {
                        state.SnapshotCanCancel = false;
                    }

                    _statusText = "The operation had already finished or could not accept cancellation.";
                    SignalModelChangedLocked();
                }
            }
            else
            {
                SetStatus("Cancellation requested; the terminal phase remains authoritative.");
            }

            RequestRefresh();
        }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
        {
            operation.ClearCancellationPending();
            SetStatus("The cancellation request timed out. Rain did not retry it.");
        }
        catch (Exception exception) when (
            exception is ArchiveClientException or ObjectDisposedException or IOException)
        {
            operation.ClearCancellationPending();
            SetOffline("Activity is offline because the authenticated Worker connection is unavailable.");
        }
    }

    private void ApplyActivityLocked(GetOperationActivityResponse response)
    {
        foreach (var state in _states.Values)
        {
            state.SeenInSnapshot = false;
        }

        for (var index = 0; index < response.Operations.Count; index++)
        {
            var snapshot = response.Operations[index];
            var state = TryGetOrCreateState(snapshot.OperationId);
            if (state is null)
            {
                throw new InvalidDataException("The bounded activity state could not accept a validated snapshot.");
            }
            state.Kind = snapshot.Kind;
            if (string.IsNullOrWhiteSpace(state.OperationName))
            {
                state.OperationName = DefaultOperationName(snapshot.Kind);
            }

            if (snapshot.LastEventSequence >= state.LastEventSequence)
            {
                var preserveCurrentItem = snapshot.LastEventSequence == state.LastEventSequence;
                state.Phase = snapshot.Phase;
                state.LastEventSequence = snapshot.LastEventSequence;
                state.CreatedAtUtc = snapshot.CreatedAtUtc;
                state.UpdatedAtUtc = snapshot.UpdatedAtUtc;
                state.CompletedAtUtc = snapshot.CompletedAtUtc;
                state.Progress = snapshot.Progress;
                state.WarningCount = snapshot.WarningCount;
                if (!preserveCurrentItem && snapshot.Progress is not null)
                {
                    state.CurrentItem = "Not retained in the bounded activity snapshot";
                    state.ErrorCategory = null;
                }
            }

            state.SnapshotCanCancel = snapshot.CanCancel;
            state.SnapshotOrder = index;
            state.SeenInSnapshot = true;
        }

        foreach (var operationId in _states
                     .Where(pair => pair.Value.IsTerminal && !pair.Value.SeenInSnapshot)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _states.Remove(operationId);
        }

        if (_states.Count > ProtocolConstants.MaximumOperationActivityRecords)
        {
            foreach (var operationId in _states.Values
                         .Where(state => !state.SeenInSnapshot)
                         .OrderBy(state => state.UpdatedAtUtc)
                         .Select(state => state.OperationId)
                         .Take(_states.Count - ProtocolConstants.MaximumOperationActivityRecords)
                         .ToArray())
            {
                _states.Remove(operationId);
            }
        }
    }

    private void ApplyUiState()
    {
        ActivityPresentationState[] states;
        GetOperationActivityResponse? activity;
        GetSupportDiagnosticsResponse? diagnostics;
        ActivityCenterConnectionState connectionState;
        string status;
        long revision;
        long snapshotRevision;
        lock (_stateGate)
        {
            revision = _modelRevision;
            if (_disposed)
            {
                _lastAppliedUiRevision = revision;
                return;
            }

            if (revision == _lastAppliedUiRevision)
            {
                return;
            }

            _lastAppliedUiRevision = revision;
            states = _states.Values
                .Where(state => state.Kind is not null)
                .OrderBy(state => state.SeenInSnapshot ? state.SnapshotOrder : -1)
                .ThenByDescending(state => state.UpdatedAtUtc)
                .Take(ProtocolConstants.MaximumOperationActivityRecords)
                .Select(state => state.ToPresentationState())
                .ToArray();
            activity = _latestActivity;
            diagnostics = _latestDiagnostics;
            connectionState = _connectionState;
            status = _statusText;
            snapshotRevision = _snapshotRevision;
        }

        var expectedIds = states.Select(state => state.OperationId).ToHashSet();
        foreach (var row in Operations.Where(row => !expectedIds.Contains(row.OperationId)).ToArray())
        {
            Operations.Remove(row);
            _rowsById.Remove(row.OperationId);
        }

        for (var index = 0; index < states.Length; index++)
        {
            var state = states[index];
            if (!_rowsById.TryGetValue(state.OperationId, out var row))
            {
                row = new ActivityOperationViewModel(state.OperationId, CancelOperationAsync);
                _rowsById.Add(state.OperationId, row);
                Operations.Insert(Math.Min(index, Operations.Count), row);
            }
            else
            {
                var currentIndex = Operations.IndexOf(row);
                if (currentIndex >= 0 && currentIndex != index)
                {
                    Operations.Move(currentIndex, index);
                }
            }

            row.Apply(state);
        }

        _uiConnectionState = connectionState;
        StatusText = status;
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(IsOffline));
        OnPropertyChanged(nameof(IsUnavailable));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(HasOperations));
        OnPropertyChanged(nameof(HasNoOperations));
        OnPropertyChanged(nameof(CanRefresh));
        RefreshCommand.NotifyCanExecuteChanged();

        if (activity is not null)
        {
            ActiveAndQueuedCount = activity.Operations.Count(operation => !IsTerminal(operation.Phase));
            RetainedTerminalCount = activity.Operations.Count(operation => IsTerminal(operation.Phase));
            HistoryTruncated = activity.HistoryTruncated;
            LastRefreshedText = $"Updated {activity.SnapshotAtUtc.ToLocalTime():T}";
            PolicySummary =
                $"At most {activity.MaximumActiveAndQueuedOperations:N0} operations are admitted, "
                + $"{activity.MaximumConcurrentOperations:N0} runs, queue order is unspecified, and the newest "
                + $"{activity.MaximumRetainedTerminalOperations:N0} terminal records are retained for this connection.";
            OnPropertyChanged(nameof(InventorySummaryText));
        }

        if (diagnostics is not null)
        {
            SupportDiagnostics.Apply(diagnostics);
        }
        else if (connectionState == ActivityCenterConnectionState.Offline)
        {
            SupportDiagnostics.MarkUnavailable(requiresProtocolUpgrade: false);
        }

        if (activity is not null && snapshotRevision != _lastNotifiedSnapshotRevision)
        {
            _lastNotifiedSnapshotRevision = snapshotRevision;
            ActivitySnapshotApplied?.Invoke(activity);
        }
    }

    private MutableActivityState? TryGetOrCreateState(OperationId operationId)
    {
        if (_states.TryGetValue(operationId, out var state))
        {
            return state;
        }

        if (_states.Count >= ProtocolConstants.MaximumOperationActivityRecords)
        {
            var removable = _states.Values
                .Where(candidate => candidate.IsTerminal || candidate.Kind is null)
                .OrderBy(candidate => candidate.UpdatedAtUtc)
                .FirstOrDefault();
            if (removable is null)
            {
                return null;
            }

            _states.Remove(removable.OperationId);
        }

        state = new MutableActivityState(operationId);
        _states.Add(operationId, state);
        return state;
    }

    private void SetUnavailable(bool requiresProtocolUpgrade)
    {
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            _connectionState = ActivityCenterConnectionState.Unsupported;
            _statusText = "Operation activity requires the negotiated protocol 1.19 activity feature.";
            _latestActivity = null;
            _latestDiagnostics = null;
            SignalModelChangedLocked();
        }

        PostUi(() => SupportDiagnostics.MarkUnavailable(requiresProtocolUpgrade));
    }

    private void SetOffline(string status)
    {
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            _connectionState = ActivityCenterConnectionState.Offline;
            _statusText = status;
            _generation++;
            _latestDiagnostics = null;
            SignalModelChangedLocked();
        }
    }

    private void SetStatus(string status)
    {
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }

            _statusText = status;
            SignalModelChangedLocked();
        }
    }

    private void SetRefreshing(bool refreshing) => PostUi(() => IsRefreshing = refreshing);

    private void SignalModelChangedLocked()
    {
        _modelRevision++;
        ScheduleUiUpdateLocked();
    }

    private void ScheduleUiUpdateLocked()
    {
        if (_uiUpdateScheduled)
        {
            return;
        }

        _uiUpdateScheduled = true;
        _uiDrainCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ThreadPool.QueueUserWorkItem(static state =>
        {
            var owner = (ActivityCenterViewModel)state!;
            owner.PostUi(owner.DrainUiUpdates);
        }, this);
    }

    private void DrainUiUpdates()
    {
        while (true)
        {
            ApplyUiState();
            lock (_stateGate)
            {
                if (_lastAppliedUiRevision == _modelRevision)
                {
                    _uiUpdateScheduled = false;
                    _uiDrainCompletion?.TrySetResult();
                    return;
                }
            }
        }
    }

    private void PostUi(Action action)
    {
        lock (_stateGate)
        {
            if (_pendingUiPosts == 0)
            {
                _allUiPostsDrained = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            _pendingUiPosts++;
        }

        var completed = 0;
        void CompletePost()
        {
            if (Interlocked.Exchange(ref completed, 1) != 0)
            {
                return;
            }

            lock (_stateGate)
            {
                _pendingUiPosts--;
                if (_pendingUiPosts == 0)
                {
                    _allUiPostsDrained?.TrySetResult();
                }
            }
        }

        try
        {
            _postToUi(() =>
            {
                try
                {
                    action();
                }
                finally
                {
                    CompletePost();
                }
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            CompletePost();
            lock (_stateGate)
            {
                _uiUpdateScheduled = false;
                _uiDrainCompletion?.TrySetResult();
            }
        }
    }

    private static OperationActivityProgress ToActivityProgress(OperationProgress progress) =>
        new(
            progress.BytesProcessed,
            progress.TotalBytes,
            progress.EntriesProcessed,
            progress.TotalEntries,
            progress.Elapsed,
            progress.BytesPerSecond,
            progress.CompressionRatio);

    private static string DefaultOperationName(OperationKind kind) => kind switch
    {
        OperationKind.Extract => "Extraction",
        OperationKind.IntegrityTest => "Integrity test",
        OperationKind.CreateArchive => "Archive creation",
        OperationKind.RebuildArchive => "Archive rebuild",
        _ => "Archive operation",
    };

    private static bool IsTerminal(OperationPhase phase) => phase is OperationPhase.Completed
        or OperationPhase.CompletedWithWarnings
        or OperationPhase.Cancelled
        or OperationPhase.Failed;

    private static void ValidateActivity(GetOperationActivityResponse response)
    {
        if (response is null
            || response.Operations is null
            || response.MaximumActiveAndQueuedOperations != ProtocolConstants.MaximumQueuedOperations
            || response.MaximumConcurrentOperations != ProtocolConstants.MaximumConcurrentOperations
            || response.QueueOrdering != OperationQueueOrdering.Unspecified
            || response.MaximumRetainedTerminalOperations != ProtocolConstants.MaximumRetainedOperationActivity
            || response.SupportsReordering
            || response.SupportsPause
            || response.SupportsRetry
            || response.PersistsAcrossWorkerRestart
            || response.Operations.Count > ProtocolConstants.MaximumOperationActivityRecords
            || response.Operations.Select(operation => operation.OperationId).Distinct().Count()
                != response.Operations.Count
            || response.Operations.Count(operation => !IsTerminal(operation.Phase))
                > ProtocolConstants.MaximumQueuedOperations
            || response.Operations.Count(operation => IsTerminal(operation.Phase))
                > ProtocolConstants.MaximumRetainedOperationActivity)
        {
            throw new InvalidDataException("The Client returned an invalid operation activity snapshot.");
        }

        foreach (var operation in response.Operations)
        {
            if (operation.OperationId.Value == Guid.Empty
                || !Enum.IsDefined(operation.Kind)
                || !Enum.IsDefined(operation.Phase)
                || operation.WarningCount < 0
                || IsTerminal(operation.Phase) == operation.CanCancel
                || operation.Progress is { } progress
                    && (progress.BytesProcessed < 0
                        || progress.TotalBytes is < 0
                        || progress.EntriesProcessed < 0
                        || progress.TotalEntries is < 0
                        || progress.Elapsed < TimeSpan.Zero
                        || progress.BytesPerSecond is { } throughput && !double.IsFinite(throughput)
                        || progress.CompressionRatio is { } ratio && !double.IsFinite(ratio)))
            {
                throw new InvalidDataException("The Client returned invalid operation activity data.");
            }
        }
    }

    private static void ValidateDiagnostics(GetSupportDiagnosticsResponse response)
    {
        if (response is null
            || response.ProtocolMajor != ProtocolConstants.MajorVersion
            || response.ProtocolMinor < ProtocolConstants.SupportDiagnosticsMinorVersion
            || response.EnabledFeatureCount < 1
            || response.ContainmentModel != WorkerContainmentModel.SameUserWorkerProcess
            || response.IsSameUserSecuritySandbox
            || response.MaximumActiveAndQueuedOperations != ProtocolConstants.MaximumQueuedOperations
            || response.MaximumConcurrentOperations != ProtocolConstants.MaximumConcurrentOperations
            || response.QueueOrdering != OperationQueueOrdering.Unspecified
            || response.MaximumRetainedTerminalOperations != ProtocolConstants.MaximumRetainedOperationActivity
            || response.ActiveAndQueuedOperationCount is < 0 or > ProtocolConstants.MaximumQueuedOperations
            || response.RetainedTerminalOperationCount is < 0 or > ProtocolConstants.MaximumRetainedOperationActivity
            || response.SupportsQueueReordering
            || response.SupportsPause
            || response.SupportsRetry
            || response.SupportsAutomaticRecoveryDisposition
            || response.PersistsOperationActivityAcrossWorkerRestart)
        {
            throw new InvalidDataException("The Client returned invalid fixed-field support diagnostics.");
        }
    }

    private sealed class MutableActivityState
    {
        public MutableActivityState(OperationId operationId)
        {
            OperationId = operationId;
        }

        public OperationId OperationId { get; }
        public string OperationName { get; set; } = string.Empty;
        public OperationKind? Kind { get; set; }
        public OperationPhase Phase { get; set; } = OperationPhase.Queued;
        public long LastEventSequence { get; set; }
        public DateTimeOffset CreatedAtUtc { get; set; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
        public DateTimeOffset? CompletedAtUtc { get; set; }
        public OperationActivityProgress? Progress { get; set; }
        public string CurrentItem { get; set; } = "Not reported";
        public int WarningCount { get; set; }
        public ArchiveErrorCategory? ErrorCategory { get; set; }
        public bool SnapshotCanCancel { get; set; }
        public int SnapshotOrder { get; set; }
        public bool SeenInSnapshot { get; set; }
        public bool IsTerminal => ActivityCenterViewModel.IsTerminal(Phase);

        public ActivityPresentationState ToPresentationState() =>
            new(
                OperationId,
                string.IsNullOrWhiteSpace(OperationName)
                    ? DefaultOperationName(Kind!.Value)
                    : OperationName,
                Kind!.Value,
                Phase,
                LastEventSequence,
                CreatedAtUtc,
                UpdatedAtUtc,
                CompletedAtUtc,
                Progress,
                CurrentItem,
                WarningCount,
                ErrorCategory,
                SnapshotCanCancel,
                SnapshotOrder,
                SeenInSnapshot);
    }

    private enum ActivityCenterConnectionState
    {
        Loading,
        Ready,
        Unsupported,
        Offline,
    }
}
