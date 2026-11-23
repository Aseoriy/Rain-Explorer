// Avalonia 11.3 marks the IDataObject drag API obsolete in favour of DataTransfer; it is still the
// documented 11.x path and works on X11 (same as PaneView and ClipboardService).
#pragma warning disable CS0618
using System.ComponentModel;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RainExplorer.Helpers;
using RainExplorer.Models;
using RainExplorer.Services;
using RainExplorer.ViewModels;
using RainExplorer.Views;

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
        _panes = [PaneAppearance, PaneBrowsing, PaneFiles, PaneShortcuts, PaneAdvanced, PaneAbout];
        DataContext = S;

        SectionList.SelectionChanged += (_, _) =>
        {
            if (_searching) SettingsSearch.Text = string.Empty;
            ShowSection(SectionList.SelectedIndex);
        };
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
        BuildShortcutRows();
        SettingsSearch.TextChanged += (_, _) => RunSearch();
        ShortcutService.Changed += BuildShortcutRows;
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
        if (ButtonToggle.JustClosed(ImportFontButton)) return;
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
        ButtonToggle.Track(menu, ImportFontButton);
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

    // ---- Search -------------------------------------------------------------------------------------

    private static readonly string[] PageNames = ["Appearance", "Browsing", "Files", "Shortcuts", "Advanced", "About"];
    private bool _searching;

    private sealed record SearchHit(int Page, Border Card, string Title, string Where, string Hint);

    /// <summary>Every settings card on every page, with its title, hints and section, for searching.</summary>
    private List<SearchHit> IndexCards()
    {
        var hits = new List<SearchHit>();
        for (int page = 0; page < _panes.Length; page++)
        {
            if (_panes[page].Content is not Control root) continue;
            string section = "";
            foreach (var control in root.GetLogicalDescendants().OfType<Control>())
            {
                if (control is TextBlock { Classes: var c } label && c.Contains("sectionLabel"))
                {
                    section = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase((label.Text ?? "").ToLowerInvariant());
                    continue;
                }
                if (control is not Border card || !card.Classes.Contains("card")) continue;
                var texts = card.GetLogicalDescendants().OfType<TextBlock>().Where(t => !string.IsNullOrWhiteSpace(t.Text)).ToList();
                string title = texts.FirstOrDefault(t => t.Classes.Contains("rowTitle"))?.Text ?? texts.FirstOrDefault()?.Text ?? "";
                if (title.Length == 0) continue;
                string hint = string.Join(" ", texts.Where(t => t.Classes.Contains("rowHint")).Select(t => t.Text));
                string where = section.Length > 0 ? $"{PageNames[page]} › {section}" : PageNames[page];
                // Shortcut cards also match their keys ("ctrl+t").
                string keys = string.Join(" ", texts.Where(t => !t.Classes.Contains("rowTitle") && !t.Classes.Contains("rowHint")).Select(t => t.Text));
                hits.Add(new SearchHit(page, card, title, where, (hint + " " + keys).Trim()));
            }
        }
        return hits;
    }

    private void RunSearch()
    {
        string query = SettingsSearch.Text?.Trim() ?? "";
        _searching = query.Length > 0;
        if (!_searching)
        {
            PaneSearch.IsVisible = false;
            int page = Math.Max(0, SectionList.SelectedIndex);
            for (int i = 0; i < _panes.Length; i++) _panes[i].IsVisible = i == page;
            return;
        }

        string[] words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = IndexCards()
            .Select(h => (Hit: h, Score: Score(h, words)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Hit)
            .ToList();

        foreach (var pane in _panes) pane.IsVisible = false;
        PaneSearch.IsVisible = true;
        SearchResults.Children.Clear();
        SearchHeading.Text = matches.Count == 1 ? "1 RESULT" : $"{matches.Count} RESULTS";
        foreach (var hit in matches) SearchResults.Children.Add(ResultButton(hit));
        SearchEmpty.Text = $"Nothing matches “{query}”. Try a shorter word.";
        SearchEmpty.IsVisible = matches.Count == 0;
        PaneSearch.Offset = default;
    }

    /// <summary>Every word must appear somewhere; title matches rank first.</summary>
    private static int Score(SearchHit hit, string[] words)
    {
        int score = 0;
        foreach (string word in words)
        {
            var forms = WordForms(word);
            if (forms.Any(f => hit.Title.Contains(f, StringComparison.CurrentCultureIgnoreCase))) score += 3;
            else if (forms.Any(f => hit.Where.Contains(f, StringComparison.CurrentCultureIgnoreCase))) score += 2;
            else if (forms.Any(f => hit.Hint.Contains(f, StringComparison.CurrentCultureIgnoreCase))) score += 1;
            else return 0;
        }
        return score;
    }

    /// <summary>"delete" also finds "deleting", "themes" finds "theme", "scrolling" finds "scroll".</summary>
    internal static string[] WordForms(string word)
    {
        var forms = new List<string> { word };
        void Add(string stem) { if (stem.Length >= 3) forms.Add(stem); }
        if (word.EndsWith("ing", StringComparison.OrdinalIgnoreCase)) Add(word[..^3]);
        if (word.EndsWith("es", StringComparison.OrdinalIgnoreCase)) Add(word[..^2]);
        if (word.EndsWith('s')) Add(word[..^1]);
        if (word.EndsWith('e')) Add(word[..^1]);
        if (word.EndsWith("ed", StringComparison.OrdinalIgnoreCase)) Add(word[..^2]);
        return forms.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private Button ResultButton(SearchHit hit)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock { Text = hit.Title, Classes = { "rowTitle" } });
        var where = new TextBlock { Text = hit.Where, FontSize = 11, Margin = new Thickness(0, 2, 0, 0) };
        where.Bind(TextBlock.ForegroundProperty, where.GetResourceObservable("AccentBright"));
        content.Children.Add(where);
        if (hit.Hint.Length > 0)
            content.Children.Add(new TextBlock { Text = hit.Hint, Classes = { "rowHint" }, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
        var button = new Button { Content = content, Classes = { "searchResult" }, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        button.Theme = (Avalonia.Styling.ControlTheme?)this.FindResourceOrDefault("BareButton");
        Avalonia.Automation.AutomationProperties.SetName(button, $"{hit.Title}, in {hit.Where}");
        button.Click += (_, _) => GoTo(hit);
        return button;
    }

    /// <summary>Leave search, open the page and point at the setting.</summary>
    private void GoTo(SearchHit hit)
    {
        // Leave search mode right here (not via the search box's change event), so the page is visible
        // before the card is scrolled to.
        _searching = false;
        SettingsSearch.Text = string.Empty;
        PaneSearch.IsVisible = false;
        OpenPage(hit.Page);
        for (int i = 0; i < _panes.Length; i++) _panes[i].IsVisible = i == hit.Page;
        _selectedSection = hit.Page;
        Dispatcher.UIThread.Post(() =>
        {
            hit.Card.BringIntoView();
            hit.Card.Classes.Add("found");
            DispatcherTimer.RunOnce(() => hit.Card.Classes.Remove("found"), TimeSpan.FromMilliseconds(1600));
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Jump to the best match for <paramref name="query"/> ("Show me" in What's new). Empty: focus search.</summary>
    public bool Reveal(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            Dispatcher.UIThread.Post(FocusSearch, DispatcherPriority.Loaded);
            return true;
        }
        string[] words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var best = IndexCards().Select(h => (Hit: h, Score: Score(h, words)))
            .Where(x => x.Score > 0).OrderByDescending(x => x.Score).Select(x => x.Hit).FirstOrDefault();
        if (best is null)
        {
            SettingsSearch.Text = query;
            return false;
        }
        GoTo(best);
        return true;
    }

    /// <summary>Ctrl+F while Settings is open.</summary>
    public void FocusSearch()
    {
        SettingsSearch.Focus();
        SettingsSearch.SelectAll();
    }

    /// <summary>Escape: clear the search first; returns false when there was nothing to clear.</summary>
    public bool HandleEscape()
    {
        if (ShortcutService.IsRecording) return true;
        if (string.IsNullOrEmpty(SettingsSearch.Text)) return false;
        SettingsSearch.Text = string.Empty;
        return true;
    }

    // ---- Shortcuts page ----------------------------------------------------------------------------

    /// <summary>The Settings page index of each section, for code that opens a page directly.</summary>
    public const int ShortcutsPage = 3;

    private string? _recordingId;
    private TextBlock? _recordingHint;

    private void BuildShortcutRows()
    {
        ShortcutRows.Children.Clear();
        foreach (var group in ShortcutService.Actions.GroupBy(a => a.Group))
        {
            ShortcutRows.Children.Add(new TextBlock { Text = group.Key.ToUpperInvariant(), Classes = { "sectionLabel" } });
            bool first = true;
            foreach (var action in group)
            {
                var card = new Border { Classes = { "card" } };
                if (first) card.Classes.Add("first");
                first = false;
                card.Child = ShortcutRow(action);
                ShortcutRows.Children.Add(card);
            }
        }
        ResetAllShortcutsButton.IsEnabled = ShortcutService.Actions.Any(a => ShortcutService.IsCustomized(a.Id));
    }

    private Grid ShortcutRow(ShortcutAction action)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Classes = { "row" } };
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = action.Name, Classes = { "rowTitle" } });
        var keys = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var gestures = ShortcutService.GesturesFor(action.Id);
        if (gestures.Count == 0) keys.Children.Add(new TextBlock { Text = "None", Classes = { "rowHint" } });
        foreach (var gesture in gestures)
        {
            var chip = Views.ShortcutsSheet.KeyChip(ShortcutService.Describe(gesture));
            chip.Margin = new Thickness(0, 0, 6, 0);
            keys.Children.Add(chip);
        }
        text.Children.Add(keys);
        var hint = new TextBlock { Classes = { "rowHint" }, IsVisible = false, Margin = new Thickness(0, 6, 0, 0) };
        text.Children.Add(hint);
        row.Children.Add(text);

        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        if (ShortcutService.IsCustomized(action.Id))
        {
            var reset = new Button { Content = "Reset", MinWidth = 0, Padding = new Thickness(12, 7) };
            reset.Theme = (Avalonia.Styling.ControlTheme?)this.FindResourceOrDefault("GhostButton");
            ToolTip.SetTip(reset, "Back to " + string.Join(", ", action.Defaults.Select(d =>
                ShortcutService.TryParse(d, out var g) ? ShortcutService.Describe(g) : d)));
            reset.Click += (_, _) => ShortcutService.Reset(action.Id);
            buttons.Children.Add(reset);
        }
        var change = new Button { Content = "Change", MinWidth = 0, Padding = new Thickness(14, 7) };
        change.Theme = (Avalonia.Styling.ControlTheme?)this.FindResourceOrDefault("DialogButton");
        Avalonia.Automation.AutomationProperties.SetName(change, "Change the shortcut for " + action.Name);
        change.Click += (_, _) => StartRecording(action.Id, hint, change);
        buttons.Children.Add(change);
        Grid.SetColumn(buttons, 1);
        row.Children.Add(buttons);
        return row;
    }

    private void StartRecording(string id, TextBlock hint, Button change)
    {
        StopRecording();
        _recordingId = id;
        _recordingHint = hint;
        hint.Text = "Press the new shortcut… (Esc cancels, Backspace turns it off)";
        hint.IsVisible = true;
        change.Content = "Listening…";
        ShortcutService.Recorder = e => RecordShortcut_KeyDown(this, e);
        ShortcutService.IsRecording = true;
    }

    private void StopRecording()
    {
        ShortcutService.IsRecording = false;
        ShortcutService.Recorder = null;
        bool wasRecording = _recordingId is not null;
        _recordingId = null;
        _recordingHint = null;
        if (wasRecording) BuildShortcutRows();
    }

    private async void RecordShortcut_KeyDown(object? sender, KeyEventArgs e)
    {
        if (_recordingId is not { } id) return;
        e.Handled = true;
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin or Key.System) return;   // wait for the actual key
        var mods = e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Meta);
        if (e.Key == Key.Escape && mods == KeyModifiers.None) { StopRecording(); return; }
        if (e.Key == Key.Back && mods == KeyModifiers.None)
        {
            ShortcutService.IsRecording = false;
            ShortcutService.Recorder = null;
            _recordingId = null;
            ShortcutService.Set(id, null);
            return;
        }

        var gesture = new KeyGesture(e.Key == Key.Return ? Key.Enter : e.Key, mods);
        if (ShortcutService.Validate(gesture) is { } problem)
        {
            if (_recordingHint is { } hint) hint.Text = "⚠️ " + problem + " Try another, or Esc to cancel.";
            return;
        }
        ShortcutService.IsRecording = false;
        ShortcutService.Recorder = null;
        _recordingId = null;
        if (ShortcutService.ConflictFor(id, gesture) is { } other)
        {
            string key = ShortcutService.Describe(gesture);
            bool move = await Views.ConfirmDialog.Ask(TopLevel.GetTopLevel(this) as Window, "Shortcut already in use",
                $"{key} is already the shortcut for “{other.Name}”. Use it for “{ShortcutService.Find(id)?.Name}” instead? " +
                $"“{other.Name}” will lose {key}.", "Use it here", "Cancel");
            if (!move) { BuildShortcutRows(); return; }
            ShortcutService.RemoveGesture(other.Id, gesture);
        }
        ShortcutService.Set(id, gesture);
    }

    private async void ResetAllShortcuts_Click(object? sender, RoutedEventArgs e)
    {
        if (await Views.ConfirmDialog.Ask(TopLevel.GetTopLevel(this) as Window, "Reset all shortcuts",
                "Put every keyboard shortcut back to Rain's defaults?", "Reset all", "Cancel"))
            ShortcutService.ResetAll();
    }

    /// <summary>Open a given page (used by the shortcuts sheet's Customize button).</summary>
    public void OpenPage(int index) => SectionList.SelectedIndex = index;

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

    private void ShowBackupStatus(string text)
    {
        BackupStatus.Text = text;
        BackupStatus.IsVisible = true;
    }

    private async void BackupSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Back up Rain Explorer settings",
            SuggestedFileName = $"rain-explorer-{DateTime.Now:yyyy-MM-dd}.{SettingsBackupService.FileExtension}",
            DefaultExtension = SettingsBackupService.FileExtension,
            FileTypeChoices =
            [
                new FilePickerFileType("Rain Explorer backup") { Patterns = [$"*.{SettingsBackupService.FileExtension}"] },
            ],
        });
        if (file is null) return;
        try
        {
            SettingsStore.Instance.Flush();
            string json = SettingsBackupService.Create(S, SettingsStore.Instance.ConfigDir);
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            await writer.WriteAsync(json);
            ShowBackupStatus($"✓ Backed up to {PathUtil.Tildify(file.TryGetLocalPath() ?? file.Name)}.");
        }
        catch (Exception ex)
        {
            ShowBackupStatus($"⚠️ Couldn't save the backup: {ex.Message}");
        }
    }

    private async void RestoreSettings_Click(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner?.StorageProvider is not { CanOpen: true } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Restore Rain Explorer settings",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Rain Explorer backup") { Patterns = [$"*.{SettingsBackupService.FileExtension}", "*.json"] },
                FilePickerFileTypes.All,
            ],
        });
        if (files.Count == 0) return;
        try
        {
            string json;
            await using (var stream = await files[0].OpenReadAsync())
            using (var reader = new StreamReader(stream))
                json = await reader.ReadToEndAsync();
            var (settingsJson, sortsJson, summary) = SettingsBackupService.Read(json);
            bool ok = await Views.ConfirmDialog.Ask(owner, "Restore this backup?",
                SettingsBackupService.Describe(summary) + "\n\nRestoring replaces all of your current settings, then Rain restarts. " +
                "Your current settings are kept as settings.json" + SettingsBackupService.BeforeRestoreSuffix + " in the settings folder.",
                "Restore and restart", "Cancel");
            if (!ok) return;
            SettingsStore.Instance.Flush();
            SettingsBackupService.WriteRestore(SettingsStore.Instance.ConfigDir, settingsJson, sortsJson);
            SettingsStore.Instance.Freeze();
            App.RestartOnExit = true;
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        }
        catch (InvalidDataException ex)
        {
            ShowBackupStatus($"⚠️ {ex.Message}");
        }
        catch (Exception ex)
        {
            ShowBackupStatus($"⚠️ Couldn't restore the backup: {ex.Message}");
        }
    }

    private void OpenConfigFolder_Click(object? sender, RoutedEventArgs e)
    {
        string configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RainExplorer");
        if (TopLevel.GetTopLevel(this)?.DataContext is MainViewModel vm)
        {
            vm.IsSettingsOpen = false;
            vm.ActivePane.NewTab(configDir, activate: true);
        }
    }

    private void WhatsNew_Click(object? sender, RoutedEventArgs e) => (TopLevel.GetTopLevel(this) as MainWindow)?.ShowWhatsNew();

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
