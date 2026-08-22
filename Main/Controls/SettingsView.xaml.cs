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

        // Installed UI fonts, alphabetised.
        _installedFonts = System.Windows.Media.Fonts.SystemFontFamilies
            .Select(f => f.Source)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
        FontBox.ItemsSource = _installedFonts;

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
            VersionText.Text = v is null ? "v1.2.1" : $"v{v.Major}.{v.Minor}.{v.Build}";
        }

        ConfigPathText.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RainExplorer", "settings.json");

        _panes = new[] { PaneAppearance, PaneBrowsing, PaneFiles, PaneAdvanced, PaneAbout };

        DataContext = SettingsStore.Instance.Settings;
        FontBox.SelectedItem = SettingsStore.Instance.Settings.FontFamily;
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
