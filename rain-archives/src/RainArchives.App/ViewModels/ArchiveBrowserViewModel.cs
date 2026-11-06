using System.Collections.ObjectModel;
using System.IO;
using RainArchives.App.Infrastructure;
using RainArchives.Contracts;

namespace RainArchives.App.ViewModels;

public enum ArchiveBrowserState
{
    NoArchive,
    Loading,
    Ready,
    Empty,
    Offline,
    Unsupported,
    Error,
    Stale,
}

public enum ArchiveBrowserFocusTarget
{
    EntryList,
    Search,
}

public sealed class ArchiveBrowserFocusRequestedEventArgs(
    ArchiveBrowserFocusTarget target) : EventArgs
{
    public ArchiveBrowserFocusTarget Target { get; } = target;
}

/// <summary>
/// Presentation-only archive navigation state. It consumes validated Contracts models and never
/// receives a source path, provider object, filesystem handle, or mutation authority.
/// </summary>
public sealed class ArchiveBrowserViewModel : ObservableObject
{
    public const int VisiblePageSize = 512;
    internal const int MaximumHistoryEntries = 256;
    internal const int MaximumIndexedEntries = 500_000;

    private readonly List<ArchiveBrowserLocation> _history = [];
    private ArchiveBrowserIndex? _index;
    private ArchiveDescriptor? _archive;
    private ArchiveBrowserState _state = ArchiveBrowserState.NoArchive;
    private ArchiveBrowserState _stateBeforeLoading = ArchiveBrowserState.NoArchive;
    private string _stateTitleBeforeLoading = "No archive open";
    private string _stateMessageBeforeLoading = "Choose an archive to browse its contents.";
    private string _statusBeforeLoading = "No archive is open.";
    private string _archiveTitleBeforeLoading = "Rain Archives";
    private string _stateTitle = "No archive open";
    private string _stateMessage = "Choose an archive to browse its contents.";
    private string _statusAnnouncement = "No archive is open.";
    private string _archiveTitle = "Rain Archives";
    private string _searchText = string.Empty;
    private EntryId? _currentFolderId;
    private ArchiveEntryViewModel? _selectedEntry;
    private EntryId? _selectedEntryId;
    private int _historyPosition = -1;
    private int _pageIndex;
    private int _totalResults;
    private bool _persistentKeysEnabled;
    private bool _hostBusy;

    public ArchiveBrowserViewModel()
    {
        BackCommand = new AsyncRelayCommand(
            () =>
            {
                GoBack();
                return Task.CompletedTask;
            },
            () => CanGoBack);
        ForwardCommand = new AsyncRelayCommand(
            () =>
            {
                GoForward();
                return Task.CompletedTask;
            },
            () => CanGoForward);
        UpCommand = new AsyncRelayCommand(
            () =>
            {
                GoUp();
                return Task.CompletedTask;
            },
            () => CanGoUp);
        RootCommand = new AsyncRelayCommand(
            () =>
            {
                NavigateTo(null);
                return Task.CompletedTask;
            },
            () => CanGoUp);
        PreviousPageCommand = new AsyncRelayCommand(
            () =>
            {
                GoToPage(PageIndex - 1);
                return Task.CompletedTask;
            },
            () => CanGoToPreviousPage);
        NextPageCommand = new AsyncRelayCommand(
            () =>
            {
                GoToPage(PageIndex + 1);
                return Task.CompletedTask;
            },
            () => CanGoToNextPage);
        FocusSearchCommand = new AsyncRelayCommand(
            () =>
            {
                RequestFocus(ArchiveBrowserFocusTarget.Search);
                return Task.CompletedTask;
            },
            () => CanSearch);
    }

    public event EventHandler<ArchiveBrowserFocusRequestedEventArgs>? FocusRequested;

    public ObservableCollection<ArchiveEntryViewModel> Entries { get; } = [];
    public ObservableCollection<BreadcrumbItemViewModel> Breadcrumbs { get; } = [];

    public AsyncRelayCommand BackCommand { get; }
    public AsyncRelayCommand ForwardCommand { get; }
    public AsyncRelayCommand UpCommand { get; }
    public AsyncRelayCommand RootCommand { get; }
    public AsyncRelayCommand PreviousPageCommand { get; }
    public AsyncRelayCommand NextPageCommand { get; }
    public AsyncRelayCommand FocusSearchCommand { get; }

