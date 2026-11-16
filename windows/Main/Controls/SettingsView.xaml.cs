using System.Diagnostics;
using System.IO;
using System.Reflection;
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
            VersionText.Text = v is null ? "v2.0.0-beta-2" : $"v{v.Major}.{v.Minor}.{v.Build}";
        }

        ConfigPathText.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RainExplorer", "settings.json");

        _panes = new[] { PaneAppearance, PaneBrowsing, PaneFiles, PaneAdvanced, PaneAbout };

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
}
