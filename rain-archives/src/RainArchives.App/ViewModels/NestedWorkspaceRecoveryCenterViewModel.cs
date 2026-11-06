using System.Collections.ObjectModel;
using System.IO;
using RainArchives.App.Infrastructure;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.App.ViewModels;

public sealed class NestedWorkspaceRecoveryCenterViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IArchiveClient _client;
    private readonly Func<NestedWorkspaceRecoveryDiscardConfirmation, bool> _confirmDiscard;
    private readonly Func<NestedWorkspaceRecoveryReopenConfirmation, bool>? _confirmReopen;
    private readonly Func<CancellationToken, Task<Guid?>>? _requestPasswordSlot;
    private readonly Action<ReopenNestedWorkspaceRecoveryResponse>? _onReopened;
    private readonly object _operationGate = new();
    private CancellationTokenSource? _lifetime = new();
    private Task _activeOperation = Task.CompletedTask;
    private NestedWorkspaceRecoveryItemViewModel? _selectedRecovery;
    private InspectNestedWorkspaceRecoveryResponse? _inspection;
    private InspectNestedWorkspaceReopenResponse? _reopenInspection;
    private NestedWorkspaceRecoverySourceViewModel? _selectedSource;
    private ReopenNestedWorkspaceRecoveryResponse? _reopenedArchive;
    private bool _reopenedWithPassword;
    private string _statusText = "Loading nested-workspace recovery records…";
    private string _inventorySummaryText = string.Empty;
    private bool _isBusy;

    public NestedWorkspaceRecoveryCenterViewModel(
        IArchiveClient client,
        Func<NestedWorkspaceRecoveryDiscardConfirmation, bool> confirmDiscard,
        Func<NestedWorkspaceRecoveryReopenConfirmation, bool>? confirmReopen = null,
        Func<CancellationToken, Task<Guid?>>? requestPasswordSlot = null,
        Action<ReopenNestedWorkspaceRecoveryResponse>? onReopened = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _confirmDiscard = confirmDiscard ?? throw new ArgumentNullException(nameof(confirmDiscard));
        _confirmReopen = confirmReopen;
        _requestPasswordSlot = requestPasswordSlot;
        _onReopened = onReopened;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => CanRefresh);
        InspectCommand = new AsyncRelayCommand(InspectSelectedAsync, () => CanInspect);
        DiscardCommand = new AsyncRelayCommand(DiscardInspectedAsync, () => CanDiscard);
        ReopenCommand = new AsyncRelayCommand(ReopenInspectedAsync, () => CanReopen);
    }

    public ObservableCollection<NestedWorkspaceRecoveryItemViewModel> Recoveries { get; } = [];

    public ObservableCollection<NestedWorkspaceRecoverySourceViewModel> Sources { get; } = [];

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand InspectCommand { get; }

    public AsyncRelayCommand DiscardCommand { get; }

    public AsyncRelayCommand ReopenCommand { get; }

    public bool SupportsRecovery =>
        _client.SupportsFeature(ProtocolFeatures.NestedWorkspaceRecovery);

    public bool SupportsIndependentReopen =>
        _client.SupportsFeature(ProtocolFeatures.NestedWorkspaceIndependentReopen);

    public NestedWorkspaceRecoveryItemViewModel? SelectedRecovery
    {
        get => _selectedRecovery;
        set
        {
            if (!SetProperty(ref _selectedRecovery, value))
            {
                return;
            }

            ClearInspection();
            if (value is not null)
            {
                StatusText = "Select Inspect before nested-workspace cleanup is available.";
            }

            NotifyCommandStates();
        }
    }

    public bool HasRecoveries => Recoveries.Count > 0;

    public bool HasNoRecoveries => !HasRecoveries;

    public bool HasInspection => _inspection is not null;

    public bool HasNoInspection => !HasInspection;

    public bool HasSources => Sources.Count > 0;

    public bool HasNoSources => !HasSources;

    public NestedWorkspaceRecoverySourceViewModel? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (SetProperty(ref _selectedSource, value))
            {
                NotifyCommandStates();
            }
        }
    }

    public ReopenNestedWorkspaceRecoveryResponse? ReopenedArchive
    {
        get => _reopenedArchive;
        private set => SetProperty(ref _reopenedArchive, value);
    }

    public bool ReopenedWithPassword
    {
        get => _reopenedWithPassword;
        private set => SetProperty(ref _reopenedWithPassword, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                NotifyCommandStates();
            }
        }
    }

    public bool CanRefresh => !IsBusy;

    public bool CanInspect => !IsBusy && SupportsRecovery && SelectedRecovery is not null;

    public bool CanDiscard =>
        !IsBusy
        && SupportsRecovery
        && IsVerifiedDisposable(_inspection)
        && string.Equals(
            SelectedRecovery?.RecoveryId,
            _inspection?.RecoveryId,
            StringComparison.Ordinal);

    public bool CanReopen =>
        !IsBusy
        && SupportsIndependentReopen
        && _confirmReopen is not null
        && IsVerifiedReopenable(_reopenInspection)
        && SelectedSource is not null
        && _reopenInspection!.Sources!.Any(source =>
            string.Equals(source.SourceId, SelectedSource.SourceId, StringComparison.Ordinal));

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string InventorySummaryText
    {
        get => _inventorySummaryText;
        private set => SetProperty(ref _inventorySummaryText, value);
    }

    public string RecoveryIdText => _inspection?.RecoveryId ?? string.Empty;

    public string AssessmentText => _inspection is null
        ? string.Empty
        : FormatAssessment(_inspection.Assessment);

    public string SourceCountText => _inspection?.SourceCount is { } sourceCount
        ? $"{sourceCount:N0}"
        : "Unavailable";

    public string TotalSourceBytesText => _inspection?.TotalSourceBytes is { } totalBytes
        ? FormatBytes(totalBytes)
        : "Unavailable";

    public string RevisionText => _inspection?.Revision ?? "Unavailable";

    public string InspectedAtText => _inspection is null
        ? string.Empty
        : $"Inspected {_inspection.InspectedAtUtc.LocalDateTime:G}";

    public string ActionGuidance
    {
        get
        {
            if (_inspection is null)
            {
                return "Select a path-free record and inspect it before choosing independent reopen or cleanup.";
            }

            if (!SupportsRecovery)
            {
                return "This worker does not support nested-workspace recovery.";
            }

            if (CanReopen && CanDiscard)
            {
                return "Choose one exact recovered source to reopen as a fresh independent archive, or explicitly discard the exact inactive temporary workspace.";
            }

            if (CanReopen)
            {
                return "The selected source can be revalidated and reopened as a fresh independent archive. The temporary recovery workspace is preserved.";
            }

            return _inspection.Assessment switch
            {
                NestedWorkspaceRecoveryAssessment.VerifiedDisposable when CanDiscard =>
                    "Rain can revalidate and discard only this exact inactive temporary workspace. "
                    + "No archive destination or user-selected file is part of this action.",
                NestedWorkspaceRecoveryAssessment.Active =>
                    "The workspace is still active or held open. Rain will leave it untouched.",
                NestedWorkspaceRecoveryAssessment.IdentityUnavailable =>
                    "The filesystem cannot provide enough stable identity evidence for safe cleanup.",
                NestedWorkspaceRecoveryAssessment.UnexpectedContents =>
                    "The workspace contains changed or unexpected state. Rain will preserve it.",
                NestedWorkspaceRecoveryAssessment.InspectionIncomplete =>
                    "Inspection could not prove the complete workspace shape. Rain will preserve it.",
                _ => "The inspected evidence cannot authorize cleanup.",
            };
        }
    }

    public Task InitializeAsync() => RefreshAsync();

    public Task RefreshAsync() => RunTrackedAsync(
        RefreshCoreAsync,
        NestedRecoveryUiOperation.Refresh);

    public Task InspectSelectedAsync()
    {
        var recovery = SelectedRecovery;
        if (recovery is null)
        {
            return Task.CompletedTask;
        }

        return RunTrackedAsync(
            cancellationToken => InspectCoreAsync(recovery, cancellationToken),
            NestedRecoveryUiOperation.Inspect);
    }

    public Task DiscardInspectedAsync()
    {
        var inspection = _inspection;
        if (!CanDiscard || inspection is null)
        {
            StatusText = "Cleanup is not available for the current inspected evidence.";
            return Task.CompletedTask;
        }

        return RunTrackedAsync(
            cancellationToken => DiscardCoreAsync(inspection, cancellationToken),
            NestedRecoveryUiOperation.Discard);
    }

    public Task ReopenInspectedAsync()
    {
        var inspection = _reopenInspection;
        var source = SelectedSource;
        if (!CanReopen || inspection is null || source is null)
        {
            StatusText = "Independent reopen is not available for the current inspected evidence.";
            return Task.CompletedTask;
        }

        return RunTrackedAsync(
            cancellationToken => ReopenCoreAsync(inspection, source, cancellationToken),
            NestedRecoveryUiOperation.Reopen);
    }

    public void RequestCancellation() => _lifetime?.Cancel();

    public async ValueTask DisposeAsync()
    {
        var lifetime = Interlocked.Exchange(ref _lifetime, null);
        if (lifetime is null)
        {
            return;
        }

        await lifetime.CancelAsync().ConfigureAwait(false);
        Task activeOperation;
        lock (_operationGate)
        {
            activeOperation = _activeOperation;
        }

        try
        {
            await activeOperation.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is translated into status by the tracked operation.
        }
        finally
        {
            lifetime.Dispose();
        }
    }

    private Task RunTrackedAsync(
        Func<CancellationToken, Task> action,
        NestedRecoveryUiOperation operation)
    {
        lock (_operationGate)
        {
            if (_lifetime is null || !_activeOperation.IsCompleted)
            {
                return Task.CompletedTask;
            }

            _activeOperation = RunTrackedCoreAsync(action, operation, _lifetime.Token);
            return _activeOperation;
        }
    }

    private async Task RunTrackedCoreAsync(
        Func<CancellationToken, Task> action,
        NestedRecoveryUiOperation operation,
        CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            await action(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (operation is NestedRecoveryUiOperation.Discard or NestedRecoveryUiOperation.Reopen)
            {
                ClearInspection();
                StatusText =
                    "The recovery result could not be confirmed. Rain did not retry automatically. "
                    + "Refresh and inspect again.";
            }
            else
            {
                StatusText = "Nested-workspace recovery review cancelled.";
            }
        }
        catch (Exception exception) when (
            exception is ArchiveClientException or IOException or InvalidDataException)
        {
            HandleOperationFailure(operation);
        }
        catch (Exception)
        {
            HandleOperationFailure(operation);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        if (!SupportsRecovery)
        {
            ReplaceInventory(new ListNestedWorkspaceRecoveriesResponse(
                [],
                ActiveWorkspaceCount: 0,
                SuspiciousWorkspaceCount: 0,
                IsTruncated: false,
                InspectionIncomplete: false));
            InventorySummaryText = "Nested-workspace recovery is unavailable.";
            StatusText = "The connected archive worker does not support nested-workspace recovery.";
            return;
        }

        ClearInspection();
        var inventory = await _client
            .ListNestedWorkspaceRecoveriesAsync(cancellationToken)
            .ConfigureAwait(true);
        ReplaceInventory(inventory);
        StatusText = BuildInventoryStatus(inventory);
    }

    private async Task InspectCoreAsync(
        NestedWorkspaceRecoveryItemViewModel selected,
        CancellationToken cancellationToken)
    {
        ClearInspection();
        var inspection = await _client
            .InspectNestedWorkspaceRecoveryAsync(selected.RecoveryId, cancellationToken)
            .ConfigureAwait(true);

        InspectNestedWorkspaceReopenResponse? reopenInspection = null;
        if (SupportsIndependentReopen)
        {
            reopenInspection = await _client
                .InspectNestedWorkspaceReopenAsync(selected.RecoveryId, cancellationToken)
                .ConfigureAwait(true);
        }

        if (!ReferenceEquals(SelectedRecovery, selected))
        {
            StatusText = "The selection changed. Inspect the newly selected record before cleanup.";
            return;
        }

        _inspection = inspection;
        _reopenInspection = reopenInspection;
        ReplaceSources(reopenInspection?.Sources);
        NotifyInspectionChanged();
        StatusText = CanReopen || CanDiscard
            ? "Inspection complete. Review the exact evidence before choosing an explicit action."
            : "Inspection complete. No safe recovery action is available for this workspace.";
    }

    private async Task DiscardCoreAsync(
        InspectNestedWorkspaceRecoveryResponse inspection,
        CancellationToken cancellationToken)
    {
        if (!IsVerifiedDisposable(inspection))
        {
            throw new InvalidDataException(
                "The nested-workspace inspection no longer contains complete discard evidence.");
        }

        var confirmation = BuildConfirmation(inspection);
        if (!_confirmDiscard(confirmation))
        {
            StatusText = "No nested-workspace recovery changes were made.";
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var result = await _client.DiscardNestedWorkspaceRecoveryAsync(
                inspection.RecoveryId,
                inspection.Revision!,
                inspection.SourceCount!.Value,
                inspection.TotalSourceBytes!.Value,
                cancellationToken)
            .ConfigureAwait(true);

        ClearInspection();
        var completionText = result.Result switch
        {
            NestedWorkspaceRecoveryDiscardResult.Discarded =>
                "The verified temporary workspace was discarded.",
            NestedWorkspaceRecoveryDiscardResult.AlreadyAbsent =>
                "That exact temporary workspace was already absent; no other object was deleted.",
            _ => throw new InvalidDataException(
                "The worker returned an unknown nested-workspace cleanup result."),
        };

        try
        {
            var inventory = await _client
                .ListNestedWorkspaceRecoveriesAsync(cancellationToken)
                .ConfigureAwait(true);
            ReplaceInventory(inventory);
            StatusText = $"{completionText} {BuildInventoryStatus(inventory)}";
        }
        catch (Exception exception) when (
            exception is ArchiveClientException
                or IOException
                or InvalidDataException
                or OperationCanceledException)
        {
            StatusText = $"{completionText} The recovery list could not be refreshed. No further action was taken.";
        }
    }

    private async Task ReopenCoreAsync(
        InspectNestedWorkspaceReopenResponse inspection,
        NestedWorkspaceRecoverySourceViewModel source,
        CancellationToken cancellationToken)
    {
        if (!IsVerifiedReopenable(inspection)
            || !inspection.Sources!.Any(candidate =>
                string.Equals(candidate.SourceId, source.SourceId, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("The independent reopen evidence is incomplete.");
        }

        var confirmation = BuildReopenConfirmation(inspection, source.Evidence);
        if (_confirmReopen?.Invoke(confirmation) != true)
        {
            StatusText = "No nested-workspace recovery changes were made.";
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        ReopenNestedWorkspaceRecoveryResponse reopened;
        var openedWithPassword = false;
        try
        {
            reopened = await ReopenWithEvidenceAsync(
                    inspection,
                    source.Evidence,
                    passwordSlotId: null,
                    cancellationToken)
                .ConfigureAwait(true);
        }
        catch (ArchiveClientException exception)
            when (exception.Error.Category == ArchiveErrorCategory.PasswordRequired
                && _requestPasswordSlot is not null)
        {
            var passwordSlotId = await _requestPasswordSlot(cancellationToken).ConfigureAwait(true);
            if (passwordSlotId is null)
            {
                StatusText = "Independent reopen was cancelled before a password was submitted.";
                return;
            }

            reopened = await ReopenWithEvidenceAsync(
                    inspection,
                    source.Evidence,
                    passwordSlotId,
                    cancellationToken)
                .ConfigureAwait(true);
            openedWithPassword = true;
        }

        ClearInspection();
        ReopenedArchive = reopened;
        ReopenedWithPassword = openedWithPassword;
        StatusText = "The recovered source opened as a fresh independent archive. The recovery workspace remains preserved.";
        _onReopened?.Invoke(reopened);
    }

    private Task<ReopenNestedWorkspaceRecoveryResponse> ReopenWithEvidenceAsync(
        InspectNestedWorkspaceReopenResponse inspection,
        NestedWorkspaceRecoverySourceEvidence source,
        Guid? passwordSlotId,
        CancellationToken cancellationToken) =>
        _client.ReopenNestedWorkspaceRecoveryAsync(
            inspection.RecoveryId,
            inspection.Revision!,
            inspection.SourceCount!.Value,
            inspection.TotalSourceBytes!.Value,
            source.SourceId,
            source.Length,
            source.ContentSha256,
            passwordSlotId,
            cancellationToken);

    private void ReplaceInventory(ListNestedWorkspaceRecoveriesResponse inventory)
    {
        SelectedRecovery = null;
        Recoveries.Clear();
        foreach (var recovery in inventory.Recoveries)
        {
            Recoveries.Add(new NestedWorkspaceRecoveryItemViewModel(recovery));
        }

        OnPropertyChanged(nameof(HasRecoveries));
        OnPropertyChanged(nameof(HasNoRecoveries));
        InventorySummaryText =
            $"{inventory.Recoveries.Count:N0} reviewable · "
            + $"{inventory.ActiveWorkspaceCount:N0} active · "
            + $"{inventory.SuspiciousWorkspaceCount:N0} suspicious";
        NotifyCommandStates();
    }

    private static string BuildInventoryStatus(ListNestedWorkspaceRecoveriesResponse inventory)
    {
        var status = inventory.Recoveries.Count == 0
            ? "No verified inactive nested workspaces currently need cleanup review."
            : $"{inventory.Recoveries.Count:N0} verified inactive nested workspace"
              + (inventory.Recoveries.Count == 1 ? " needs" : "s need")
              + " review.";

        if (inventory.ActiveWorkspaceCount > 0)
        {
            status += " Active workspaces were left untouched.";
        }

        if (inventory.SuspiciousWorkspaceCount > 0)
        {
            status += " Suspicious workspaces were preserved and are not actionable.";
        }

        if (inventory.IsTruncated || inventory.InspectionIncomplete)
        {
            status += " The inventory is incomplete; no automatic action was taken.";
        }

        return status;
    }

    private void HandleOperationFailure(NestedRecoveryUiOperation operation)
    {
        switch (operation)
        {
            case NestedRecoveryUiOperation.Refresh:
                ClearInspection();
                StatusText = "The nested-workspace recovery list could not be refreshed. No action was taken.";
                break;
            case NestedRecoveryUiOperation.Inspect:
                ClearInspection();
                StatusText = "Inspection failed. Reopen and cleanup are unavailable.";
                break;
            case NestedRecoveryUiOperation.Discard:
                ClearInspection();
                StatusText =
                    "The cleanup result could not be confirmed. Rain did not retry automatically. "
                    + "Refresh and inspect again.";
                break;
            case NestedRecoveryUiOperation.Reopen:
                ClearInspection();
                StatusText =
                    "Independent reopen did not complete. Rain did not retry automatically. "
                    + "Refresh and inspect again.";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private void ClearInspection()
    {
        if (_inspection is null && _reopenInspection is null && Sources.Count == 0)
        {
            return;
        }

        _inspection = null;
        _reopenInspection = null;
        SelectedSource = null;
        Sources.Clear();
        NotifyInspectionChanged();
    }

    private void NotifyInspectionChanged()
    {
        OnPropertyChanged(nameof(HasInspection));
        OnPropertyChanged(nameof(HasNoInspection));
        OnPropertyChanged(nameof(RecoveryIdText));
        OnPropertyChanged(nameof(AssessmentText));
        OnPropertyChanged(nameof(SourceCountText));
        OnPropertyChanged(nameof(TotalSourceBytesText));
        OnPropertyChanged(nameof(RevisionText));
        OnPropertyChanged(nameof(InspectedAtText));
        OnPropertyChanged(nameof(ActionGuidance));
        OnPropertyChanged(nameof(HasSources));
        OnPropertyChanged(nameof(HasNoSources));
        NotifyCommandStates();
    }

    private void NotifyCommandStates()
    {
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(CanInspect));
        OnPropertyChanged(nameof(CanDiscard));
        OnPropertyChanged(nameof(CanReopen));
        OnPropertyChanged(nameof(ActionGuidance));
        RefreshCommand.NotifyCanExecuteChanged();
        InspectCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
        ReopenCommand.NotifyCanExecuteChanged();
    }

    private void ReplaceSources(IReadOnlyList<NestedWorkspaceRecoverySourceEvidence>? sources)
    {
        SelectedSource = null;
        Sources.Clear();
        if (sources is not null)
        {
            for (var index = 0; index < sources.Count; index++)
            {
                Sources.Add(new NestedWorkspaceRecoverySourceViewModel(sources[index], index));
            }
        }

        SelectedSource = Sources.FirstOrDefault();
        OnPropertyChanged(nameof(HasSources));
        OnPropertyChanged(nameof(HasNoSources));
    }

    private static bool IsVerifiedDisposable(InspectNestedWorkspaceRecoveryResponse? inspection) =>
        inspection is
        {
            Assessment: NestedWorkspaceRecoveryAssessment.VerifiedDisposable,
            SourceCount: >= 0 and <= ProtocolConstants.MaximumOpenSessions,
            TotalSourceBytes: >= 0 and <= ProtocolConstants.MaximumActiveNestedArchiveBytes,
            Revision: { Length: 64 },
        }
        && IsLowercaseSha256(inspection.Revision)
        && (inspection.SourceCount != 0 || inspection.TotalSourceBytes == 0);

    private static bool IsVerifiedReopenable(InspectNestedWorkspaceReopenResponse? inspection) =>
        inspection is
        {
            Assessment: NestedWorkspaceRecoveryAssessment.VerifiedDisposable,
            SupportsIndependentReopen: true,
            SourceCount: > 0 and <= ProtocolConstants.MaximumOpenSessions,
            TotalSourceBytes: >= 0 and <= ProtocolConstants.MaximumActiveNestedArchiveBytes,
            Revision: { Length: 64 },
            Sources: { Count: > 0 },
        }
        && inspection.Sources.Count == inspection.SourceCount
        && IsLowercaseSha256(inspection.Revision)
        && inspection.Sources.All(source =>
            source.SourceId.Length == 64
            && IsLowercaseSha256(source.SourceId)
            && source.Length is >= 0 and <= ProtocolConstants.MaximumNestedArchiveBytes
            && source.ContentSha256.Length == 64
            && IsLowercaseSha256(source.ContentSha256))
        && inspection.Sources.Sum(source => source.Length) == inspection.TotalSourceBytes;

    private static NestedWorkspaceRecoveryDiscardConfirmation BuildConfirmation(
        InspectNestedWorkspaceRecoveryResponse inspection)
    {
        var recoveryId = UntrustedDisplayText.Escape(inspection.RecoveryId);
        var revision = UntrustedDisplayText.Escape(inspection.Revision!);
        var sourceCount = inspection.SourceCount!.Value;
        var totalSourceBytes = inspection.TotalSourceBytes!.Value;
        return new NestedWorkspaceRecoveryDiscardConfirmation(
            inspection.RecoveryId,
            inspection.Revision!,
            sourceCount,
            totalSourceBytes,
            "Discard verified nested workspace?",
            $"Permanently discard Rain's verified inactive temporary workspace?\n\n"
            + $"Recovery ID:\n{recoveryId}\n\n"
            + $"Revision:\n{revision}\n\n"
            + $"Nested sources: {sourceCount:N0}\n"
            + $"Total source bytes: {totalSourceBytes:N0}\n\n"
            + "The worker will revalidate the exact identities, names, links, streams, counts, "
            + "and lengths immediately before deletion. No archive destination or user-selected "
            + "file is part of this action. If the worker is interrupted after deletion begins, "
            + "Rain may preserve a smaller suspicious workspace instead of retrying. Continue?");
    }

    private static NestedWorkspaceRecoveryReopenConfirmation BuildReopenConfirmation(
        InspectNestedWorkspaceReopenResponse inspection,
        NestedWorkspaceRecoverySourceEvidence source) =>
        new(
            inspection.RecoveryId,
            inspection.Revision!,
            inspection.SourceCount!.Value,
            inspection.TotalSourceBytes!.Value,
            source.SourceId,
            source.Length,
            source.ContentSha256,
            "Open recovered archive?",
            "Revalidate this exact recovered source and open it as a fresh independent archive?\n\n"
            + $"Recovery revision: {UntrustedDisplayText.Escape(inspection.Revision!)}\n"
            + $"Workspace sources: {inspection.SourceCount.Value:N0}\n"
            + $"Workspace bytes: {inspection.TotalSourceBytes.Value:N0}\n"
            + $"Selected source bytes: {source.Length:N0}\n"
            + $"Selected source SHA-256: {source.ContentSha256}\n\n"
            + "No temporary path is revealed. The recovery workspace is preserved, and the reopened archive becomes a depth-zero independent session. Continue?");

    private static bool IsLowercaseSha256(string value) =>
        value.All(character =>
            character is >= '0' and <= '9'
            or >= 'a' and <= 'f');

    private static string FormatAssessment(NestedWorkspaceRecoveryAssessment assessment) =>
        assessment switch
        {
            NestedWorkspaceRecoveryAssessment.VerifiedDisposable => "Verified inactive workspace",
            NestedWorkspaceRecoveryAssessment.Active => "Active or held open",
            NestedWorkspaceRecoveryAssessment.IdentityUnavailable => "Stable identity unavailable",
            NestedWorkspaceRecoveryAssessment.UnexpectedContents => "Unexpected or changed contents",
            NestedWorkspaceRecoveryAssessment.InspectionIncomplete => "Inspection incomplete",
            _ => "Unknown assessment",
        };

    private static string FormatBytes(long bytes)
    {
        const double kibibyte = 1024;
        const double mebibyte = kibibyte * 1024;
        return bytes switch
        {
            >= (long)mebibyte => $"{bytes / mebibyte:N2} MiB ({bytes:N0} bytes)",
            >= (long)kibibyte => $"{bytes / kibibyte:N2} KiB ({bytes:N0} bytes)",
            _ => $"{bytes:N0} bytes",
        };
    }

    private enum NestedRecoveryUiOperation
    {
        Refresh,
        Inspect,
        Discard,
        Reopen,
    }
}

public sealed class NestedWorkspaceRecoveryItemViewModel
{
    public NestedWorkspaceRecoveryItemViewModel(NestedWorkspaceRecoverySummary summary)
    {
        Summary = summary ?? throw new ArgumentNullException(nameof(summary));
    }

    internal NestedWorkspaceRecoverySummary Summary { get; }

    public string RecoveryId => Summary.RecoveryId;

    public string DisplayId => RecoveryId.Length <= 24
        ? RecoveryId
        : $"{RecoveryId[..12]}…{RecoveryId[^8..]}";
}

public sealed record NestedWorkspaceRecoveryDiscardConfirmation(
    string RecoveryId,
    string Revision,
    int SourceCount,
    long TotalSourceBytes,
    string Title,
    string Message);

public sealed class NestedWorkspaceRecoverySourceViewModel
{
    public NestedWorkspaceRecoverySourceViewModel(
        NestedWorkspaceRecoverySourceEvidence evidence,
        int index)
    {
        Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        DisplayName = $"Recovered source {checked((long)index + 1):N0}";
    }

    internal NestedWorkspaceRecoverySourceEvidence Evidence { get; }

    public string SourceId => Evidence.SourceId;

    public string DisplayName { get; }

    public string LengthText => $"{Evidence.Length:N0} bytes";

    public string HashText => $"SHA-256 {Evidence.ContentSha256}";
}

public sealed record NestedWorkspaceRecoveryReopenConfirmation(
    string RecoveryId,
    string Revision,
    int SourceCount,
    long TotalSourceBytes,
    string SourceId,
    long SourceLength,
    string ContentSha256,
    string Title,
    string Message);