    public ArchiveBrowserState State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsWelcomeVisible));
            OnPropertyChanged(nameof(IsBrowserSurfaceVisible));
            OnPropertyChanged(nameof(IsStatePanelVisible));
            OnPropertyChanged(nameof(IsEntryListVisible));
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(CanChooseArchiveFromState));
            OnPropertyChanged(nameof(CanBrowse));
            OnPropertyChanged(nameof(CanSearch));
            NotifyCommandStates();
        }
    }

    public string StateTitle
    {
        get => _stateTitle;
        private set => SetProperty(ref _stateTitle, value);
    }

    public string StateMessage
    {
        get => _stateMessage;
        private set => SetProperty(ref _stateMessage, value);
    }

    public string StatusAnnouncement
    {
        get => _statusAnnouncement;
        private set => SetProperty(ref _statusAnnouncement, value);
    }

    public string ArchiveTitle
    {
        get => _archiveTitle;
        private set => SetProperty(ref _archiveTitle, value);
    }

    public string CapabilitySummary => _archive is null
        ? string.Empty
        : BuildCapabilitySummary(_archive);

    public string SearchText
    {
        get => _searchText;
        set
        {
            value ??= string.Empty;
            if (!SetProperty(ref _searchText, value))
            {
                return;
            }

            if (_index is null
                || State is not (ArchiveBrowserState.Ready or ArchiveBrowserState.Empty))
            {
                return;
            }

            _pageIndex = 0;
            RefreshVisibleEntries(_selectedEntryId, selectFirstWhenUnavailable: true);
        }
    }

    public ArchiveEntryViewModel? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (value is not null
                && !Entries.Any(entry => entry.EntryId == value.EntryId))
            {
                value = null;
            }

            if (!SetProperty(ref _selectedEntry, value))
            {
                return;
            }

            _selectedEntryId = value?.EntryId;
            OnPropertyChanged(nameof(SelectedPersistentKey));
        }
    }

    public ArchiveEntryKey? SelectedPersistentKey =>
        _selectedEntryId is { } selected
        && _index?.TryGetPersistentKey(selected, out var key) == true
            ? key
            : null;

    public EntryId? CurrentFolderId
    {
        get => _currentFolderId;
        private set
        {
            if (SetProperty(ref _currentFolderId, value))
            {
                OnPropertyChanged(nameof(CurrentLocationName));
                OnPropertyChanged(nameof(CanGoUp));
            }
        }
    }

    public string CurrentLocationName => CurrentFolderId is { } folderId
        && _index?.TryGet(folderId, out var folder) == true
            ? UntrustedDisplayText.Escape(folder.DisplayName)
            : ArchiveTitle;

    public int PageIndex
    {
        get => _pageIndex;
        private set
        {
            if (SetProperty(ref _pageIndex, value))
            {
                OnPropertyChanged(nameof(PageNumber));
                OnPropertyChanged(nameof(CanGoToPreviousPage));
                OnPropertyChanged(nameof(CanGoToNextPage));
            }
        }
    }

    public int PageNumber => PageCount == 0 ? 0 : PageIndex + 1;
    public int PageCount => TotalResults == 0
        ? 0
        : checked((TotalResults + VisiblePageSize - 1) / VisiblePageSize);

    public int TotalResults
    {
        get => _totalResults;
        private set
        {
            if (!SetProperty(ref _totalResults, value))
            {
                return;
            }

            OnPropertyChanged(nameof(PageCount));
            OnPropertyChanged(nameof(PageNumber));
            OnPropertyChanged(nameof(CanGoToPreviousPage));
            OnPropertyChanged(nameof(CanGoToNextPage));
        }
    }

    public string PageSummary
    {
        get
        {
            if (TotalResults == 0)
            {
                return "0 items";
            }

            var first = checked(PageIndex * VisiblePageSize + 1);
            var last = Math.Min(TotalResults, checked(first + Entries.Count - 1));
            return $"{first:N0}–{last:N0} of {TotalResults:N0} items · page {PageNumber:N0} of {PageCount:N0}";
        }
    }

    public bool HasSession => _archive is not null;
    public bool IsWelcomeVisible => State == ArchiveBrowserState.NoArchive;
    public bool IsBrowserSurfaceVisible => State != ArchiveBrowserState.NoArchive;
    public bool IsStatePanelVisible => State != ArchiveBrowserState.Ready;
    public bool IsEntryListVisible => State == ArchiveBrowserState.Ready;
    public bool IsLoading => State == ArchiveBrowserState.Loading;
    public bool CanChooseArchiveFromState => State is ArchiveBrowserState.Offline
        or ArchiveBrowserState.Unsupported
        or ArchiveBrowserState.Error
        or ArchiveBrowserState.Stale;
    public bool CanBrowse =>
        _index is not null
        && State is ArchiveBrowserState.Ready or ArchiveBrowserState.Empty;
    public bool CanSearch => CanBrowse && !_hostBusy;
    public bool CanGoBack => CanSearch && _historyPosition > 0;
    public bool CanGoForward => CanSearch && _historyPosition >= 0 && _historyPosition < _history.Count - 1;
    public bool CanGoUp => CanSearch && CurrentFolderId is not null;
    public bool CanGoToPreviousPage => CanSearch && PageIndex > 0;
    public bool CanGoToNextPage => CanSearch && PageIndex + 1 < PageCount;
    public bool PersistentKeysEnabled => _persistentKeysEnabled;
    internal int HistoryCount => _history.Count;

    public void SetHostBusy(bool value)
    {
        if (_hostBusy == value)
        {
            return;
        }

        _hostBusy = value;
        OnPropertyChanged(nameof(CanSearch));
        NotifyCommandStates();
    }

    public void BeginLoading(string archiveTitle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveTitle);
        _stateBeforeLoading = State;
        _stateTitleBeforeLoading = StateTitle;
        _stateMessageBeforeLoading = StateMessage;
        _statusBeforeLoading = StatusAnnouncement;
        _archiveTitleBeforeLoading = ArchiveTitle;
        ArchiveTitle = archiveTitle;
        SetState(
            ArchiveBrowserState.Loading,
            "Loading archive",
            "Opening the archive and loading bounded entry pages.",
            "Loading archive entries.");
    }

    public void ReportLoadingProgress(int loadedEntries, int totalEntries)
    {
        if (State != ArchiveBrowserState.Loading
            || loadedEntries < 0
            || totalEntries < 0
            || loadedEntries > totalEntries)
        {
            return;
        }

        StateMessage = $"Loaded {loadedEntries:N0} of {totalEntries:N0} entry records.";
        StatusAnnouncement = StateMessage;
    }

    public void FailLoading(ArchiveBrowserState failureState)
    {
        if (_archive is not null)
        {
            ArchiveTitle = _archiveTitleBeforeLoading;
            RebuildBreadcrumbs();
            if (_index is not null
                && _stateBeforeLoading is ArchiveBrowserState.Ready or ArchiveBrowserState.Empty)
            {
                RefreshVisibleEntries(_selectedEntryId, selectFirstWhenUnavailable: false, _pageIndex);
            }
            else
            {
                SetState(
                    _stateBeforeLoading,
                    _stateTitleBeforeLoading,
                    _stateMessageBeforeLoading,
                    _statusBeforeLoading);
            }

            return;
        }

        switch (failureState)
        {
            case ArchiveBrowserState.Offline:
                SetState(
                    failureState,
                    "Archive Worker offline",
                    "The authenticated archive Worker is unavailable. Open the archive again after reconnecting.",
                    "Archive browser offline.");
                break;
            case ArchiveBrowserState.Unsupported:
                SetState(
                    failureState,
                    "Browsing unavailable",
                    "The connected protocol or archive session does not support entry listing.",
                    "Archive browsing is unsupported.");
                break;
            default:
                SetState(
                    ArchiveBrowserState.Error,
                    "Archive could not be opened",
                    "Rain Archives could not load a trustworthy entry index. No archive action was enabled.",
                    "Archive browser error.");
                break;
        }
    }

    public void CancelLoading()
    {
        if (_archive is not null)
        {
            ArchiveTitle = _archiveTitleBeforeLoading;
            RebuildBreadcrumbs();
            if (_index is not null
                && _stateBeforeLoading is ArchiveBrowserState.Ready or ArchiveBrowserState.Empty)
            {
                RefreshVisibleEntries(_selectedEntryId, selectFirstWhenUnavailable: false, _pageIndex);
            }
            else
            {
                SetState(
                    _stateBeforeLoading,
                    _stateTitleBeforeLoading,
                    _stateMessageBeforeLoading,
                    _statusBeforeLoading);
            }

            return;
        }

        ArchiveTitle = "Rain Archives";
        SetState(
            ArchiveBrowserState.NoArchive,
            "No archive open",
            "Choose an archive to browse its contents.",
            "No archive is open.");
    }

    public void OpenUnsupportedSession(ArchiveDescriptor archive, string archiveTitle)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveTitle);
        ResetSessionData();
        _archive = archive;
        ArchiveTitle = archiveTitle;
        OnPropertyChanged(nameof(HasSession));
        OnPropertyChanged(nameof(CapabilitySummary));
        SetState(
            ArchiveBrowserState.Unsupported,
            "Browsing unavailable",
            "This archive session does not advertise the entry-listing capability.",
            "Archive entry listing is unavailable for this session.");
    }

    public void OpenSession(
        ArchiveDescriptor archive,
        IReadOnlyList<ArchiveEntryDescriptor> entries,
        string archiveTitle,
        bool persistentKeysEnabled,
        bool sameAuthoritativeSource)
    {
        var prepared = PrepareSession(archive, entries, persistentKeysEnabled);
        ApplyPreparedSession(prepared, archiveTitle, sameAuthoritativeSource);
    }

    internal static ArchiveBrowserPreparedSession PrepareSession(
        ArchiveDescriptor archive,
        IReadOnlyList<ArchiveEntryDescriptor> entries,
        bool persistentKeysEnabled)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(entries);
        return new ArchiveBrowserPreparedSession(
            archive,
            ArchiveBrowserIndex.Create(archive, entries, persistentKeysEnabled),
            persistentKeysEnabled);
    }

    internal void ApplyPreparedSession(
        ArchiveBrowserPreparedSession prepared,
        string archiveTitle,
        bool sameAuthoritativeSource)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveTitle);
        var archive = prepared.Archive;
        var replacement = prepared.Index;
        var persistentKeysEnabled = prepared.PersistentKeysEnabled;
        var priorState = State == ArchiveBrowserState.Loading
            ? _stateBeforeLoading
            : State;
        var canRestore = sameAuthoritativeSource
            && priorState is ArchiveBrowserState.Ready or ArchiveBrowserState.Empty
            && _archive is not null
            && _index is not null
            && _persistentKeysEnabled
            && persistentKeysEnabled
            && FingerprintsEqual(_archive.Fingerprint, archive.Fingerprint);
        var restore = canRestore ? CaptureRestoreState() : null;

        _archive = archive;
        _index = replacement;
        _persistentKeysEnabled = persistentKeysEnabled;
        ArchiveTitle = archiveTitle;
        Entries.Clear();
        Breadcrumbs.Clear();
        _history.Clear();
        _historyPosition = -1;
        CurrentFolderId = null;
        _selectedEntryId = null;
        SelectedEntry = null;
        _searchText = string.Empty;
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(HasSession));
        OnPropertyChanged(nameof(PersistentKeysEnabled));
        OnPropertyChanged(nameof(CapabilitySummary));

        if (restore is null || !TryApplyRestoreState(restore))
        {
            _history.Add(ArchiveBrowserLocation.Root);
            _historyPosition = 0;
            CurrentFolderId = null;
            RebuildBreadcrumbs();
            RefreshVisibleEntries(null, selectFirstWhenUnavailable: true, desiredPageIndex: 0);
        }

        NotifyCommandStates();
    }

    public void CloseSession()
    {
        ResetSessionData();
        ArchiveTitle = "Rain Archives";
        SetState(
            ArchiveBrowserState.NoArchive,
            "No archive open",
            "Choose an archive to browse its contents.",
            "No archive is open.");
    }

    public void MarkStale()
    {
        if (_archive is null)
        {
            return;
        }

        Entries.Clear();
        SelectedEntry = null;
        TotalResults = 0;
        PageIndex = 0;
        SetState(
            ArchiveBrowserState.Stale,
            "Archive source changed",
            "The opened source no longer matches its fingerprint. Reopen it before continuing.",
            "Archive source changed. Browser actions are disabled until an explicit reopen.");
    }

    public void MarkOffline()
    {
        Entries.Clear();
        SelectedEntry = null;
        TotalResults = 0;
        PageIndex = 0;
        SetState(
            ArchiveBrowserState.Offline,
            "Archive Worker offline",
            "The authenticated archive Worker is unavailable. Reopen the archive after reconnecting.",
            "Archive browser offline. Browser actions are disabled.");
    }

    public void MarkError()
    {
        Entries.Clear();
        SelectedEntry = null;
        TotalResults = 0;
        PageIndex = 0;
        SetState(
            ArchiveBrowserState.Error,
            "Archive browser error",
            "The current entry view could not be trusted. Reopen the archive before continuing.",
            "Archive browser error. Browser actions are disabled.");
    }

    public bool NavigateTo(EntryId? folderId)
    {
        if (!CanSearch || !IsDirectoryLocation(folderId))
        {
            return false;
        }

        if (folderId == CurrentFolderId)
        {
            RequestFocus(ArchiveBrowserFocusTarget.EntryList);
            return true;
        }

        if (_historyPosition + 1 < _history.Count)
        {
            _history.RemoveRange(_historyPosition + 1, _history.Count - _historyPosition - 1);
        }

        _history.Add(CreateLocation(folderId));
        _historyPosition = _history.Count - 1;
        if (_history.Count > MaximumHistoryEntries)
        {
            _history.RemoveAt(0);
            _historyPosition--;
        }

        ApplyCurrentHistoryLocation(requestFocus: true);
        return true;
    }

    public void RequestSearchFocus() => RequestFocus(ArchiveBrowserFocusTarget.Search);

    public void RestoreEntryListFocus()
    {
        if (IsEntryListVisible)
        {
            RequestFocus(ArchiveBrowserFocusTarget.EntryList);
        }
    }

    public bool TrySelectPersistentKey(ArchiveEntryKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!CanSearch
            || !_persistentKeysEnabled
            || _index?.TryResolvePersistentKey(key, out var entry) != true
            || entry.ParentEntryId != CurrentFolderId)
        {
            return false;
        }

        _selectedEntryId = entry.EntryId;
        RefreshVisibleEntries(entry.EntryId, selectFirstWhenUnavailable: false);
        if (SelectedEntry is null)
        {
            return false;
        }

        RequestFocus(ArchiveBrowserFocusTarget.EntryList);
        return true;
    }

    private void GoBack()
    {
        if (!CanGoBack)
        {
            return;
        }

        _historyPosition--;
        ApplyCurrentHistoryLocation(requestFocus: true);
    }

    private void GoForward()
    {
        if (!CanGoForward)
        {
            return;
        }

        _historyPosition++;
        ApplyCurrentHistoryLocation(requestFocus: true);
    }

    private void GoUp()
    {
        if (!CanGoUp
            || CurrentFolderId is not { } current
            || _index?.TryGet(current, out var entry) != true)
        {
            return;
        }

        NavigateTo(entry.ParentEntryId);
    }

    private void GoToPage(int pageIndex)
    {
        if (!CanSearch || pageIndex < 0 || pageIndex >= PageCount || pageIndex == PageIndex)
        {
            return;
        }

        PageIndex = pageIndex;
        _selectedEntryId = null;
        RefreshVisibleEntries(null, selectFirstWhenUnavailable: true, pageIndex);
        RequestFocus(ArchiveBrowserFocusTarget.EntryList);
    }

    private void ApplyCurrentHistoryLocation(bool requestFocus)
    {
        if (_historyPosition < 0 || _historyPosition >= _history.Count)
        {
            return;
        }

        CurrentFolderId = _history[_historyPosition].EntryId;
        _searchText = string.Empty;
        OnPropertyChanged(nameof(SearchText));
        _selectedEntryId = null;
        PageIndex = 0;
        RebuildBreadcrumbs();
        RefreshVisibleEntries(null, selectFirstWhenUnavailable: true, desiredPageIndex: 0);
        NotifyCommandStates();
        if (requestFocus)
        {
            RequestFocus(ArchiveBrowserFocusTarget.EntryList);
        }
    }

    private void RebuildBreadcrumbs()
    {
        Breadcrumbs.Clear();
        var chain = new Stack<ArchiveEntryDescriptor>();
        var current = CurrentFolderId;
        while (current is { } entryId && _index?.TryGet(entryId, out var entry) == true)
        {
            chain.Push(entry);
            current = entry.ParentEntryId;
        }

        Breadcrumbs.Add(new BreadcrumbItemViewModel(
            ArchiveTitle,
            null,
            $"Archive root, {ArchiveTitle}",
            chain.Count == 0));
        while (chain.Count > 0)
        {
            var entry = chain.Pop();
            Breadcrumbs.Add(new BreadcrumbItemViewModel(
                UntrustedDisplayText.Escape(entry.DisplayName),
                entry.EntryId,
                $"Archive folder, {UntrustedDisplayText.Escape(entry.DisplayName)}",
                chain.Count == 0));
        }
    }

    private void RefreshVisibleEntries(
        EntryId? preferredSelection,
        bool selectFirstWhenUnavailable,
        int? desiredPageIndex = null)
    {
        if (_index is null || _archive is null || !IsDirectoryLocation(CurrentFolderId))
        {
            Entries.Clear();
            SelectedEntry = null;
            TotalResults = 0;
            PageIndex = 0;
            return;
        }

        var children = _index.GetChildren(CurrentFolderId);
        var total = 0;
        var preferredResultIndex = -1;
        foreach (var entry in children)
        {
            if (!MatchesSearch(entry))
            {
                continue;
            }

            if (entry.EntryId == preferredSelection)
            {
                preferredResultIndex = total;
            }

            total++;
        }

        TotalResults = total;
        var maximumPage = Math.Max(0, PageCount - 1);
        var targetPage = desiredPageIndex.HasValue
            ? Math.Clamp(desiredPageIndex.Value, 0, maximumPage)
            : preferredResultIndex >= 0
                ? preferredResultIndex / VisiblePageSize
                : Math.Clamp(PageIndex, 0, maximumPage);
        PageIndex = targetPage;

        var firstResultIndex = checked(targetPage * VisiblePageSize);
        var resultIndex = 0;
        var page = new List<ArchiveEntryViewModel>(Math.Min(VisiblePageSize, total));
        foreach (var entry in children)
        {
            if (!MatchesSearch(entry))
            {
                continue;
            }

            if (resultIndex >= firstResultIndex && page.Count < VisiblePageSize)
            {
                page.Add(new ArchiveEntryViewModel(entry));
            }

            resultIndex++;
            if (page.Count == VisiblePageSize)
            {
                break;
            }
        }

        Entries.Clear();
        foreach (var entry in page)
        {
            Entries.Add(entry);
        }

        var selection = preferredSelection is { } preferred
            ? Entries.FirstOrDefault(entry => entry.EntryId == preferred)
            : null;
        if (selection is null && selectFirstWhenUnavailable)
        {
            selection = Entries.FirstOrDefault();
        }

        SelectedEntry = selection;
        _selectedEntryId = selection?.EntryId;
        OnPropertyChanged(nameof(PageSummary));

        if (total == 0)
        {
            var (title, message) = EmptyStateText();
            SetState(ArchiveBrowserState.Empty, title, message, message);
        }
        else
        {
            var summary = $"{PageSummary} in {CurrentLocationName}.";
            SetState(
                ArchiveBrowserState.Ready,
                "Archive entries",
                string.Empty,
                summary);
        }

        NotifyCommandStates();
    }

    private (string Title, string Message) EmptyStateText()
    {
        if (!string.IsNullOrEmpty(SearchText))
        {
            return ("No search results", "No entries in this folder match the current search.");
        }

        if (CurrentFolderId is null && _archive?.EntryCount == 0)
        {
            return ("Empty archive", "This archive contains no entries.");
        }

        return ("Empty folder", "This archive folder contains no entries.");
    }

    private bool MatchesSearch(ArchiveEntryDescriptor entry) =>
        string.IsNullOrEmpty(SearchText)
        || entry.DisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase);

    private bool IsDirectoryLocation(EntryId? entryId) =>
        entryId is null
        || (_index?.TryGet(entryId.Value, out var entry) == true
            && entry.Kind == ArchiveEntryKind.Directory);

    private ArchiveBrowserLocation CreateLocation(EntryId? entryId)
    {
        if (entryId is null)
        {
            return ArchiveBrowserLocation.Root;
        }

        _ = _index!.TryGetPersistentKey(entryId.Value, out var key);
        return new ArchiveBrowserLocation(entryId, key);
    }

    private BrowserRestoreState CaptureRestoreState()
    {
        var history = _history
            .Select(location => location.PersistentKey)
            .ToArray();
        ArchiveEntryKey? selection = null;
        if (_selectedEntryId is { } selected)
        {
            _ = _index!.TryGetPersistentKey(selected, out selection);
        }

        return new BrowserRestoreState(
            history,
            _historyPosition,
            selection,
            SearchText,
            PageIndex);
    }

    private bool TryApplyRestoreState(BrowserRestoreState restore)
    {
        if (_index is null
            || restore.HistoryPosition < 0
            || restore.HistoryPosition >= restore.History.Count)
        {
            return false;
        }

        var resolved = new List<ArchiveBrowserLocation>();
        var resolvedCurrent = -1;
        for (var index = 0; index < restore.History.Count; index++)
        {
            var key = restore.History[index];
            ArchiveBrowserLocation? location;
            if (key is null)
            {
                location = ArchiveBrowserLocation.Root;
            }
            else if (_index.TryResolvePersistentKey(key, out var folder)
                     && folder.Kind == ArchiveEntryKind.Directory)
            {
                location = new ArchiveBrowserLocation(folder.EntryId, key);
            }
            else
            {
                location = null;
            }

            if (location is null)
            {
                continue;
            }

            if (index == restore.HistoryPosition)
            {
                resolvedCurrent = resolved.Count;
            }

            resolved.Add(location);
        }

        if (resolvedCurrent < 0)
        {
            return false;
        }

        _history.AddRange(resolved);
        _historyPosition = resolvedCurrent;
        CurrentFolderId = _history[_historyPosition].EntryId;
        _searchText = restore.SearchText;
        OnPropertyChanged(nameof(SearchText));
        EntryId? restoredSelection = null;
        if (restore.SelectedEntryKey is not null
            && _index.TryResolvePersistentKey(restore.SelectedEntryKey, out var entry)
            && entry.ParentEntryId == CurrentFolderId)
        {
            restoredSelection = entry.EntryId;
        }

        _selectedEntryId = restoredSelection;
        RebuildBreadcrumbs();
        RefreshVisibleEntries(
            restoredSelection,
            selectFirstWhenUnavailable: restoredSelection is null,
            desiredPageIndex: restoredSelection is null ? restore.PageIndex : null);
        return true;
    }

    private void ResetSessionData()
    {
        _archive = null;
        _index = null;
        _persistentKeysEnabled = false;
        _history.Clear();
        _historyPosition = -1;
        _searchText = string.Empty;
        OnPropertyChanged(nameof(SearchText));
        CurrentFolderId = null;
        _selectedEntryId = null;
        SelectedEntry = null;
        Entries.Clear();
        Breadcrumbs.Clear();
        TotalResults = 0;
        PageIndex = 0;
        OnPropertyChanged(nameof(HasSession));
        OnPropertyChanged(nameof(PersistentKeysEnabled));
        OnPropertyChanged(nameof(CapabilitySummary));
    }

    private void SetState(
        ArchiveBrowserState state,
        string title,
        string message,
        string announcement)
    {
        StateTitle = title;
        StateMessage = message;
        StatusAnnouncement = announcement;
        State = state;
    }

    private void RequestFocus(ArchiveBrowserFocusTarget target) =>
        FocusRequested?.Invoke(this, new ArchiveBrowserFocusRequestedEventArgs(target));

    private void NotifyCommandStates()
    {
        BackCommand.NotifyCanExecuteChanged();
        ForwardCommand.NotifyCanExecuteChanged();
        UpCommand.NotifyCanExecuteChanged();
        RootCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
        FocusSearchCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(CanGoUp));
        OnPropertyChanged(nameof(CanGoToPreviousPage));
        OnPropertyChanged(nameof(CanGoToNextPage));
    }

    private static bool FingerprintsEqual(ArchiveFingerprint left, ArchiveFingerprint right) =>
        left.Length == right.Length
        && left.LastWriteTimeUtc == right.LastWriteTimeUtc
        && string.Equals(left.FileIdentity, right.FileIdentity, StringComparison.Ordinal)
        && string.Equals(left.PrefixSha256, right.PrefixSha256, StringComparison.Ordinal);

    private static string BuildCapabilitySummary(ArchiveDescriptor archive)
    {
        var values = new List<string>
        {
            "Read-only source",
            archive.Capabilities.HasFlag(ArchiveCapability.Extract)
                ? "Extract available"
                : "Extract unavailable",
            archive.Capabilities.HasFlag(ArchiveCapability.Test)
                ? "Test available"
                : "Test unavailable",
            archive.Capabilities.HasFlag(ArchiveCapability.OpenEntryStream)
                ? "Text preview available"
                : "Text preview unavailable",
            archive.Capabilities.HasFlag(ArchiveCapability.UpdateByRewrite)
                ? "Rebuild to a new copy available"
                : "Rebuild unavailable",
        };
        return string.Join(" · ", values);
    }

    private sealed record ArchiveBrowserLocation(
        EntryId? EntryId,
        ArchiveEntryKey? PersistentKey)
    {
        public static ArchiveBrowserLocation Root { get; } = new(null, null);
    }

    private sealed record BrowserRestoreState(
        IReadOnlyList<ArchiveEntryKey?> History,
        int HistoryPosition,
        ArchiveEntryKey? SelectedEntryKey,
        string SearchText,
        int PageIndex);
}

