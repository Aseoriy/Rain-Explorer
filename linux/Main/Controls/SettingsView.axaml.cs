// Avalonia 11.3 marks the IDataObject drag API obsolete in favour of DataTransfer; it is still the
// documented 11.x path and works on X11 (same as PaneView and ClipboardService).
#pragma warning disable CS0618
using System.ComponentModel;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RainExplorer.Helpers;
using RainExplorer.Models;
using RainExplorer.Services;
using RainExplorer.ViewModels;

namespace RainExplorer.Controls;

/// <summary>A theme choice shown as a swatch card.</summary>
public sealed record ThemeSwatch(string Name, IBrush Swatch);

/// <summary>
/// Full-page settings overlay, ported from the Windows build: scrim over the blurred
/// window, sticky header, left section nav and one section visible at a time. Every
/// control binds two-way to <see cref="AppSettings"/>, which saves itself on change.
/// Windows-only rows (shell registration, Git for Windows, managed archives, updates) are not shown.
/// </summary>
public partial class SettingsView : UserControl
{
    private static AppSettings S => SettingsStore.Instance.Settings;
    private bool _syncing;
    private readonly Dictionary<string, Action> _enumSyncs = new(StringComparer.Ordinal);
    private readonly ScrollViewer[] _panes;
    private int _selectedSection;
    private int _openRevision;
    private int _sectionRevision;

