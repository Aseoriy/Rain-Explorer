using System.Collections.ObjectModel;
using System.Globalization;
using RainArchives.App.Infrastructure;
using RainArchives.App.Services;
using RainArchives.Contracts;

namespace RainArchives.App.ViewModels;

public enum SettingsCenterPersistenceState
{
    Loading,
    Ready,
    Defaults,
    Recovered,
    Corrupt,
    Unsupported,
    Unsafe,
    Unavailable,
    Saving,
    Saved,
    Reset,
    Error,
    Closed,
}

public sealed record SettingsPresetOption(
    ArchiveCompressionPreset Value,
    string Name);

public sealed class ManagedFormatPreferenceViewModel : ObservableObject
{
    private readonly Action _changed;
    private SettingsPresetOption? _selectedPreset;
    private bool _isSupported;
    private string _availabilityText = "Capabilities have not been loaded.";
    private string _constraintText = string.Empty;

    internal ManagedFormatPreferenceViewModel(
        ArchiveCreationFormat format,
        string name,
        Action changed)
    {
        Format = format;
        Name = name;
        _changed = changed;
    }

    public ArchiveCreationFormat Format { get; }
    public string Name { get; }
    public ObservableCollection<SettingsPresetOption> Presets { get; } = [];

    public SettingsPresetOption? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (SetProperty(ref _selectedPreset, value))
            {
                _changed();
            }
        }
    }

    public bool IsSupported
    {
        get => _isSupported;
        private set => SetProperty(ref _isSupported, value);
    }

    public string AvailabilityText
    {
        get => _availabilityText;
        private set => SetProperty(ref _availabilityText, value);
    }

    public string ConstraintText
    {
        get => _constraintText;
        private set => SetProperty(ref _constraintText, value);
    }

    internal ArchiveCompressionPreset CurrentPreset =>
        SelectedPreset?.Value ?? AppUiSettings.Default.ManagedCreation.GetPreset(Format);

    internal void Apply(
        bool isSupported,
        IReadOnlyList<ArchiveCompressionPreset> advertisedPresets,
        ArchiveCompressionPreset preferredPreset,
        string availabilityText,
        string constraintText)
    {
        IsSupported = isSupported;
        AvailabilityText = availabilityText;
        ConstraintText = constraintText;
        var values = advertisedPresets.Count == 0
            ? new[] { preferredPreset }
            : advertisedPresets.Distinct().ToArray();
        Presets.Clear();
        foreach (var value in values)
        {
            Presets.Add(new SettingsPresetOption(value, value.ToString()));
        }

        var resolved = values.Contains(preferredPreset)
            ? preferredPreset
            : values.Contains(ArchiveCompressionPreset.Balanced)
                ? ArchiveCompressionPreset.Balanced
                : values[0];
        SelectedPreset = Presets.First(option => option.Value == resolved);
    }
}

public sealed class ZipPresetSettingsItem : ObservableObject
{
    private bool _isSelected;

    public ZipPresetSettingsItem(ZipPreset preset)
    {
        Preset = preset ?? throw new ArgumentNullException(nameof(preset));
    }