internal sealed record ArchiveBrowserPreparedSession(
    ArchiveDescriptor Archive,
    ArchiveBrowserIndex Index,
    bool PersistentKeysEnabled);

internal sealed class ArchiveBrowserIndex
{
    private static readonly IComparer<ArchiveEntryDescriptor> EntryComparer =
        Comparer<ArchiveEntryDescriptor>.Create(CompareEntries);

    private readonly Dictionary<EntryId, ArchiveEntryDescriptor> _byId;
    private readonly Dictionary<EntryId, IReadOnlyList<ArchiveEntryDescriptor>> _childrenByParent;
    private readonly IReadOnlyList<ArchiveEntryDescriptor> _rootEntries;
    private readonly Dictionary<ArchiveEntryKey, ArchiveEntryDescriptor> _byPersistentKey;

    private ArchiveBrowserIndex(
        Dictionary<EntryId, ArchiveEntryDescriptor> byId,
        Dictionary<EntryId, IReadOnlyList<ArchiveEntryDescriptor>> childrenByParent,
        IReadOnlyList<ArchiveEntryDescriptor> rootEntries,
        Dictionary<ArchiveEntryKey, ArchiveEntryDescriptor> byPersistentKey)
    {
        _byId = byId;
        _childrenByParent = childrenByParent;
        _rootEntries = rootEntries;
        _byPersistentKey = byPersistentKey;
    }

