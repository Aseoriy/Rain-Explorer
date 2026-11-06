using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using RainArchives.App.Services;
using RainArchives.Contracts;

namespace RainArchives.App.Views;

public partial class CreateArchiveDialog : Window
{
    private readonly bool _canCreate;
    private readonly ZipPresetStore? _presetStore;
    private readonly GetZipWriteCapabilitiesResponse? _zipCapabilities;
    private readonly AppUiSettings _uiSettings;
    private readonly string? _settingsStatus;

    public CreateArchiveDialog()
        : this(supportsDirectoryCreation: true)
    {
    }

    public CreateArchiveDialog(bool supportsDirectoryCreation)
        : this(supportsDirectoryCreation, presetStore: null, capabilities: null)
    {
    }

    public CreateArchiveDialog(
        bool supportsDirectoryCreation,
        ZipPresetStore? presetStore,
        GetZipWriteCapabilitiesResponse? capabilities,
        GetTarWriteCapabilitiesResponse? tarCapabilities = null,
        GetManagedWriteCapabilitiesResponse? managedCapabilities = null,
        AppUiSettings? uiSettings = null,
        string? settingsStatus = null)
    {
        _zipCapabilities = capabilities;
        _uiSettings = uiSettings ?? AppUiSettings.Default;
        _settingsStatus = settingsStatus;
        _canCreate = CreationFormatOptionFactory.Load(
            supportsDirectoryCreation,
            capabilities,
            tarCapabilities,
            managedCapabilities).Any(option => option.IsAvailable);
        _presetStore = presetStore;
        InitializeComponent();
        SourcesGrid.ItemsSource = Sources;
        var formatOptions = CreationFormatOptionFactory.Load(
            supportsDirectoryCreation,
            capabilities,
            tarCapabilities,
            managedCapabilities);
        FormatInput.ItemsSource = new ObservableCollection<CreationFormatOption>(formatOptions);
        var availableFormats = formatOptions
            .Where(option => option.IsAvailable)
            .Select(option => option.Value)
            .ToArray();
        var preferredFormat = availableFormats.Length == 0
            ? formatOptions.FirstOrDefault()?.Value
            : ManagedCreationDefaultResolver.ResolveFormat(_uiSettings, availableFormats);
        FormatInput.SelectedItem = formatOptions.FirstOrDefault(option => option.Value == preferredFormat)
            ?? formatOptions.FirstOrDefault();
        ApplySelectedFormat();
        if (!_canCreate)
        {
            ValidationMessage.Text = "The connected worker does not advertise a supported archive creation format for this protocol minor; this action is unavailable.";
        }
        if (!supportsDirectoryCreation && SelectedFormat?.Value == ArchiveCreationFormat.Zip)
        {
            AddFolderButton.IsEnabled = false;
            AutomationProperties.SetHelpText(
                AddFolderButton,
                "The connected archive worker supports file-based ZIP creation but not folder input.");
            LimitationsText.Text =
                "This connected worker can create a new, unencrypted ZIP from files only. "
                + "Folder input, overwrite, update, split volumes, and encryption are unavailable.";
        }

        if (!_canCreate)
        {
            AddFilesButton.IsEnabled = false;
            AddFolderButton.IsEnabled = false;
            BrowseDestinationButton.IsEnabled = false;
            CreateArchiveButton.IsEnabled = false;
        }

        Loaded += (_, _) => AddFilesButton.Focus();
        PresetInput.SelectionChanged += (_, _) => UpdateReview();
        UpdateReview();
    }

    public ObservableCollection<ArchiveCreationSourceItem> Sources { get; } = [];

    public ArchiveCreationSelection? Selection { get; private set; }

