using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using RainExplorer.Models;
using RainExplorer.Services;
using RainExplorer.ViewModels;
using RainExplorer.Views;

namespace RainExplorer.Controls;

/// <summary>
/// Full-page settings overlay. A pinned header + a left-nav of sections, each
/// section a scrolling pane. Binds straight to the live <see cref="AppSettings"/>
/// (which auto-saves), so every change applies instantly.
/// </summary>
public partial class SettingsView : UserControl
{
    public sealed record ThemeOption(string Name, Brush Swatch);

    private ScrollViewer[] _panes = System.Array.Empty<ScrollViewer>();
    private List<string> _installedFonts = [];
    private bool _filteringFonts;

    public SettingsView()
    {
        InitializeComponent();

        ThemeList.ItemsSource = ThemeService.Names
            .Select(n => new ThemeOption(n, ThemeService.Swatch(n)))
            .ToList();
        DensityBox.ItemsSource = Enum.GetValues(typeof(ViewDensity));
        RenameBox.ItemsSource = Enum.GetValues(typeof(RenameMode));
        SizeFormatBox.ItemsSource = Enum.GetValues(typeof(SizeFormat));
        LayoutBox.ItemsSource = Enum.GetValues(typeof(ViewLayout));
        DeleteBehaviorBox.ItemsSource = Enum.GetValues(typeof(DeleteBehavior));
        TerminalAppBox.ItemsSource = Enum.GetValues(typeof(TerminalApp));
        ScrollSpeedBox.ItemsSource = Enum.GetValues(typeof(ScrollSpeed));

        LoadFontList();

        // Prefer the informational version (e.g. "1.2.0-PreRelease"); fall back to the numeric one.
        var info = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            int plus = info.IndexOf('+');   // strip any "+<git hash>" build metadata
            VersionText.Text = "v" + (plus >= 0 ? info[..plus] : info);
        }
        else
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            VersionText.Text = v is null ? "v2.0.0" : $"v{v.Major}.{v.Minor}.{v.Build}";
        }

        ConfigPathText.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RainExplorer", "settings.json");

        _panes = new[] { PaneAppearance, PaneBrowsing, PaneFiles, PaneShortcuts, PaneAdvanced, PaneAbout };
        BuildShortcutRows();
        // A static event: subscribe only while this window's Settings is alive.
        Loaded += (_, _) =>
        {
            ShortcutService.Changed -= BuildShortcutRows;
            ShortcutService.Changed += BuildShortcutRows;
            BuildShortcutRows();
        };
        Unloaded += (_, _) =>
        {
            ShortcutService.Changed -= BuildShortcutRows;
            StopRecording();
        };

        DataContext = SettingsStore.Instance.Settings;
        FontBox.SelectedItem = SettingsStore.Instance.Settings.FontFamily;
        RefreshArchiveDefaultsStatus();
        BindRetention();
    }

    // ---- Detailed activity history ---------------------------------------------------------------

    internal static readonly (int Days, string Label)[] RetentionChoices =
    [
        (1, "1 day"), (3, "3 days"), (7, "7 days"), (30, "30 days"), (90, "90 days"), (0, "Until I clear it"),
    ];

    private bool _syncingRetention;

    private void BindRetention()
    {
        _syncingRetention = true;
        RetentionBox.ItemsSource = RetentionChoices.Select(c => c.Label).ToList();
        int index = Array.FindIndex(RetentionChoices,
            c => c.Days == SettingsStore.Instance.Settings.ActivityRetentionDays);
        RetentionBox.SelectedIndex = index >= 0 ? index : 2;
        _syncingRetention = false;
    }

    private void RetentionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingRetention || RetentionBox.SelectedIndex < 0) return;
        SettingsStore.Instance.Settings.ActivityRetentionDays = RetentionChoices[RetentionBox.SelectedIndex].Days;
    }

    private void ExportActivity_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export activity history",
            FileName = $"Rain Explorer activity {DateTime.Now:yyyy-MM-dd HHmm}.txt",
            Filter = "Text file (*.txt)|*.txt",
            DefaultExt = ".txt",
        };
        if (dialog.ShowDialog(System.Windows.Window.GetWindow(this)) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, ActivityService.Instance.ExportText(),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            ExportActivityStatus.Text = $"Saved {ActivityService.Instance.Items.Count} activities to {dialog.FileName}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ExportActivityStatus.Text = $"⚠️ Couldn't save the file: {ex.Message}";
        }
    }

    /// <summary>Installed UI fonts plus fonts imported "for me", alphabetised.</summary>
    private void LoadFontList()
    {
        _installedFonts = System.Windows.Media.Fonts.SystemFontFamilies
            .Select(f => f.Source)
            .Concat(ImportedFontCatalog.Families.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
        FontBox.ItemsSource = _installedFonts;
    }

    // ---- Import font (ported from the Linux build) -------------------------------------------------

    private void ImportFont_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private async void ImportFontFromFile_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import fonts",
            Multiselect = true,
            Filter = "Fonts and zip files (*.ttf;*.otf;*.ttc;*.otc;*.zip)|*.ttf;*.otf;*.ttc;*.otc;*.zip",
        };
        if (dialog.ShowDialog(System.Windows.Window.GetWindow(this)) != true) return;
        await RunFontImport(FontImportService.ImportFilesAsync(dialog.FileNames));
    }

    private async void ImportFontFromLink_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new InputDialog("Import font from a link",
            "Direct link to a .ttf, .otf or .zip file:", "https://")
        {
            Owner = System.Windows.Window.GetWindow(this),
        };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Value)) return;
        await RunFontImport(FontImportService.ImportFromUrlAsync(dialog.Value));
    }

    private void OpenFontsFolder_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(FontImportService.ImportFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{FontImportService.ImportFolder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { FontImportStatus.Text = $"⚠️ {ex.Message}"; }
    }

    private void FontCard_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        bool fonts = e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files
                     && files.Any(FontImportService.IsImportable);
        e.Effects = fonts ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private async void FontCard_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] files) return;
        var importable = files.Where(FontImportService.IsImportable).ToList();
        e.Handled = true;
        if (importable.Count == 0)
        {
            FontImportStatus.Text = "Drop .ttf, .otf, .ttc or .zip files to import them.";
            return;
        }
        await RunFontImport(FontImportService.ImportFilesAsync(importable));
    }

    private async Task RunFontImport(Task<FontImportResult> import)
    {
        ImportFontButton.IsEnabled = false;
        FontImportStatus.Text = "Importing…";
        try
        {
            FontImportResult result = await import;
            if (!result.Succeeded)
            {
                FontImportStatus.Text = $"⚠️ {result.Error}";
                return;
            }
            LoadFontList();
            FontSearchBox.Text = string.Empty;
            string first = result.Families[0];
            string names = result.Families.Count == 1 ? first : $"{first} and {result.Families.Count - 1} more";
            string skipped = result.Skipped.Count > 0 ? $" Skipped {result.Skipped.Count} file(s) that weren't fonts." : "";
            FontImportStatus.Text = $"Imported {names}. It's selected now and other apps can use it too.{skipped}";
            SettingsStore.Instance.Settings.FontFamily = first;
            FontBox.SelectedItem = _installedFonts.FirstOrDefault(f => string.Equals(f, first, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            FontImportStatus.Text = $"⚠️ {ex.Message}";
        }
        finally
        {
            ImportFontButton.IsEnabled = true;
        }
    }

    private void FontSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        string query = FontSearchBox.Text.Trim();
        IEnumerable<string> matches = _installedFonts;
        if (query.Length > 0)
        {
            matches = _installedFonts
                .Where(font => font.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(font => font.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(font => font, StringComparer.OrdinalIgnoreCase);
        }

        string selected = SettingsStore.Instance.Settings.FontFamily;
        var filtered = matches.ToList();
        _filteringFonts = true;
        FontBox.ItemsSource = filtered;
        FontBox.SelectedItem = filtered.Contains(selected, StringComparer.OrdinalIgnoreCase)
            ? selected
            : null;
        _filteringFonts = false;

        if (query.Length > 0 && FontSearchBox.IsKeyboardFocusWithin)
            FontBox.IsDropDownOpen = true;
    }

    private void FontBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filteringFonts || FontBox.SelectedItem is not string font) return;
        SettingsStore.Instance.Settings.FontFamily = font;
    }

    // ---- Section nav: show the selected pane, hide the rest ----------------
    private void SectionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_searching) SettingsSearch.Text = string.Empty;
        int idx = SectionList.SelectedIndex;
        if (idx < 0 || _panes.Length == 0) return;
        for (int i = 0; i < _panes.Length; i++)
            _panes[i].Visibility = i == idx
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
    }

    private void Browse_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose default starting folder" };
        string current = SettingsStore.Instance.Settings.DefaultFolder;
        if (Directory.Exists(current)) dlg.InitialDirectory = current;
        if (dlg.ShowDialog() == true)
            SettingsStore.Instance.Settings.DefaultFolder = dlg.FolderName;
    }

    private void NewSidebarList_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dlg = new InputDialog("New sidebar list", "List name:", "")
        {
            Owner = System.Windows.Window.GetWindow(this),
        };
        if (dlg.ShowDialog() == true)
            MainViewModel.AddCustomGroup(dlg.Value);
    }

    // ---- Manual "Check for updates" ----------------------------------------
    private async void CheckUpdates_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking for updates…";
        try
        {
            bool beta = SettingsStore.Instance.Settings.BetaUpdates;
            var info = await UpdateService.CheckAsync(beta);
            if (info is null)
            {
                UpdateStatusText.Text = $"You're on the latest version (v{UpdateService.CurrentVersionString}).";
            }
            else
            {
                UpdateStatusText.Text = $"Version {info.Version} is available.";
                UpdateDialog.ShowFor(info, System.Windows.Window.GetWindow(this));
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

    private void ArchiveDefaults_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        ArchiveDefaultsButton.IsEnabled = false;
        try
        {
            ArchiveAssociationRegistrationResult result =
                ArchiveFileAssociationService.RegisterForCurrentUser();
            if (result.Outcome == ArchiveAssociationRegistrationOutcome.Conflict)
            {
                ArchiveDefaultsStatusText.Text =
                    "Rain Explorer's archive-handler identity is already owned by another registration. Nothing was changed.";
                return;
            }
            if (result.Outcome != ArchiveAssociationRegistrationOutcome.Completed)
            {
                ArchiveDefaultsStatusText.Text =
                    "Rain Explorer couldn't register its archive handler. Nothing changed your current defaults.";
                return;
            }

            ArchiveDefaultsStatusText.Text =
                ArchiveFileAssociationService.OpenWindowsDefaultAppsPage()
                    ? "Ready. In Windows, press Set default once to use Rain Explorer for every listed archive type."
                    : "Rain Explorer is registered, but Windows Settings couldn't open. Open Settings > Apps > Default apps to finish.";
        }
        finally
        {
            ArchiveDefaultsButton.IsEnabled = true;
        }
    }

    private void RefreshArchiveDefaultsStatus()
    {
        ArchiveDefaultsStatusText.Text =
            ArchiveFileAssociationService.GetRegistrationState() switch
            {
                ArchiveAssociationRegistrationState.Registered =>
                    "Ready to apply. Choose Set as default, then press Set default once in Windows.",
                ArchiveAssociationRegistrationState.NeedsRepair =>
                    "Rain Explorer's registration needs repair. Choose Set as default to repair it.",
                ArchiveAssociationRegistrationState.Conflict =>
                    "Rain Explorer's archive-handler identity is owned by another registration. Nothing will be overwritten.",
                ArchiveAssociationRegistrationState.Unavailable =>
                    "Windows archive-handler registration isn't available in this session.",
                _ =>
                    "Choose Set as default. Windows requires one final Set default confirmation.",
            };
    }

    private void OpenConfigFolder_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RainExplorer");
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch { /* best effort */ }
    }

    private void BrowseGit_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose git.exe",
            Filter = "Git executable (git.exe)|git.exe|Applications (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false,
        };
        string current = SettingsStore.Instance.Settings.GitExecutablePath;
        if (File.Exists(current))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(current);
            dialog.FileName = current;
        }
        if (dialog.ShowDialog() == true)
            SettingsStore.Instance.Settings.GitExecutablePath = dialog.FileName;
    }

    // ---- Search -------------------------------------------------------------------------------------

    private static readonly string[] PageNames = ["Appearance", "Browsing", "Files", "Shortcuts", "Advanced", "About"];
    private bool _searching;

    private sealed record SearchHit(int Page, Border Card, string Title, string Where, string Hint);

    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject d) continue;
            yield return d;
            foreach (var inner in LogicalDescendants(d)) yield return inner;
        }
    }

    /// <summary>Every settings card on every page, with its title, hints and section, for searching.</summary>
    private List<SearchHit> IndexCards()
    {
        var cardStyle = (Style)FindResource("Card");
        var titleStyle = (Style)FindResource("RowTitle");
        var hintStyle = (Style)FindResource("RowHint");
        var sectionStyle = (Style)FindResource("SectionLabel");
        var hits = new List<SearchHit>();
        for (int page = 0; page < _panes.Length; page++)
        {
            string section = "";
            foreach (var node in LogicalDescendants(_panes[page]))
            {
                if (node is TextBlock label && label.Style == sectionStyle)
                {
                    section = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(label.Text.ToLowerInvariant());
                    continue;
                }
                if (node is not Border card || card.Style != cardStyle) continue;
                var texts = LogicalDescendants(card).OfType<TextBlock>()
                    .Where(t => !string.IsNullOrWhiteSpace(t.Text) && t.Visibility == Visibility.Visible).ToList();
                string title = texts.FirstOrDefault(t => t.Style == titleStyle)?.Text ?? texts.FirstOrDefault()?.Text ?? "";
                if (title.Length == 0) continue;
                string hint = string.Join(" ", texts.Where(t => t.Style == hintStyle).Select(t => t.Text));
                string where = section.Length > 0 ? $"{PageNames[page]} › {section}" : PageNames[page];
                // Shortcut cards also match their keys ("ctrl+t").
                string keys = string.Join(" ", texts.Where(t => t.Style != titleStyle && t.Style != hintStyle).Select(t => t.Text));
                hits.Add(new SearchHit(page, card, title, where, (hint + " " + keys).Trim()));
            }
        }
        return hits;
    }

    private void SettingsSearch_TextChanged(object sender, TextChangedEventArgs e) => RunSearch();

    private void RunSearch()
    {
        if (_panes.Length == 0) return;
        string query = SettingsSearch.Text?.Trim() ?? "";
        _searching = query.Length > 0;
        if (!_searching)
        {
            PaneSearch.Visibility = Visibility.Collapsed;
            int page = Math.Max(0, SectionList.SelectedIndex);
            for (int i = 0; i < _panes.Length; i++) _panes[i].Visibility = i == page ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        string[] words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = IndexCards()
            .Select(h => (Hit: h, Score: Score(h, words)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Hit)
            .ToList();

        foreach (var pane in _panes) pane.Visibility = Visibility.Collapsed;
        PaneSearch.Visibility = Visibility.Visible;
        SearchResults.Children.Clear();
        SearchHeading.Text = matches.Count == 1 ? "1 RESULT" : $"{matches.Count} RESULTS";
        foreach (var hit in matches) SearchResults.Children.Add(ResultButton(hit));
        SearchEmpty.Text = $"Nothing matches “{query}”. Try a shorter word.";
        SearchEmpty.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PaneSearch.ScrollToTop();
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
        content.Children.Add(new TextBlock { Text = hit.Title, Style = (Style)FindResource("RowTitle") });
        var where = new TextBlock { Text = hit.Where, FontSize = 11, Margin = new Thickness(0, 2, 0, 0) };
        where.SetResourceReference(TextBlock.ForegroundProperty, "AccentBright");
        content.Children.Add(where);
        if (hit.Hint.Length > 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = hit.Hint, Style = (Style)FindResource("RowHint"),
                MaxHeight = 34, TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }
        var card = new Border { Style = (Style)FindResource("Card"), Child = content, Padding = new Thickness(16, 12, 16, 12) };
        var button = new Button
        {
            Content = card, Margin = new Thickness(0, 0, 0, 8), Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Template = BareButtonTemplate(),
        };
        System.Windows.Automation.AutomationProperties.SetName(button, $"{hit.Title}, in {hit.Where}");
        button.Click += (_, _) => GoTo(hit);
        return button;
    }

    /// <summary>A button that is just its content (the card inside draws the look, and lights up on hover).</summary>
    private static ControlTemplate BareButtonTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        template.VisualTree = presenter;
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.85));
        template.Triggers.Add(hover);
        return template;
    }

    /// <summary>Leave search, open the page and point at the setting.</summary>
    private void GoTo(SearchHit hit)
    {
        // Leave search mode right here, so the page is visible before the card is scrolled to.
        _searching = false;
        SettingsSearch.Text = string.Empty;
        PaneSearch.Visibility = Visibility.Collapsed;
        SectionList.SelectedIndex = hit.Page;
        for (int i = 0; i < _panes.Length; i++) _panes[i].Visibility = i == hit.Page ? Visibility.Visible : Visibility.Collapsed;
        Dispatcher.BeginInvoke(() =>
        {
            hit.Card.BringIntoView();
            Highlight(hit.Card);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Briefly outline a card in the accent colour so it's easy to spot.</summary>
    private static void Highlight(Border card)
    {
        card.SetResourceReference(Border.BorderBrushProperty, "AccentBright");
        card.BorderThickness = new Thickness(2);
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            card.ClearValue(Border.BorderBrushProperty);
            card.ClearValue(Border.BorderThicknessProperty);
        };
        timer.Start();
    }

    /// <summary>Jump to the best match for <paramref name="query"/> ("Show me" in What's new). Empty: focus search.</summary>
    public bool Reveal(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            Dispatcher.BeginInvoke(FocusSearch, System.Windows.Threading.DispatcherPriority.Loaded);
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

    /// <summary>Escape: cancel a shortcut recording or clear the search first; false when there was nothing to do.</summary>
    public bool HandleEscape()
    {
        if (ShortcutService.IsRecording) return true;
        if (string.IsNullOrEmpty(SettingsSearch.Text)) return false;
        SettingsSearch.Text = string.Empty;
        return true;
    }

    // ---- Shortcuts page ----------------------------------------------------------------------------

    /// <summary>The Settings page index of the Shortcuts section, for code that opens it directly.</summary>
    public const int ShortcutsPage = 3;

    private string? _recordingId;
    private TextBlock? _recordingHint;

    private void BuildShortcutRows()
    {
        ShortcutRows.Children.Clear();
        foreach (var group in ShortcutService.Actions.GroupBy(a => a.Group))
        {
            ShortcutRows.Children.Add(new TextBlock
            {
                Text = group.Key.ToUpperInvariant(), Style = (Style)FindResource("SectionLabel"),
                Margin = new Thickness(4, 18, 0, 8),
            });
            foreach (var action in group)
            {
                ShortcutRows.Children.Add(new Border
                {
                    Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 0, 8), Child = ShortcutRow(action),
                });
            }
        }
        ResetAllShortcutsButton.IsEnabled = ShortcutService.Actions.Any(a => ShortcutService.IsCustomized(a.Id));
    }

    private Grid ShortcutRow(ShortcutAction action)
    {
        var row = new Grid { Margin = new Thickness(0, 12, 0, 12) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = action.Name, Style = (Style)FindResource("RowTitle") });
        var keys = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var gestures = ShortcutService.GesturesFor(action.Id);
        if (gestures.Count == 0) keys.Children.Add(new TextBlock { Text = "None", Style = (Style)FindResource("RowHint") });
        foreach (var gesture in gestures)
        {
            var chip = ShortcutsSheet.KeyChip(ShortcutService.Describe(gesture));
            chip.Margin = new Thickness(0, 0, 6, 0);
            keys.Children.Add(chip);
        }
        text.Children.Add(keys);
        var hint = new TextBlock { Style = (Style)FindResource("RowHint"), Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
        text.Children.Add(hint);
        row.Children.Add(text);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0),
        };
        if (ShortcutService.IsCustomized(action.Id))
        {
            var reset = new Button
            {
                Content = "Reset", MinWidth = 0, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 6, 0),
                Style = (Style)FindResource("GhostButton"),
                ToolTip = "Back to " + string.Join(", ", action.Defaults.Select(d =>
                    ShortcutService.TryParse(d, out var g) ? ShortcutService.Describe(g) : d)),
            };
            reset.Click += (_, _) => ShortcutService.Reset(action.Id);
            buttons.Children.Add(reset);
        }
        var change = new Button
        {
            Content = "Change", MinWidth = 0, Padding = new Thickness(14, 7, 14, 7), Style = (Style)FindResource("DialogButton"),
        };
        System.Windows.Automation.AutomationProperties.SetName(change, "Change the shortcut for " + action.Name);
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
        hint.Visibility = Visibility.Visible;
        change.Content = "Listening…";
        ShortcutService.Recorder = RecordShortcut_KeyDown;
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

    private void RecordShortcut_KeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (_recordingId is not { } id) return;
        e.Handled = true;
        var key = ShortcutService.RealKey(e);
        if (ShortcutService.IsModifierKey(key)) return;   // wait for the actual key
        var mods = System.Windows.Input.Keyboard.Modifiers;
        if (key == System.Windows.Input.Key.Escape && mods == System.Windows.Input.ModifierKeys.None) { StopRecording(); return; }
        if (key == System.Windows.Input.Key.Back && mods == System.Windows.Input.ModifierKeys.None)
        {
            ShortcutService.IsRecording = false;
            ShortcutService.Recorder = null;
            _recordingId = null;
            ShortcutService.Set(id, null);
            return;
        }

        var gesture = new Shortcut(key, mods);
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
            string keyText = ShortcutService.Describe(gesture);
            // Ask after this key press has finished, so the dialog doesn't receive it.
            Dispatcher.BeginInvoke(() =>
            {
                bool move = ConfirmDialog.Ask(Window.GetWindow(this), "Shortcut already in use",
                    $"{keyText} is already the shortcut for “{other.Name}”. Use it for “{ShortcutService.Find(id)?.Name}” instead? " +
                    $"“{other.Name}” will lose {keyText}.", "Use it here", "Cancel", danger: false);
                if (!move) { BuildShortcutRows(); return; }
                ShortcutService.RemoveGesture(other.Id, gesture);
                ShortcutService.Set(id, gesture);
            });
            return;
        }
        ShortcutService.Set(id, gesture);
    }

    private void ResetAllShortcuts_Click(object sender, RoutedEventArgs e)
    {
        if (ConfirmDialog.Ask(Window.GetWindow(this), "Reset all shortcuts",
                "Put every keyboard shortcut back to Rain's defaults?", "Reset all", "Cancel", danger: false))
            ShortcutService.ResetAll();
    }

    /// <summary>Open a given page (used by the shortcuts sheet's Customize button).</summary>
    public void OpenPage(int index)
    {
        if (_searching) SettingsSearch.Text = string.Empty;
        SectionList.SelectedIndex = index;
    }

    /// <summary>Settings closed: stop listening for a shortcut.</summary>
    public void OnClosed() => StopRecording();

    // ---- Back up and restore -----------------------------------------------------------------------

    private void ShowBackupStatus(string text)
    {
        BackupStatus.Text = text;
        BackupStatus.Visibility = Visibility.Visible;
    }

    private void BackupSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Back up Rain Explorer settings",
            FileName = $"rain-explorer-{DateTime.Now:yyyy-MM-dd}.{SettingsBackupService.FileExtension}",
            DefaultExt = "." + SettingsBackupService.FileExtension,
            Filter = $"Rain Explorer backup (*.{SettingsBackupService.FileExtension})|*.{SettingsBackupService.FileExtension}",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            SettingsStore.Instance.Flush();
            string json = SettingsBackupService.Create(SettingsStore.Instance.Settings, SettingsStore.Instance.ConfigDir);
            File.WriteAllText(dialog.FileName, json, new System.Text.UTF8Encoding(false));
            ShowBackupStatus($"✓ Backed up to {dialog.FileName}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ShowBackupStatus($"⚠️ Couldn't save the backup: {ex.Message}");
        }
    }

    private void RestoreSettings_Click(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        var dialog = new OpenFileDialog
        {
            Title = "Restore Rain Explorer settings",
            Filter = $"Rain Explorer backup (*.{SettingsBackupService.FileExtension};*.json)|*.{SettingsBackupService.FileExtension};*.json|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(owner) != true) return;
        try
        {
            string json = File.ReadAllText(dialog.FileName);
            var (settingsJson, sortsJson, summary) = SettingsBackupService.Read(json);
            bool ok = ConfirmDialog.Ask(owner, "Restore this backup?",
                SettingsBackupService.Describe(summary) + "\n\nRestoring replaces all of your current settings, then Rain restarts. " +
                "Your current settings are kept as settings.json" + SettingsBackupService.BeforeRestoreSuffix + " in the settings folder.",
                "Restore and restart", "Cancel", danger: false);
            if (!ok) return;
            SettingsStore.Instance.Flush();
            SettingsBackupService.WriteRestore(SettingsStore.Instance.ConfigDir, settingsJson, sortsJson);
            SettingsStore.Instance.Freeze();
            App.RestartOnExit = true;
            Application.Current.Shutdown();
        }
        catch (InvalidDataException ex)
        {
            ShowBackupStatus($"⚠️ {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ShowBackupStatus($"⚠️ Couldn't restore the backup: {ex.Message}");
        }
    }

    private void WhatsNew_Click(object sender, RoutedEventArgs e) => (Window.GetWindow(this) as MainWindow)?.ShowWhatsNew();
}
