using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using RainExplorer.Services;

namespace RainExplorer.Views;

/// <summary>
/// "Update available" dialog, ported from the Windows build. What the main button does depends on how Rain
/// was installed: an AppImage is replaced in place and restarted, a .deb is downloaded and opened with the
/// system's package installer, and other installs open the release page.
/// </summary>
public sealed class UpdateDialog : DialogWindow
{
    private readonly UpdateInfo _info;
    private readonly InstallKind _kind = UpdateService.CurrentInstallKind;
    private readonly TextBlock _status;
    private readonly ProgressBar _progress;
    private readonly Button _primary;
    private readonly Button _skip;
    private readonly Button _later;
    private CancellationTokenSource? _cts;

    private UpdateDialog(UpdateInfo info) : base("Update available", "download", width: 500)
    {
        _info = info;
        var body = new StackPanel();
        body.Children.Add(Message(
            $"Rain Explorer {info.Version}{(info.IsPrerelease ? " (beta)" : "")} is available. "
            + $"You have {UpdateService.CurrentVersionString}.", "Text"));

        if (!string.IsNullOrWhiteSpace(info.Notes))
        {
            var notes = new SelectableTextBlock
            {
                Text = info.Notes.Trim(),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
            };
            notes.Bind(SelectableTextBlock.ForegroundProperty, notes.GetResourceObservable("TextDim"));
            var scroller = new ScrollViewer { Content = notes, MaxHeight = 260, Padding = new Thickness(12, 10) };
            var frame = new Border
            {
                Child = scroller,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(0, 8, 0, 0),
            };
            frame.Bind(Border.BackgroundProperty, frame.GetResourceObservable("Glass2"));
            frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable("Line1"));
            body.Children.Add(frame);
        }

        _progress = new ProgressBar
        {
            Minimum = 0, Maximum = 1, Height = 6, IsVisible = false,
            Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _status = Message(_kind switch
        {
            InstallKind.AppImage => "Rain will replace this AppImage and restart.",
            InstallKind.Deb => "Rain will download the .deb package and open it with your software installer.",
            _ => "This copy of Rain can't update itself. The release page will open so you can download it.",
        }, "TextMuted", top: 12);
        body.Children.Add(_progress);
        body.Children.Add(_status);
        Body = body;

        _skip = AddButton("Skip this version", () =>
        {
            SettingsStore.Instance.Settings.SkippedUpdateVersion = _info.Version;
            SettingsStore.Instance.Flush();
            Finish(null);
        }, DialogButtonKind.Ghost);
        _later = AddButton("Later", () =>
        {
            _cts?.Cancel();
            Finish(null);
        });
        _primary = AddButton(_kind switch
        {
            InstallKind.AppImage => "Update and restart",
            InstallKind.Deb => "Download and install",
            _ => "Open release page",
        }, () => _ = Run(), DialogButtonKind.Primary, isDefault: true);
        Closing += (_, _) => _cts?.Cancel();
    }

    public static Task ShowFor(UpdateInfo info, Window? owner) => new UpdateDialog(info).ShowFor<object>(owner);

    private async Task Run()
    {
        if (_kind == InstallKind.Manual || _info.DownloadUrl is null)
        {
            UpdateService.OpenUrl(_info.HtmlUrl);
            Finish(null);
            return;
        }

        string? appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        string directory = _kind == InstallKind.AppImage && appImage is not null
            ? Path.GetDirectoryName(appImage) ?? XdgDirs.Downloads
            : XdgDirs.Downloads;

        _cts = new CancellationTokenSource();
        _primary.IsEnabled = false;
        _skip.IsEnabled = false;
        _later.Content = "Cancel";
        _progress.IsVisible = true;
        _progress.Value = 0;
        _status.Text = "Downloading…";
        var progress = new Progress<double>(v => _progress.Value = v);

        string? file = await UpdateService.DownloadAsync(_info, directory, progress, _cts.Token);
        if (_cts.IsCancellationRequested) return;
        if (file is null)
        {
            Failed("The download failed or didn't match its checksum. Nothing was changed.");
            return;
        }

        if (_kind == InstallKind.AppImage && appImage is not null)
        {
            if (UpdateService.ReplaceAppImage(file, appImage) is { } error)
            {
                try { File.Delete(file); } catch { }
                Failed($"Couldn't replace the AppImage: {error}");
                return;
            }
            _status.Text = "Updated. Restarting…";
            App.RelaunchOnExit = appImage;
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
            return;
        }

        // .deb: the desktop's package installer asks for permission and installs it.
        try
        {
            ProcessLauncher.OpenDefault(file);
            _status.Text = $"Downloaded to {file}. Finish the install in your software installer "
                           + $"(or run: sudo apt install \"{file}\"), then restart Rain.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Downloaded to {file}, but no installer could open it ({ex.Message}). "
                           + $"Install it with: sudo apt install \"{file}\"";
        }
        _progress.IsVisible = false;
        _later.Content = "Close";
    }

    private void Failed(string message)
    {
        _status.Text = $"⚠️ {message}";
        _progress.IsVisible = false;
        _primary.IsEnabled = true;
        _skip.IsEnabled = true;
        _later.Content = "Later";
    }
}
