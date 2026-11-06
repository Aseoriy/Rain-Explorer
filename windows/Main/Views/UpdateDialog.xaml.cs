using System.Threading;
using System.Windows;
using System.Windows.Input;
using RainExplorer.Services;

namespace RainExplorer.Views;

/// <summary>
/// Themed "update available" window: shows the new version and its release notes, then
/// downloads the installer (with a progress bar) and launches it. Falls back to opening
/// the release page in a browser if the release has no downloadable installer.
/// </summary>
public partial class UpdateDialog : Window
{
    private readonly UpdateInfo _info;
    private bool _installing;

    public UpdateDialog(UpdateInfo info)
    {
        InitializeComponent();
        _info = info;

        SubtitleText.Text = $"Rain Explorer {info.Version}  ·  you have {UpdateService.CurrentVersionString}";
        NotesText.Text = string.IsNullOrWhiteSpace(info.Notes)
            ? "No release notes were provided for this version."
            : Clean(info.Notes);
        BetaBadge.Visibility = info.IsPrerelease ? Visibility.Visible : Visibility.Collapsed;
        UpdateButton.Content = info.DownloadUrl is null ? "View release" : "Update now";
    }

    /// <summary>Show the dialog for an available update (owner may be null on startup).</summary>
    public static void ShowFor(UpdateInfo info, Window? owner)
    {
        var dlg = new UpdateDialog(info);
        if (owner is not null && owner.IsLoaded) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dlg.ShowDialog();
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        if (_installing) return;
        DialogResult = false;
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        if (_installing) return;
        SettingsStore.Instance.Settings.SkippedUpdateVersion = _info.Version;
        DialogResult = false;
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        // No installer asset — just open the release page.
        if (_info.DownloadUrl is null)
        {
            UpdateService.OpenUrl(_info.HtmlUrl);
            DialogResult = false;
            return;
        }

        if (_installing) return;
        _installing = true;

        ButtonRow.IsEnabled = false;
        ProgressArea.Visibility = Visibility.Visible;
        var progress = new Progress<double>(v =>
        {
            Progress.Value = v;
            ProgressText.Text = $"Downloading update… {v * 100:0}%";
        });

        string? path = await UpdateService.DownloadAsync(_info, progress, CancellationToken.None);
        if (path is null)
        {
            // Download failed — offer the release page as a fallback.
            ProgressText.Text = "Download failed. Opening the release page instead…";
            UpdateService.OpenUrl(_info.HtmlUrl);
            _installing = false;
            ButtonRow.IsEnabled = true;
            return;
        }

        ProgressText.Text = "Starting installer…";
        UpdateService.RunInstallerAndExit(path);   // launches the setup and closes the app
    }

    private void Title_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    // GitHub bodies are Markdown; strip the noisiest markers for a clean plain-text view.
    private static string Clean(string md)
    {
        var sb = new System.Text.StringBuilder(md.Length);
        foreach (var raw in md.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();
            string t = line.TrimStart();
            if (t.StartsWith("### ")) line = "• " + t[4..];
            else if (t.StartsWith("## ")) line = t[3..];
            else if (t.StartsWith("# ")) line = t[2..];
            else if (t.StartsWith("- ") || t.StartsWith("* ")) line = "  •" + t[1..];
            line = line.Replace("**", "").Replace("`", "");
            sb.AppendLine(line);
        }
        return sb.ToString().Trim();
    }
}