    public static ArchiveBrowserIndex Create(
        ArchiveDescriptor archive,
        IReadOnlyList<ArchiveEntryDescriptor> entries,
        bool persistentKeysEnabled)
    {
        if (archive.EntryCount < 0
            || archive.EntryCount > ArchiveBrowserViewModel.MaximumIndexedEntries
            || entries.Count != archive.EntryCount)
        {
            throw new InvalidDataException("The archive entry index does not match its bounded descriptor count.");
        }

        var byId = new Dictionary<EntryId, ArchiveEntryDescriptor>(entries.Count);
        var childBuilders = new Dictionary<EntryId, List<ArchiveEntryDescriptor>>();
        var roots = new List<ArchiveEntryDescriptor>();
        var byPersistentKey = new Dictionary<ArchiveEntryKey, ArchiveEntryDescriptor>();
        foreach (var entry in entries)
        {
            if (entry is null
                || entry.EntryId.Value == Guid.Empty
                || entry.ParentEntryId?.Value == Guid.Empty
                || string.IsNullOrEmpty(entry.RawPath)
                || string.IsNullOrEmpty(entry.DisplayName)
                || !Enum.IsDefined(entry.Kind)
                || !Enum.IsDefined(entry.SafetyStatus)
                || entry.OriginalSize is < 0
                || entry.CompressedSize is < 0
                || entry.DuplicateOrdinal < 0
                || !byId.TryAdd(entry.EntryId, entry))
            {
                throw new InvalidDataException("The archive contains invalid or duplicate entry metadata.");
            }

            if (persistentKeysEnabled)
            {
                if (string.IsNullOrEmpty(entry.NormalizedPath)
                    || entry.NormalizedPath.Length > ArchivePathRules.MaximumNormalizedPathLength
                    || !byPersistentKey.TryAdd(
                        new ArchiveEntryKey(entry.NormalizedPath, entry.DuplicateOrdinal),
                        entry))
                {
                    throw new InvalidDataException("The archive contains invalid or duplicate persistent entry keys.");
                }
            }
            else if (entry.NormalizedPath is not null || entry.DuplicateOrdinal != 0)
            {
                throw new InvalidDataException("The archive returned persistent entry keys without negotiating them.");
            }
        }

        foreach (var entry in entries)
        {
            if (entry.ParentEntryId is not { } parentId)
            {
                roots.Add(entry);
                continue;
            }

            if (!byId.TryGetValue(parentId, out var parent)
                || parent.Kind != ArchiveEntryKind.Directory
                || parent.EntryId == entry.EntryId)
            {
                throw new InvalidDataException("The archive contains an invalid entry hierarchy.");
            }

            if (!childBuilders.TryGetValue(parentId, out var children))
            {
                children = [];
                childBuilders.Add(parentId, children);
            }

            children.Add(entry);
        }

        ValidateAcyclicDepth(byId);
        roots.Sort(EntryComparer);
        var childrenByParent = new Dictionary<EntryId, IReadOnlyList<ArchiveEntryDescriptor>>(
            childBuilders.Count);
        foreach (var (parent, children) in childBuilders)
        {
            children.Sort(EntryComparer);
            childrenByParent.Add(parent, children);
        }

        return new ArchiveBrowserIndex(byId, childrenByParent, roots, byPersistentKey);
    }

