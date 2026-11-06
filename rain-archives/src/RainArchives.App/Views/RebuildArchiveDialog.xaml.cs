using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RainArchives.App.Services;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.App.Views;

public partial class RebuildArchiveDialog : Window
{
    private readonly string _sourcePath;
    private readonly string _sourceFullPath;
    private readonly IReadOnlyList<ZipRebuildTreePlanNode> _treePlan;
    private readonly IArchiveClient? _client;
    private readonly ArchiveSessionId? _sessionId;
    private readonly bool _canRebuild;
    private readonly ZipPresetStore? _presetStore;
    private readonly GetZipWriteCapabilitiesResponse? _capabilities;
    private readonly AppUiSettings _uiSettings;
    private readonly string? _settingsStatus;

    public RebuildArchiveDialog(
        string sourcePath,
        IReadOnlyList<ArchiveEntryDescriptor> entries,
        IReadOnlyList<ZipRebuildTreePlanNode>? treePlan = null)
        : this(
            sourcePath,
            entries,
            treePlan,
            client: null,
            sessionId: null,
            presetStore: null,
            capabilities: null,
            uiSettings: null,
            settingsStatus: null)
    {
    }

    public RebuildArchiveDialog(
        string sourcePath,
        IReadOnlyList<ArchiveEntryDescriptor> entries,
        IReadOnlyList<ZipRebuildTreePlanNode>? treePlan,
        IArchiveClient? client,
        ArchiveSessionId? sessionId,
        ZipPresetStore? presetStore = null,
        GetZipWriteCapabilitiesResponse? capabilities = null,
        AppUiSettings? uiSettings = null,
        string? settingsStatus = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(entries);
        _sourcePath = sourcePath;
        _sourceFullPath = Path.GetFullPath(sourcePath);
        _treePlan = treePlan ?? [];
        _client = client;
        _sessionId = sessionId;
        _canRebuild = capabilities?.CanRebuild ?? true;
        _presetStore = presetStore;
        _capabilities = capabilities;
        _uiSettings = uiSettings ?? AppUiSettings.Default;
        _settingsStatus = settingsStatus;
        InitializeComponent();
        EntriesGrid.ItemsSource = PersistentEntries;
        foreach (var entry in entries.Where(entry =>
                     entry.ProviderOrdinal >= 0
                     && entry.NormalizedPath is { Length: > 0 }
                     && entry.Kind is ArchiveEntryKind.File or ArchiveEntryKind.Directory))
        {
            PersistentEntries.Add(new RebuildCopyEntryItem(entry));
        }

        MetadataGrid.ItemsSource = Metadata;
        foreach (var entry in PersistentEntries)
        {
            Metadata.Add(new RebuildMetadataItem(entry));
        }

        FilesGrid.ItemsSource = Files;
        TreePlanGrid.ItemsSource = TreePlan;
        FolderAddsGrid.ItemsSource = FolderAdds;
        foreach (var node in _treePlan)
        {
            TreePlan.Add(new RebuildTreePlanItem(node));
        }
        var presetOptions = ZipPresetOptionFactory.Load(presetStore, capabilities);
        PresetInput.ItemsSource = new ObservableCollection<CompressionPresetOption>(presetOptions.Options);
        var preferredPreset = presetOptions.Options.Count == 0
            ? ArchiveCompressionPreset.Balanced
            : ManagedCreationDefaultResolver.ResolveZipRebuildPreset(
                _uiSettings,
                presetOptions.Options.Select(option => option.Value).Distinct().ToArray());
        PresetInput.SelectedItem = presetOptions.Options.FirstOrDefault(
            option => option.Value == preferredPreset)
            ?? presetOptions.Options.FirstOrDefault();
        PresetWarningText.Text = presetOptions.Warning ?? _settingsStatus ?? string.Empty;
        ApplyPresetMutationAvailability(presetOptions);
        AdvancedMetadataExpander.IsExpanded = _uiSettings.ShowAdvancedRebuildOptions;
        if (!_canRebuild)
        {
            ValidationMessage.Text = "The connected worker does not support ZIP rebuild for this protocol minor; this action is unavailable.";
        }
        DestinationInput.Text = GetSuggestedDestination();
        EntriesGrid.SelectionChanged += EntriesGrid_SelectionChanged;
        FilesGrid.SelectionChanged += FilesGrid_SelectionChanged;
        PresetInput.SelectionChanged += (_, _) => UpdateReview();
        Loaded += (_, _) => EntriesGrid.Focus();
        RefreshButtons();
        if (!_canRebuild)
        {
            AddFilesButton.IsEnabled = false;
            ReplaceFileButton.IsEnabled = false;
            AddFolderButton.IsEnabled = false;
            BrowseDestinationButton.IsEnabled = false;
            RebuildCopyButton.IsEnabled = false;
        }
    }

    public ObservableCollection<RebuildCopyEntryItem> PersistentEntries { get; } = [];
    public ObservableCollection<RebuildCopyFileItem> Files { get; } = [];
    public ObservableCollection<RebuildTreePlanItem> TreePlan { get; } = [];
    public ObservableCollection<RebuildFolderAddItem> FolderAdds { get; } = [];
    public ObservableCollection<RebuildMetadataItem> Metadata { get; } = [];
    public ArchiveRebuildCopySelection? Selection { get; private set; }

    private void SavePreset_Click(object sender, RoutedEventArgs e) =>
        TrySaveUserPreset(PresetNameInput.Text);

    private void RemovePreset_Click(object sender, RoutedEventArgs e) =>
        TryRemoveSelectedUserPreset();

