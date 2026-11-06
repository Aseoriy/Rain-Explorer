using System.IO;
using System.Windows;
using Microsoft.Win32;
using RainArchives.App.Infrastructure;
using RainArchives.App.Views;
using RainArchives.App.ViewModels;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.App.Services;

public sealed class AppDialogService : IAppDialogService
{
    private readonly Window _owner;
    private readonly ZipPresetStore _presetStore = new();
    private readonly AppSettingsStore _settingsStore = new();
    private readonly Func<IArchiveClient, Window, IArchiveRecoveryDialogSession>
        _archiveRecoveryDialogFactory;
    private readonly Func<CancellationToken, Task> _beforeRecoveryHandoffAsync;
    private readonly AppThemeManager? _themeManager;
    private ActivityCenterWindow? _activityCenterWindow;

    public AppDialogService(
        Window owner,
        ZipPresetStore? presetStore = null,
        AppSettingsStore? settingsStore = null)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _presetStore = presetStore ?? new ZipPresetStore();
        _settingsStore = settingsStore ?? new AppSettingsStore();
        _themeManager = (Application.Current as RainArchives.App.App)?.ThemeManager;
        _themeManager?.Initialize();
        var themeManager = _themeManager;
        _archiveRecoveryDialogFactory = (client, dialogOwner) =>
            new WpfArchiveRecoveryDialogSession(client, dialogOwner, themeManager);
        _beforeRecoveryHandoffAsync = static _ => Task.CompletedTask;
    }

    internal AppDialogService(
        Window owner,
        Func<IArchiveClient, Window, IArchiveRecoveryDialogSession> archiveRecoveryDialogFactory,
        Func<CancellationToken, Task>? beforeRecoveryHandoffAsync = null)
        : this(owner)
    {
        _archiveRecoveryDialogFactory = archiveRecoveryDialogFactory
            ?? throw new ArgumentNullException(nameof(archiveRecoveryDialogFactory));
        _beforeRecoveryHandoffAsync = beforeRecoveryHandoffAsync
            ?? (static _ => Task.CompletedTask);
    }

    public string? ChooseArchive()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open archive",
            CheckFileExists = true,
            Multiselect = false,
            Filter = "Archives|*.zip;*.7z;*.rar;*.tar;*.gz;*.tgz;*.bz2;*.tbz2;*.xz;*.txz|All files|*.*",
        };
        return dialog.ShowDialog(_owner) == true ? dialog.FileName : null;
    }

    public ArchiveCreationSelection? ChooseArchiveCreation(bool supportsDirectoryCreation)
    {
        var settings = LoadUiSettings();
        var dialog = new CreateArchiveDialog(
            supportsDirectoryCreation,
            _presetStore,
            capabilities: null,
            uiSettings: settings.Settings,
            settingsStatus: GetSettingsNotice(settings))
        { Owner = _owner };
        AttachWindowChrome(dialog);
        return dialog.ShowDialog() == true ? dialog.Selection : null;
    }

    public ArchiveCreationSelection? ChooseArchiveCreation(
        bool supportsDirectoryCreation,
        GetZipWriteCapabilitiesResponse capabilities)
    {
        var settings = LoadUiSettings();
        var dialog = new CreateArchiveDialog(
            supportsDirectoryCreation,
            _presetStore,
            capabilities,
            uiSettings: settings.Settings,
            settingsStatus: GetSettingsNotice(settings))
        { Owner = _owner };
        AttachWindowChrome(dialog);
        return dialog.ShowDialog() == true ? dialog.Selection : null;
    }

    public ArchiveCreationSelection? ChooseArchiveCreation(
        bool supportsDirectoryCreation,
        GetZipWriteCapabilitiesResponse? zipCapabilities,
        GetManagedWriteCapabilitiesResponse? managedCapabilities)
    {
        var settings = LoadUiSettings();
        var dialog = new CreateArchiveDialog(
            supportsDirectoryCreation,
            _presetStore,
            zipCapabilities,
            tarCapabilities: null,
            managedCapabilities: managedCapabilities,
            uiSettings: settings.Settings,
            settingsStatus: GetSettingsNotice(settings))
        { Owner = _owner };
        AttachWindowChrome(dialog);
        return dialog.ShowDialog() == true ? dialog.Selection : null;
    }

    public ArchiveCreationSelection? ChooseArchiveCreation(
        bool supportsDirectoryCreation,
        GetZipWriteCapabilitiesResponse? zipCapabilities,
        GetTarWriteCapabilitiesResponse? tarCapabilities,
        GetManagedWriteCapabilitiesResponse? managedCapabilities)
    {
        var settings = LoadUiSettings();
        var dialog = new CreateArchiveDialog(
            supportsDirectoryCreation,
            _presetStore,
            zipCapabilities,
            tarCapabilities,
            managedCapabilities,
            settings.Settings,
            GetSettingsNotice(settings))
        { Owner = _owner };
        AttachWindowChrome(dialog);
        return dialog.ShowDialog() == true ? dialog.Selection : null;
    }

    public ArchiveRebuildCopySelection? ChooseArchiveRebuildCopy(
        string sourcePath,
        IReadOnlyList<ArchiveEntryDescriptor> entries)
    {
        var settings = LoadUiSettings();
        var dialog = new RebuildArchiveDialog(
            sourcePath,
            entries,
            [],
            client: null,
            sessionId: null,
            presetStore: _presetStore,
            capabilities: null,
            uiSettings: settings.Settings,
            settingsStatus: GetSettingsNotice(settings))
        { Owner = _owner };
        AttachWindowChrome(dialog);
        return dialog.ShowDialog() == true ? dialog.Selection : null;
    }

    public ArchiveRebuildCopySelection? ChooseArchiveRebuildCopy(
        string sourcePath,
        IReadOnlyList<ArchiveEntryDescriptor> entries,
        IReadOnlyList<ZipRebuildTreePlanNode> treePlan)
    {
        var settings = LoadUiSettings();
        var dialog = new RebuildArchiveDialog(
            sourcePath,
            entries,
            treePlan,
            client: null,
            sessionId: null,
            presetStore: _presetStore,
            capabilities: null,
            uiSettings: settings.Settings,
            settingsStatus: GetSettingsNotice(settings))
        { Owner = _owner };
        AttachWindowChrome(dialog);
        return dialog.ShowDialog() == true ? dialog.Selection : null;
    }

    public ArchiveRebuildCopySelection? ChooseArchiveRebuildCopy(
        IArchiveClient client,
        ArchiveSessionId sessionId,
        string sourcePath,
        IReadOnlyList<ArchiveEntryDescriptor> entries,
        IReadOnlyList<ZipRebuildTreePlanNode> treePlan)
    {
        var settings = LoadUiSettings();
        var dialog = new RebuildArchiveDialog(
            sourcePath,
            entries,
            treePlan,
            client,
            sessionId,
            _presetStore,
            capabilities: null,
            uiSettings: settings.Settings,
            settingsStatus: GetSettingsNotice(settings))
        {
            Owner = _owner,
        };
        AttachWindowChrome(dialog);
        return dialog.ShowDialog() == true ? dialog.Selection : null;
    }

    public ArchiveRebuildCopySelection? ChooseArchiveRebuildCopy(
        IArchiveClient client,
        ArchiveSessionId sessionId,
        string sourcePath,
        IReadOnlyList<ArchiveEntryDescriptor> entries,
        IReadOnlyList<ZipRebuildTreePlanNode> treePlan,
        GetZipWriteCapabilitiesResponse capabilities)
    {
        var settings = LoadUiSettings();
        var dialog = new RebuildArchiveDialog(
            sourcePath,
            entries,
            treePlan,
            client,
            sessionId,
            _presetStore,
            capabilities,
            settings.Settings,
            GetSettingsNotice(settings))
        {
            Owner = _owner,
        };
        AttachWindowChrome(dialog);
        return dialog.ShowDialog() == true ? dialog.Selection : null;
    }

    public string? ChooseNewExtractionDestination(string archivePath, bool selectedEntriesOnly)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose where to create the extracted folder",
            Multiselect = false,
        };
        if (dialog.ShowDialog(_owner) != true)
        {
            return null;
        }

        var baseName = selectedEntriesOnly
            ? "Extracted files"
            : GetArchiveBaseName(Path.GetFileName(archivePath));
        var candidate = Path.Combine(dialog.FolderName, baseName);
        for (var suffix = 2; File.Exists(candidate) || Directory.Exists(candidate); suffix++)
        {
            candidate = Path.Combine(dialog.FolderName, $"{baseName} ({suffix})");
        }

        return candidate;
    }

    public Task<char[]?> RequestPasswordAsync(string archiveName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dialog = new PasswordDialog(archiveName) { Owner = _owner };
        AttachWindowChrome(dialog);
        using var cancellationRegistration = cancellationToken.Register(
            static state => ((PasswordDialog)state!).RequestCancellation(),
            dialog);
        try
        {
            var accepted = dialog.ShowDialog() == true;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<char[]?>(accepted ? dialog.TakePassword() : null);
        }
        finally
        {
            dialog.ClearCapturedPassword();
        }
    }

    public async Task ShowArchiveRecoveryAsync(
        IArchiveClient client,
        CancellationToken cancellationToken)
    {
        var unclaimed = await ShowArchiveRecoveryWithResultAsync(client, cancellationToken)
            .ConfigureAwait(true);
        if (unclaimed is null)
        {
            return;
        }

        await TryCloseRecoveredArchiveAsync(client, unclaimed.Response.Archive.SessionId)
            .ConfigureAwait(true);
    }

    public async Task<RecoveredArchiveDialogResult?> ShowArchiveRecoveryWithResultAsync(
        IArchiveClient client,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        cancellationToken.ThrowIfCancellationRequested();
        var dialog = _archiveRecoveryDialogFactory(client, _owner);
        using var cancellationRegistration = cancellationToken.Register(
            static state => ((IArchiveRecoveryDialogSession)state!).RequestCancellation(),
            dialog);
        ReopenNestedWorkspaceRecoveryResponse? ownedArchive = null;
        var ownershipTransferred = false;
        try
        {
            try
            {
                _ = dialog.ShowDialog();
            }
            finally
            {
                await dialog.DrainAsync().ConfigureAwait(true);
            }

            ownedArchive = dialog.ReopenedArchive;
            await _beforeRecoveryHandoffAsync(cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            if (ownedArchive is null)
            {
                return null;
            }

            var result = new RecoveredArchiveDialogResult(
                ownedArchive,
                dialog.ReopenedWithPassword);
            ownershipTransferred = true;
            return result;
        }
        finally
        {
            ownedArchive ??= dialog.ReopenedArchive;
            if (!ownershipTransferred && ownedArchive is not null)
            {
                await TryCloseRecoveredArchiveAsync(client, ownedArchive.Archive.SessionId)
                    .ConfigureAwait(true);
            }
        }
    }

    private static async Task TryCloseRecoveredArchiveAsync(
        IArchiveClient client,
        ArchiveSessionId sessionId)
    {
        using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            _ = await client.CloseArchiveAsync(sessionId, closeTimeout.Token).ConfigureAwait(true);
        }
        catch (Exception exception) when (
            exception is ArchiveClientException or OperationCanceledException or ObjectDisposedException)
        {
            // Client disposal remains the bounded containment fallback.
        }
    }

    public async Task ShowSettingsAsync(
        IArchiveClient? client,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var viewModel = new SettingsCenterViewModel(
            _settingsStore,
            _presetStore,
            token => SettingsCapabilitySnapshot.LoadAsync(client, token));
        var dialog = new SettingsCenterWindow(viewModel) { Owner = _owner };
        AttachWindowChrome(dialog);
        using var cancellationRegistration = cancellationToken.Register(
            static state => ((SettingsCenterWindow)state!).RequestCancellation(),
            dialog);
        try
        {
            _ = dialog.ShowDialog();
        }
        finally
        {
            await dialog.DrainAsync().ConfigureAwait(true);
            await viewModel.DisposeAsync().ConfigureAwait(true);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    public void ShowActivityCenter(ActivityCenterViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (_activityCenterWindow is { IsLoaded: true } existing)
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }

            _ = existing.Activate();
            return;
        }

        var window = new ActivityCenterWindow(viewModel)
        {
            Owner = _owner,
        };
        _activityCenterWindow = window;
        AttachWindowChrome(window);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activityCenterWindow, window))
            {
                _activityCenterWindow = null;
            }
        };
        window.Show();
    }

    public void ShowError(string title, string message) =>
        MessageBox.Show(_owner, message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    private void AttachWindowChrome(Window window) =>
        _themeManager?.AttachWindowChrome(window);

    private sealed class WpfArchiveRecoveryDialogSession : IArchiveRecoveryDialogSession
    {
        private readonly ArchiveRecoveryDialog _dialog;

        public WpfArchiveRecoveryDialogSession(
            IArchiveClient client,
            Window owner,
            AppThemeManager? themeManager)
        {
            _dialog = new ArchiveRecoveryDialog(client) { Owner = owner };
            themeManager?.AttachWindowChrome(_dialog);
        }

        public ReopenNestedWorkspaceRecoveryResponse? ReopenedArchive => _dialog.ReopenedArchive;

        public bool ReopenedWithPassword => _dialog.ReopenedWithPassword;

        public bool? ShowDialog() => _dialog.ShowDialog();

        public void RequestCancellation() => _dialog.RequestCancellation();

        public ValueTask DrainAsync() => _dialog.DrainAsync();
    }

    public void ShowInformation(string title, string message) =>
        MessageBox.Show(_owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    private static string GetArchiveBaseName(string fileName)
    {
        foreach (var extension in new[] { ".tar.gz", ".tar.bz2", ".tar.xz", ".tgz", ".tbz2", ".txz" })
        {
            if (fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return fileName[..^extension.Length];
            }
        }

        return Path.GetFileNameWithoutExtension(fileName);
    }

    private AppSettingsLoadResult LoadUiSettings() => _settingsStore.Load();

    private static string? GetSettingsNotice(AppSettingsLoadResult settings) =>
        settings.State is AppSettingsLoadState.Ready or AppSettingsLoadState.Defaults
            ? null
            : settings.Status;
}

internal interface IArchiveRecoveryDialogSession
{
    ReopenNestedWorkspaceRecoveryResponse? ReopenedArchive { get; }

    bool ReopenedWithPassword { get; }

    bool? ShowDialog();

    void RequestCancellation();

    ValueTask DrainAsync();
}