    private sealed record Choice<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }

    public SettingsView()
    {
        InitializeComponent();
        _panes = [PaneAppearance, PaneBrowsing, PaneFiles, PaneAdvanced, PaneAbout];
        DataContext = S;

        SectionList.SelectionChanged += (_, _) => ShowSection(SectionList.SelectedIndex);
        ThemeList.ItemsSource = ThemeService.Names.Select(n => new ThemeSwatch(n, ThemeService.Swatch(n))).ToList();
        ThemeList.SelectionChanged += (_, _) =>
        {
            if (!_syncing && ThemeList.SelectedItem is ThemeSwatch swatch) S.Theme = swatch.Name;
        };

        BindEnum(LayoutBox, nameof(AppSettings.ViewLayout), new Dictionary<ViewLayout, string>
        {
            [ViewLayout.Details] = "Details", [ViewLayout.List] = "List",
            [ViewLayout.Tiles] = "Tiles", [ViewLayout.Grid] = "Grid",
        });
        BindEnum(DensityBox, nameof(AppSettings.Density), new Dictionary<ViewDensity, string>
        {
            [ViewDensity.Comfortable] = "Comfortable", [ViewDensity.Compact] = "Compact",
        });
        BindEnum(RenameBox, nameof(AppSettings.RenameMode), new Dictionary<RenameMode, string>
        {
            [RenameMode.Dialog] = "Pop-up dialog", [RenameMode.Inline] = "Inline",
        });
        BindEnum(DeleteBehaviorBox, nameof(AppSettings.DeleteBehavior), new Dictionary<DeleteBehavior, string>
        {
            [DeleteBehavior.Prompt] = "Prompt", [DeleteBehavior.Recycle] = "Trash",
            [DeleteBehavior.Permanent] = "Permanent",
        });
        BindEnum(ScrollSpeedBox, nameof(AppSettings.ScrollSpeed), new Dictionary<ScrollSpeed, string>
        {
            [ScrollSpeed.Slow] = "Slow", [ScrollSpeed.Normal] = "Normal", [ScrollSpeed.Fast] = "Fast",
        });
        BindRetention();
        BindEnum(SizeFormatBox, nameof(AppSettings.SizeFormat), new Dictionary<SizeFormat, string>
        {
            [SizeFormat.Binary] = "Binary (KiB)", [SizeFormat.Decimal] = "Decimal (KB)",
        });

        BuildFontList();
        FontSearch.TextChanged += (_, _) => FilterFonts();
        FontSearch.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Down && FontList.ItemCount > 0)
            {
                e.Handled = true;
                FontList.SelectedIndex = Math.Max(0, FontList.SelectedIndex);
                FontList.ContainerFromIndex(FontList.SelectedIndex)?.Focus();
            }
        };
        FontList.SelectionChanged += (_, _) =>
        {
            if (!_syncing && FontList.SelectedItem is FontChoice font) S.FontFamily = font.Name;
            UpdateFontStatus();
        };
        // The font list grows with the window but never makes its card taller than the visible page.
        PaneAppearance.SizeChanged += (_, e) => FontList.Height = Math.Clamp(e.NewSize.Height - 215, 130, 260);
        FontCard.AddHandler(Avalonia.Input.DragDrop.DragOverEvent, FontCard_DragOver);
        FontCard.AddHandler(Avalonia.Input.DragDrop.DropEvent, FontCard_Drop);

        TerminalAppBox.SelectionChanged += (_, _) =>
        {
            if (!_syncing && TerminalAppBox.SelectedItem is Choice<string> choice) S.TerminalCommand = choice.Value;
        };
        ArchiveAppBox.SelectionChanged += (_, _) =>
        {
            if (!_syncing && ArchiveAppBox.SelectedItem is Choice<string> choice) S.ArchiveManager = choice.Value;
        };

        string configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RainExplorer");
        ConfigPathText.Text = PathUtil.Tildify(Path.Combine(configDir, "settings.json"));
        VersionText.Text = $"Version {AppVersion}";
        Refresh();
        UpdateIntegrationStatus();
    }

    public static string AppVersion =>
        typeof(SettingsView).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0]
        ?? typeof(SettingsView).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    /// <summary>Re-sync controls that are not plain bindings.</summary>
    public void Refresh()
    {
        _syncing = true;
        try
        {
            ThemeList.SelectedItem = ThemeList.Items.OfType<ThemeSwatch>().FirstOrDefault(t => t.Name == S.Theme);
            SelectCurrentFont();
            var terminals = new List<Choice<string>> { new("", "Automatic") };
            terminals.AddRange(TerminalLauncher.Installed().Select(t => new Choice<string>(t.Exe, t.Label)));
            if (!string.IsNullOrEmpty(S.TerminalCommand) && terminals.All(c => c.Value != S.TerminalCommand))
                terminals.Add(new Choice<string>(S.TerminalCommand, S.TerminalCommand));
            TerminalAppBox.ItemsSource = terminals;
            TerminalAppBox.SelectedItem = terminals.FirstOrDefault(c => c.Value == S.TerminalCommand) ?? terminals[0];
            var selectedArchiveApp = ArchiveLauncher.Resolve(out string? archiveError);
            var archiveApps = new List<Choice<string>>
            {
                new("", string.IsNullOrEmpty(S.ArchiveManager) && selectedArchiveApp is not null
                    ? $"Automatic ({selectedArchiveApp.Label})" : "Automatic"),
            };
            archiveApps.AddRange(ArchiveLauncher.Installed().Select(app => new Choice<string>(app.Id, app.Label)));
            if (!string.IsNullOrEmpty(S.ArchiveManager) && archiveApps.All(choice => choice.Value != S.ArchiveManager))
                archiveApps.Add(new Choice<string>(S.ArchiveManager, $"{ArchiveLauncher.LabelFor(S.ArchiveManager)} (not installed)"));
            ArchiveAppBox.ItemsSource = archiveApps;
            ArchiveAppBox.SelectedItem = archiveApps.FirstOrDefault(choice => choice.Value == S.ArchiveManager) ?? archiveApps[0];
            ArchiveAppStatus.Text = archiveError ?? (selectedArchiveApp?.CanExtractHere == false
                ? "Extraction opens the app's destination dialog. Passwords and overwrite choices are handled there."
                : "Passwords, extraction options and overwrite choices are handled by the archive app.");
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.Theme) or nameof(AppSettings.FontFamily) or nameof(AppSettings.ArchiveManager)) Refresh();
        if (e.PropertyName is { } name && _enumSyncs.TryGetValue(name, out var sync)) sync();
    }

    private void ShowSection(int index)
    {
        index = Math.Clamp(index, 0, _panes.Length - 1);
        if (index == _selectedSection) return;
        _selectedSection = index;
        int revision = ++_sectionRevision;
        for (int i = 0; i < _panes.Length; i++)
        {
            if (_panes[i].Content is Control content) ResetMotion(content);
            _panes[i].IsVisible = i == index;
        }
        if (IsVisible && this.GetVisualRoot() is not null && _panes[index].Content is Control incoming)
            QueueArrival(incoming, 6, 150, () => revision == _sectionRevision);
    }

    internal void AnimateOpening()
    {
        StopAnimations();
        int revision = _openRevision;
        QueueArrival(SettingsSurface, 8, 180, () => revision == _openRevision);
    }

    private void QueueArrival(Control surface, double y, int milliseconds, Func<bool> isCurrent)
    {
        surface.Opacity = 0;
        // Wait for the newly visible section to lay out before starting its clock.
        Dispatcher.UIThread.Post(() =>
        {
            if (!isCurrent() || !IsVisible || this.GetVisualRoot() is null) return;
            surface.Opacity = 1;
            _ = Motion.Arrive(surface, scale: 1, y: y, milliseconds: milliseconds);
        }, DispatcherPriority.Render);
    }

    internal void StopAnimations()
    {
        _openRevision++;
        _sectionRevision++;
        ResetMotion(SettingsSurface);
        foreach (var pane in _panes)
            if (pane.Content is Control content) ResetMotion(content);
    }

    private static void ResetMotion(Control surface)
    {
        Motion.Cancel(surface);
        surface.Opacity = 1;
        surface.RenderTransform = null;
    }

    // ---- Interface font: search, preview, import --------------------------------------------------

    private List<FontChoice> _allFonts = [];

    private void BuildFontList()
    {
        var imported = ImportedFontCollection.Instance.FamilyNames;
        var names = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase) { AppSettings.DefaultFontFamily };
        try
        {
            foreach (var family in FontManager.Current.SystemFonts)
                if (!string.IsNullOrWhiteSpace(family.Name)) names.Add(family.Name);
        }
        catch { }
        foreach (string name in imported) names.Add(name);
        _allFonts = names
            .Select(name => new FontChoice(name, imported.Contains(name, StringComparer.OrdinalIgnoreCase)))
            .OrderBy(f => f.Name == AppSettings.DefaultFontFamily ? 0 : 1)
            .ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        FilterFonts();
    }

    private void FilterFonts()
    {
        string query = FontSearch.Text?.Trim() ?? string.Empty;
        var shown = query.Length == 0
            ? _allFonts
            : _allFonts.Where(f => f.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
        _syncing = true;
        FontList.ItemsSource = shown;
        SelectCurrentFont();
        _syncing = false;
        UpdateFontStatus(shown.Count == 0 ? $"No fonts match “{query}”." : null);
    }

    private void SelectCurrentFont()
    {
        bool wasSyncing = _syncing;
        _syncing = true;
        var current = FontList.Items.OfType<FontChoice>().FirstOrDefault(f => f.Name == S.FontFamily);
        FontList.SelectedItem = current;
        if (current is not null) FontList.ScrollIntoView(current);
        _syncing = wasSyncing;
    }

    private void UpdateFontStatus(string? message = null)
    {
        FontStatus.Text = message
            ?? $"Using {S.FontFamily}. Drop .ttf, .otf or .zip font files here to import them.";
    }

    private void ImportFont_Click(object? sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            ItemsSource = new Control[]
            {
                MenuFactory.Item("From a file…", "file-plus", () => _ = ImportFontFromFile()),
                MenuFactory.Item("From a link…", "link", () => _ = ImportFontFromLink()),
                new Separator(),
                MenuFactory.Item("Open fonts folder", "folder-open", OpenFontsFolder),
            },
        };
        menu.Open(ImportFontButton);
    }

    private async Task ImportFontFromFile()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import fonts",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Fonts and font archives") { Patterns = ["*.ttf", "*.otf", "*.ttc", "*.otc", "*.zip", "*.TTF", "*.OTF", "*.ZIP"] },
                FilePickerFileTypes.All,
            ],
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0) await RunFontImport(FontImportService.ImportFilesAsync(paths));
    }

    private async Task ImportFontFromLink()
    {
        string? url = await Views.InputDialog.Ask(TopLevel.GetTopLevel(this) as Window, "Import font from a link",
            "Paste a direct link to a .ttf, .otf or .zip file (for example, a font's Download button):", "https://");
        if (string.IsNullOrWhiteSpace(url) || url.Trim() == "https://") return;
        await RunFontImport(FontImportService.ImportFromUrlAsync(url));
    }

    private void OpenFontsFolder()
    {
        Directory.CreateDirectory(FontImportService.ImportFolder);
        if (TopLevel.GetTopLevel(this)?.DataContext is MainViewModel vm)
        {
            vm.IsSettingsOpen = false;
            vm.ActivePane.NewTab(FontImportService.ImportFolder, activate: true);
        }
    }

    private void FontCard_DragOver(object? sender, Avalonia.Input.DragEventArgs e)
    {
        bool fonts = e.Data.GetFiles()?.Any(f => f.TryGetLocalPath() is { } p && FontImportService.IsImportable(p)) == true;
        e.DragEffects = fonts ? Avalonia.Input.DragDropEffects.Copy : Avalonia.Input.DragDropEffects.None;
        e.Handled = true;
    }

    private async void FontCard_Drop(object? sender, Avalonia.Input.DragEventArgs e)
    {
        e.Handled = true;
        var paths = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>()
            .Where(FontImportService.IsImportable).ToList() ?? [];
        if (paths.Count > 0) await RunFontImport(FontImportService.ImportFilesAsync(paths));
    }

    private async Task RunFontImport(Task<FontImportResult> import)
    {
        ImportFontButton.IsEnabled = false;
        UpdateFontStatus("Importing…");
        FontImportResult result;
        try { result = await import; }
        catch (Exception ex) { result = new FontImportResult([], 0, ex.Message); }
        finally { ImportFontButton.IsEnabled = true; }

        if (!result.Succeeded)
        {
            UpdateFontStatus($"⚠️ {result.Error}");
            return;
        }
        ImportedFontCollection.Instance.Reload();
        FontSearch.Text = string.Empty;
        BuildFontList();
        if (result.Families.Count == 1)
        {
            S.FontFamily = result.Families[0];
            SelectCurrentFont();
            UpdateFontStatus($"Imported {result.Families[0]} and switched to it. It's installed for your account, so other apps can use it too.");
        }
        else
        {
            UpdateFontStatus($"Imported {result.Families.Count} fonts: {string.Join(", ", result.Families.Take(4))}"
                             + $"{(result.Families.Count > 4 ? "…" : "")}. Pick one below.");
            FontSearch.Text = result.Families[0];
        }
    }

    private static readonly (int Days, string Label)[] RetentionChoices =
    [
        (1, "1 day"), (3, "3 days"), (7, "7 days"), (30, "30 days"), (90, "90 days"), (0, "Until I clear it"),
    ];

    private void BindRetention()
    {
        RetentionBox.ItemsSource = RetentionChoices.Select(c => c.Label).ToList();
        void Sync()
        {
            _syncing = true;
            int index = Array.FindIndex(RetentionChoices, c => c.Days == S.ActivityRetentionDays);
            RetentionBox.SelectedIndex = index >= 0 ? index : 2;
            _syncing = false;
        }
        Sync();
        RetentionBox.SelectionChanged += (_, _) =>
        {
            if (!_syncing && RetentionBox.SelectedIndex >= 0)
                S.ActivityRetentionDays = RetentionChoices[RetentionBox.SelectedIndex].Days;
        };
        _enumSyncs[nameof(AppSettings.ActivityRetentionDays)] = Sync;
    }

    private async void ExportActivity_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export activity history",
            SuggestedFileName = $"rain-explorer-activity-{DateTime.Now:yyyy-MM-dd}.txt",
            DefaultExtension = "txt",
        });
        if (file is null) return;
        try
        {
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            await writer.WriteAsync(ActivityService.Instance.ExportText());
            ExportActivityStatus.Text = $"Saved to {file.TryGetLocalPath() ?? file.Name}.";
        }
        catch (Exception ex)
        {
            ExportActivityStatus.Text = $"⚠️ Couldn't save the file: {ex.Message}";
        }
    }

    private void BindEnum<T>(ComboBox box, string property, IReadOnlyDictionary<T, string> labels) where T : struct, Enum
    {
        var options = labels.Select(p => new Choice<T>(p.Key, p.Value)).ToList();
        box.ItemsSource = options;
        var prop = typeof(AppSettings).GetProperty(property)!;
        void Sync()
        {
            _syncing = true;
            box.SelectedItem = options.FirstOrDefault(o => o.Value.Equals((T)prop.GetValue(S)!));
            _syncing = false;
        }
        Sync();
        box.SelectionChanged += (_, _) =>
        {
            if (!_syncing && box.SelectedItem is Choice<T> choice && !choice.Value.Equals((T)prop.GetValue(S)!))
                prop.SetValue(S, choice.Value);
        };
        _enumSyncs[property] = Sync;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        S.PropertyChanged -= OnSettingsChanged;
        S.PropertyChanged += OnSettingsChanged;
        FileManagerIntegration.Instance.PropertyChanged -= OnIntegrationChanged;
        FileManagerIntegration.Instance.PropertyChanged += OnIntegrationChanged;
        UpdateIntegrationStatus();
        foreach (var sync in _enumSyncs.Values) sync();
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        StopAnimations();
        S.PropertyChanged -= OnSettingsChanged;
        FileManagerIntegration.Instance.PropertyChanged -= OnIntegrationChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is MainViewModel vm) vm.IsSettingsOpen = false;
    }

    private void OnIntegrationChanged(object? sender, PropertyChangedEventArgs e) => UpdateIntegrationStatus();

    private void UpdateIntegrationStatus()
    {
        var integration = FileManagerIntegration.Instance;
        ShowInFolderStatus.Text = integration.Status;
        DefaultFileManagerStatus.Text = integration.DefaultStatus;
        DefaultFileManagerButton.IsEnabled = !integration.IsMakingDefault;
    }

    private async void MakeDefaultFileManager_Click(object? sender, RoutedEventArgs e) =>
        await FileManagerIntegration.Instance.MakeDefaultAsync();

    private async void RetryShowInFolder_Click(object? sender, RoutedEventArgs e) =>
        await FileManagerIntegration.Instance.RetryAsync();

    private async void BrowseDefaultFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Default starting folder" });
        if (picked.FirstOrDefault()?.TryGetLocalPath() is { } path) S.DefaultFolder = path;
    }

    private void NewList_Click(object? sender, RoutedEventArgs e) => MainViewModel.AddCustomGroup();

    private void OpenConfigFolder_Click(object? sender, RoutedEventArgs e)
    {
        string configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RainExplorer");
        if (TopLevel.GetTopLevel(this)?.DataContext is MainViewModel vm)
        {
            vm.IsSettingsOpen = false;
            vm.ActivePane.NewTab(configDir, activate: true);
        }
    }

    private async void CheckUpdates_Click(object? sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking for updates…";
        try
        {
            var info = await UpdateService.CheckAsync(SettingsStore.Instance.Settings.BetaUpdates);
            if (info is null)
            {
                UpdateStatusText.Text = $"You're on the latest version (v{UpdateService.CurrentVersionString}).";
            }
            else
            {
                UpdateStatusText.Text = $"Version {info.Version} is available.";
                await Views.UpdateDialog.ShowFor(info, TopLevel.GetTopLevel(this) as Window);
            }
        }
        catch
        {
            UpdateStatusText.Text = "Couldn't check for updates. Check your connection and try again.";
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private async void BrowseGit_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose Git executable",
            AllowMultiple = false,
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            SettingsStore.Instance.Settings.GitExecutablePath = path;
    }

    private void Link_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url })
        {
            try { ProcessLauncher.OpenDefault(url); } catch { }
        }
    }
}

/// <summary>One entry in the interface font list, drawn in its own typeface.</summary>
public sealed class FontChoice(string name, bool imported)
{
    public string Name { get; } = name;
    public string Badge { get; } = imported ? "Imported" : "";
    public FontFamily Preview { get; } = imported
        ? new FontFamily($"{ImportedFontCollection.CollectionKey}#{name}, Noto Sans")
        : new FontFamily($"{name}, Noto Sans");
}