    public bool TrySaveUserPreset(string name)
    {
        if (_presetStore is null || PresetInput.SelectedItem is not CompressionPresetOption selected)
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
            if (!TryParsePresetDefaults(
                    PresetDefaultTimeInput.Text,
                    PresetDefaultAttributesInput.Text,
                    out var defaults,
                    out var defaultsError))
            {
                ValidationMessage.Text = defaultsError;
                return false;
            }

            var preset = ZipPreset.CreateUser(name, selected.Value, defaults);
            var users = loaded.Options
                .Where(option => option.Preset?.IsBuiltIn == false)
                .Select(option => option.Preset!)
                .Where(option => !string.Equals(option.Name, preset.Name, StringComparison.OrdinalIgnoreCase))
                .Append(preset)
                .ToArray();
            var result = _presetStore.Save(users);
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
        if (_presetStore is null || PresetInput.SelectedItem is not CompressionPresetOption selected
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
        var loaded = ZipPresetOptionFactory.Load(_presetStore, _capabilities);
        ApplyLoadedPresetOptions(loaded, selectedName, selectedValue: null);
    }

    private ZipPresetOptionSet RefreshPresetOptionsForMutation(CompressionPresetOption selected)
    {
        var loaded = ZipPresetOptionFactory.Load(_presetStore, _capabilities);
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

    private void ApplyPresetMutationAvailability(ZipPresetOptionSet loaded)
    {
        var enabled = _presetStore is not null && loaded.CanMutate;
        SavePresetButton.IsEnabled = enabled;
        RemovePresetButton.IsEnabled = enabled;
        PresetNameInput.IsEnabled = enabled;
        PresetDefaultTimeInput.IsEnabled = enabled;
        PresetDefaultAttributesInput.IsEnabled = enabled;
    }

    private static bool TryParsePresetDefaults(
        string? timeText,
        string? attributesText,
        out ZipPresetMetadataDefaults defaults,
        out string error)
    {
        ArchiveDosLocalTimestamp? time = null;
        ushort? attributes = null;
        if (!string.IsNullOrWhiteSpace(timeText) && timeText.Trim() != "-")
        {
            if (!ArchiveMetadataInput.TryParseTimestamp(timeText.Trim(), out time))
            {
                defaults = new ZipPresetMetadataDefaults();
                error = "Preset default archive time must use exact YYYY-MM-DDTHH:mm:ss syntax with even seconds.";
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(attributesText) && attributesText.Trim() != "-")
        {
            if (!ArchiveMetadataInput.TryParseAttributes(attributesText.Trim(), out var parsedAttributes))
            {
                defaults = new ZipPresetMetadataDefaults();
                error = "Preset default attributes may contain only ReadOnly, Hidden, System, and Archive bits.";
                return false;
            }

            attributes = parsedAttributes;
        }

        defaults = new ZipPresetMetadataDefaults(time, attributes);
        error = string.Empty;
        return true;
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null || _sessionId is null
            || !_client.SupportsFeature(ProtocolFeatures.ZipRebuildSourceFolderReview))
        {
            ValidationMessage.Text =
                "The connected worker does not support bounded source-folder review.";
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "Choose a folder to add to rebuilt ZIP",
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) == true)
        {
            try
            {
                var source = Path.GetFullPath(dialog.FolderName);
                var root = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrWhiteSpace(root))
                {
                    ValidationMessage.Text = "Choose a named source folder.";
                    return;
                }

                var requestedRoots = FolderAdds
                    .Select(folder => new ArchiveCreationDirectory(folder.SourcePath, folder.ArchiveRoot))
                    .Append(new ArchiveCreationDirectory(source, root + "/"))
                    .ToArray();
                var review = await _client.GetZipRebuildSourceFolderReviewAsync(
                        _sessionId.Value,
                        requestedRoots)
                    .ConfigureAwait(true);
                foreach (var metadata in Metadata.Where(item =>
                             item.Target.Kind == ArchiveRebuildMetadataTargetKind.MappedFinalPath
                             && item.Target.MappedKind == ArchiveEntryKind.Directory).ToArray())
                {
                    Metadata.Remove(metadata);
                }
                FolderAdds.Clear();
                foreach (var item in review.Directories)
                {
                    var folder = new RebuildFolderAddItem(
                        item.SourcePath,
                        item.ArchiveRoot,
                        item.DescendantCount,
                        item.FileCount);
                    FolderAdds.Add(folder);
                    if (!Metadata.Any(candidate =>
                            IsMappedMetadataFor(candidate, item.ArchiveRoot, ArchiveEntryKind.Directory)))
                    {
                        Metadata.Add(RebuildMetadataItem.ForMappedDirectory(folder));
                    }
                }
                ValidationMessage.Text = string.Empty;
            }
            catch (Exception exception) when (exception is ArchiveClientException or ArgumentException or IOException)
            {
                ValidationMessage.Text = exception is ArchiveClientException
                    ? "The authenticated Worker refused the source-folder review. No folder input was added."
                    : "The selected source folder could not be reviewed safely.";
            }
        }
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (FolderAddsGrid.SelectedItem is RebuildFolderAddItem item)
        {
            FolderAdds.Remove(item);
            var metadata = Metadata.FirstOrDefault(candidate =>
                IsMappedMetadataFor(
                    candidate,
                    item.ArchiveRoot.TrimEnd('/', '\\'),
                    ArchiveEntryKind.Directory));
            if (metadata is not null)
            {
                Metadata.Remove(metadata);
            }
        }
    }

    public bool TryAddFiles(IReadOnlyList<string> sourcePaths)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        if (sourcePaths.Count == 0)
        {
            ValidationMessage.Text = "Choose at least one file to add.";
            return false;
        }