    public bool TryGet(EntryId entryId, out ArchiveEntryDescriptor entry) =>
        _byId.TryGetValue(entryId, out entry!);

    public IReadOnlyList<ArchiveEntryDescriptor> GetChildren(EntryId? parentId) =>
        parentId is null
            ? _rootEntries
            : _childrenByParent.TryGetValue(parentId.Value, out var entries)
                ? entries
                : [];

    public bool TryGetPersistentKey(EntryId entryId, out ArchiveEntryKey? key)
    {
        key = null;
        if (!_byId.TryGetValue(entryId, out var entry) || entry.NormalizedPath is null)
        {
            return false;
        }

        key = new ArchiveEntryKey(entry.NormalizedPath, entry.DuplicateOrdinal);
        return true;
    }

    public bool TryResolvePersistentKey(
        ArchiveEntryKey key,
        out ArchiveEntryDescriptor entry) =>
        _byPersistentKey.TryGetValue(key, out entry!);

    private static void ValidateAcyclicDepth(
        IReadOnlyDictionary<EntryId, ArchiveEntryDescriptor> byId)
    {
        var states = new Dictionary<EntryId, byte>(byId.Count);
        var depths = new Dictionary<EntryId, int>(byId.Count);
        var path = new List<EntryId>(
            Math.Min(byId.Count, ArchivePathRules.MaximumPathDepth + 1));
        foreach (var entryId in byId.Keys)
        {
            if (depths.ContainsKey(entryId))
            {
                continue;
            }

            path.Clear();
            var current = entryId;
            var resolvedDepth = 0;
            while (true)
            {
                if (depths.TryGetValue(current, out resolvedDepth))
                {
                    break;
                }

                if (states.TryGetValue(current, out var state))
                {
                    if (state == 1)
                    {
                        throw new InvalidDataException("The archive entry hierarchy contains a cycle.");
                    }

                    throw new InvalidDataException("The archive entry hierarchy is inconsistent.");
                }

                states.Add(current, 1);
                path.Add(current);
                if (byId[current].ParentEntryId is not { } parent)
                {
                    resolvedDepth = 0;
                    break;
                }

                current = parent;
            }

            for (var index = path.Count - 1; index >= 0; index--)
            {
                if (resolvedDepth >= ArchivePathRules.MaximumPathDepth)
                {
                    throw new InvalidDataException("The archive entry hierarchy exceeds the browser depth limit.");
                }

                resolvedDepth++;
                var resolvedEntryId = path[index];
                states[resolvedEntryId] = 2;
                depths.Add(resolvedEntryId, resolvedDepth);
            }
        }
    }

    private static int CompareEntries(
        ArchiveEntryDescriptor? left,
        ArchiveEntryDescriptor? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        var result = (right.Kind == ArchiveEntryKind.Directory)
            .CompareTo(left.Kind == ArchiveEntryKind.Directory);
        if (result != 0)
        {
            return result;
        }

        result = StringComparer.OrdinalIgnoreCase.Compare(left.DisplayName, right.DisplayName);
        if (result != 0)
        {
            return result;
        }

        result = StringComparer.Ordinal.Compare(left.DisplayName, right.DisplayName);
        if (result != 0)
        {
            return result;
        }

        result = StringComparer.Ordinal.Compare(left.NormalizedPath, right.NormalizedPath);
        if (result != 0)
        {
            return result;
        }

        result = left.DuplicateOrdinal.CompareTo(right.DuplicateOrdinal);
        if (result != 0)
        {
            return result;
        }

        result = left.ProviderOrdinal.CompareTo(right.ProviderOrdinal);
        return result != 0
            ? result
            : left.EntryId.Value.CompareTo(right.EntryId.Value);
    }
}