    public ZipPreset Preset { get; }
    public string Name => Preset.Name;
    public string Compression => Preset.CompressionPreset.ToString();
    public string MetadataSummary => Preset.MetadataDefaults.LastWriteTime is null
        && Preset.MetadataDefaults.AttributesLowWord is null
        ? "No metadata defaults"
        : "Bounded mapped-entry metadata defaults";

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>
/// App-only Settings/Presets model. Capability reads use IArchiveClient and all
/// disk work remains confined to the two App-private settings stores.
/// </summary>
public sealed class SettingsCenterViewModel : ObservableObject, IAsyncDisposable
{
    private readonly AppSettingsStore _settingsStore;
    private readonly ZipPresetStore _presetStore;
    private readonly Func<CancellationToken, Task<SettingsCapabilitySnapshot>> _capabilityLoader;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _workGate = new();
    private Task _activeWork = Task.CompletedTask;
    private AppUiSettings _loadedSettings = AppUiSettings.Default;
    private SettingsCenterPersistenceState _persistenceState = SettingsCenterPersistenceState.Loading;
    private SettingsCapabilityState _capabilityState = SettingsCapabilityState.Loading;
    private string _persistenceStatus = "Loading bounded local UI settings.";
    private string _capabilityStatus = SettingsCapabilitySnapshot.Loading.Status;
    private string _presetStatus = "Loading bounded ZIP presets.";
    private string _statusAnnouncement = "Settings Center is loading.";
    private ManagedFormatPreferenceViewModel? _selectedDefaultFormat;
    private ZipPresetSettingsItem? _selectedUserPreset;
    private string _presetName = string.Empty;
    private SettingsPresetOption _presetCompression;
    private SettingsPresetOption _zipRebuildPreset;
    private string _presetTime = string.Empty;
    private string _presetAttributes = string.Empty;
    private bool _showAdvancedRebuildOptions;
    private bool _isWorking;
    private bool _hasUnsavedChanges;
    private bool _appSettingsMaySave;
    private bool _presetSettingsMaySave;
    private bool _applying;
    private bool _initialized;
    private volatile bool _disposed;
    private long _capabilityGeneration;