        var sourceKeys = new HashSet<string>(
            Files.Select(file => file.SourcePath),
            StringComparer.OrdinalIgnoreCase)
        {
            _sourceFullPath,
        };
        var archiveKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files)
        {
            if (!TryNormalizeArchivePath(file.ArchivePath, out var normalizedArchivePath))
            {
                ValidationMessage.Text =
                    "Fix invalid new or replacement archive paths before adding more files.";
                return false;
            }

            archiveKeys.Add(GetArchiveCollisionKey(normalizedArchivePath));
        }

        foreach (var entry in PersistentEntries)
        {
            if (!TryGetArchiveCollisionKey(entry.TargetPath, out var collisionKey))
            {
                ValidationMessage.Text =
                    "The opened archive contains a path that cannot be represented safely in a rebuilt copy.";
                return false;
            }

            archiveKeys.Add(collisionKey);
        }
        var additions = new List<RebuildCopyFileItem>(sourcePaths.Count);
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
            if (!File.Exists(fullPath)
                || !sourceKeys.Add(fullPath)
                || !TryNormalizeArchivePath(archivePath, out var normalizedArchivePath)
                || !archiveKeys.Add(GetArchiveCollisionKey(normalizedArchivePath)))
            {
                conflicts.Add(string.IsNullOrEmpty(archivePath) ? "unnamed file" : archivePath);
                continue;
            }

            additions.Add(new RebuildCopyFileItem(
                RebuildCopyFileMode.Add,
                fullPath,
                normalizedArchivePath));
        }

        if (conflicts.Count > 0)
        {
            ValidationMessage.Text =
                $"Nothing was added because these source or archive paths conflict: {FormatConflictNames(conflicts)}.";
            return false;
        }

        foreach (var addition in additions)
        {
            Files.Add(addition);
            if (!Metadata.Any(item =>
                    item.Target.Kind == ArchiveRebuildMetadataTargetKind.MappedFinalPath
                    && string.Equals(
                        item.Target.MappedFinalArchivePath,
                        addition.ArchivePath,
                        StringComparison.Ordinal)))
            {
                Metadata.Add(RebuildMetadataItem.ForMappedFile(addition));
            }
        }

        ValidationMessage.Text = string.Empty;
        RefreshButtons();
        return true;
    }

    public bool TryReplaceSelectedFile(string sourcePath)
    {
        if (EntriesGrid.SelectedItem is not RebuildCopyEntryItem entry
            || entry.Kind != ArchiveEntryKind.File)
        {
            ValidationMessage.Text = "Select one persistent file before choosing Replace selected.";
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(sourcePath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ValidationMessage.Text = "Choose a valid replacement file.";
            return false;
        }

        if (string.Equals(fullPath, _sourceFullPath, StringComparison.OrdinalIgnoreCase))
        {
            ValidationMessage.Text = "The original archive cannot also be a replacement source.";
            return false;
        }

        if (!File.Exists(fullPath))
        {
            ValidationMessage.Text = "Choose an existing regular replacement file.";
            return false;
        }

        var existing = Files.FirstOrDefault(file =>
            ArchivePathsMatch(file.ArchivePath, entry.TargetPath));
        if (existing is not null)
        {
            Files.Remove(existing);
            if (existing.IsReplacement)
            {
                RemoveMappedMetadata(existing.ArchivePath, ArchiveEntryKind.File);
            }
        }

        var replacementFile = new RebuildCopyFileItem(
            RebuildCopyFileMode.Replace,
            fullPath,
            entry.TargetPath);
        Files.Add(replacementFile);
        RemoveRetainedMetadata(entry);
        if (!Metadata.Any(item => IsMappedMetadataFor(
                item,
                entry.TargetPath,
                ArchiveEntryKind.File)))
        {
            Metadata.Add(RebuildMetadataItem.ForMappedFile(replacementFile));
        }
        ValidationMessage.Text = string.Empty;
        RefreshButtons();
        return true;
    }

    public bool TryRenameSelectedEntry(string finalArchivePath)
    {
        if (EntriesGrid.SelectedItem is not RebuildCopyEntryItem entry
            || entry.Kind != ArchiveEntryKind.File)
        {
            ValidationMessage.Text = "Select one persistent regular file before entering a rename.";
            return false;
        }

        if (entry.IsMarkedForDeletion)
        {
            ValidationMessage.Text = "A persistent entry cannot be both deleted and renamed.";
            return false;
        }

        if (Files.Any(file =>
                file.IsReplacement
                && ArchivePathsMatch(file.ArchivePath, entry.TargetPath)))
        {
            ValidationMessage.Text = "A persistent entry cannot be both replaced and renamed.";
            return false;
        }

        if (!TryNormalizeFileArchivePath(finalArchivePath, out var normalizedFinalPath))
        {
            ValidationMessage.Text = "Enter a safe final archive-relative file path for the rename.";
            return false;
        }

        entry.RenamePath = normalizedFinalPath;
        ValidationMessage.Text = string.Empty;
        RefreshButtons();
        return true;
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add files to rebuilt ZIP",
            CheckFileExists = true,
            Multiselect = true,
            Filter = "All files|*.*",
        };
        if (dialog.ShowDialog(this) == true)
        {
            _ = TryAddFiles(dialog.FileNames);
        }
    }

    private void ReplaceFile_Click(object sender, RoutedEventArgs e)
    {
        if (EntriesGrid.SelectedItem is not RebuildCopyEntryItem { Kind: ArchiveEntryKind.File })
        {
            ValidationMessage.Text = "Select one persistent file before choosing Replace selected.";
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Choose replacement file",
            CheckFileExists = true,
            Multiselect = false,
            Filter = "All files|*.*",
        };
        if (dialog.ShowDialog(this) == true)
        {
            _ = TryReplaceSelectedFile(dialog.FileName);
        }
    }

    private void RemoveFile_Click(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItem is RebuildCopyFileItem file)
        {
            Files.Remove(file);
            if (file.IsReplacement)
            {
                RemoveMappedMetadata(file.ArchivePath, ArchiveEntryKind.File);
                var retainedEntry = PersistentEntries.FirstOrDefault(entry =>
                    entry.Kind == ArchiveEntryKind.File
                    && ArchivePathsMatch(entry.TargetPath, file.ArchivePath));
                if (retainedEntry is not null)
                {
                    RestoreRetainedMetadata(retainedEntry);
                }
            }
            else
            {
                RemoveMappedMetadata(file.ArchivePath, ArchiveEntryKind.File);
            }
            ValidationMessage.Text = string.Empty;
            RefreshButtons();
        }
    }

    private void EntriesGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e) => RefreshButtons();
    private void FilesGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e) => RefreshButtons();

    private void DestinationInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        ValidationMessage.Text = string.Empty;
        UpdateReview();
    }

    private void BrowseDestination_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Choose a new rebuilt ZIP",
            AddExtension = true,
            CheckPathExists = true,
            DefaultExt = ".zip",
            FileName = Path.GetFileName(DestinationInput.Text.Trim()),
            Filter = "ZIP archive|*.zip",
            OverwritePrompt = false,
        };
        var directory = Path.GetDirectoryName(DestinationInput.Text.Trim());
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            dialog.InitialDirectory = directory;
        }

        if (dialog.ShowDialog(this) == true)
        {
            DestinationInput.Text = dialog.FileName;
        }
    }

    private void RebuildCopy_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBuildSelection(out var selection, out var message))
        {
            ValidationMessage.Text = message;
            return;
        }

        Selection = selection;
        DialogResult = true;
    }

    public bool TryBuildSelection(
        out ArchiveRebuildCopySelection? selection,
        out string validationMessage)
    {
        selection = null;
        if (!_canRebuild)
        {
            return FailValidation("The connected worker does not support ZIP rebuild for this protocol minor.", out validationMessage);
        }
        if (PresetInput.SelectedItem is not CompressionPresetOption preset)
        {
            return FailValidation("Choose a compression preset.", out validationMessage);
        }
        string destination;
        try
        {
            destination = Path.GetFullPath(DestinationInput.Text.Trim());
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return FailValidation("Choose a valid, fully qualified destination path.", out validationMessage);
        }

        if (!Path.IsPathFullyQualified(destination)
            || !string.Equals(Path.GetExtension(destination), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            return FailValidation("Choose a fully qualified destination ending in .zip.", out validationMessage);
        }

        if (File.Exists(destination) || Directory.Exists(destination))
        {
            return FailValidation("The destination must be new and absent; existing files and folders are never overwritten.", out validationMessage);
        }

        var sourcePaths = new List<string>(Files.Count + 1) { _sourceFullPath };
        var archiveKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mappedFiles = new List<ArchiveRebuildCopyFile>(Files.Count);
        foreach (var file in Files)
        {
            string source;
            try
            {
                source = Path.GetFullPath(file.SourcePath);
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return FailValidation("Every new or replacement source must be a valid path.", out validationMessage);
            }

            if (!File.Exists(source) || Directory.Exists(source))
            {
                return FailValidation("Every new or replacement source must be an existing regular file.", out validationMessage);
            }

            sourcePaths.Add(source);
            if (!TryNormalizeFileArchivePath(file.ArchivePath, out var archivePath)
                || !archiveKeys.Add(GetArchiveCollisionKey(archivePath)))
            {
                return FailValidation("New and replacement archive paths must be safe, unique file paths without traversal, aliases, reserved names, or collisions.", out validationMessage);
            }

            var existingMatches = PersistentEntries
                .Where(entry => ArchivePathsMatch(entry.TargetPath, archivePath))
                .ToArray();
            if (file.IsReplacement)
            {
                if (existingMatches.Length != 1
                    || existingMatches[0].Kind != ArchiveEntryKind.File)
                {
                    return FailValidation("Each replacement must target exactly one existing material file.", out validationMessage);
                }
            }
            else if (existingMatches.Length != 0)
            {
                return FailValidation("An added file cannot target an existing material archive path; use Replace selected instead.", out validationMessage);
            }

            if (existingMatches.Any(entry => entry.IsMarkedForDeletion))
            {
                return FailValidation("The same material entry cannot be both deleted and replaced.", out validationMessage);
            }

            mappedFiles.Add(new ArchiveRebuildCopyFile(source, archivePath));
        }

        if (sourcePaths.Any(source =>
                string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)))
        {
            return FailValidation("The new destination must differ from the original archive and every new source file.", out validationMessage);
        }

        var destinationParent = Path.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(destinationParent) || !Directory.Exists(destinationParent))
        {
            return FailValidation("The new destination parent folder must already exist.", out validationMessage);
        }

        var deletions = PersistentEntries
            .Where(entry => entry.IsMarkedForDeletion)
            .Select(entry => new ArchiveEntryKey(entry.PersistentKeyPath, entry.DuplicateOrdinal))
            .ToArray();
        var deletionKeys = new HashSet<ArchiveEntryKey>(deletions);
        if (deletionKeys.Count != deletions.Length)
        {
            return FailValidation("The deletion list contains the same persistent entry more than once.", out validationMessage);
        }

        var renameOperations = new List<ArchiveRebuildRename>();
        var renameKeys = new HashSet<ArchiveEntryKey>();
        foreach (var entry in PersistentEntries.Where(entry =>
                     !string.IsNullOrWhiteSpace(entry.RenamePath)))
        {
            if (entry.Kind != ArchiveEntryKind.File)
            {
                return FailValidation("Only retained regular files may be renamed; folders remain unchanged in this rebuild slice.", out validationMessage);
            }

            var entryKey = new ArchiveEntryKey(entry.PersistentKeyPath, entry.DuplicateOrdinal);
            if (!renameKeys.Add(entryKey))
            {
                return FailValidation("Each persistent entry may be renamed only once.", out validationMessage);
            }

            if (deletionKeys.Contains(entryKey))
            {
                return FailValidation("A persistent entry cannot be both deleted and renamed.", out validationMessage);
            }

            if (Files.Any(file =>
                    file.IsReplacement
                    && ArchivePathsMatch(file.ArchivePath, entry.TargetPath)))
            {
                return FailValidation("A persistent entry cannot be both replaced and renamed.", out validationMessage);
            }

            if (!TryNormalizeFileArchivePath(entry.RenamePath!, out var finalArchivePath)
                || !archiveKeys.Add(GetArchiveCollisionKey(finalArchivePath)))
            {
                return FailValidation("Rename targets must be safe, unique file paths without traversal, aliases, reserved names, or collisions.", out validationMessage);
            }

            if (PersistentEntries.Any(existing =>
                    existing.Kind == ArchiveEntryKind.Directory
                    && ArchivePathsMatch(existing.TargetPath, finalArchivePath)))
            {
                return FailValidation("Rename targets must identify regular files, not directories.", out validationMessage);
            }

            renameOperations.Add(new ArchiveRebuildRename(entryKey, finalArchivePath));
        }

        var folderAdds = FolderAdds
            .Select(folder =>
            {
                var archiveRoot = TryNormalizeDirectoryArchivePath(folder.ArchiveRoot, out var normalizedRoot)
                    ? normalizedRoot
                    : string.Empty;
                return new ArchiveCreationDirectory(
                    Path.GetFullPath(folder.SourcePath),
                    archiveRoot);
            })
            .ToArray();
        if (folderAdds.Any(folder => string.IsNullOrEmpty(folder.SourcePath)
                || string.IsNullOrEmpty(folder.ArchivePath)))
        {
            return FailValidation("Every added folder must have a selected source path and a safe archive-relative root.", out validationMessage);
        }

        foreach (var folder in folderAdds)
        {
            if (PersistentEntries.Any(entry => ArchivePathsMatch(entry.TargetPath, folder.ArchivePath))
                || !archiveKeys.Add(GetArchiveCollisionKey(folder.ArchivePath)))
            {
                return FailValidation("Added folder roots must be unique and must not collide with file or archive paths.", out validationMessage);
            }
        }

        var treeActions = new List<ArchiveRebuildTreeAction>();
        foreach (var node in TreePlan.Where(node => node.IsMarkedForDeletion || !string.IsNullOrWhiteSpace(node.MovePrefix)))
        {
            if (node.IsMarkedForDeletion && !string.IsNullOrWhiteSpace(node.MovePrefix))
            {
                return FailValidation("A planned folder cannot be both moved and deleted.", out validationMessage);
            }

            if (node.IsMarkedForDeletion)
            {
                treeActions.Add(new ArchiveRebuildTreeAction(
                    node.NormalizedPath,
                    node.SubtreeRevision,
                    ArchiveRebuildTreeActionKind.Delete));
            }
            else if (!TryNormalizeDirectoryArchivePath(node.MovePrefix!, out var finalPrefix))
            {
                return FailValidation("Move targets must be safe archive-relative directory prefixes.", out validationMessage);
            }
            else
            {
                treeActions.Add(new ArchiveRebuildTreeAction(
                    node.NormalizedPath,
                    node.SubtreeRevision,
                    ArchiveRebuildTreeActionKind.Move,
                    finalPrefix));
            }
        }

        var metadata = new List<ArchiveRebuildMetadataDirective>();
        foreach (var item in Metadata.Where(item =>
                     item.HasChanges
                     || (item.Entry is null
                         && item.Target.Kind == ArchiveRebuildMetadataTargetKind.MappedFinalPath
                         && (preset.Preset?.MetadataDefaults.LastWriteTime is not null
                             || preset.Preset?.MetadataDefaults.AttributesLowWord is not null))))
        {
            if (item.Entry is { } retainedEntry)
            {
                var retainedKey = new ArchiveEntryKey(
                    retainedEntry.PersistentKeyPath,
                    retainedEntry.DuplicateOrdinal);
                if (deletionKeys.Contains(retainedKey))
                {
                    return FailValidation(
                        "Metadata cannot target an entry that is deleted from the final archive.",
                        out validationMessage);
                }

                if (Files.Any(file =>
                        file.IsReplacement
                        && ArchivePathsMatch(file.ArchivePath, retainedEntry.TargetPath)))
                {
                    return FailValidation(
                        "Metadata for a replaced entry must use its mapped final-path row.",
                        out validationMessage);
                }

                if (treeActions.Any(action =>
                        action.Kind == ArchiveRebuildTreeActionKind.Delete
                        && IsWithinArchiveTree(retainedEntry.TargetPath, action.NormalizedPath)))
                {
                    return FailValidation(
                        "Metadata cannot target an entry deleted by a retained-tree action.",
                        out validationMessage);
                }
            }

            ArchiveDosLocalTimestamp? timestamp = null;
            ushort? attributes = null;
            if (!string.IsNullOrWhiteSpace(item.FinalTimeText))
            {
                if (!ArchiveMetadataInput.TryParseTimestamp(item.FinalTimeText.Trim(), out var parsedTimestamp))
                {
                    return FailValidation("Archive time must use exact YYYY-MM-DDTHH:mm:ss syntax with year 1980-2107 and even seconds from 00 through 58.", out validationMessage);
                }

                timestamp = parsedTimestamp;
            }

            if (!string.IsNullOrWhiteSpace(item.FinalAttributesText))
            {
                if (!ArchiveMetadataInput.TryParseAttributes(item.FinalAttributesText.Trim(), out var parsedAttributes))
                {
                    return FailValidation("Archive attributes may contain only ReadOnly, Hidden, System, and Archive bits; Directory is derived from entry kind.", out validationMessage);
                }

                attributes = parsedAttributes;
            }

            if (item.Target.Kind == ArchiveRebuildMetadataTargetKind.MappedFinalPath
                && item.Entry is null)
            {
                timestamp ??= preset.Preset?.MetadataDefaults.LastWriteTime;
                attributes ??= preset.Preset?.MetadataDefaults.AttributesLowWord;
            }

            if (timestamp is null && attributes is null)
            {
                continue;
            }

            if (!TryMapMetadataTarget(item.Target, treeActions, out var finalTarget))
            {
                return FailValidation(
                    "Metadata cannot target a mapped entry removed by the final tree plan.",
                    out validationMessage);
            }

            metadata.Add(new ArchiveRebuildMetadataDirective(finalTarget, timestamp, attributes));
        }

        if (treeActions.Count == 0 && folderAdds.Length == 0)
        {
            var finalNamespace = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var replacementPaths = Files
                .Where(file => file.IsReplacement)
                .Select(file => GetArchiveCollisionKey(file.ArchivePath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in PersistentEntries)
            {
                var entryKey = new ArchiveEntryKey(entry.PersistentKeyPath, entry.DuplicateOrdinal);
                if (deletionKeys.Contains(entryKey))
                {
                    continue;
                }

                if (replacementPaths.Contains(GetArchiveCollisionKey(entry.TargetPath)))
                {
                    continue;
                }

                var finalPath = renameOperations
                    .FirstOrDefault(rename => rename.EntryKey == entryKey)
                    ?.FinalArchivePath
                    ?? entry.TargetPath;
                if (!TryGetArchiveCollisionKey(finalPath, out var finalKey)
                    || !finalNamespace.TryAdd(finalKey, finalPath))
                {
                    return FailValidation("Rename targets must leave one unambiguous final archive namespace without collisions.", out validationMessage);
                }
            }

            foreach (var file in mappedFiles)
            {
                var fileKey = GetArchiveCollisionKey(file.ArchivePath);
                if (!finalNamespace.TryAdd(fileKey, file.ArchivePath))
                {
                    return FailValidation("New, replacement, and rename targets must leave one unambiguous final archive namespace without collisions.", out validationMessage);
                }
            }
        }

        selection = new ArchiveRebuildCopySelection(
            destination,
            deletions,
            mappedFiles,
            preset.Value,
            renameOperations,
            folderAdds,
            treeActions,
            metadata);
        validationMessage = string.Empty;
        return true;
    }

    private void RefreshButtons()
    {
        ReplaceFileButton.IsEnabled = EntriesGrid.SelectedItem is RebuildCopyEntryItem
        {
            Kind: ArchiveEntryKind.File,
        };
        RemoveFileButton.IsEnabled = FilesGrid.SelectedItem is RebuildCopyFileItem;
        AddFolderButton.IsEnabled = _client is not null
            && _sessionId is not null
            && _canRebuild
            && _client.SupportsFeature(ProtocolFeatures.ZipRebuildSourceFolderReview);
        UpdateReview();
    }

    private void UpdateReview()
    {
        if (!IsInitialized)
        {
            return;
        }

        var presetName = (PresetInput.SelectedItem as CompressionPresetOption)?.Name ?? "no preset";
        var deletions = PersistentEntries.Count(entry => entry.IsMarkedForDeletion);
        var treeChanges = TreePlan.Count(item => item.IsMarkedForDeletion || !string.IsNullOrWhiteSpace(item.MovePrefix));
        ReviewSummaryText.Text =
            $"Final review: full guarded ZIP rebuild-to-copy; {presetName}; {deletions:N0} direct deletion(s), {Files.Count:N0} mapped file(s), {FolderAdds.Count:N0} reviewed folder root(s), and {treeChanges:N0} retained-tree change(s).";
        DestinationPolicyText.Text =
            "The original archive and every source remain untouched. The destination must be a distinct absent ZIP; existing or raced targets are preserved. Rain opens the new copy only after authoritative Worker success.";
    }

    private string GetSuggestedDestination()
    {
        var baseName = Path.GetFileNameWithoutExtension(_sourcePath);
        var directory = Path.GetDirectoryName(_sourceFullPath) ?? string.Empty;
        var candidate = Path.Combine(directory, $"{baseName} - rebuilt.zip");
        for (var suffix = 2; File.Exists(candidate) || Directory.Exists(candidate); suffix++)
        {
            candidate = Path.Combine(directory, $"{baseName} - rebuilt ({suffix}).zip");
        }

        return candidate;
    }

    private bool FailValidation(string message, out string validationMessage)
    {
        validationMessage = message;
        ValidationMessage.Text = message;
        return false;
    }

    private static bool TryNormalizeArchivePath(string value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > ProtocolConstants.MaximumPathCharacters
            || !ArchivePathRules.TryNormalize(
                value,
                out var safePath,
                out _,
                out _))
        {
            return false;
        }

        normalized = safePath.Replace(Path.DirectorySeparatorChar, '/');
        return true;
    }

    private static bool TryNormalizeFileArchivePath(string value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value)
            || value.EndsWith('/')
            || value.EndsWith('\\')
            || !TryNormalizeArchivePath(value, out normalized))
        {
            normalized = string.Empty;
            return false;
        }

        return true;
    }

    private static bool TryNormalizeDirectoryArchivePath(string value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value)
            || !TryNormalizeArchivePath(value.TrimEnd('/', '\\'), out normalized))
        {
            return false;
        }

        return normalized.Length > 0;
    }

    private static bool ArchivePathsMatch(string left, string right) =>
        string.Equals(
            GetArchiveCollisionKey(left),
            GetArchiveCollisionKey(right),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsMappedMetadataFor(
        RebuildMetadataItem item,
        string archivePath,
        ArchiveEntryKind kind) =>
        item.Target.Kind == ArchiveRebuildMetadataTargetKind.MappedFinalPath
        && item.Target.MappedKind == kind
        && item.Target.MappedFinalArchivePath is { } mappedPath
        && ArchivePathsMatch(mappedPath, archivePath);

    private void RemoveMappedMetadata(string archivePath, ArchiveEntryKind kind)
    {
        foreach (var item in Metadata.Where(candidate =>
                     IsMappedMetadataFor(candidate, archivePath, kind)).ToArray())
        {
            Metadata.Remove(item);
        }
    }

    private void RemoveRetainedMetadata(RebuildCopyEntryItem entry)
    {
        var key = new ArchiveEntryKey(entry.PersistentKeyPath, entry.DuplicateOrdinal);
        foreach (var item in Metadata.Where(candidate =>
                     candidate.Target.Kind == ArchiveRebuildMetadataTargetKind.RetainedEntryKey
                     && candidate.Target.RetainedEntryKey == key).ToArray())
        {
            Metadata.Remove(item);
        }
    }

    private void RestoreRetainedMetadata(RebuildCopyEntryItem entry)
    {
        var key = new ArchiveEntryKey(entry.PersistentKeyPath, entry.DuplicateOrdinal);
        if (!Metadata.Any(candidate =>
                candidate.Target.Kind == ArchiveRebuildMetadataTargetKind.RetainedEntryKey
                && candidate.Target.RetainedEntryKey == key))
        {
            Metadata.Add(new RebuildMetadataItem(entry));
        }
    }

    private static bool IsWithinArchiveTree(string path, string root)
    {
        var pathKey = GetArchiveCollisionKey(path);
        var rootKey = GetArchiveCollisionKey(root);
        return pathKey == rootKey
            || pathKey.StartsWith(rootKey + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryMapMetadataTarget(
        ArchiveRebuildMetadataTarget target,
        IReadOnlyList<ArchiveRebuildTreeAction> treeActions,
        out ArchiveRebuildMetadataTarget finalTarget)
    {
        finalTarget = target;
        if (target.Kind != ArchiveRebuildMetadataTargetKind.MappedFinalPath
            || target.MappedFinalArchivePath is not { } path
            || target.MappedKind is not { } kind)
        {
            return true;
        }

        if (!TryNormalizeArchivePath(path, out path))
        {
            return false;
        }

        foreach (var action in treeActions)
        {
            if (!TryNormalizeDirectoryArchivePath(action.NormalizedPath, out var sourceRoot)
                || !IsWithinArchiveTree(path, sourceRoot))
            {
                continue;
            }

            if (action.Kind == ArchiveRebuildTreeActionKind.Delete
                || string.IsNullOrWhiteSpace(action.FinalArchivePath))
            {
                return false;
            }

            var relative = ArchivePathsMatch(path, sourceRoot)
                ? string.Empty
                : path[(sourceRoot.Length + 1)..];
            var destinationRoot = action.FinalArchivePath!.TrimEnd('/', '\\');
            path = string.IsNullOrEmpty(relative)
                ? destinationRoot
                : $"{destinationRoot}/{relative}";
        }

        finalTarget = new ArchiveRebuildMetadataTarget(
            ArchiveRebuildMetadataTargetKind.MappedFinalPath,
            MappedFinalArchivePath: path,
            MappedKind: kind);
        return true;
    }

    private static string GetArchiveCollisionKey(string value) =>
        ArchivePathRules.GetCollisionKey(value.TrimEnd('/', '\\'));


    private static bool TryGetArchiveCollisionKey(string value, out string collisionKey)
    {
        var candidate = value.TrimEnd('/', '\\');
        if (candidate.Length == 0
            || !ArchivePathRules.TryNormalize(
                candidate,
                out _,
                out collisionKey,
                out _))
        {
            collisionKey = string.Empty;
            return false;
        }

        return true;
    }

    private static string FormatConflictNames(IReadOnlyList<string> conflicts) =>
        string.Join(", ", conflicts.Distinct(StringComparer.Ordinal).Take(4).Select(name => $"‘{UntrustedDisplayText.Escape(name)}’"));
}

public enum RebuildCopyFileMode
{
    Add,
    Replace,
}

public sealed class RebuildCopyEntryItem
{
    public RebuildCopyEntryItem(ArchiveEntryDescriptor descriptor)
    {
        Descriptor = descriptor;
    }

    public ArchiveEntryDescriptor Descriptor { get; }
    public string DisplayPath => UntrustedDisplayText.Escape(TargetPath);
    public string TargetPath => Descriptor.NormalizedPath ?? Descriptor.RawPath;
    public ArchiveEntryKind Kind => Descriptor.Kind;
    public int DuplicateOrdinal => Descriptor.DuplicateOrdinal;
    public string PersistentKeyPath => Descriptor.NormalizedPath!;
    public string SizeText => Descriptor.OriginalSize is { } size ? $"{size:N0} B" : "unknown";
    public bool IsMarkedForDeletion { get; set; }
    public string RenamePath { get; set; } = string.Empty;
    public string DisplayRenamePath => UntrustedDisplayText.Escape(RenamePath);
}

public sealed class RebuildCopyFileItem
{
    public RebuildCopyFileItem(RebuildCopyFileMode mode, string sourcePath, string archivePath)
    {
        ModeValue = mode;
        SourcePath = sourcePath;
        ArchivePath = archivePath;
    }

    public RebuildCopyFileMode ModeValue { get; }
    public string Mode => ModeValue == RebuildCopyFileMode.Replace ? "Replace" : "Add";
    public bool IsReplacement => ModeValue == RebuildCopyFileMode.Replace;
    public string SourcePath { get; }
    public string ArchivePath { get; set; }
    public string DisplaySourcePath => UntrustedDisplayText.Escape(SourcePath);
    public string DisplayArchivePath => UntrustedDisplayText.Escape(ArchivePath);
}

public sealed class RebuildTreePlanItem
{
    public RebuildTreePlanItem(ZipRebuildTreePlanNode node)
    {
        NormalizedPath = node.NormalizedPath;
        HasExplicitDirectoryEntry = node.HasExplicitDirectoryEntry;
        DescendantCount = node.DescendantCount;
        MaterialEntryCount = node.MaterialEntryCount;
        SubtreeRevision = node.SubtreeRevision;
    }

    public string NormalizedPath { get; }
    public string DisplayPath => UntrustedDisplayText.Escape(NormalizedPath);
    public bool HasExplicitDirectoryEntry { get; }
    public int DescendantCount { get; }
    public int MaterialEntryCount { get; }
    public string SubtreeRevision { get; }
    public bool IsMarkedForDeletion { get; set; }
    public string MovePrefix { get; set; } = string.Empty;
    public string DisplayMovePrefix => UntrustedDisplayText.Escape(MovePrefix);
}

public sealed class RebuildFolderAddItem
{
    public RebuildFolderAddItem(
        string sourcePath,
        string archiveRoot,
        int descendantCount = 0,
        int fileCount = 0)
    {
        SourcePath = sourcePath;
        ArchiveRoot = archiveRoot;
        DescendantCount = descendantCount;
        FileCount = fileCount;
    }

    public string SourcePath { get; }
    public string ArchiveRoot { get; set; }
    public string DisplaySourcePath => UntrustedDisplayText.Escape(SourcePath);
    public string DisplayArchiveRoot => UntrustedDisplayText.Escape(ArchiveRoot);
    public int DescendantCount { get; }
    public int FileCount { get; }
    public string CountText => $"{DescendantCount:N0} descendants / {FileCount:N0} files";
}

public sealed class RebuildMetadataItem
{
    private readonly RebuildCopyFileItem? _mappedFile;
    private readonly RebuildFolderAddItem? _mappedDirectory;
    private readonly ArchiveRebuildMetadataTarget _target;
    private readonly string _targetText;

    public RebuildMetadataItem(RebuildCopyEntryItem entry)
    {
        Entry = entry;
        _target = new ArchiveRebuildMetadataTarget(
            ArchiveRebuildMetadataTargetKind.RetainedEntryKey,
            new ArchiveEntryKey(entry.PersistentKeyPath, entry.DuplicateOrdinal));
        _targetText = $"{UntrustedDisplayText.Escape(entry.TargetPath)} [{entry.DuplicateOrdinal}] ({entry.Kind})";
        // The opened snapshot exposes a provider-converted UTC value, not the
        // raw timezone-less ZIP DOS tuple. Keep the current local value honest.
        CurrentTimeText = "unavailable (raw ZIP DOS time not exposed)";
        CurrentAttributesText = entry.Descriptor.WindowsAttributes is { } attributes
            && (attributes & ~0x0037) == 0
            ? $"0x{attributes & 0xFFFF:X4}"
            : "unavailable";
    }

    private RebuildMetadataItem(
        ArchiveRebuildMetadataTarget target,
        string targetText,
        RebuildCopyFileItem? mappedFile = null,
        RebuildFolderAddItem? mappedDirectory = null)
    {
        _target = target;
        _targetText = targetText;
        _mappedFile = mappedFile;
        _mappedDirectory = mappedDirectory;
        CurrentTimeText = "unavailable";
        CurrentAttributesText = "unavailable";
        Entry = null!;
    }

    public static RebuildMetadataItem ForMappedFile(RebuildCopyFileItem file) =>
        new(
            new ArchiveRebuildMetadataTarget(
                ArchiveRebuildMetadataTargetKind.MappedFinalPath,
                MappedFinalArchivePath: file.ArchivePath,
                MappedKind: ArchiveEntryKind.File),
            $"{UntrustedDisplayText.Escape(file.ArchivePath)} (mapped file; final mapped path)",
            file);

    public static RebuildMetadataItem ForMappedDirectory(RebuildFolderAddItem folder)
    {
        var normalized = folder.ArchiveRoot.TrimEnd('/', '\\');
        return new RebuildMetadataItem(
            new ArchiveRebuildMetadataTarget(
                ArchiveRebuildMetadataTargetKind.MappedFinalPath,
                MappedFinalArchivePath: normalized,
                MappedKind: ArchiveEntryKind.Directory),
            $"{UntrustedDisplayText.Escape(normalized)} (new directory; final mapped path)",
            mappedDirectory: folder);
    }

    public RebuildCopyEntryItem? Entry { get; }
    public ArchiveRebuildMetadataTarget Target => _mappedFile is not null
        ? new ArchiveRebuildMetadataTarget(
            ArchiveRebuildMetadataTargetKind.MappedFinalPath,
            MappedFinalArchivePath: _mappedFile.ArchivePath,
            MappedKind: ArchiveEntryKind.File)
        : _mappedDirectory is not null
            ? new ArchiveRebuildMetadataTarget(
                ArchiveRebuildMetadataTargetKind.MappedFinalPath,
                MappedFinalArchivePath: _mappedDirectory.ArchiveRoot.TrimEnd('/', '\\'),
                MappedKind: ArchiveEntryKind.Directory)
            : _target;
    public string TargetText => _mappedFile is not null
        ? $"{UntrustedDisplayText.Escape(_mappedFile.ArchivePath)} (mapped file; final mapped path)"
        : _mappedDirectory is not null
            ? $"{UntrustedDisplayText.Escape(_mappedDirectory.ArchiveRoot.TrimEnd('/', '\\'))} (new directory; final mapped path)"
            : _targetText;
    public string CurrentTimeText { get; }
    public string CurrentAttributesText { get; }
    public string FinalTimeText { get; set; } = string.Empty;
    public string FinalAttributesText { get; set; } = string.Empty;
    public bool HasChanges => !string.IsNullOrWhiteSpace(FinalTimeText)
        || !string.IsNullOrWhiteSpace(FinalAttributesText);
}