    private CreationFormatOption? SelectedFormat => FormatInput.SelectedItem as CreationFormatOption;

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Add files to {SelectedFormat?.Name ?? "archive"}",
            CheckFileExists = true,
            Multiselect = true,
            Filter = "All files|*.*",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _ = TryAddFiles(dialog.FileNames);
    }

    public bool TryAddFiles(IReadOnlyList<string> sourcePaths)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        if (!_canCreate)
        {
            ValidationMessage.Text = "The connected worker does not support ZIP creation for this protocol minor.";
            return false;
        }
        if (sourcePaths.Count == 0)
        {
            ValidationMessage.Text = "Choose at least one file to add.";
            return false;
        }

        var sourceKeys = new HashSet<string>(
            Sources.Select(source => source.SourcePath),
            StringComparer.OrdinalIgnoreCase);
        var archiveRootKeys = new HashSet<string>(
            Sources.Select(source => NormalizeArchiveRootName(source.ArchivePath)),
            StringComparer.OrdinalIgnoreCase);
        var additions = new List<ArchiveCreationSourceItem>(sourcePaths.Count);
        var conflicts = new List<string>();
        foreach (var sourcePath in sourcePaths)
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(sourcePath);
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                conflicts.Add("invalid path");
                continue;
            }

            var archivePath = Path.GetFileName(fullPath);
            if (string.IsNullOrEmpty(archivePath)
                || !sourceKeys.Add(fullPath)
                || !archiveRootKeys.Add(NormalizeArchiveRootName(archivePath)))
            {
                conflicts.Add(string.IsNullOrEmpty(archivePath) ? "unnamed file" : archivePath);
                continue;
            }

            additions.Add(new ArchiveCreationSourceItem(
                ArchiveCreationSourceKind.File,
                fullPath,
                archivePath));
        }

        if (conflicts.Count > 0)
        {
            ValidationMessage.Text =
                $"Nothing was added because these selected archive names conflict with an existing or selected input: {FormatConflictNames(conflicts)}. "
                + "Rename the sources or remove the conflicting input, then try again.";
            return false;
        }

        foreach (var addition in additions)
        {
            Sources.Add(addition);
        }

        ValidationMessage.Text = string.Empty;
        UpdateReview();
        return true;
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var format = SelectedFormat;
        if (format?.SupportsDirectories != true)
        {
            ValidationMessage.Text =
                "The selected archive format does not support secure folder creation.";
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = $"Add folder to {format.Name} archive",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var fullPath = Path.GetFullPath(dialog.FolderName);
        var archivePath = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath));
        if (string.IsNullOrEmpty(archivePath))
        {
            ValidationMessage.Text = "Choose a folder below a drive or share root so it has a safe archive name.";
            return;
        }

        if (HasSource(ArchiveCreationSourceKind.Directory, fullPath))
        {
            ValidationMessage.Text = "That folder is already in the archive input list.";
            return;
        }

        if (HasOrdinaryArchiveRootCollision(archivePath))
        {
            ValidationMessage.Text =
                $"Another input already uses the root archive name {FormatDisplayName(archivePath)}. "
                + "Remove it before adding this folder.";
            return;
        }

        Sources.Add(new ArchiveCreationSourceItem(
            ArchiveCreationSourceKind.Directory,
            fullPath,
            archivePath));
        ValidationMessage.Text = string.Empty;
        UpdateReview();
    }

    private void BrowseDestination_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = $"Choose a new {SelectedFormat?.Name ?? "archive"}",
            AddExtension = true,
            CheckPathExists = true,
            CreatePrompt = false,
            DefaultExt = SelectedFormat?.Extension ?? ".zip",
            Filter = $"{SelectedFormat?.Name ?? "Archive"}|*{SelectedFormat?.Extension ?? ".zip"}",
            FilterIndex = 1,
            FileName = GetSuggestedFileName(),
            OverwritePrompt = false,
        };
        ApplyExistingDestination(dialog);
        if (dialog.ShowDialog(this) == true)
        {
            DestinationInput.Text = dialog.FileName;
        }
    }

    private void Remove_Click(object sender, RoutedEventArgs e) => RemoveSelectedSources();

    private void SourcesGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete)
        {
            return;
        }

        RemoveSelectedSources();
        e.Handled = true;
    }

    private void SourcesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RemoveButton.IsEnabled = SourcesGrid.SelectedItems.Count > 0;

    private void DestinationInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        ValidationMessage.Text = string.Empty;
        UpdateReview();
    }

    private void PresetInput_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ValidationMessage.Text = string.Empty;

    private void FormatInput_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplySelectedFormat();
        ValidationMessage.Text = string.Empty;
    }

    private void ApplySelectedFormat()
    {
        var format = SelectedFormat;
        if (format is null)
        {
            return;
        }

        var zipPresetLoad = format.Value == ArchiveCreationFormat.Zip
            ? ZipPresetOptionFactory.Load(_presetStore, _zipCapabilities)
            : null;
        var options = zipPresetLoad?.Options
            ?? CreationFormatOptionFactory.LoadPresets(
                format,
                _presetStore,
                _zipCapabilities);
        PresetInput.ItemsSource = new ObservableCollection<CompressionPresetOption>(options);
        var preferredPreset = options.Count == 0
            ? ArchiveCompressionPreset.Balanced
            : ManagedCreationDefaultResolver.ResolvePreset(
                _uiSettings,
                format.Value,
                options.Select(option => option.Value).Distinct().ToArray());
        PresetInput.SelectedItem = options.FirstOrDefault(option =>
            option.Value == preferredPreset)
            ?? options.FirstOrDefault();
        ApplyPresetMutationAvailability(zipPresetLoad);
        AddFolderButton.IsEnabled = _canCreate && format.SupportsDirectories;
        Title = $"Create {format.Name} archive";
        HeaderText.Text = $"Create a new {format.Name} archive";
        DestinationLabel.Text = $"New {format.Name} archive destination";
        AutomationProperties.SetHelpText(
            AddFilesButton,
            $"Choose one or more files to place at the root of the new {format.Name} archive.");
        AutomationProperties.SetHelpText(
            AddFolderButton,
            $"Choose a folder to include recursively in the new {format.Name} archive. Rain performs the actual bounded enumeration in the archive worker.");
        AutomationProperties.SetHelpText(
            SourcesGrid,
            $"Files and folders that will be placed in the new {format.Name} archive.");
        AutomationProperties.SetName(
            DestinationInput,
            $"New {format.Name} archive destination");
        DestinationInput.SetValue(
            AutomationProperties.HelpTextProperty,
            $"Enter a fully qualified path ending in {format.Extension}. Existing destinations are never overwritten.");
        AutomationProperties.SetName(
            BrowseDestinationButton,
            $"Browse for {format.Name} destination");
        AutomationProperties.SetName(PresetInput, $"{format.Name} compression preset");
        AutomationProperties.SetHelpText(
            PresetInput,
            $"Choose an advertised {format.Name} compression preset: {string.Join(", ", options.Select(option => option.Name))}.");
        AutomationProperties.SetName(PresetNameInput, $"New {format.Name} preset name");
        AutomationProperties.SetName(
            CancelSetupButton,
            $"Cancel {format.Name} creation setup");
        AutomationProperties.SetName(
            CreateArchiveButton,
            $"Create {format.Name} archive");
        CreateArchiveButton.Content = $"_Create {format.Name}";
        LimitationsText.Text = format.SupportsDirectories
            ? $"This creates a distinct new, unencrypted {format.Name}. Every source remains untouched. Overwrite, replacement, update, split volumes, and encryption are unavailable."
            : $"This creates a distinct new, unencrypted {format.Name} from exactly one ordinary file. The source remains untouched. Overwrite, replacement, update, split volumes, and encryption are unavailable.";
        PresetWarningText.Text = zipPresetLoad?.Warning ?? _settingsStatus ?? string.Empty;
        UpdateReview();
    }

    private void SavePreset_Click(object sender, RoutedEventArgs e) =>
        TrySaveUserPreset(PresetNameInput.Text);

    private void RemovePreset_Click(object sender, RoutedEventArgs e) =>
        TryRemoveSelectedUserPreset();

    public bool TrySaveUserPreset(string name)
    {
        if (SelectedFormat?.Value != ArchiveCreationFormat.Zip
            || _presetStore is null
            || PresetInput.SelectedItem is not CompressionPresetOption selected)
        {
            ValidationMessage.Text = "Preset storage is unavailable until a connected settings store is provided.";
            return false;
        }

        var loaded = RefreshPresetOptionsForMutation(selected);
        if (!loaded.CanMutate)
        {
            ValidationMessage.Text = loaded.Warning ?? ZipPresetOptionFactory.ResetRequiredWarning;
            return false;
        }

        try
        {
            var preset = ZipPreset.CreateUser(
                name,
                selected.Value,
                selected.Preset?.MetadataDefaults);
            var currentUsers = loaded.Options
                .Where(option => option.Preset?.IsBuiltIn == false)
                .Select(option => option.Preset!)
                .Where(option => !string.Equals(option.Name, preset.Name, StringComparison.OrdinalIgnoreCase))
                .Append(preset)
                .ToArray();
            var result = _presetStore.Save(currentUsers);
            if (!result.Succeeded)
            {
                PresetWarningText.Text = result.Warning ?? "ZIP preset save failed; built-ins remain available.";
                ValidationMessage.Text = PresetWarningText.Text;
                return false;
            }

            ReloadPresetOptions(preset.Name);
            ValidationMessage.Text = string.Empty;
            return true;
        }
        catch (ArgumentException)
        {
            ValidationMessage.Text = "The ZIP preset name or metadata defaults are invalid.";
            return false;
        }
    }

    public bool TryRemoveSelectedUserPreset()
    {
        if (SelectedFormat?.Value != ArchiveCreationFormat.Zip
            || _presetStore is null || PresetInput.SelectedItem is not CompressionPresetOption selected
            || selected.Preset?.IsBuiltIn != false)
        {
            ValidationMessage.Text = "Select a user preset to remove; built-in presets are immutable.";
            return false;
        }

        var loaded = RefreshPresetOptionsForMutation(selected);
        if (!loaded.CanMutate)
        {
            ValidationMessage.Text = loaded.Warning ?? ZipPresetOptionFactory.ResetRequiredWarning;
            return false;
        }

        var currentSelection = loaded.Options.FirstOrDefault(option =>
            option.Preset?.IsBuiltIn == false
            && string.Equals(option.Name, selected.Name, StringComparison.OrdinalIgnoreCase));
        if (currentSelection is null)
        {
            ValidationMessage.Text = "The selected user preset is no longer present; reload before removing it.";
            return false;
        }

        var users = loaded.Options
            .Where(option => option.Preset?.IsBuiltIn == false
                && !string.Equals(option.Name, currentSelection.Name, StringComparison.OrdinalIgnoreCase))
            .Select(option => option.Preset!)
            .ToArray();
        var result = _presetStore.Save(users);
        if (!result.Succeeded)
        {
            PresetWarningText.Text = result.Warning ?? "ZIP preset save failed; built-ins remain available.";
            ValidationMessage.Text = PresetWarningText.Text;
            return false;
        }

        ReloadPresetOptions(ArchiveCompressionPreset.Balanced.ToString());
        ValidationMessage.Text = string.Empty;
        return true;
    }

    private void ReloadPresetOptions(string selectedName)
    {
        var loaded = ZipPresetOptionFactory.Load(_presetStore, _zipCapabilities);
        ApplyLoadedPresetOptions(loaded, selectedName, selectedValue: null);
    }

    private ZipPresetOptionSet RefreshPresetOptionsForMutation(CompressionPresetOption selected)
    {
        var loaded = ZipPresetOptionFactory.Load(_presetStore, _zipCapabilities);
        ApplyLoadedPresetOptions(loaded, selected.Name, selected.Value);
        return loaded;
    }

    private void ApplyLoadedPresetOptions(
        ZipPresetOptionSet loaded,
        string? selectedName,
        ArchiveCompressionPreset? selectedValue)
    {
        var options = new ObservableCollection<CompressionPresetOption>(loaded.Options);
        PresetInput.ItemsSource = options;
        PresetInput.SelectedItem = options.FirstOrDefault(option =>
            string.Equals(option.Name, selectedName, StringComparison.OrdinalIgnoreCase))
            ?? options.FirstOrDefault(option => option.Value == selectedValue && option.Preset?.IsBuiltIn != false)
            ?? options.FirstOrDefault(option => option.Value == ArchiveCompressionPreset.Balanced);
        PresetWarningText.Text = loaded.Warning ?? string.Empty;
        ApplyPresetMutationAvailability(loaded);
    }

    private void ApplyPresetMutationAvailability(ZipPresetOptionSet? loaded)
    {
        var enabled = SelectedFormat?.Value == ArchiveCreationFormat.Zip
            && _presetStore is not null
            && loaded?.CanMutate == true;
        SavePresetButton.IsEnabled = enabled;
        RemovePresetButton.IsEnabled = enabled;
        PresetNameInput.IsEnabled = enabled;
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildSelection(out var selection, out var validationMessage))
        {
            ValidationMessage.Text = validationMessage;
            return;
        }

        Selection = selection;
        DialogResult = true;
    }

    private bool TryBuildSelection(
        out ArchiveCreationSelection? selection,
        out string validationMessage)
    {
        selection = null;
        if (!_canCreate)
        {
            validationMessage = "The connected worker does not support archive creation for this protocol minor.";
            return false;
        }
        if (Sources.Count == 0)
        {
            validationMessage = "Add at least one file or folder before creating the archive.";
            return false;
        }

        var format = SelectedFormat;
        if (format is null || !format.IsAvailable)
        {
            validationMessage = "Choose an archive format supported by the connected worker.";
            return false;
        }

        var maximumEntries = format.ManagedCapability?.MaximumEntries
            ?? format.TarCapability?.MaximumEntries
            ?? ProtocolConstants.MaximumZipCreationEntries;
        if (Sources.Count > maximumEntries)
        {
            validationMessage = $"This {format.Name} creation slice accepts at most {maximumEntries:N0} input roots.";
            return false;
        }

        if (!format.SupportsDirectories
            && Sources.Any(source => source.Kind == ArchiveCreationSourceKind.Directory))
        {
            validationMessage =
                $"The selected {format.Name} format does not support secure folder creation.";
            return false;
        }

        if (format.Value is ArchiveCreationFormat.GZip or ArchiveCreationFormat.BZip2
            && Sources.Count != 1)
        {
            validationMessage = $"{format.Name} creation requires exactly one ordinary file.";
            return false;
        }

        if (PresetInput.SelectedItem is not CompressionPresetOption preset)
        {
            validationMessage = "Choose a compression preset.";
            return false;
        }

        string destinationPath;
        try
        {
            destinationPath = Path.GetFullPath(DestinationInput.Text.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            validationMessage = "Choose a valid, fully qualified destination path.";
            return false;
        }

        if (!Path.IsPathFullyQualified(destinationPath)
            || !destinationPath.EndsWith(format.Extension, StringComparison.OrdinalIgnoreCase))
        {
            validationMessage = $"Choose a fully qualified destination ending in {format.Extension}.";
            return false;
        }

        if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
        {
            validationMessage = "The destination must be new and absent. Existing or raced files and folders are preserved and never overwritten.";
            return false;
        }

        selection = new ArchiveCreationSelection(
            destinationPath,
            Sources
                .Where(source => source.Kind == ArchiveCreationSourceKind.File)
                .Select(source => new ArchiveCreationFile(source.SourcePath, source.ArchivePath))
                .ToArray(),
            Sources
                .Where(source => source.Kind == ArchiveCreationSourceKind.Directory)
                .Select(source => new ArchiveCreationDirectory(source.SourcePath, source.ArchivePath))
                .ToArray(),
            preset.Value,
            format.Value);
        validationMessage = string.Empty;
        return true;
    }

    private bool HasSource(ArchiveCreationSourceKind kind, string sourcePath) =>
        Sources.Any(source =>
            source.Kind == kind
            && string.Equals(source.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase));

    private bool HasOrdinaryArchiveRootCollision(string archivePath)
    {
        var normalized = NormalizeArchiveRootName(archivePath);
        return Sources.Any(source => string.Equals(
            NormalizeArchiveRootName(source.ArchivePath),
            normalized,
            StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeArchiveRootName(string archivePath) =>
        archivePath.Normalize(NormalizationForm.FormC);

    private static string FormatConflictNames(IReadOnlyList<string> conflicts)
    {
        var distinct = conflicts
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var displayed = distinct
            .Take(4)
            .Select(FormatDisplayName)
            .ToArray();
        var suffix = distinct.Length > 4
            ? $", and {distinct.Length - 4:N0} more"
            : string.Empty;
        return string.Join(", ", displayed) + suffix;
    }

    private static string FormatDisplayName(string name)
    {
        const int maximumCharacters = 80;
        var escaped = UntrustedDisplayText.Escape(name);
        if (escaped.Length > maximumCharacters)
        {
            escaped = escaped[..(maximumCharacters - 1)] + "…";
        }

        return $"‘{escaped}’";
    }

    private void RemoveSelectedSources()
    {
        foreach (var source in SourcesGrid.SelectedItems
                     .OfType<ArchiveCreationSourceItem>()
                     .ToArray())
        {
            Sources.Remove(source);
        }

        ValidationMessage.Text = string.Empty;
        UpdateReview();
    }

    private string GetSuggestedFileName()
    {
        if (Sources.Count != 1)
        {
            return $"Archive{SelectedFormat?.Extension ?? ".zip"}";
        }

        var source = Sources[0];
        var baseName = source.Kind == ArchiveCreationSourceKind.File
            ? Path.GetFileNameWithoutExtension(source.ArchivePath)
            : source.ArchivePath;
        var extension = SelectedFormat?.Extension ?? ".zip";
        return string.IsNullOrWhiteSpace(baseName) ? $"Archive{extension}" : $"{baseName}{extension}";
    }

    private void UpdateReview()
    {
        if (!IsInitialized)
        {
            return;
        }

        var format = SelectedFormat;
        var preset = PresetInput.SelectedItem as CompressionPresetOption;
        var fileCount = Sources.Count(source => source.Kind == ArchiveCreationSourceKind.File);
        var folderCount = Sources.Count(source => source.Kind == ArchiveCreationSourceKind.Directory);
        ReviewSummaryText.Text = format is null
            ? "Choose an advertised managed format to complete the review."
            : $"Final review: {format.Name}; {preset?.Name ?? "no preset"}; {fileCount:N0} file input(s) and {folderCount:N0} folder input(s).";
        DestinationPolicyText.Text =
            "The final destination must be fully qualified, distinct, and absent. Existing or raced targets are preserved. Rain opens the output only after authoritative Worker success.";
    }

    private void ApplyExistingDestination(SaveFileDialog dialog)
    {
        var current = DestinationInput.Text.Trim();
        if (string.IsNullOrEmpty(current))
        {
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(current);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            {
                dialog.InitialDirectory = directory;
                dialog.FileName = Path.GetFileName(fullPath);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // The Create action owns user-facing validation for manually entered paths.
        }
    }

}

public enum ArchiveCreationSourceKind
{
    File,
    Directory,
}

public sealed record ArchiveCreationSourceItem(
    ArchiveCreationSourceKind Kind,
    string SourcePath,
    string ArchivePath);

public sealed record CompressionPresetOption(
    ArchiveCompressionPreset Value,
    string Name,
    string Description,
    ZipPreset? Preset = null);