    public SettingsCenterViewModel(
        AppSettingsStore settingsStore,
        ZipPresetStore presetStore,
        Func<CancellationToken, Task<SettingsCapabilitySnapshot>> capabilityLoader)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _presetStore = presetStore ?? throw new ArgumentNullException(nameof(presetStore));
        _capabilityLoader = capabilityLoader ?? throw new ArgumentNullException(nameof(capabilityLoader));
        _presetCompression = CompressionPresets[2];
        _zipRebuildPreset = CompressionPresets[2];
        foreach (var (format, name) in new[]
                 {
                     (ArchiveCreationFormat.Zip, "ZIP"),
                     (ArchiveCreationFormat.Tar, "TAR"),
                     (ArchiveCreationFormat.TarGZip, "TAR.GZ"),
                     (ArchiveCreationFormat.TarBZip2, "TAR.BZ2"),
                     (ArchiveCreationFormat.GZip, "GZIP"),
                     (ArchiveCreationFormat.BZip2, "BZIP2"),
                 })
        {
            Formats.Add(new ManagedFormatPreferenceViewModel(format, name, MarkChanged));
        }

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsWorking && !_disposed);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => CanSave);
        ResetCommand = new AsyncRelayCommand(ResetAsync, () => !IsWorking && !_disposed);
        AddOrUpdatePresetCommand = new AsyncRelayCommand(
            AddOrUpdatePresetAsync,
            () => CanEditPresets && !IsWorking);
        RemovePresetCommand = new AsyncRelayCommand(
            RemoveSelectedPresetAsync,
            () => CanEditPresets && SelectedUserPreset is not null && !IsWorking);
    }

    public ObservableCollection<ManagedFormatPreferenceViewModel> Formats { get; } = [];
    public ObservableCollection<ZipPresetSettingsItem> UserPresets { get; } = [];
    public ObservableCollection<SettingsPresetOption> ZipRebuildPresets { get; } = [];
    public IReadOnlyList<SettingsPresetOption> CompressionPresets { get; } =
        Enum.GetValues<ArchiveCompressionPreset>()
            .Select(value => new SettingsPresetOption(value, value.ToString()))
            .ToArray();

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand ResetCommand { get; }
    public AsyncRelayCommand AddOrUpdatePresetCommand { get; }
    public AsyncRelayCommand RemovePresetCommand { get; }

    public SettingsCenterPersistenceState PersistenceState
    {
        get => _persistenceState;
        private set => SetProperty(ref _persistenceState, value);
    }

    public SettingsCapabilityState CapabilityState
    {
        get => _capabilityState;
        private set
        {
            if (SetProperty(ref _capabilityState, value))
            {
                OnPropertyChanged(nameof(CanEditDefaults));
            }
        }
    }

    public string PersistenceStatus
    {
        get => _persistenceStatus;
        private set => SetProperty(ref _persistenceStatus, value);
    }

    public string CapabilityStatus
    {
        get => _capabilityStatus;
        private set => SetProperty(ref _capabilityStatus, value);
    }

    public string PresetStatus
    {
        get => _presetStatus;
        private set => SetProperty(ref _presetStatus, value);
    }

    public string StatusAnnouncement
    {
        get => _statusAnnouncement;
        private set => SetProperty(ref _statusAnnouncement, value);
    }

    public ManagedFormatPreferenceViewModel? SelectedDefaultFormat
    {
        get => _selectedDefaultFormat;
        set
        {
            if (!_applying
                && CanEditDefaults
                && value?.IsSupported != true)
            {
                StatusAnnouncement = "Choose a format advertised by the current authenticated Worker.";
                OnPropertyChanged();
                return;
            }

            if (SetProperty(ref _selectedDefaultFormat, value))
            {
                MarkChanged();
            }
        }
    }

    public ZipPresetSettingsItem? SelectedUserPreset
    {
        get => _selectedUserPreset;
        set
        {
            if (!SetProperty(ref _selectedUserPreset, value))
            {
                return;
            }

            foreach (var item in UserPresets)
            {
                item.IsSelected = ReferenceEquals(item, value);
            }

            if (value is not null)
            {
                LoadPresetEditor(value.Preset);
            }

            RemovePresetCommand.NotifyCanExecuteChanged();
        }
    }

    public string PresetName
    {
        get => _presetName;
        set => SetProperty(ref _presetName, value);
    }

    public SettingsPresetOption PresetCompression
    {
        get => _presetCompression;
        set => SetProperty(ref _presetCompression, value);
    }

    public SettingsPresetOption ZipRebuildPreset
    {
        get => _zipRebuildPreset;
        set
        {
            if (SetProperty(ref _zipRebuildPreset, value))
            {
                MarkChanged();
            }
        }
    }

    public string PresetTime
    {
        get => _presetTime;
        set => SetProperty(ref _presetTime, value);
    }

    public string PresetAttributes
    {
        get => _presetAttributes;
        set => SetProperty(ref _presetAttributes, value);
    }

    public bool ShowAdvancedRebuildOptions
    {
        get => _showAdvancedRebuildOptions;
        set
        {
            if (SetProperty(ref _showAdvancedRebuildOptions, value))
            {
                MarkChanged();
            }
        }
    }

    public bool IsWorking
    {
        get => _isWorking;
        private set
        {
            if (SetProperty(ref _isWorking, value))
            {
                OnPropertyChanged(nameof(CanSave));
                OnPropertyChanged(nameof(CanEditDefaults));
                OnPropertyChanged(nameof(CanEditPresets));
                OnPropertyChanged(nameof(CanEditLocalPreferences));
                NotifyCommands();
            }
        }
    }

    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        private set
        {
            if (SetProperty(ref _hasUnsavedChanges, value))
            {
                OnPropertyChanged(nameof(CanSave));
                SaveCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool CanEditDefaults =>
        !_disposed
        && !IsWorking
        && CapabilityState == SettingsCapabilityState.Ready;

    public bool CanEditPresets =>
        !_disposed
        && !IsWorking
        && _presetSettingsMaySave;

    public bool CanEditLocalPreferences =>
        !_disposed
        && !IsWorking;

    public bool CanSave =>
        !_disposed
        && !IsWorking
        && HasUnsavedChanges
        && _appSettingsMaySave
        && _presetSettingsMaySave;

    public Task InitializeAsync() => RunTrackedAsync(InitializeCoreAsync);

    public Task RefreshAsync() => RunTrackedAsync(async cancellationToken =>
    {
        await RefreshCapabilitiesCoreAsync(cancellationToken).ConfigureAwait(true);
        StatusAnnouncement = "Worker capability status refreshed.";
    });

    public Task SaveAsync() => RunTrackedAsync(SaveCoreAsync);

    public Task ResetAsync() => RunTrackedAsync(ResetCoreAsync);

    public void RequestCancellation()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A close callback may race the final lifetime-source disposal.
        }
    }

    public async Task DrainAsync()
    {
        Task active;
        lock (_workGate)
        {
            active = _activeWork;
        }

        try
        {
            await active.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Cancellation settlement is the expected close path.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Increment(ref _capabilityGeneration);
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await DrainAsync().ConfigureAwait(false);
        _operationGate.Dispose();
        _lifetime.Dispose();
        CapabilityState = SettingsCapabilityState.Closed;
        PersistenceState = SettingsCenterPersistenceState.Closed;
        StatusAnnouncement = "Settings Center closed.";
        NotifyCommands();
    }

    public bool AddOrUpdatePreset()
    {
        if (!CanEditPresets)
        {
            StatusAnnouncement = "Preset storage must be reset or become available before it can be changed.";
            return false;
        }

        try
        {
            var defaults = ParsePresetMetadata();
            var preset = ZipPreset.CreateUser(PresetName, PresetCompression.Value, defaults);
            var existing = UserPresets.FirstOrDefault(item => string.Equals(
                item.Name,
                preset.Name,
                StringComparison.OrdinalIgnoreCase));
            if (existing is null && UserPresets.Count >= ZipPresetStore.MaximumUserPresets)
            {
                StatusAnnouncement = $"A maximum of {ZipPresetStore.MaximumUserPresets} user ZIP presets is supported.";
                return false;
            }

            var replacement = new ZipPresetSettingsItem(preset);
            if (existing is null)
            {
                UserPresets.Add(replacement);
            }
            else
            {
                var index = UserPresets.IndexOf(existing);
                UserPresets[index] = replacement;
            }

            SelectedUserPreset = replacement;
            HasUnsavedChanges = true;
            StatusAnnouncement = "The bounded ZIP preset is ready to save.";
            return true;
        }
        catch (ArgumentException)
        {
            StatusAnnouncement = "The ZIP preset name or metadata defaults are invalid. Use a bounded name, an exact even-second DOS timestamp, and supported attributes only.";
            return false;
        }
    }

    public bool RemoveSelectedPreset()
    {
        var selected = SelectedUserPreset;
        if (!CanEditPresets || selected is null)
        {
            StatusAnnouncement = "Select a user ZIP preset that can be removed.";
            return false;
        }

        _ = UserPresets.Remove(selected);
        SelectedUserPreset = null;
        PresetName = string.Empty;
        PresetTime = string.Empty;
        PresetAttributes = string.Empty;
        HasUnsavedChanges = true;
        StatusAnnouncement = "The user ZIP preset will be removed when settings are saved.";
        return true;
    }

    internal AppUiSettings BuildCurrentSettings()
    {
        var defaultFormat = SelectedDefaultFormat?.Format
            ?? _loadedSettings.ManagedCreation.DefaultFormat;
        var settings = new AppUiSettings(
            new ManagedCreationDefaults(
                defaultFormat,
                FindFormat(ArchiveCreationFormat.Zip).CurrentPreset,
                FindFormat(ArchiveCreationFormat.Tar).CurrentPreset,
                FindFormat(ArchiveCreationFormat.TarGZip).CurrentPreset,
                FindFormat(ArchiveCreationFormat.TarBZip2).CurrentPreset,
                FindFormat(ArchiveCreationFormat.GZip).CurrentPreset,
                FindFormat(ArchiveCreationFormat.BZip2).CurrentPreset,
                ZipRebuildPreset.Value),
            ShowAdvancedRebuildOptions);
        return AppSettingsStore.Validate(settings);
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        PersistenceState = SettingsCenterPersistenceState.Loading;
        StatusAnnouncement = "Loading bounded local settings and authenticated capabilities.";
        var settingsTask = Task.Run(() => _settingsStore.Load(cancellationToken), cancellationToken);
        var presetsTask = Task.Run(() => _presetStore.Load(cancellationToken), cancellationToken);
        var settings = await settingsTask.ConfigureAwait(true);
        var presets = await presetsTask.ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        ApplyLocalSettings(settings, presets);
        await RefreshCapabilitiesCoreAsync(cancellationToken).ConfigureAwait(true);
        HasUnsavedChanges = false;
        StatusAnnouncement = "Settings Center is ready.";
    }

    private async Task RefreshCapabilitiesCoreAsync(CancellationToken cancellationToken)
    {
        var generation = Interlocked.Increment(ref _capabilityGeneration);
        CapabilityState = SettingsCapabilityState.Loading;
        CapabilityStatus = SettingsCapabilitySnapshot.Loading.Status;
        var snapshot = await _capabilityLoader(cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed || generation != Volatile.Read(ref _capabilityGeneration))
        {
            return;
        }

        ApplyCapabilities(snapshot);
    }

    private async Task SaveCoreAsync(CancellationToken cancellationToken)
    {
        if (!HasUnsavedChanges || !_appSettingsMaySave || !_presetSettingsMaySave)
        {
            StatusAnnouncement = "Settings cannot be saved until the local documents are usable or explicitly reset.";
            return;
        }

        await RefreshCapabilitiesCoreAsync(cancellationToken).ConfigureAwait(true);
        if (CapabilityState == SettingsCapabilityState.Ready
            && SelectedDefaultFormat?.IsSupported != true)
        {
            PersistenceState = SettingsCenterPersistenceState.Error;
            StatusAnnouncement = "Choose a format advertised by the current authenticated Worker before saving defaults.";
            return;
        }

        PersistenceState = SettingsCenterPersistenceState.Saving;
        StatusAnnouncement = "Saving bounded local settings.";
        var settings = BuildCurrentSettings();
        var userPresets = UserPresets.Select(item => item.Preset).ToArray();
        var presetResult = await Task.Run(
            () => _presetStore.Save(userPresets, cancellationToken),
            cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        var settingsResult = await Task.Run(
            () => _settingsStore.Save(settings, cancellationToken),
            cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        if (!presetResult.Succeeded || !settingsResult.Succeeded)
        {
            PersistenceState = settingsResult.State == AppSettingsSaveState.UnsafeStorage
                ? SettingsCenterPersistenceState.Unsafe
                : SettingsCenterPersistenceState.Error;
            PersistenceStatus = settingsResult.Status;
            PresetStatus = presetResult.Warning
                ?? "ZIP presets were saved, but another local settings document was not changed.";
            StatusAnnouncement = "Some local settings were not changed. Reload or use explicit Reset after reviewing the status.";
            return;
        }

        _loadedSettings = settings;
        _appSettingsMaySave = true;
        _presetSettingsMaySave = true;
        PersistenceState = SettingsCenterPersistenceState.Saved;
        PersistenceStatus = settingsResult.Status;
        PresetStatus = "User ZIP presets were saved atomically.";
        HasUnsavedChanges = false;
        StatusAnnouncement = "Settings and presets were saved.";
    }

    private async Task ResetCoreAsync(CancellationToken cancellationToken)
    {
        PersistenceState = SettingsCenterPersistenceState.Saving;
        StatusAnnouncement = "Resetting UI defaults and user ZIP presets.";
        var presetResult = await Task.Run(
            () => _presetStore.Reset(cancellationToken),
            cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        var settingsResult = await Task.Run(
            () => _settingsStore.Reset(cancellationToken),
            cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        if (!presetResult.Succeeded || !settingsResult.Succeeded)
        {
            PersistenceState = settingsResult.State == AppSettingsSaveState.UnsafeStorage
                ? SettingsCenterPersistenceState.Unsafe
                : SettingsCenterPersistenceState.Error;
            PersistenceStatus = settingsResult.Status;
            PresetStatus = presetResult.Warning ?? "User ZIP presets were reset.";
            StatusAnnouncement = "Reset did not finish for every independent settings document.";
            return;
        }

        ApplyLocalSettings(
            new AppSettingsLoadResult(
                AppUiSettings.Default,
                AppSettingsLoadState.Ready,
                false,
                "UI settings were reset to bounded defaults."),
            new ZipPresetLoadResult(
                ZipPresetStore.BuiltIns,
                null,
                ZipPresetLoadState.Ready));
        await RefreshCapabilitiesCoreAsync(cancellationToken).ConfigureAwait(true);
        PersistenceState = SettingsCenterPersistenceState.Reset;
        PersistenceStatus = "UI settings were reset to bounded defaults.";
        PresetStatus = "User ZIP presets were removed; immutable built-ins remain available.";
        HasUnsavedChanges = false;
        StatusAnnouncement = "Settings and user presets were reset.";
    }

    private void ApplyLocalSettings(
        AppSettingsLoadResult settings,
        ZipPresetLoadResult presets)
    {
        _applying = true;
        try
        {
            _loadedSettings = settings.Settings;
            ShowAdvancedRebuildOptions = settings.Settings.ShowAdvancedRebuildOptions;
            ZipRebuildPreset = CompressionPresets.First(option =>
                option.Value == settings.Settings.ManagedCreation.ZipRebuildPreset);
            PersistenceState = settings.State switch
            {
                AppSettingsLoadState.Ready => SettingsCenterPersistenceState.Ready,
                AppSettingsLoadState.Defaults => SettingsCenterPersistenceState.Defaults,
                AppSettingsLoadState.RecoveredFromBackup => SettingsCenterPersistenceState.Recovered,
                AppSettingsLoadState.Corrupt => SettingsCenterPersistenceState.Corrupt,
                AppSettingsLoadState.UnsupportedVersion => SettingsCenterPersistenceState.Unsupported,
                AppSettingsLoadState.UnsafeStorage => SettingsCenterPersistenceState.Unsafe,
                AppSettingsLoadState.Unavailable => SettingsCenterPersistenceState.Unavailable,
                _ => SettingsCenterPersistenceState.Error,
            };
            PersistenceStatus = settings.Status;
            _appSettingsMaySave = settings.State is AppSettingsLoadState.Ready
                or AppSettingsLoadState.Defaults;

            UserPresets.Clear();
            foreach (var preset in presets.Presets.Where(item => !item.IsBuiltIn))
            {
                UserPresets.Add(new ZipPresetSettingsItem(preset));
            }

            _presetSettingsMaySave = presets.State is ZipPresetLoadState.Ready
                or ZipPresetLoadState.Missing;
            PresetStatus = presets.State switch
            {
                ZipPresetLoadState.Ready => "User ZIP presets are loaded.",
                ZipPresetLoadState.Missing => "No user ZIP preset document exists; immutable built-ins remain available.",
                _ => "The ZIP preset document is unavailable or invalid. Use explicit Reset to replace it; ordinary Save is disabled.",
            };
            SelectedUserPreset = null;
        }
        finally
        {
            _applying = false;
        }

        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanEditPresets));
        NotifyCommands();
    }

    private void ApplyCapabilities(SettingsCapabilitySnapshot snapshot)
    {
        var preferredSettings = HasUnsavedChanges
            ? BuildCurrentSettings()
            : _loadedSettings;
        _applying = true;
        try
        {
            CapabilityState = snapshot.State;
            CapabilityStatus = snapshot.Status;
            foreach (var format in Formats)
            {
                ApplyFormatCapability(format, snapshot, preferredSettings);
            }

            var rebuildPresets = snapshot.State == SettingsCapabilityState.Ready
                && snapshot.Zip?.CompressionPresets.Count > 0
                ? snapshot.Zip.CompressionPresets
                : new[] { preferredSettings.ManagedCreation.ZipRebuildPreset };
            var rebuildPreset = ManagedCreationDefaultResolver.ResolveZipRebuildPreset(
                preferredSettings,
                rebuildPresets);
            ZipRebuildPresets.Clear();
            foreach (var preset in rebuildPresets.Distinct())
            {
                ZipRebuildPresets.Add(CompressionPresets.First(option => option.Value == preset));
            }

            ZipRebuildPreset = ZipRebuildPresets.First(option => option.Value == rebuildPreset);

            var supported = Formats.Where(format => format.IsSupported).ToArray();
            SelectedDefaultFormat = supported.FirstOrDefault(format =>
                    format.Format == preferredSettings.ManagedCreation.DefaultFormat)
                ?? supported.FirstOrDefault()
                ?? Formats.First(format => format.Format == preferredSettings.ManagedCreation.DefaultFormat);
        }
        finally
        {
            _applying = false;
        }

        OnPropertyChanged(nameof(CanEditDefaults));
        OnPropertyChanged(nameof(CanEditLocalPreferences));
        NotifyCommands();
    }

    private void ApplyFormatCapability(
        ManagedFormatPreferenceViewModel format,
        SettingsCapabilitySnapshot snapshot,
        AppUiSettings preferredSettings)
    {
        var preferred = preferredSettings.ManagedCreation.GetPreset(format.Format);
        if (snapshot.State != SettingsCapabilityState.Ready)
        {
            format.Apply(
                false,
                [preferred],
                preferred,
                "Unavailable until authenticated capabilities are ready.",
                GetStaticConstraintText(format.Format));
            return;
        }

        if (format.Format == ArchiveCreationFormat.Zip)
        {
            var capability = snapshot.Zip;
            var supported = capability?.CanCreate == true && capability.CompressionPresets.Count > 0;
            format.Apply(
                supported,
                capability?.CompressionPresets ?? [preferred],
                preferred,
                supported ? "Advertised by the current Worker." : "Not advertised for creation by the current Worker.",
                capability is null
                    ? GetStaticConstraintText(format.Format)
                    : $"Up to {capability.MaximumEntries:N0} input roots and {capability.MaximumSourceBytes:N0} source bytes. Folder support is checked again in Create.");
            return;
        }

        if (format.Format == ArchiveCreationFormat.Tar)
        {
            var capability = snapshot.Tar;
            var supported = capability?.CanCreate == true && capability.CompressionPresets.Count > 0;
            format.Apply(
                supported,
                capability?.CompressionPresets ?? [preferred],
                preferred,
                supported ? "Advertised by the current Worker." : "Not advertised for creation by the current Worker.",
                capability is null
                    ? GetStaticConstraintText(format.Format)
                    : $"Up to {capability.MaximumEntries:N0} input roots and {capability.MaximumSourceBytes:N0} source bytes; folder input: {(capability.SupportsTreeCreation ? "available" : "unavailable")}.");
            return;
        }

        var managedFormat = format.Format switch
        {
            ArchiveCreationFormat.TarGZip => ManagedArchiveCreationFormat.TarGZip,
            ArchiveCreationFormat.TarBZip2 => ManagedArchiveCreationFormat.TarBZip2,
            ArchiveCreationFormat.GZip => ManagedArchiveCreationFormat.GZip,
            ArchiveCreationFormat.BZip2 => ManagedArchiveCreationFormat.BZip2,
            _ => throw new InvalidOperationException("The managed format is unsupported."),
        };
        var managed = snapshot.Managed?.Capabilities.FirstOrDefault(item => item.Format == managedFormat);
        var isSupported = managed?.CanCreate == true && managed.CompressionPresets.Count > 0;
        format.Apply(
            isSupported,
            managed?.CompressionPresets ?? [preferred],
            preferred,
            isSupported ? "Advertised by the current Worker." : "Not advertised for creation by the current Worker.",
            managed is null
                ? GetStaticConstraintText(format.Format)
                : format.Format is ArchiveCreationFormat.GZip or ArchiveCreationFormat.BZip2
                    ? $"Exactly one ordinary file; no folders. Maximum source size {managed.MaximumSourceBytes:N0} bytes."
                    : $"Up to {managed.MaximumEntries:N0} input roots and {managed.MaximumSourceBytes:N0} source bytes; folder input: {(managed.SupportsTreeCreation ? "available" : "unavailable")}.");
    }

    private static string GetStaticConstraintText(ArchiveCreationFormat format) => format switch
    {
        ArchiveCreationFormat.GZip or ArchiveCreationFormat.BZip2 =>
            "Exactly one ordinary file is required; folder input is unavailable.",
        ArchiveCreationFormat.Tar =>
            "Uncompressed TAR uses Store; source and folder limits are checked against current capabilities.",
        _ => "Source count, byte, and folder limits are checked against current capabilities before use.",
    };

    private ZipPresetMetadataDefaults ParsePresetMetadata()
    {
        ArchiveDosLocalTimestamp? timestamp = null;
        ushort? attributes = null;
        var time = PresetTime.Trim();
        if (time.Length > 0)
        {
            if (!ArchiveMetadataInput.TryParseTimestamp(time, out timestamp!))
            {
                throw new ArgumentException("The preset time is invalid.");
            }
        }

        var attributeText = PresetAttributes.Trim();
        if (attributeText.Length > 0)
        {
            if (!ArchiveMetadataInput.TryParseAttributes(attributeText, out var parsed))
            {
                throw new ArgumentException("The preset attributes are invalid.");
            }

            attributes = parsed;
        }

        return new ZipPresetMetadataDefaults(timestamp, attributes);
    }

    private void LoadPresetEditor(ZipPreset preset)
    {
        PresetName = preset.Name;
        PresetCompression = CompressionPresets.First(option => option.Value == preset.CompressionPreset);
        var time = preset.MetadataDefaults.LastWriteTime;
        PresetTime = time is null
            ? string.Empty
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{time.Year:0000}-{time.Month:00}-{time.Day:00}T{time.Hour:00}:{time.Minute:00}:{time.Second:00}");
        PresetAttributes = preset.MetadataDefaults.AttributesLowWord is { } attributes
            ? $"0x{attributes:X4}"
            : string.Empty;
    }

    private ManagedFormatPreferenceViewModel FindFormat(ArchiveCreationFormat format) =>
        Formats.Single(item => item.Format == format);

    private Task AddOrUpdatePresetAsync()
    {
        _ = AddOrUpdatePreset();
        return Task.CompletedTask;
    }

    private Task RemoveSelectedPresetAsync()
    {
        _ = RemoveSelectedPreset();
        return Task.CompletedTask;
    }

    private Task RunTrackedAsync(Func<CancellationToken, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_workGate)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            if (!_activeWork.IsCompleted)
            {
                return _activeWork;
            }

            _activeWork = RunExclusiveAsync(action);
            return _activeWork;
        }
    }

    private async Task RunExclusiveAsync(Func<CancellationToken, Task> action)
    {
        await _operationGate.WaitAsync(_lifetime.Token).ConfigureAwait(true);
        IsWorking = true;
        try
        {
            await action(_lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            StatusAnnouncement = "Settings work was cancelled and settled.";
        }
        catch (Exception)
        {
            PersistenceState = SettingsCenterPersistenceState.Error;
            PersistenceStatus = "Settings work could not be completed safely. No exception or provider text was retained.";
            StatusAnnouncement = PersistenceStatus;
        }
        finally
        {
            IsWorking = false;
            _operationGate.Release();
        }
    }

    private void MarkChanged()
    {
        if (!_applying && _initialized)
        {
            HasUnsavedChanges = true;
        }
    }

    private void NotifyCommands()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        ResetCommand.NotifyCanExecuteChanged();
        AddOrUpdatePresetCommand.NotifyCanExecuteChanged();
        RemovePresetCommand.NotifyCanExecuteChanged();
    }
}
