using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using RainExplorer.Models;
using RainExplorer.Services;

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

        // Installed UI fonts, alphabetised.
        FontBox.ItemsSource = System.Windows.Media.Fonts.SystemFontFamilies
            .Select(f => f.Source)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Prefer the informational version (e.g. "1.0.0-PreRelease"); fall back to the numeric one.
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
            VersionText.Text = v is null ? "v1.0" : $"v{v.Major}.{v.Minor}.{v.Build}";
        }

        ConfigPathText.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RainExplorer", "settings.json");

        _panes = new[] { PaneAppearance, PaneBrowsing, PaneFiles, PaneAdvanced, PaneAbout };

        DataContext = SettingsStore.Instance.Settings;
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
}
