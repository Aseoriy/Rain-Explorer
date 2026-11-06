using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using RainArchives.App.Infrastructure;
using RainArchives.App.Services;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IAppDialogService _dialogs;
    private readonly Func<CancellationToken, Task<IArchiveClient>> _clientFactory;
    private readonly Func<IArchiveClient, IEntryPreviewService> _previewServiceFactory;
    private readonly object _activityStateGate = new();
    private readonly object _previewRequestGate = new();
    private readonly object _settingsWorkGate = new();
    private readonly List<ArchiveEntryDescriptor> _allEntries = [];
    private readonly List<EntryId> _contextSelectionIds = [];
    private IArchiveClient? _client;
    private IEntryPreviewService? _previewService;
    private ActivityCenterViewModel? _activityCenter;
    private PreviewRequest? _activePreviewRequest;
    private PreviewRequest? _pendingPreviewRequest;
    private Task _previewPump = Task.CompletedTask;
    private Task _settingsWork = Task.CompletedTask;
    private ArchiveDescriptor? _archive;
    private GetZipWriteCapabilitiesResponse? _zipWriteCapabilities;
    private GetTarWriteCapabilitiesResponse? _tarWriteCapabilities;
    private GetManagedWriteCapabilitiesResponse? _managedWriteCapabilities;
    private ArchiveSessionId? _zipWriteCapabilitiesSessionId;
    private string? _archivePath;
    private string? _archiveDisplayName;
    private string _statusText = "Open an archive to get started.";
    private bool _isBusy;
    private bool _isInspectorVisible;
    private bool _isInspectorClosing;
    private bool _isPreviewVisible;
    private int _inspectorSelectedTabIndex;
    private long _previewRequestSequence;
    private long _currentPreviewRequest;
    private bool _sessionHasPassword;
    private bool _canCancelCurrentOperationFromSnapshot;
    private bool _currentOperationCancellationPending;
    private ArchiveCommandContext _commandContext = ArchiveCommandContext.Unavailable;
    private CancellationTokenSource? _lifetime = new();

    public MainWindowViewModel(
        IAppDialogService dialogs,
        Func<CancellationToken, Task<IArchiveClient>>? clientFactory = null,
        Func<IArchiveClient, IEntryPreviewService>? previewServiceFactory = null)
    {
        _dialogs = dialogs;
        _clientFactory = clientFactory ?? CreatePackagedClientAsync;
        _previewServiceFactory = previewServiceFactory ?? TextEntryPreviewService.CreateDefault;
        Browser.PropertyChanged += OnBrowserPropertyChanged;
        Preview.PropertyChanged += OnPreviewPropertyChanged;
        OpenCommand = new AsyncRelayCommand(OpenFromDialogAsync, () => !IsBusy);
        CreateCommand = new AsyncRelayCommand(CreateFromDialogAsync, () => CanCreate);
        RecoveryCommand = new AsyncRelayCommand(ReviewRecoveriesAsync, () => CanReviewRecoveries);
        ActivityCenterCommand = new AsyncRelayCommand(
            ShowActivityCenterAsync,
            () => _lifetime is not null && (_client is not null || !IsBusy));
        SettingsCommand = new AsyncRelayCommand(
            ShowSettingsAsync,
            () => _lifetime is not null && !IsBusy);
        RebuildCopyCommand = new AsyncRelayCommand(
            RebuildCopyFromDialogAsync,
            () => CanRebuildCopy);
        NavigateSelectionCommand = new AsyncRelayCommand(
            NavigateSelectedEntryAsync,
            () => CommandContext.CanNavigate);
        PreviewSelectionCommand = new AsyncRelayCommand(
            PreviewSelectedEntryAsync,
            () => CommandContext.CanPreview && !IsInspectorClosing);
        ShowPropertiesCommand = new AsyncRelayCommand(
            ShowPropertiesAsync,
            () => CommandContext.CanShowProperties && !IsInspectorClosing);
        ExtractSelectionCommand = new AsyncRelayCommand(
            ExtractSelectedEntriesAsync,
            () => CommandContext.CanExtractSelected);
        TestCommand = new AsyncRelayCommand(TestArchiveAsync, () => CanTest);
        CancelCommand = new AsyncRelayCommand(
            CancelCurrentOperationAsync,
            () => CanCancelCurrentOperationFromSnapshot);
        CloseInspectorCommand = new AsyncRelayCommand(
            CloseInspectorAsync,
            () => IsInspectorVisible && !IsInspectorClosing);
        ClosePreviewCommand = CloseInspectorCommand;
    }

    public ArchiveBrowserViewModel Browser { get; } = new();
    public ArchivePropertiesViewModel Properties { get; } = new();
    public SafePreviewViewModel Preview { get; } = new();
    public ObservableCollection<ArchiveEntryViewModel> Entries => Browser.Entries;
    public ObservableCollection<BreadcrumbItemViewModel> Breadcrumbs => Browser.Breadcrumbs;
    public ActivityViewModel ActiveActivity { get; } = new();
    public ActivityCenterViewModel? ActivityCenter => _activityCenter;

    public event EventHandler? PreviewFocusRequested;
    public event EventHandler? PropertiesFocusRequested;

    public AsyncRelayCommand OpenCommand { get; }
    public AsyncRelayCommand CreateCommand { get; }
    public AsyncRelayCommand RecoveryCommand { get; }
    public AsyncRelayCommand ActivityCenterCommand { get; }
    public AsyncRelayCommand SettingsCommand { get; }
    public AsyncRelayCommand RebuildCopyCommand { get; }
    public AsyncRelayCommand NavigateSelectionCommand { get; }
    public AsyncRelayCommand PreviewSelectionCommand { get; }
    public AsyncRelayCommand ShowPropertiesCommand { get; }
    public AsyncRelayCommand ExtractSelectionCommand { get; }
    public AsyncRelayCommand BackCommand => Browser.BackCommand;
    public AsyncRelayCommand ForwardCommand => Browser.ForwardCommand;
    public AsyncRelayCommand UpCommand => Browser.UpCommand;
    public AsyncRelayCommand RootCommand => Browser.RootCommand;
    public AsyncRelayCommand FocusSearchCommand => Browser.FocusSearchCommand;
    public AsyncRelayCommand TestCommand { get; }
    public AsyncRelayCommand CancelCommand { get; }
    public AsyncRelayCommand CloseInspectorCommand { get; }
    public AsyncRelayCommand ClosePreviewCommand { get; }

    public ArchiveCommandContext CommandContext
    {
        get => _commandContext;
        private set => SetProperty(ref _commandContext, value);
    }

    public bool HasArchive => _archive is not null;
    public bool CanCreate =>
        !IsBusy
        && (_client is null || CanCreateWithKnownCapabilities());
    public bool CanReviewRecoveries =>
        !IsBusy
        && (_client is null
            || _client.SupportsFeature(ProtocolFeatures.ArchiveCreationRecoveryInspection)
            || _client.SupportsFeature(ProtocolFeatures.NestedWorkspaceRecovery)
            || _client.SupportsFeature(ProtocolFeatures.ExtractionRecoveryInspection)
            || _client.SupportsFeature(ProtocolFeatures.SupportDiagnostics));
    public bool CanCancelCurrentOperationFromSnapshot
    {
        get => _canCancelCurrentOperationFromSnapshot;
        private set
        {
            if (SetProperty(ref _canCancelCurrentOperationFromSnapshot, value))
            {
                CancelCommand.NotifyCanExecuteChanged();
            }
        }
    }
    public bool CanExtract => CommandContext.CanExtractArchive;
    public bool CanTest => CommandContext.CanTestIntegrity;
    public bool CanRebuildCopy =>
        !IsBusy
        && Browser.CanBrowse
        && _archive is not null
        && _archivePath is not null
        && string.Equals(_archive.Format, "zip", StringComparison.OrdinalIgnoreCase)
        && _client?.SupportsFeature(ProtocolFeatures.ZipRebuildCopy) == true
        && _client.SupportsFeature(ProtocolFeatures.ZipWriteCapabilities)
        && (_zipWriteCapabilitiesSessionId != _archive.SessionId
            || _zipWriteCapabilities?.CanRebuild == true)
        && _archive.Capabilities.HasFlag(ArchiveCapability.UpdateByRewrite);
    public string RebuildCopyHelpText
    {
        get
        {
            if (_archive is null)
            {
                return "Open an eligible ZIP archive to enable Rebuild copy.";
            }

            if (_archivePath is null)
            {
                return "Rebuild copy requires an archive opened from a filesystem source. Recovered independent sessions remain read-only for rebuild purposes.";
            }

            if (!string.Equals(_archive.Format, "zip", StringComparison.OrdinalIgnoreCase))
            {
                return "Rebuild copy is available only for an open ZIP archive.";
            }

            if (_client?.SupportsFeature(ProtocolFeatures.ZipRebuildCopy) != true)
            {
                return "Rebuild copy is unavailable because the connected worker did not negotiate the ZIP Rebuild Copy feature.";
            }

            if (_client.SupportsFeature(ProtocolFeatures.ZipWriteCapabilities) != true)
            {
                return "Rebuild copy is unavailable because the connected worker did not negotiate the current-minor ZIP write capabilities query.";
            }

            if (_zipWriteCapabilitiesSessionId == _archive.SessionId
                && _zipWriteCapabilities?.CanRebuild != true)
            {
                return "Rebuild copy is unavailable because the connected worker did not advertise this opened session as rebuild-eligible.";
            }

            if (!_archive.Capabilities.HasFlag(ArchiveCapability.UpdateByRewrite))
            {
                return "This open ZIP session is not eligible for a safe rebuild copy.";
            }

            return IsBusy
                ? "Rebuild copy is unavailable while another archive operation is active."
                : "Review changes and create a new ZIP copy. The original archive remains untouched.";
        }
    }
    public bool IsPreviewVisible
    {
        get => _isPreviewVisible;
        private set
        {
            if (SetProperty(ref _isPreviewVisible, value))
            {
                ClosePreviewCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsInspectorVisible
    {
        get => _isInspectorVisible;
        private set
        {
            if (SetProperty(ref _isInspectorVisible, value))
            {
                CloseInspectorCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsInspectorClosing
    {
        get => _isInspectorClosing;
        private set
        {
            if (!SetProperty(ref _isInspectorClosing, value))
            {
                return;
            }

            PreviewSelectionCommand.NotifyCanExecuteChanged();
            ShowPropertiesCommand.NotifyCanExecuteChanged();
            CloseInspectorCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsPreviewLoading => Preview.IsLoading;
    public string PreviewTitle => Preview.EntryTitle;
    public string PreviewDetails => Preview.Details;
    public string PreviewText => Preview.Text;

    public int InspectorSelectedTabIndex
    {
        get => _inspectorSelectedTabIndex;
        set
        {
            if (value is >= 0 and <= 1)
            {
                SetProperty(ref _inspectorSelectedTabIndex, value);
            }
        }
    }
    public string ArchiveTitle => _archiveDisplayName is null
        ? "Rain Archives"
        : UntrustedDisplayText.Escape(_archiveDisplayName);
    public string ArchiveDetails => _archive is null
        ? string.Empty
        : $"{ArchivePropertiesViewModel.FormatDisplayName(_archive.Format)} · {_archive.EntryCount:N0} entries · {Browser.CapabilitySummary}";

    public string SearchText
    {
        get => Browser.SearchText;
        set => Browser.SearchText = value;
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                Browser.SetHostBusy(value);
                RefreshCommandContextState(notifyCommands: false);
                OnPropertyChanged(nameof(CanExtract));
                OnPropertyChanged(nameof(CanTest));
                OnPropertyChanged(nameof(CanCreate));
                OnPropertyChanged(nameof(CanReviewRecoveries));
                OnPropertyChanged(nameof(CanRebuildCopy));
                OnPropertyChanged(nameof(RebuildCopyHelpText));
                RefreshCommandStates();
            }
        }
    }

    public EntryId? CurrentFolderId
    {
        get => Browser.CurrentFolderId;
    }

    internal void UpdateContextSelection(IReadOnlyList<ArchiveEntryViewModel> selectedEntries)
    {
        ArgumentNullException.ThrowIfNull(selectedEntries);
        if (selectedEntries.Count > ArchiveBrowserViewModel.VisiblePageSize)
        {
            throw new InvalidDataException(
                $"Archive command selection exceeds the {ArchiveBrowserViewModel.VisiblePageSize:N0}-row presentation bound.");
        }

        var requested = selectedEntries.Select(entry => entry.EntryId).ToHashSet();
        _contextSelectionIds.Clear();
        foreach (var visible in Browser.Entries)
        {
            if (requested.Remove(visible.EntryId))
            {
                _contextSelectionIds.Add(visible.EntryId);
            }
        }

        RefreshCommandContextState();
    }

    public async Task ExtractAsync(IReadOnlyList<ArchiveEntryViewModel> selectedEntries)
    {
        if (_archive is null || _archiveDisplayName is null || !CanExtract)
        {
            return;
        }

        var destination = _dialogs.ChooseNewExtractionDestination(
            _archiveDisplayName,
            selectedEntries.Count > 0);
        if (destination is null)
        {
            return;
        }

        await RunGuardedAsync(async cancellationToken =>
        {
            await CancelAndDrainPreviewAsync(clearPresentation: true).ConfigureAwait(true);
            var secret = await CreateOperationSecretIfNeededAsync(cancellationToken).ConfigureAwait(true);
            var started = await _client!.StartExtractionAsync(
                _archive.SessionId,
                selectedEntries.Select(entry => entry.EntryId).ToArray(),
                destination,
                secret,
                cancellationToken).ConfigureAwait(true);
            BeginActivity(started.OperationId, OperationKind.Extract, "Preparing extraction", "Extraction");
            var terminal = await _client.WaitForTerminalAsync(started.OperationId, cancellationToken).ConfigureAwait(true);
            if (secret is not null
                && terminal.Phase is OperationPhase.Completed or OperationPhase.CompletedWithWarnings)
            {
                _sessionHasPassword = true;
            }

            ApplyTerminal(
                terminal,
                "Extraction completed.",
                invalidateBrowserOnSourceChanged: true);
        }, invalidateBrowserOnSourceChanged: true).ConfigureAwait(true);
    }

    public async Task CreateArchiveAsync(ArchiveCreationSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        await RunGuardedAsync(async cancellationToken =>
        {
            await CancelAndDrainPreviewAsync(clearPresentation: true).ConfigureAwait(true);
            var client = await EnsureClientAsync(cancellationToken).ConfigureAwait(true);
            EnsureCreationFeatures(client, selection);
            StartOperationResponse started;
            string formatName;
            if (selection.Format == ArchiveCreationFormat.Zip)
            {
                var capabilities = await GetZipWriteCapabilitiesAsync(
                    client,
                    sessionId: null,
                    cancellationToken).ConfigureAwait(true);
                EnsureZipWriteCapabilitiesForCreation(capabilities, selection);
                started = selection.Directories.Count == 0
                    ? await client.StartZipCreationAsync(selection.DestinationPath, selection.Files, selection.CompressionPreset, cancellationToken).ConfigureAwait(true)
                    : await client.StartZipTreeCreationAsync(selection.DestinationPath, selection.Files, selection.Directories, selection.CompressionPreset, cancellationToken).ConfigureAwait(true);
                formatName = "ZIP";
            }
            else if (selection.Format == ArchiveCreationFormat.Tar)
            {
                var capabilities = await GetTarWriteCapabilitiesAsync(
                    client,
                    cancellationToken).ConfigureAwait(true);
                EnsureTarWriteCapability(capabilities, selection);
                started = selection.Directories.Count == 0
                    ? await client.StartTarCreationAsync(
                        selection.DestinationPath,
                        selection.Files,
                        cancellationToken).ConfigureAwait(true)
                    : await client.StartTarTreeCreationAsync(
                        selection.DestinationPath,
                        selection.Files,
                        selection.Directories,
                        cancellationToken).ConfigureAwait(true);
                formatName = "TAR";
            }
            else
            {
                var capabilities = await GetManagedWriteCapabilitiesAsync(client, cancellationToken).ConfigureAwait(true);
                EnsureManagedWriteCapability(capabilities, selection);
                (started, formatName) = selection.Format switch
                {
                    ArchiveCreationFormat.TarGZip => (selection.Directories.Count == 0
                        ? await client.StartTarGZipCreationAsync(selection.DestinationPath, selection.Files, selection.CompressionPreset, cancellationToken).ConfigureAwait(true)
                        : await client.StartTarGZipTreeCreationAsync(selection.DestinationPath, selection.Files, selection.Directories, selection.CompressionPreset, cancellationToken).ConfigureAwait(true), "TAR.GZ"),
                    ArchiveCreationFormat.TarBZip2 => (selection.Directories.Count == 0
                        ? await client.StartTarBZip2CreationAsync(selection.DestinationPath, selection.Files, selection.CompressionPreset, cancellationToken).ConfigureAwait(true)
                        : await client.StartTarBZip2TreeCreationAsync(selection.DestinationPath, selection.Files, selection.Directories, selection.CompressionPreset, cancellationToken).ConfigureAwait(true), "TAR.BZ2"),
                    ArchiveCreationFormat.GZip => (await client.StartGZipCreationAsync(selection.DestinationPath, selection.Files.Single().SourcePath, selection.CompressionPreset, cancellationToken).ConfigureAwait(true), "GZIP"),
                    ArchiveCreationFormat.BZip2 => (await client.StartBZip2CreationAsync(selection.DestinationPath, selection.Files.Single().SourcePath, selection.CompressionPreset, cancellationToken).ConfigureAwait(true), "BZIP2"),
                    _ => throw new InvalidOperationException("The selected archive creation format is not supported."),
                };
            }
            BeginActivity(
                started.OperationId,
                OperationKind.CreateArchive,
                $"Preparing {formatName} creation",
                $"{formatName} archive creation");
            var terminal = await client
                .WaitForTerminalAsync(started.OperationId, cancellationToken)
                .ConfigureAwait(true);
            ApplyTerminal(terminal, $"{formatName} archive created.");
            if (terminal.Phase is not (OperationPhase.Completed or OperationPhase.CompletedWithWarnings))
            {
                return;
            }

            await OpenCreatedArchiveAsync(
                selection.DestinationPath,
                terminal.Phase == OperationPhase.CompletedWithWarnings,
                $"{formatName} archive created and opened.",
                $"{formatName} archive created with warnings and opened.",
                cancellationToken).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    public async Task RebuildArchiveAsync(ArchiveRebuildCopySelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        await RunGuardedAsync(async cancellationToken =>
        {
            await CancelAndDrainPreviewAsync(clearPresentation: true).ConfigureAwait(true);
            if (_archive is null || _archivePath is null || !Browser.CanBrowse)
            {
                throw new ArchiveClientException(new ArchiveError(
                    "zip.rebuild_copy_archive_not_open",
                    ArchiveErrorCategory.InvalidRequest,
                    "Open or reopen an eligible ZIP archive before starting a rebuild copy."));
            }

            var client = await EnsureClientAsync(cancellationToken).ConfigureAwait(true);
            EnsureRebuildCopyFeatures(client, _archive);
            var capabilities = await GetZipWriteCapabilitiesAsync(
                client,
                _archive.SessionId,
                cancellationToken).ConfigureAwait(true);
            EnsureZipWriteCapabilitiesForRebuild(capabilities, selection);
            IReadOnlyList<ArchiveRebuildRename> renames = selection.Renames ?? [];
            IReadOnlyList<ArchiveCreationDirectory> directories = selection.Directories ?? [];
            IReadOnlyList<ArchiveRebuildTreeAction> treeActions = selection.TreeActions ?? [];
            IReadOnlyList<ArchiveRebuildMetadataDirective> metadata = selection.Metadata ?? [];
            StartOperationResponse started;
            var operationLabel = metadata.Count > 0
                ? "ZIP metadata edit"
                : directories.Count > 0 || treeActions.Count > 0
                ? "ZIP tree edit"
                : renames.Count == 0
                ? "ZIP rebuild copy"
                : "ZIP rebuild rename";
            if (metadata.Count > 0)
            {
                if (!client.SupportsFeature(ProtocolFeatures.ZipRebuildMetadata))
                {
                    throw new ArchiveClientException(new ArchiveError(
                        "zip.rebuild_metadata_feature_not_negotiated",
                        ArchiveErrorCategory.UnsupportedCapability,
                        "The connected archive worker does not support secure ZIP Rebuild Metadata."));
                }

                started = await client.StartZipRebuildMetadataAsync(
                    _archive.SessionId,
                    selection.DestinationPath,
                    selection.EntriesToDelete,
                    selection.Files,
                    renames,
                    directories,
                    treeActions,
                    metadata,
                    selection.CompressionPreset,
                    cancellationToken).ConfigureAwait(true);
            }
            else if (directories.Count > 0 || treeActions.Count > 0)
            {
                if (!client.SupportsFeature(ProtocolFeatures.ZipRebuildTreeEdit))
                {
                    throw new ArchiveClientException(new ArchiveError(
                        "zip.rebuild_tree_edit_feature_not_negotiated",
                        ArchiveErrorCategory.UnsupportedCapability,
                        "The connected archive worker does not support secure ZIP Rebuild Tree Edit."));
                }

                started = await client.StartZipRebuildTreeEditAsync(
                    _archive.SessionId,
                    selection.DestinationPath,
                    selection.EntriesToDelete,
                    selection.Files,
                    renames,
                    directories,
                    treeActions,
                    selection.CompressionPreset,
                    cancellationToken).ConfigureAwait(true);
            }
            else if (renames.Count == 0)
            {
                started = await client.StartZipRebuildCopyAsync(
                    _archive.SessionId,
                    selection.DestinationPath,
                    selection.EntriesToDelete,
                    selection.Files,
                    selection.CompressionPreset,
                    cancellationToken).ConfigureAwait(true);
            }
            else
            {
                if (!client.SupportsFeature(ProtocolFeatures.ZipRebuildRename))
                {
                    throw new ArchiveClientException(new ArchiveError(
                        "zip.rebuild_rename_feature_not_negotiated",
                        ArchiveErrorCategory.UnsupportedCapability,
                        "The connected archive worker does not support secure ZIP Rebuild Rename."));
                }

                started = await client.StartZipRebuildRenameAsync(
                    _archive.SessionId,
                    selection.DestinationPath,
                    selection.EntriesToDelete,
                    selection.Files,
                    renames,
                    selection.CompressionPreset,
                    cancellationToken).ConfigureAwait(true);
            }

            BeginActivity(
                started.OperationId,
                OperationKind.RebuildArchive,
                $"Preparing {operationLabel}",
                operationLabel);
            var terminal = await client
                .WaitForTerminalAsync(started.OperationId, cancellationToken)
                .ConfigureAwait(true);
            ApplyTerminal(
                terminal,
                $"{operationLabel} completed.",
                invalidateBrowserOnSourceChanged: true);
            if (terminal.Phase is not (OperationPhase.Completed or OperationPhase.CompletedWithWarnings))
            {
                return;
            }

            await OpenCreatedArchiveAsync(
                selection.DestinationPath,
                terminal.Phase == OperationPhase.CompletedWithWarnings,
                $"{operationLabel} completed and opened.",
                $"{operationLabel} completed and opened. Warnings were reported.",
                cancellationToken).ConfigureAwait(true);
        }, invalidateBrowserOnSourceChanged: true).ConfigureAwait(true);
    }

    public Task ActivateEntryAsync(ArchiveEntryViewModel? entry)
    {
        if (entry is null || IsBusy || IsInspectorClosing || !Browser.CanBrowse)
        {
            return Task.CompletedTask;
        }

        if (entry.IsDirectory)
        {
            CancelPreviewRequests(clearPresentation: true);
            NavigateTo(entry.EntryId);
            return Task.CompletedTask;
        }

        if (_archive is null || _client is null)
        {
            return Task.CompletedTask;
        }

        if (!_archive.Capabilities.HasFlag(ArchiveCapability.OpenEntryStream))
        {
            Preview.ShowUnsupported(_archive.SessionId, entry.Descriptor);
            RevealPreviewInspector();
            StatusText = "Safe preview is unavailable for this archive session.";
            PreviewFocusRequested?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        return StartPreviewRequest(entry.Descriptor, _archive.SessionId, _client);
    }

    private Task NavigateSelectedEntryAsync()
    {
        var entry = GetCanonicalContextPrimary();
        if (entry?.IsDirectory == true && CommandContext.CanNavigate)
        {
            NavigateTo(entry.EntryId);
        }

        return Task.CompletedTask;
    }

    private Task PreviewSelectedEntryAsync()
    {
        var entry = GetCanonicalContextPrimary();
        if (entry is not null && CommandContext.CanPreview)
        {
            // Queue ownership stays with the bounded preview pump. Returning immediately keeps
            // a newer keyboard or context-menu selection able to cancel and replace this request.
            _ = ActivateEntryAsync(entry);
        }

        return Task.CompletedTask;
    }

    private async Task ShowPropertiesAsync()
    {
        if (!CommandContext.CanShowProperties || IsInspectorClosing)
        {
            return;
        }

        IsInspectorClosing = true;
        try
        {
            await CancelAndDrainPreviewAsync(clearPresentation: false).ConfigureAwait(true);
            if (!CommandContext.CanShowProperties)
            {
                HideInspectorPresentation();
                return;
            }

            ResetPreviewPresentation();
            InspectorSelectedTabIndex = 0;
            IsInspectorVisible = true;
            PropertiesFocusRequested?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsInspectorClosing = false;
        }
    }

    private Task ExtractSelectedEntriesAsync()
    {
        var selected = GetCanonicalContextSelection();
        return !CommandContext.CanExtractSelected || selected.Count == 0
            ? Task.CompletedTask
            : ExtractAsync(selected);
    }

    private ArchiveEntryViewModel? GetCanonicalContextPrimary() =>
        CommandContext.PrimaryEntryId is { } primaryId
            ? Browser.Entries.FirstOrDefault(entry => entry.EntryId == primaryId)
            : null;

    private IReadOnlyList<ArchiveEntryViewModel> GetCanonicalContextSelection()
    {
        var requested = _contextSelectionIds.ToHashSet();
        if (CommandContext.PrimaryEntryId is { } primaryId)
        {
            _ = requested.Add(primaryId);
        }

        return Browser.Entries
            .Where(entry => requested.Contains(entry.EntryId))
            .ToArray();
    }

    public void NavigateTo(EntryId? entryId)
    {
        CancelPreviewRequests(clearPresentation: true);
        _ = Browser.NavigateTo(entryId);
    }

    public Task ClosePreviewAsync() => CloseInspectorAsync();

    public async Task CloseInspectorAsync()
    {
        if (IsInspectorClosing
            || (!IsInspectorVisible && !IsPreviewLoading && !Preview.HasPresentation))
        {
            return;
        }

        IsInspectorClosing = true;
        try
        {
            await CancelAndDrainPreviewAsync(clearPresentation: false).ConfigureAwait(true);
        }
        finally
        {
            HideInspectorPresentation();
            IsInspectorClosing = false;
            Browser.RestoreEntryListFocus();
        }
    }

    public Task OpenArchivePathAsync(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        return RunGuardedAsync(cancellationToken => OpenArchiveAsync(sourcePath, cancellationToken));
    }

    public Task ReviewRecoveriesAsync() => RunGuardedAsync(async cancellationToken =>
    {
        var client = await EnsureClientAsync(cancellationToken).ConfigureAwait(true);
        if (!client.SupportsFeature(ProtocolFeatures.ArchiveCreationRecoveryInspection)
            && !client.SupportsFeature(ProtocolFeatures.NestedWorkspaceRecovery)
            && !client.SupportsFeature(ProtocolFeatures.ExtractionRecoveryInspection)
            && !client.SupportsFeature(ProtocolFeatures.SupportDiagnostics))
        {
            throw new ArchiveClientException(new ArchiveError(
                "recovery.center_feature_not_negotiated",
                ArchiveErrorCategory.UnsupportedCapability,
                "The connected archive worker does not support Recovery Center inspection or support evidence."));
        }

        var reopened = await _dialogs
            .ShowArchiveRecoveryWithResultAsync(client, cancellationToken)
            .ConfigureAwait(true);
        if (reopened is null)
        {
            StatusText = "Recovery Center closed.";
            return;
        }

        await AdoptRecoveredArchiveAsync(
                client,
                reopened.Response.Archive,
                reopened.OpenedWithPassword,
                cancellationToken)
            .ConfigureAwait(true);
    });

    public async ValueTask DisposeAsync()
    {
        var lifetime = Interlocked.Exchange(ref _lifetime, null);
        if (lifetime is null)
        {
            return;
        }

        Browser.PropertyChanged -= OnBrowserPropertyChanged;
        Preview.PropertyChanged -= OnPreviewPropertyChanged;
        Browser.CloseSession();
        await lifetime.CancelAsync().ConfigureAwait(false);
        Task settingsWork;
        lock (_settingsWorkGate)
        {
            settingsWork = _settingsWork;
        }

        try
        {
            await settingsWork.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The settings dialog settles cancellation before client disposal.
        }

        var activityCenter = _activityCenter;
        _activityCenter = null;
        if (activityCenter is not null)
        {
            activityCenter.ActivitySnapshotApplied -= OnActivitySnapshotApplied;
            await activityCenter.DisposeAsync().ConfigureAwait(false);
        }

        Exception? previewCleanupFailure = null;
        try
        {
            await CancelAndDrainPreviewAsync(clearPresentation: false).ConfigureAwait(false);
            if (_previewService is not null)
            {
                await _previewService.DisposeAsync().ConfigureAwait(false);
                _previewService = null;
            }
        }
        catch (Exception exception)
        {
            previewCleanupFailure = exception;
        }

        if (_client is not null)
        {
            var client = _client;
            client.OperationEventReceived -= OnOperationEventReceived;
            using var gracefulShutdown = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                if (ActiveActivity.CanCancel && ActiveActivity.OperationId is { } operationId)
                {
                    await client.CancelOperationAsync(operationId, gracefulShutdown.Token).ConfigureAwait(false);
                }

                if (_archive is not null)
                {
                    await client.CloseArchiveAsync(
                        _archive.SessionId,
                        gracefulShutdown.Token).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is ArchiveClientException or OperationCanceledException)
            {
                // The client disposal below owns bounded worker termination.
            }
            finally
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }

        lifetime.Dispose();
        if (previewCleanupFailure is not null)
        {
            throw previewCleanupFailure;
        }
    }

    private async Task OpenFromDialogAsync()
    {
        var path = _dialogs.ChooseArchive();
        if (path is null)
        {
            return;
        }

        await OpenArchivePathAsync(path).ConfigureAwait(true);
    }

    private async Task CreateFromDialogAsync()
    {
        IArchiveClient? client = null;
        GetZipWriteCapabilitiesResponse? capabilities = null;
        GetTarWriteCapabilitiesResponse? tarCapabilities = null;
        GetManagedWriteCapabilitiesResponse? managedCapabilities = null;
        await RunGuardedAsync(async cancellationToken =>
        {
            var connected = await EnsureClientAsync(cancellationToken).ConfigureAwait(true);
            if (connected.SupportsFeature(ProtocolFeatures.ZipWriteCapabilities))
            {
                capabilities = await GetZipWriteCapabilitiesAsync(
                    connected,
                    sessionId: null,
                    cancellationToken).ConfigureAwait(true);
            }
            if (connected.SupportsFeature(ProtocolFeatures.ManagedWriteCapabilities))
            {
                managedCapabilities = await GetManagedWriteCapabilitiesAsync(
                    connected,
                    cancellationToken).ConfigureAwait(true);
            }
            if (connected.SupportsFeature(ProtocolFeatures.TarWriteCapabilities))
            {
                tarCapabilities = await GetTarWriteCapabilitiesAsync(
                    connected,
                    cancellationToken).ConfigureAwait(true);
            }
            _zipWriteCapabilities = capabilities;
            _tarWriteCapabilities = tarCapabilities;
            _managedWriteCapabilities = managedCapabilities;
            _zipWriteCapabilitiesSessionId = null;
            OnPropertyChanged(nameof(CanCreate));
            CreateCommand.NotifyCanExecuteChanged();
            if ((capabilities?.CanCreate != true)
                && (tarCapabilities?.CanCreate != true)
                && (managedCapabilities?.Capabilities.Any(capability => capability.CanCreate) != true))
            {
                throw UnsupportedZipWriteCapability(
                    "The connected worker does not advertise a supported archive creation format for this protocol minor.");
            }

            client = connected;
        }).ConfigureAwait(true);
        if (client is null
            || (capabilities is null
                && tarCapabilities is null
                && managedCapabilities is null))
        {
            return;
        }

        var selection = _dialogs.ChooseArchiveCreation(
            client.SupportsFeature(ProtocolFeatures.ZipDirectoryCreation),
            capabilities,
            tarCapabilities,
            managedCapabilities);
        if (selection is null)
        {
            return;
        }

        await CreateArchiveAsync(selection).ConfigureAwait(true);
    }

    private async Task RebuildCopyFromDialogAsync()
    {
        if (!CanRebuildCopy || _archive is null || _archivePath is null)
        {
            return;
        }

        IArchiveClient? client = null;
        GetZipWriteCapabilitiesResponse? capabilities = null;
        IReadOnlyList<ZipRebuildTreePlanNode>? treePlan = null;
        await RunGuardedAsync(async cancellationToken =>
        {
            client = await EnsureClientAsync(cancellationToken).ConfigureAwait(true);
            EnsureRebuildCopyFeatures(client, _archive);
            capabilities = await GetZipWriteCapabilitiesAsync(
                client,
                _archive.SessionId,
                cancellationToken).ConfigureAwait(true);
            _zipWriteCapabilities = capabilities;
            _zipWriteCapabilitiesSessionId = _archive.SessionId;
            OnPropertyChanged(nameof(CanCreate));
            OnPropertyChanged(nameof(CanRebuildCopy));
            CreateCommand.NotifyCanExecuteChanged();
            RebuildCopyCommand.NotifyCanExecuteChanged();
            if (!capabilities.CanRebuild)
            {
                throw UnsupportedZipWriteCapability(
                    "The connected worker does not advertise ZIP rebuild for this opened session and protocol minor.");
            }

            treePlan = client.SupportsFeature(ProtocolFeatures.ZipRebuildTreeEdit)
                && capabilities.SupportsTreeEdit
                ? (await client.GetZipRebuildTreePlanAsync(
                    _archive.SessionId,
                    cancellationToken).ConfigureAwait(true)).Directories
                : Array.Empty<ZipRebuildTreePlanNode>();
        }, invalidateBrowserOnSourceChanged: true).ConfigureAwait(true);
        if (client is null || capabilities is null || treePlan is null)
        {
            return;
        }

        var selection = _dialogs.ChooseArchiveRebuildCopy(
            client,
            _archive.SessionId,
            _archivePath,
            _allEntries,
            treePlan,
            capabilities);
        if (selection is null)
        {
            return;
        }

        await RebuildArchiveAsync(selection).ConfigureAwait(true);
    }

    private static void EnsureCreationFeatures(
        IArchiveClient client,
        ArchiveCreationSelection selection)
    {
        if (selection.Format == ArchiveCreationFormat.Tar)
        {
            if (!client.SupportsFeature(ProtocolFeatures.TarCreation)
                || (selection.Directories.Count > 0
                    && !client.SupportsFeature(ProtocolFeatures.TarDirectoryCreation)))
            {
                throw new ArchiveClientException(new ArchiveError(
                    "tar.creation_feature_not_negotiated",
                    ArchiveErrorCategory.UnsupportedCapability,
                    "The connected archive worker does not support the selected TAR creation route."));
            }

            return;
        }

        if (selection.Format != ArchiveCreationFormat.Zip)
        {
            var required = selection.Format switch
            {
                ArchiveCreationFormat.TarGZip => ProtocolFeatures.TarGZipCreation,
                ArchiveCreationFormat.TarBZip2 => ProtocolFeatures.TarBZip2Creation,
                ArchiveCreationFormat.GZip => ProtocolFeatures.GZipCreation,
                ArchiveCreationFormat.BZip2 => ProtocolFeatures.BZip2Creation,
                _ => throw new InvalidOperationException("The selected archive creation format is not supported."),
            };
            if (!client.SupportsFeature(required)
                || (selection.Directories.Count > 0 && selection.Format is (ArchiveCreationFormat.TarGZip or ArchiveCreationFormat.TarBZip2)
                    && !client.SupportsFeature(selection.Format == ArchiveCreationFormat.TarGZip
                        ? ProtocolFeatures.TarGZipDirectoryCreation
                        : ProtocolFeatures.TarBZip2DirectoryCreation)))
            {
                throw new ArchiveClientException(new ArchiveError(
                    "managed.creation_feature_not_negotiated",
                    ArchiveErrorCategory.UnsupportedCapability,
                    "The connected archive worker does not support the selected managed creation format."));
            }

            return;
        }

        EnsureZipCreationFeature(client);

        if (selection.Directories.Count > 0
            && !client.SupportsFeature(ProtocolFeatures.ZipDirectoryCreation))
        {
            throw new ArchiveClientException(new ArchiveError(
                "zip.directory_creation_feature_not_negotiated",
                ArchiveErrorCategory.UnsupportedCapability,
                "The connected archive worker does not support secure ZIP directory creation."));
        }
    }

    private static void EnsureZipWriteCapabilitiesForCreation(
        GetZipWriteCapabilitiesResponse capabilities,
        ArchiveCreationSelection selection)
    {
        if (!capabilities.CanCreate)
        {
            throw UnsupportedZipWriteCapability(
                "The connected worker does not advertise ZIP creation for this protocol minor.");
        }

        EnsureSelectedPreset(capabilities, selection.CompressionPreset);
        if ((selection.Files.Count > 0 || selection.Directories.Count > 0)
            && !capabilities.SupportsAdd)
        {
            throw UnsupportedZipWriteCapability(
                "The connected worker does not advertise ZIP add support for this creation.");
        }
    }

    private void EnsureZipWriteCapabilitiesForRebuild(
        GetZipWriteCapabilitiesResponse capabilities,
        ArchiveRebuildCopySelection selection)
    {
        if (!capabilities.CanRebuild)
        {
            throw UnsupportedZipWriteCapability(
                "The connected worker does not advertise ZIP rebuild for this opened session and protocol minor.");
        }

        EnsureSelectedPreset(capabilities, selection.CompressionPreset);
        var requiresAdd = selection.Files.Any(file =>
            !_allEntries.Any(entry =>
                entry.NormalizedPath is { } existingPath
                && string.Equals(
                    ArchivePathRules.GetCollisionKey(existingPath),
                    ArchivePathRules.GetCollisionKey(file.ArchivePath),
                    StringComparison.Ordinal)));
        var requiresReplace = selection.Files.Any(file =>
            _allEntries.Any(entry =>
                entry.NormalizedPath is { } existingPath
                && string.Equals(
                    ArchivePathRules.GetCollisionKey(existingPath),
                    ArchivePathRules.GetCollisionKey(file.ArchivePath),
                    StringComparison.Ordinal)));
        if (requiresAdd && !capabilities.SupportsAdd)
        {
            throw UnsupportedZipWriteCapability(
                "The connected worker does not advertise ZIP add support for this rebuild.");
        }

        if (requiresReplace && !capabilities.SupportsReplace)
        {
            throw UnsupportedZipWriteCapability(
                "The connected worker does not advertise ZIP replace support for this rebuild.");
        }

        if (selection.EntriesToDelete.Count > 0 && !capabilities.SupportsDelete)
        {
            throw UnsupportedZipWriteCapability(
                "The connected worker does not advertise ZIP delete support for this rebuild.");
        }

        if ((selection.Renames?.Count ?? 0) > 0 && !capabilities.SupportsFileRename)
        {
            throw UnsupportedZipWriteCapability(
                "The connected worker does not advertise ZIP file-rename support for this rebuild.");
        }

        if ((selection.Directories?.Count ?? 0) > 0 && !capabilities.SupportsAdd)
        {
            throw UnsupportedZipWriteCapability(
                "The connected worker does not advertise ZIP add support for this rebuild folder input.");
        }

        if ((selection.TreeActions?.Count ?? 0) > 0 && !capabilities.SupportsTreeEdit)
        {
            throw UnsupportedZipWriteCapability(
                "The connected worker does not advertise ZIP tree-edit support for this rebuild.");
        }

        if ((selection.Metadata?.Count ?? 0) > 0 && !capabilities.SupportsMetadata)
        {
            throw UnsupportedZipWriteCapability(
                "The connected worker does not advertise ZIP metadata support for this rebuild.");
        }
    }

    private static void EnsureZipCreationFeature(IArchiveClient client)
    {
        if (!client.SupportsFeature(ProtocolFeatures.ZipCreation))
        {
            throw new ArchiveClientException(new ArchiveError(
                "zip.creation_feature_not_negotiated",
                ArchiveErrorCategory.UnsupportedCapability,
                "The connected archive worker does not support secure ZIP creation."));
        }
    }

    private static async Task<GetZipWriteCapabilitiesResponse> GetZipWriteCapabilitiesAsync(
        IArchiveClient client,
        ArchiveSessionId? sessionId,
        CancellationToken cancellationToken)
    {
        if (!client.SupportsFeature(ProtocolFeatures.ZipWriteCapabilities))
        {
            throw UnsupportedZipWriteCapability(
                "The connected worker did not negotiate the ZIP write capabilities query for this protocol minor.");
        }

        try
        {
            return await client.GetZipWriteCapabilitiesAsync(sessionId, cancellationToken)
                .ConfigureAwait(true);
        }
        catch (NotSupportedException)
        {
            throw UnsupportedZipWriteCapability(
                "The connected worker cannot answer the ZIP write capabilities query for this protocol minor.");
        }
    }

    private async Task<GetManagedWriteCapabilitiesResponse> GetManagedWriteCapabilitiesAsync(
        IArchiveClient client,
        CancellationToken cancellationToken)
    {
        if (!client.SupportsFeature(ProtocolFeatures.ManagedWriteCapabilities))
        {
            throw UnsupportedZipWriteCapability(
                "The connected worker did not negotiate managed archive creation capabilities.");
        }

        try
        {
            var capabilities = await client
                .GetManagedWriteCapabilitiesAsync(cancellationToken)
                .ConfigureAwait(true);
            _managedWriteCapabilities = capabilities;
            return capabilities;
        }
        catch (NotSupportedException)
        {
            throw UnsupportedZipWriteCapability(
                "The connected worker cannot answer the managed archive creation capabilities query.");
        }
    }

    private async Task<GetTarWriteCapabilitiesResponse> GetTarWriteCapabilitiesAsync(
        IArchiveClient client,
        CancellationToken cancellationToken)
    {
        if (!client.SupportsFeature(ProtocolFeatures.TarWriteCapabilities))
        {
            throw UnsupportedZipWriteCapability(
                "The connected worker did not negotiate TAR creation capabilities.");
        }

        try
        {
            var capabilities = await client
                .GetTarWriteCapabilitiesAsync(cancellationToken)
                .ConfigureAwait(true);
            _tarWriteCapabilities = capabilities;
            return capabilities;
        }
        catch (NotSupportedException)
        {
            throw UnsupportedZipWriteCapability(
                "The connected worker cannot answer the TAR creation capabilities query.");
        }
    }

    private bool CanCreateWithKnownCapabilities()
    {
        var zipAvailable = _client!.SupportsFeature(ProtocolFeatures.ZipCreation)
            && _client.SupportsFeature(ProtocolFeatures.ZipWriteCapabilities)
            && (_zipWriteCapabilities is null || _zipWriteCapabilities.CanCreate);
        var tarAvailable = _client.SupportsFeature(ProtocolFeatures.TarWriteCapabilities)
            && (_tarWriteCapabilities?.CanCreate == true);
        var managedAvailable = _client.SupportsFeature(ProtocolFeatures.ManagedWriteCapabilities)
            && (_managedWriteCapabilities?.Capabilities.Any(capability => capability.CanCreate) == true);
        return zipAvailable || tarAvailable || managedAvailable;
    }

    private static ArchiveClientException UnsupportedZipWriteCapability(
        string message) =>
        new(
            new ArchiveError(
                "zip.write_capabilities_unavailable",
                ArchiveErrorCategory.UnsupportedCapability,
                message));

    private static void EnsureSelectedPreset(
        GetZipWriteCapabilitiesResponse capabilities,
        ArchiveCompressionPreset selectedPreset)
    {
        if (!capabilities.CompressionPresets.Contains(selectedPreset))
        {
            throw UnsupportedZipWriteCapability(
                $"The connected worker does not advertise the selected compression preset '{selectedPreset}'.");
        }
    }

    private static void EnsureManagedWriteCapability(
        GetManagedWriteCapabilitiesResponse capabilities,
        ArchiveCreationSelection selection)
    {
        var managedFormat = selection.Format switch
        {
            ArchiveCreationFormat.TarGZip => ManagedArchiveCreationFormat.TarGZip,
            ArchiveCreationFormat.TarBZip2 => ManagedArchiveCreationFormat.TarBZip2,
            ArchiveCreationFormat.GZip => ManagedArchiveCreationFormat.GZip,
            ArchiveCreationFormat.BZip2 => ManagedArchiveCreationFormat.BZip2,
            _ => throw new InvalidOperationException("ZIP is not a managed compressed creation format."),
        };
        var capability = capabilities.Capabilities.SingleOrDefault(item => item.Format == managedFormat);
        if (capability is null || !capability.CanCreate)
        {
            throw UnsupportedZipWriteCapability(
                "The connected worker does not advertise the selected managed creation format.");
        }

        if (!capability.CompressionPresets.Contains(selection.CompressionPreset))
        {
            throw UnsupportedZipWriteCapability(
                $"The connected worker does not advertise the selected compression preset '{selection.CompressionPreset}'.");
        }

        if (selection.Directories.Count > 0 && !capability.SupportsTreeCreation)
        {
            throw UnsupportedZipWriteCapability(
                "The connected worker does not advertise directory creation for the selected format.");
        }

        if (selection.Format is ArchiveCreationFormat.GZip or ArchiveCreationFormat.BZip2
            && (selection.Files.Count != 1 || selection.Directories.Count != 0))
        {
            throw UnsupportedZipWriteCapability(
                "Raw managed creation requires exactly one ordinary file and no directories.");
        }
    }

    private static void EnsureTarWriteCapability(
        GetTarWriteCapabilitiesResponse capabilities,
        ArchiveCreationSelection selection)
    {
        if (!capabilities.CanCreate
            || !capabilities.CompressionPresets.Contains(ArchiveCompressionPreset.Store))
        {
            throw UnsupportedZipWriteCapability(
                "The connected worker does not advertise managed TAR creation.");
        }

        if (selection.CompressionPreset != ArchiveCompressionPreset.Store)
        {
            throw UnsupportedZipWriteCapability(
                "TAR creation requires the advertised Store preset.");
        }

        if (selection.Directories.Count > 0 && !capabilities.SupportsTreeCreation)
        {
            throw UnsupportedZipWriteCapability(
                "The connected worker does not advertise directory creation for TAR.");
        }
    }

    private static void EnsureRebuildCopyFeatures(
        IArchiveClient client,
        ArchiveDescriptor archive)
    {
        if (!client.SupportsFeature(ProtocolFeatures.ZipRebuildCopy))
        {
            throw new ArchiveClientException(new ArchiveError(
                "zip.rebuild_copy_feature_not_negotiated",
                ArchiveErrorCategory.UnsupportedCapability,
                "The connected archive worker does not support secure ZIP Rebuild Copy."));
        }

        if (!string.Equals(archive.Format, "zip", StringComparison.OrdinalIgnoreCase)
            || !archive.Capabilities.HasFlag(ArchiveCapability.UpdateByRewrite))
        {
            throw new ArchiveClientException(new ArchiveError(
                "zip.rebuild_copy_session_not_eligible",
                ArchiveErrorCategory.UnsupportedCapability,
                "This open archive session is not eligible for a safe ZIP rebuild copy."));
        }
    }

    private async Task OpenCreatedArchiveAsync(
        string destinationPath,
        bool completedWithWarnings,
        string successMessage,
        string warningSuccessMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            await OpenArchiveAsync(destinationPath, cancellationToken).ConfigureAwait(true);
            StatusText = completedWithWarnings
                ? warningSuccessMessage
                : successMessage;
        }
        catch (Exception exception) when (
            exception is ArchiveClientException or IOException or InvalidDataException)
        {
            StatusText = "Archive was created, but it could not be opened.";
            _dialogs.ShowInformation(
                "Archive created",
                StatusText);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Archive was created, but opening it was cancelled.";
        }
    }

    private async Task OpenArchiveAsync(string sourcePath, CancellationToken cancellationToken)
    {
        await CancelAndDrainPreviewAsync(clearPresentation: true).ConfigureAwait(true);
        var fullSourcePath = Path.GetFullPath(sourcePath);
        var archiveTitle = UntrustedDisplayText.Escape(Path.GetFileName(fullSourcePath));
        IArchiveClient? client = null;
        OpenArchiveResponse? opened = null;
        var openedWithPassword = false;
        var adopted = false;
        var priorSessionCloseStarted = false;
        var stateTransitionStarted = false;
        var browserLoadingStarted = false;
        try
        {
            if (_archive is null)
            {
                Browser.BeginLoading(archiveTitle);
                browserLoadingStarted = true;
            }

            client = await EnsureClientAsync(cancellationToken).ConfigureAwait(true);
            try
            {
                opened = await client.OpenArchiveAsync(
                    fullSourcePath,
                    cancellationToken: cancellationToken).ConfigureAwait(true);
            }
            catch (ArchiveClientException exception)
                when (exception.Error.Category == ArchiveErrorCategory.PasswordRequired)
            {
                var secret = await RequestSecretAsync(fullSourcePath, cancellationToken).ConfigureAwait(true);
                if (secret is null)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                opened = await client.OpenArchiveAsync(
                    fullSourcePath,
                    secret,
                    cancellationToken).ConfigureAwait(true);
                openedWithPassword = true;
            }

            var newEntries = new List<ArchiveEntryDescriptor>();
            ArchiveBrowserPreparedSession? prepared = null;
            var canList = opened.Archive.Capabilities.HasFlag(ArchiveCapability.List);
            var persistentKeysEnabled = client.SupportsFeature(ProtocolFeatures.PersistentEntryKeys);
            if (canList)
            {
                if (opened.Archive.EntryCount is < 0 or > ArchiveBrowserViewModel.MaximumIndexedEntries)
                {
                    throw new InvalidDataException(
                        "The archive entry count exceeds the bounded browser index.");
                }

                var seenEntryIds = new HashSet<EntryId>();
                var offset = 0;
                do
                {
                    var page = await client.ListEntriesAsync(
                        opened.Archive.SessionId,
                        offset,
                        ProtocolConstants.MaximumPageSize,
                        cancellationToken).ConfigureAwait(true);
                    if (page.TotalCount != opened.Archive.EntryCount
                        || (long)newEntries.Count + page.Entries.Count > opened.Archive.EntryCount)
                    {
                        throw new InvalidDataException(
                            "The worker changed the declared archive entry count while listing.");
                    }

                    foreach (var entry in page.Entries)
                    {
                        if (!seenEntryIds.Add(entry.EntryId))
                        {
                            throw new InvalidDataException(
                                "The worker returned a duplicate archive entry identifier.");
                        }
                    }

                    newEntries.AddRange(page.Entries);
                    if (browserLoadingStarted)
                    {
                        Browser.ReportLoadingProgress(newEntries.Count, opened.Archive.EntryCount);
                    }

                    if (page.NextOffset is not { } next)
                    {
                        break;
                    }

                    if (next <= offset || next != newEntries.Count)
                    {
                        throw new InvalidDataException(
                            "The worker returned a non-advancing archive page.");
                    }

                    offset = next;
                }
                while (true);

                if (newEntries.Count != opened.Archive.EntryCount)
                {
                    throw new InvalidDataException(
                        "The worker ended listing before the declared archive entry count.");
                }

                prepared = ArchiveBrowserViewModel.PrepareSession(
                    opened.Archive,
                    newEntries,
                    persistentKeysEnabled);
            }

            var sameAuthoritativeSource = _archivePath is not null
                && string.Equals(_archivePath, fullSourcePath, StringComparison.OrdinalIgnoreCase);
            if (_archive is not null)
            {
                priorSessionCloseStarted = true;
                var closed = await client.CloseArchiveAsync(
                    _archive.SessionId,
                    cancellationToken).ConfigureAwait(true);
                if (!closed.Closed)
                {
                    throw new InvalidDataException(
                        "The prior archive session did not close authoritatively.");
                }

                Browser.BeginLoading(archiveTitle);
                browserLoadingStarted = true;
            }

            stateTransitionStarted = true;
            _archive = opened.Archive;
            _archivePath = fullSourcePath;
            _archiveDisplayName = Path.GetFileName(fullSourcePath);
            _sessionHasPassword = openedWithPassword;
            _allEntries.Clear();
            _allEntries.AddRange(newEntries);
            if (prepared is null)
            {
                Browser.OpenUnsupportedSession(opened.Archive, archiveTitle);
            }
            else
            {
                Browser.ApplyPreparedSession(
                    prepared,
                    archiveTitle,
                    sameAuthoritativeSource);
            }

            OnPropertyChanged(nameof(HasArchive));
            OnPropertyChanged(nameof(CanExtract));
            OnPropertyChanged(nameof(CanTest));
            OnPropertyChanged(nameof(CanRebuildCopy));
            OnPropertyChanged(nameof(RebuildCopyHelpText));
            OnPropertyChanged(nameof(ArchiveTitle));
            OnPropertyChanged(nameof(ArchiveDetails));
            OnPropertyChanged(nameof(SearchText));
            OnPropertyChanged(nameof(CurrentFolderId));
            StatusText = _archive.Warnings.Count == 0
                ? $"Opened {_archive.EntryCount:N0} entries."
                : $"Opened {_archive.EntryCount:N0} entries with {_archive.Warnings.Count:N0} warning{(_archive.Warnings.Count == 1 ? string.Empty : "s")}.";
            RefreshCommandStates();
            adopted = true;
        }
        catch (Exception exception)
        {
            if (!adopted && opened is not null && client is not null)
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    _ = await client.CloseArchiveAsync(
                        opened.Archive.SessionId,
                        closeTimeout.Token).ConfigureAwait(true);
                }
                catch (Exception cleanupException) when (
                    cleanupException is ArchiveClientException
                        or OperationCanceledException
                        or ObjectDisposedException)
                {
                }
            }

            if (priorSessionCloseStarted || stateTransitionStarted)
            {
                FailOpenedArchiveTransition(archiveTitle, exception);
            }
            else if (!browserLoadingStarted)
            {
                // The prior authoritative archive and browser state were never replaced.
            }
            else if (exception is OperationCanceledException)
            {
                Browser.CancelLoading();
            }
            else
            {
                Browser.FailLoading(GetBrowserFailureState(exception));
            }

            throw;
        }
    }

    private async Task AdoptRecoveredArchiveAsync(
        IArchiveClient client,
        ArchiveDescriptor recoveredArchive,
        bool openedWithPassword,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(recoveredArchive);
        const string archiveTitle = "Recovered archive";
        var adopted = false;
        var priorSessionCloseStarted = false;
        var stateTransitionStarted = false;
        var browserLoadingStarted = false;
        try
        {
            await CancelAndDrainPreviewAsync(clearPresentation: true).ConfigureAwait(true);
            if (_archive is null)
            {
                Browser.BeginLoading(archiveTitle);
                browserLoadingStarted = true;
            }

            var newEntries = new List<ArchiveEntryDescriptor>();
            ArchiveBrowserPreparedSession? prepared = null;
            if (recoveredArchive.Capabilities.HasFlag(ArchiveCapability.List))
            {
                if (recoveredArchive.EntryCount is < 0 or > ArchiveBrowserViewModel.MaximumIndexedEntries)
                {
                    throw new InvalidDataException(
                        "The recovered archive entry count exceeds the bounded browser index.");
                }

                var seenEntryIds = new HashSet<EntryId>();
                var offset = 0;
                do
                {
                    var page = await client.ListEntriesAsync(
                            recoveredArchive.SessionId,
                            offset,
                            ProtocolConstants.MaximumPageSize,
                            cancellationToken)
                        .ConfigureAwait(true);
                    if (page.TotalCount != recoveredArchive.EntryCount
                        || (long)newEntries.Count + page.Entries.Count > recoveredArchive.EntryCount)
                    {
                        throw new InvalidDataException(
                            "The Worker changed the recovered archive entry count while listing.");
                    }

                    foreach (var entry in page.Entries)
                    {
                        if (!seenEntryIds.Add(entry.EntryId))
                        {
                            throw new InvalidDataException(
                                "The Worker returned a duplicate recovered archive entry identifier.");
                        }
                    }

                    newEntries.AddRange(page.Entries);
                    if (browserLoadingStarted)
                    {
                        Browser.ReportLoadingProgress(newEntries.Count, recoveredArchive.EntryCount);
                    }

                    if (page.NextOffset is not { } next)
                    {
                        break;
                    }

                    if (next <= offset || next != newEntries.Count)
                    {
                        throw new InvalidDataException(
                            "The Worker returned a non-advancing recovered archive page.");
                    }

                    offset = next;
                }
                while (true);

                if (newEntries.Count != recoveredArchive.EntryCount)
                {
                    throw new InvalidDataException(
                        "The Worker ended recovered archive listing before the declared entry count.");
                }

                prepared = ArchiveBrowserViewModel.PrepareSession(
                    recoveredArchive,
                    newEntries,
                    client.SupportsFeature(ProtocolFeatures.PersistentEntryKeys));
            }

            if (_archive is not null)
            {
                priorSessionCloseStarted = true;
                var closed = await client.CloseArchiveAsync(_archive.SessionId, cancellationToken)
                    .ConfigureAwait(true);
                if (!closed.Closed)
                {
                    throw new InvalidDataException(
                        "The prior archive session did not close authoritatively.");
                }


                Browser.BeginLoading(archiveTitle);
                browserLoadingStarted = true;
            }

            stateTransitionStarted = true;
            _archive = recoveredArchive;
            _archivePath = null;
            _archiveDisplayName = archiveTitle;
            _sessionHasPassword = openedWithPassword;
            _zipWriteCapabilitiesSessionId = null;
            _zipWriteCapabilities = null;
            _allEntries.Clear();
            _allEntries.AddRange(newEntries);
            if (prepared is null)
            {
                Browser.OpenUnsupportedSession(recoveredArchive, archiveTitle);
            }
            else
            {
                Browser.ApplyPreparedSession(
                    prepared,
                    archiveTitle,
                    sameAuthoritativeSource: false);
            }

            OnPropertyChanged(nameof(HasArchive));
            OnPropertyChanged(nameof(CanExtract));
            OnPropertyChanged(nameof(CanTest));
            OnPropertyChanged(nameof(CanRebuildCopy));
            OnPropertyChanged(nameof(RebuildCopyHelpText));
            OnPropertyChanged(nameof(ArchiveTitle));
            OnPropertyChanged(nameof(ArchiveDetails));
            OnPropertyChanged(nameof(SearchText));
            OnPropertyChanged(nameof(CurrentFolderId));
            StatusText = recoveredArchive.Warnings.Count == 0
                ? $"Opened recovered archive with {recoveredArchive.EntryCount:N0} entries. The recovery workspace remains preserved."
                : $"Opened recovered archive with {recoveredArchive.EntryCount:N0} entries and {recoveredArchive.Warnings.Count:N0} warning{(recoveredArchive.Warnings.Count == 1 ? string.Empty : "s")}. The recovery workspace remains preserved.";
            RefreshCommandStates();
            adopted = true;
        }
        catch (Exception exception)
        {
            if (!adopted)
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    _ = await client.CloseArchiveAsync(
                            recoveredArchive.SessionId,
                            closeTimeout.Token)
                        .ConfigureAwait(true);
                }
                catch (Exception cleanupException) when (
                    cleanupException is ArchiveClientException
                        or OperationCanceledException
                        or ObjectDisposedException)
                {
                    // Owned-client disposal remains the final bounded containment fallback.
                }
            }

            if (priorSessionCloseStarted || stateTransitionStarted)
            {
                FailOpenedArchiveTransition(archiveTitle, exception);
            }
            else if (!browserLoadingStarted)
            {
                // The prior authoritative archive and browser state were never replaced.
            }
            else if (exception is OperationCanceledException)
            {
                Browser.CancelLoading();
            }
            else
            {
                Browser.FailLoading(GetBrowserFailureState(exception));
            }

            throw;
        }
    }

    private void FailOpenedArchiveTransition(string archiveTitle, Exception exception)
    {
        try
        {
            ClearOpenedArchiveState();
        }
        finally
        {
            try
            {
                Browser.CloseSession();
            }
            finally
            {
                try
                {
                    Browser.BeginLoading(archiveTitle);
                }
                finally
                {
                    Browser.FailLoading(GetBrowserFailureState(exception));
                }
            }
        }
    }

    private void ClearOpenedArchiveState()
    {
        _archive = null;
        _archivePath = null;
        _archiveDisplayName = null;
        _sessionHasPassword = false;
        _zipWriteCapabilitiesSessionId = null;
        _allEntries.Clear();
        OnPropertyChanged(nameof(HasArchive));
        OnPropertyChanged(nameof(CanExtract));
        OnPropertyChanged(nameof(CanTest));
        OnPropertyChanged(nameof(CanRebuildCopy));
        OnPropertyChanged(nameof(RebuildCopyHelpText));
        OnPropertyChanged(nameof(ArchiveTitle));
        OnPropertyChanged(nameof(ArchiveDetails));
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(CurrentFolderId));
        RefreshCommandStates();
    }

    private async Task TestArchiveAsync()
    {
        if (_archive is null || !CanTest)
        {
            return;
        }

        await RunGuardedAsync(async cancellationToken =>
        {
            await CancelAndDrainPreviewAsync(clearPresentation: true).ConfigureAwait(true);
            var secret = await CreateOperationSecretIfNeededAsync(cancellationToken).ConfigureAwait(true);
            var started = await _client!.StartIntegrityTestAsync(
                _archive.SessionId,
                [],
                secret,
                cancellationToken).ConfigureAwait(true);
            BeginActivity(
                started.OperationId,
                OperationKind.IntegrityTest,
                "Preparing integrity test",
                "Integrity test");
            var terminal = await _client.WaitForTerminalAsync(started.OperationId, cancellationToken).ConfigureAwait(true);
            if (secret is not null
                && terminal.Phase is OperationPhase.Completed or OperationPhase.CompletedWithWarnings)
            {
                _sessionHasPassword = true;
            }

            ApplyTerminal(
                terminal,
                "Integrity test completed successfully.",
                invalidateBrowserOnSourceChanged: true);
        }, invalidateBrowserOnSourceChanged: true).ConfigureAwait(true);
    }

    private async Task<Guid?> CreateOperationSecretIfNeededAsync(CancellationToken cancellationToken)
    {
        if (_archive is null || !_archive.HasEncryptedEntries || _sessionHasPassword)
        {
            return null;
        }

        var secret = await RequestSecretAsync(_archiveDisplayName!, cancellationToken).ConfigureAwait(true);
        if (secret is null)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        return secret;
    }

    private async Task<Guid?> RequestSecretAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var password = await _dialogs
            .RequestPasswordAsync(Path.GetFileName(sourcePath), cancellationToken)
            .ConfigureAwait(true);
        if (password is null)
        {
            return null;
        }

        try
        {
            return await _client!.CreateSecretAsync(password, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            Array.Clear(password);
        }
    }

    private Task StartPreviewRequest(
        ArchiveEntryDescriptor entry,
        ArchiveSessionId sessionId,
        IArchiveClient client)
    {
        var lifetime = _lifetime;
        if (lifetime is null || IsInspectorClosing)
        {
            return Task.CompletedTask;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PreviewRequest? activeRequest;
        PreviewRequest? replacedPendingRequest;
        TaskCompletionSource? pumpCompletion = null;
        PreviewRequest request;
        lock (_previewRequestGate)
        {
            var requestId = checked(++_previewRequestSequence);
            request = new PreviewRequest(
                requestId,
                entry,
                sessionId,
                client,
                cancellation,
                completion);
            activeRequest = _activePreviewRequest;
            replacedPendingRequest = _pendingPreviewRequest;
            _pendingPreviewRequest = request;
            _currentPreviewRequest = requestId;
            if (_previewPump.IsCompleted)
            {
                pumpCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _previewPump = pumpCompletion.Task;
            }
        }

        if (activeRequest is not null)
        {
            TryCancel(activeRequest.Cancellation);
        }

        if (replacedPendingRequest is not null)
        {
            TryCancel(replacedPendingRequest.Cancellation);
            CompletePreviewRequest(replacedPendingRequest);
        }

        Preview.Begin(sessionId, entry);
        RevealPreviewInspector();
        StatusText = $"Loading text preview for {PreviewTitle}.";
        if (pumpCompletion is not null)
        {
            _ = RunPreviewPumpAsync(pumpCompletion);
        }

        return completion.Task;
    }

    private async Task RunPreviewPumpAsync(TaskCompletionSource pumpCompletion)
    {
        try
        {
            while (true)
            {
                PreviewRequest? request;
                lock (_previewRequestGate)
                {
                    request = _pendingPreviewRequest;
                    _pendingPreviewRequest = null;
                    if (request is null)
                    {
                        if (ReferenceEquals(_previewPump, pumpCompletion.Task))
                        {
                            _previewPump = Task.CompletedTask;
                        }
                    }
                    else
                    {
                        _activePreviewRequest = request;
                    }
                }

                if (request is null)
                {
                    return;
                }

                try
                {
                    request.Cancellation.Token.ThrowIfCancellationRequested();
                    await RunPreviewRequestAsync(request).ConfigureAwait(true);
                }
                finally
                {
                    lock (_previewRequestGate)
                    {
                        if (ReferenceEquals(_activePreviewRequest, request))
                        {
                            _activePreviewRequest = null;
                        }
                    }

                    CompletePreviewRequest(request);
                }
            }
        }
        finally
        {
            lock (_previewRequestGate)
            {
                if (ReferenceEquals(_previewPump, pumpCompletion.Task))
                {
                    _previewPump = Task.CompletedTask;
                }
            }

            pumpCompletion.TrySetResult();
        }
    }

    private async Task RunPreviewRequestAsync(PreviewRequest request)
    {
        var requestId = request.RequestId;
        var entry = request.Entry;
        var sessionId = request.SessionId;
        var client = request.Client;
        var requestCancellation = request.Cancellation;
        Guid? previewSecret = null;
        try
        {
            if (entry.IsEncrypted && !_sessionHasPassword)
            {
                previewSecret = await RequestSecretAsync(_archiveDisplayName!, requestCancellation.Token)
                    .ConfigureAwait(true);
                if (previewSecret is null)
                {
                    throw new OperationCanceledException(requestCancellation.Token);
                }
            }

            var service = EnsurePreviewService(client);
            var result = await service.LoadTextAsync(
                    sessionId,
                    entry,
                    previewSecret,
                    requestCancellation.Token)
                .ConfigureAwait(true);
            if (!IsCurrentPreviewRequest(requestId))
            {
                return;
            }

            Preview.Apply(sessionId, entry, result);
            if (previewSecret is not null && result.SourceWasRead)
            {
                _sessionHasPassword = true;
            }

            RevealPreviewInspector();
            StatusText = result.Outcome == EntryPreviewOutcome.Text
                ? $"Previewing {PreviewTitle}."
                : Preview.StateTitle;
            PreviewFocusRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            if (IsCurrentPreviewRequest(requestId))
            {
                Preview.ShowCancelled(entry);
                RevealPreviewInspector();
                StatusText = "Safe preview cancelled.";
                PreviewFocusRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (ArchiveClientException exception)
        {
            if (IsCurrentPreviewRequest(requestId))
            {
                var message = GetSafeErrorMessage(exception.Error.Category);
                if (exception.Error.Category is ArchiveErrorCategory.SourceChanged
                    or ArchiveErrorCategory.WorkerFailure
                    or ArchiveErrorCategory.ProtocolMismatch)
                {
                    ApplyBrowserFailure(exception.Error.Category);
                }
                else if (exception.Error.Category is ArchiveErrorCategory.UnsupportedCapability
                         or ArchiveErrorCategory.UnsupportedFormat)
                {
                    Preview.ShowUnsupported(sessionId, entry);
                    RevealPreviewInspector();
                }
                else
                {
                    Preview.ShowError(entry);
                    RevealPreviewInspector();
                }

                StatusText = message;
                PreviewFocusRequested?.Invoke(this, EventArgs.Empty);
                _dialogs.ShowError("Text preview failed", message);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            if (IsCurrentPreviewRequest(requestId))
            {
                const string message = "The authenticated archive worker is unavailable.";
                ApplyBrowserFailure(ArchiveErrorCategory.WorkerFailure);
                StatusText = message;
                PreviewFocusRequested?.Invoke(this, EventArgs.Empty);
                _dialogs.ShowError("Text preview failed", message);
            }
        }
        catch (Exception)
        {
            if (IsCurrentPreviewRequest(requestId))
            {
                const string message = "Text preview could not be completed safely.";
                Preview.ShowError(entry);
                RevealPreviewInspector();
                StatusText = message;
                PreviewFocusRequested?.Invoke(this, EventArgs.Empty);
                _dialogs.ShowError("Text preview failed", message);
            }
        }
        finally
        {
            if (previewSecret is { } secretSlot)
            {
                await TryDiscardPreviewSecretAsync(client, secretSlot).ConfigureAwait(true);
            }

        }
    }

    private static async Task TryDiscardPreviewSecretAsync(IArchiveClient client, Guid secretSlot)
    {
        using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            _ = await client.DiscardSecretAsync(secretSlot, cleanupTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is ArchiveClientException or OperationCanceledException or ObjectDisposedException)
        {
            // The worker consumes transferred slots exactly once. A failed cleanup means the
            // connection is already unavailable or the short-lived slot will expire worker-side.
        }
    }

    private IEntryPreviewService EnsurePreviewService(IArchiveClient client)
    {
        lock (_previewRequestGate)
        {
            return _previewService ??= _previewServiceFactory(client);
        }
    }

    private bool IsCurrentPreviewRequest(long requestId)
    {
        lock (_previewRequestGate)
        {
            return _currentPreviewRequest == requestId;
        }
    }

    private void CancelPreviewRequests(bool clearPresentation)
    {
        PreviewRequest? activeRequest;
        PreviewRequest? pendingRequest;
        lock (_previewRequestGate)
        {
            _currentPreviewRequest = 0;
            activeRequest = _activePreviewRequest;
            pendingRequest = _pendingPreviewRequest;
            _pendingPreviewRequest = null;
        }

        if (activeRequest is not null)
        {
            TryCancel(activeRequest.Cancellation);
        }

        if (pendingRequest is not null)
        {
            TryCancel(pendingRequest.Cancellation);
            CompletePreviewRequest(pendingRequest);
        }

        if (clearPresentation)
        {
            ClearPreviewPresentation();
        }
    }

    private async Task CancelAndDrainPreviewAsync(bool clearPresentation)
    {
        PreviewRequest? activeRequest;
        PreviewRequest? pendingRequest;
        Task previewPump;
        lock (_previewRequestGate)
        {
            _currentPreviewRequest = 0;
            activeRequest = _activePreviewRequest;
            pendingRequest = _pendingPreviewRequest;
            _pendingPreviewRequest = null;
            previewPump = _previewPump;
        }

        if (activeRequest is not null)
        {
            TryCancel(activeRequest.Cancellation);
        }

        if (pendingRequest is not null)
        {
            TryCancel(pendingRequest.Cancellation);
            CompletePreviewRequest(pendingRequest);
        }

        await previewPump.ConfigureAwait(true);
        if (clearPresentation)
        {
            // A completion that had already passed its current-request check can race the first
            // clear on a non-Dispatcher caller. Draining is the ownership boundary, so settle the
            // presentation once more after the pump has stopped.
            HideInspectorPresentation();
        }
    }

    private void ClearPreviewPresentation()
    {
        HideInspectorPresentation();
    }

    private void HideInspectorPresentation()
    {
        ResetPreviewPresentation();
        InspectorSelectedTabIndex = 0;
        IsInspectorVisible = false;
    }

    private void ResetPreviewPresentation()
    {
        IsPreviewVisible = false;
        Preview.Reset();
    }

    private void RevealPreviewInspector()
    {
        IsPreviewVisible = true;
        InspectorSelectedTabIndex = 1;
        IsInspectorVisible = true;
    }

    private static void TryCancel(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void CompletePreviewRequest(PreviewRequest request)
    {
        request.Cancellation.Dispose();
        request.Completion.TrySetResult();
    }

    public async Task CancelCurrentOperationAsync()
    {
        if (_client is null
            || !CanCancelCurrentOperationFromSnapshot
            || ActiveActivity.OperationId is not { } operationId)
        {
            return;
        }

        CanCancelCurrentOperationFromSnapshot = false;
        _currentOperationCancellationPending = true;
        try
        {
            using var cancelTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetime?.Token ?? CancellationToken.None);
            cancelTimeout.CancelAfter(TimeSpan.FromSeconds(2));
            var response = await _client
                .CancelOperationAsync(operationId, cancelTimeout.Token)
                .ConfigureAwait(true);
            lock (_activityStateGate)
            {
                if (ActiveActivity.OperationId != operationId || !ActiveActivity.CanCancel)
                {
                    return;
                }

                StatusText = response.CancellationRequested
                    ? "Cancellation requested."
                    : "The operation had already finished or could not accept cancellation.";
            }

            _activityCenter?.RequestRefresh();
        }
        catch (ArchiveClientException exception)
        {
            var message = GetSafeErrorMessage(exception.Error.Category);
            ApplyBrowserFailure(exception.Error.Category);
            StatusText = message;
            _dialogs.ShowError("Rain Archives", message);
        }
        catch (OperationCanceledException)
        {
            StatusText = "The cancellation request timed out; worker teardown remains bounded on exit.";
        }
    }

    private void ApplyTerminal(
        OperationEvent terminal,
        string successMessage,
        bool invalidateBrowserOnSourceChanged = false)
    {
        string? informationMessage = null;
        string? errorMessage = null;
        lock (_activityStateGate)
        {
            ActiveActivity.Apply(terminal);
            _currentOperationCancellationPending = false;
            CanCancelCurrentOperationFromSnapshot = false;
            if (terminal.Phase is OperationPhase.Completed or OperationPhase.CompletedWithWarnings)
            {
                StatusText = successMessage;
                if (terminal.Phase == OperationPhase.CompletedWithWarnings)
                {
                    informationMessage = "The operation completed with a warning. Review the Activity Center for its typed lifecycle state.";
                }
            }
            else if (terminal.Phase == OperationPhase.Cancelled)
            {
                StatusText = "Operation cancelled.";
            }
            else
            {
                errorMessage = terminal.Error is { } error
                    ? GetSafeErrorMessage(error.Category)
                    : "The archive operation failed.";
                if (terminal.Error is { } terminalError
                    && (invalidateBrowserOnSourceChanged
                        || terminalError.Category is ArchiveErrorCategory.WorkerFailure
                            or ArchiveErrorCategory.ProtocolMismatch))
                {
                    ApplyBrowserFailure(terminalError.Category);
                }
                StatusText = errorMessage;
            }
        }

        if (informationMessage is not null)
        {
            _dialogs.ShowInformation("Completed with warnings", informationMessage);
        }
        else if (errorMessage is not null)
        {
            _dialogs.ShowError("Archive operation failed", errorMessage);
        }
    }

    private void ApplyBrowserFailure(ArchiveErrorCategory category)
    {
        if (category == ArchiveErrorCategory.SourceChanged)
        {
            var hadPreview = Preview.HasPresentation || IsPreviewVisible;
            var selected = Browser.SelectedEntry?.Descriptor;
            CancelPreviewRequests(clearPresentation: !hadPreview);
            if (hadPreview)
            {
                Preview.ShowStale(selected);
                RevealPreviewInspector();
            }

            Browser.MarkStale();
        }
        else if (category is ArchiveErrorCategory.WorkerFailure
                 or ArchiveErrorCategory.ProtocolMismatch)
        {
            var hadPreview = Preview.HasPresentation || IsPreviewVisible;
            var selected = Browser.SelectedEntry?.Descriptor;
            CancelPreviewRequests(clearPresentation: !hadPreview);
            if (hadPreview)
            {
                Preview.ShowOffline(selected);
                RevealPreviewInspector();
            }

            Browser.MarkOffline();
        }
    }

    private static ArchiveBrowserState GetBrowserFailureState(Exception exception) =>
        exception switch
        {
            ArchiveClientException clientException
                when clientException.Error.Category == ArchiveErrorCategory.UnsupportedCapability =>
                ArchiveBrowserState.Unsupported,
            ArchiveClientException clientException
                when clientException.Error.Category is ArchiveErrorCategory.WorkerFailure
                    or ArchiveErrorCategory.ProtocolMismatch =>
                ArchiveBrowserState.Offline,
            IOException or ObjectDisposedException => ArchiveBrowserState.Offline,
            _ => ArchiveBrowserState.Error,
        };

    private static string GetSafeErrorMessage(ArchiveErrorCategory category) => category switch
    {
        ArchiveErrorCategory.InvalidRequest => "The request was rejected before it could run.",
        ArchiveErrorCategory.UnsupportedFormat => "This archive format is not supported.",
        ArchiveErrorCategory.UnsupportedCapability => "This action is unavailable for the connected archive session.",
        ArchiveErrorCategory.InvalidArchive => "The archive structure is invalid.",
        ArchiveErrorCategory.CorruptArchive => "The archive is corrupt or incomplete.",
        ArchiveErrorCategory.PasswordRequired => "A password is required to continue.",
        ArchiveErrorCategory.IncorrectPassword => "The password is incorrect.",
        ArchiveErrorCategory.UnsafePath => "The archive contains a path that cannot be used safely.",
        ArchiveErrorCategory.PathCollision => "The archive contains conflicting entry paths.",
        ArchiveErrorCategory.ResourceLimit => "The archive exceeds a configured safety limit.",
        ArchiveErrorCategory.SourceChanged => "The archive source changed. Reopen it before continuing.",
        ArchiveErrorCategory.DestinationConflict => "The destination already exists.",
        ArchiveErrorCategory.PermissionDenied => "Access was denied by the destination or source policy.",
        ArchiveErrorCategory.InsufficientSpace => "There is not enough space to complete the operation safely.",
        ArchiveErrorCategory.Cancelled => "The operation was cancelled.",
        ArchiveErrorCategory.ProviderFailure => "The archive provider could not complete the request.",
        ArchiveErrorCategory.WorkerFailure => "The authenticated archive Worker is unavailable.",
        ArchiveErrorCategory.ProtocolMismatch => "The archive Worker protocol response was refused.",
        _ => "Rain Archives could not complete the request safely.",
    };

    private async Task RunGuardedAsync(
        Func<CancellationToken, Task> action,
        bool invalidateBrowserOnSourceChanged = false)
    {
        if (IsBusy || _lifetime is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await action(_lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
        }
        catch (ArchiveClientException exception)
        {
            if (invalidateBrowserOnSourceChanged
                || exception.Error.Category is ArchiveErrorCategory.WorkerFailure
                    or ArchiveErrorCategory.ProtocolMismatch)
            {
                ApplyBrowserFailure(exception.Error.Category);
            }

            var message = GetSafeErrorMessage(exception.Error.Category);
            StatusText = message;
            _dialogs.ShowError("Rain Archives", message);
        }
        catch (IOException)
        {
            Browser.MarkOffline();
            const string message = "The authenticated archive Worker connection is unavailable.";
            StatusText = message;
            _dialogs.ShowError("Rain Archives", message);
        }
        catch (ObjectDisposedException)
        {
            Browser.MarkOffline();
            const string message = "The authenticated archive Worker connection is unavailable.";
            StatusText = message;
            _dialogs.ShowError("Rain Archives", message);
        }
        catch (Exception)
        {
            const string message = "Rain Archives could not complete the request safely.";
            StatusText = message;
            _dialogs.ShowError("Rain Archives", message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<IArchiveClient> EnsureClientAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            return _client;
        }

        _client = await _clientFactory(cancellationToken).ConfigureAwait(true);
        _client.OperationEventReceived += OnOperationEventReceived;
        _activityCenter = new ActivityCenterViewModel(_client, PostToUi);
        _activityCenter.ActivitySnapshotApplied += OnActivitySnapshotApplied;
        _activityCenter.Start();
        OnPropertyChanged(nameof(ActivityCenter));
        OnPropertyChanged(nameof(CanCreate));
        OnPropertyChanged(nameof(CanReviewRecoveries));
        CreateCommand.NotifyCanExecuteChanged();
        RecoveryCommand.NotifyCanExecuteChanged();
        ActivityCenterCommand.NotifyCanExecuteChanged();
        SettingsCommand.NotifyCanExecuteChanged();
        RebuildCopyCommand.NotifyCanExecuteChanged();
        return _client;
    }

    private void OnOperationEventReceived(object? sender, OperationEvent operationEvent)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            lock (_activityStateGate)
            {
                ActiveActivity.Apply(operationEvent);
                if (operationEvent.Kind == OperationEventKind.Terminal)
                {
                    CanCancelCurrentOperationFromSnapshot = false;
                }
            }

            CancelCommand.NotifyCanExecuteChanged();
            return;
        }

        _ = dispatcher.InvokeAsync(() =>
        {
            lock (_activityStateGate)
            {
                ActiveActivity.Apply(operationEvent);
                if (operationEvent.Kind == OperationEventKind.Terminal)
                {
                    CanCancelCurrentOperationFromSnapshot = false;
                }
            }

            CancelCommand.NotifyCanExecuteChanged();
        });
    }

    private void OnBrowserPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ArchiveBrowserViewModel.CurrentFolderId))
        {
            OnPropertyChanged(nameof(CurrentFolderId));
        }
        else if (args.PropertyName is nameof(ArchiveBrowserViewModel.SearchText))
        {
            OnPropertyChanged(nameof(SearchText));
        }

        if (args.PropertyName is nameof(ArchiveBrowserViewModel.State)
            or nameof(ArchiveBrowserViewModel.CanBrowse)
            or nameof(ArchiveBrowserViewModel.CapabilitySummary))
        {
            RefreshCommandContextState(notifyCommands: false);
            OnPropertyChanged(nameof(CanExtract));
            OnPropertyChanged(nameof(CanTest));
            OnPropertyChanged(nameof(CanRebuildCopy));
            OnPropertyChanged(nameof(RebuildCopyHelpText));
            OnPropertyChanged(nameof(ArchiveDetails));
            RefreshCommandStates();
        }

        if (args.PropertyName is nameof(ArchiveBrowserViewModel.State)
            or nameof(ArchiveBrowserViewModel.SelectedEntry)
            or nameof(ArchiveBrowserViewModel.PersistentKeysEnabled))
        {
            if (args.PropertyName == nameof(ArchiveBrowserViewModel.State)
                && Browser.State is not (ArchiveBrowserState.Ready or ArchiveBrowserState.Empty)
                && !Preview.HasPresentation)
            {
                HideInspectorPresentation();
            }

            if (args.PropertyName == nameof(ArchiveBrowserViewModel.SelectedEntry))
            {
                var selectedId = Browser.SelectedEntry?.EntryId;
                if (selectedId is null)
                {
                    _contextSelectionIds.Clear();
                }
                else if (!_contextSelectionIds.Contains(selectedId.Value))
                {
                    _contextSelectionIds.Clear();
                    _contextSelectionIds.Add(selectedId.Value);
                }
            }

            RefreshCommandContextState();
            if (args.PropertyName == nameof(ArchiveBrowserViewModel.SelectedEntry)
                && Preview.EntryId is { } previewEntryId
                && Browser.SelectedEntry?.EntryId != previewEntryId)
            {
                CancelPreviewRequests(clearPresentation: true);
            }

            Properties.Synchronize(
                _archive,
                Browser.State,
                Browser.SelectedEntry?.Descriptor,
                Browser.PersistentKeysEnabled);
        }
    }

    private void OnPreviewPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(SafePreviewViewModel.EntryTitle))
        {
            OnPropertyChanged(nameof(PreviewTitle));
        }
        else if (args.PropertyName is nameof(SafePreviewViewModel.Details))
        {
            OnPropertyChanged(nameof(PreviewDetails));
        }
        else if (args.PropertyName is nameof(SafePreviewViewModel.Text))
        {
            OnPropertyChanged(nameof(PreviewText));
        }

        if (args.PropertyName is nameof(SafePreviewViewModel.State)
            or nameof(SafePreviewViewModel.IsLoading))
        {
            OnPropertyChanged(nameof(IsPreviewLoading));
            ClosePreviewCommand.NotifyCanExecuteChanged();
        }
    }

    private void RefreshCommandStates()
    {
        RefreshCommandContextState(notifyCommands: false);
        OpenCommand.NotifyCanExecuteChanged();
        CreateCommand.NotifyCanExecuteChanged();
        RecoveryCommand.NotifyCanExecuteChanged();
        ActivityCenterCommand.NotifyCanExecuteChanged();
        SettingsCommand.NotifyCanExecuteChanged();
        RebuildCopyCommand.NotifyCanExecuteChanged();
        NavigateSelectionCommand.NotifyCanExecuteChanged();
        PreviewSelectionCommand.NotifyCanExecuteChanged();
        ShowPropertiesCommand.NotifyCanExecuteChanged();
        ExtractSelectionCommand.NotifyCanExecuteChanged();
        BackCommand.NotifyCanExecuteChanged();
        ForwardCommand.NotifyCanExecuteChanged();
        UpCommand.NotifyCanExecuteChanged();
        RootCommand.NotifyCanExecuteChanged();
        FocusSearchCommand.NotifyCanExecuteChanged();
        TestCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        ClosePreviewCommand.NotifyCanExecuteChanged();
        CloseInspectorCommand.NotifyCanExecuteChanged();
    }

    private void RefreshCommandContextState(bool notifyCommands = true)
    {
        CommandContext = ArchiveCommandContext.Create(
            _archive,
            Browser.State,
            IsBusy,
            Browser.Entries,
            _contextSelectionIds,
            Browser.SelectedEntry);
        if (!notifyCommands)
        {
            return;
        }

        NavigateSelectionCommand.NotifyCanExecuteChanged();
        PreviewSelectionCommand.NotifyCanExecuteChanged();
        ShowPropertiesCommand.NotifyCanExecuteChanged();
        ExtractSelectionCommand.NotifyCanExecuteChanged();
    }

    private void BeginActivity(
        OperationId operationId,
        OperationKind kind,
        string activeDescription,
        string activityCenterName)
    {
        ActiveActivity.Begin(operationId, activeDescription);
        _currentOperationCancellationPending = false;
        CanCancelCurrentOperationFromSnapshot = false;
        _activityCenter?.TrackStarted(operationId, kind, activityCenterName);
    }

    private async Task ShowActivityCenterAsync()
    {
        if (_lifetime is null)
        {
            return;
        }

        try
        {
            _ = await EnsureClientAsync(_lifetime.Token).ConfigureAwait(true);
            _dialogs.ShowActivityCenter(_activityCenter!);
            _activityCenter!.RequestRefresh();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Activity Center did not open because the connection was closing.";
        }
        catch (Exception exception) when (
            exception is ArchiveClientException or IOException or ObjectDisposedException)
        {
            const string message = "Activity Center could not connect to the authenticated archive Worker.";
            StatusText = message;
            _dialogs.ShowError("Activity Center", message);
        }
    }

    private Task ShowSettingsAsync()
    {
        lock (_settingsWorkGate)
        {
            if (!_settingsWork.IsCompleted)
            {
                return _settingsWork;
            }

            _settingsWork = ShowSettingsCoreAsync();
            return _settingsWork;
        }
    }

    private async Task ShowSettingsCoreAsync()
    {
        var lifetime = _lifetime;
        if (lifetime is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            IArchiveClient? client = _client;
            if (client is null)
            {
                try
                {
                    client = await EnsureClientAsync(lifetime.Token).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (
                    exception is ArchiveClientException
                        or IOException
                        or ObjectDisposedException)
                {
                    Browser.MarkOffline();
                    client = null;
                }
            }

            await _dialogs
                .ShowSettingsAsync(client, lifetime.Token)
                .ConfigureAwait(true);
            StatusText = client is null
                ? "Settings and presets closed. Worker capabilities remained offline."
                : "Settings and presets closed.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Settings work was cancelled and settled.";
        }
        catch (Exception exception) when (
            exception is ArchiveClientException
                or IOException
                or ObjectDisposedException)
        {
            const string message = "Settings and presets could not be opened safely.";
            StatusText = message;
            _dialogs.ShowError("Settings and Presets", message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnActivitySnapshotApplied(GetOperationActivityResponse response)
    {
        var currentOperationId = ActiveActivity.OperationId;
        CanCancelCurrentOperationFromSnapshot = currentOperationId is { } operationId
            && !_currentOperationCancellationPending
            && ActiveActivity.CanCancel
            && response.Operations.Any(operation =>
                operation.OperationId == operationId && operation.CanCancel);
    }

    private static void PostToUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _ = dispatcher.BeginInvoke(action);
    }

    private static async Task<IArchiveClient> CreatePackagedClientAsync(CancellationToken cancellationToken)
    {
        var workerPath = Path.Combine(
            AppContext.BaseDirectory,
            "worker",
            "RainArchives.Worker.exe");
        return await ArchiveClient
            .StartAsync(workerPath, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private sealed record PreviewRequest(
        long RequestId,
        ArchiveEntryDescriptor Entry,
        ArchiveSessionId SessionId,
        IArchiveClient Client,
        CancellationTokenSource Cancellation,
        TaskCompletionSource Completion);
}
