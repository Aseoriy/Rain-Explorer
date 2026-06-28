using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RainExplorer.Models;
using RainExplorer.Services;

namespace RainExplorer.Controls;

/// <summary>
/// Right-hand preview pane. Renders images natively, shows the head of text/code
/// files, and falls back to the Windows shell thumbnail (PDF/video/Office/etc.)
/// or a type icon. Driven by <see cref="ShowItem"/> from the hosting pane.
/// </summary>
public partial class PreviewPane : UserControl
{
    private CancellationTokenSource? _cts;

    private const long TextPreviewCap = 256 * 1024;   // bytes of a text file we read
    private const long FullDecodeCap = 40L * 1024 * 1024;   // above this, decode images downscaled

    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "bmp", "gif", "ico", "tif", "tiff", "webp",
    };

    private static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "md", "markdown", "log", "csv", "tsv", "json", "xml", "yml", "yaml", "toml",
        "ini", "cfg", "conf", "js", "ts", "jsx", "tsx", "css", "scss", "less", "html", "htm",
        "cs", "c", "cpp", "h", "hpp", "py", "rb", "go", "rs", "java", "kt", "swift", "php",
        "sql", "sh", "bat", "cmd", "ps1", "gitignore", "env",
    };

    private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "mkv", "mov", "avi", "webm", "wmv", "m4v", "mpg", "mpeg",
    };

    private static readonly HashSet<string> AudioExt = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp3", "wav", "flac", "ogg", "m4a", "aac", "wma",
    };

    private const uint PdfRenderWidth = 1400;

    public PreviewPane() => InitializeComponent();

    private void Close_Click(object sender, RoutedEventArgs e) =>
        SettingsStore.Instance.Settings.ShowPreviewPane = false;

    /// <summary>Preview a single selected item, or show a hint for none / multi-selection.</summary>
    public async void ShowItem(FileItem? item, int selectionCount = 0)
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        HideContent();

        if (item is null)
        {
            NameText.Text = string.Empty;
            MetaText.Text = string.Empty;
            ShowMessage(selectionCount > 1 ? $"{selectionCount} items selected" : "Select a file to preview", "eye");
            return;
        }

        _current = item;
        NameText.Text = item.Name;
        MetaText.Text = BuildMeta(item);

        if (item.IsDirectory) { ShowMessage("Folder", "folder"); return; }

        string ext = item.Extension.ToLowerInvariant();
        if (VideoExt.Contains(ext)) { ShowMedia(item, video: true); return; }
        if (AudioExt.Contains(ext)) { ShowMedia(item, video: false); return; }

        try
        {
            if (ImageExt.Contains(ext) && await TryShowImage(item, cts.Token)) return;
            if (ext == "pdf" && await TryShowPdf(item, cts.Token)) return;
            if (TextExt.Contains(ext) && await TryShowText(item, cts.Token)) return;
            if (await TryShowThumbnail(item, cts.Token)) return;
        }
        catch (OperationCanceledException) { return; }

        if (!cts.IsCancellationRequested) ShowMessage("No preview available", item.IconKey);
    }

    // ---- Audio / video playback --------------------------------------------
    private FileItem? _current;
    private DispatcherTimer? _mediaTimer;
    private TimeSpan _duration;
    private bool _isPlaying, _mediaIsVideo, _updatingSlider;

    private void ShowMedia(FileItem item, bool video)
    {
        StopMedia();
        _mediaIsVideo = video;
        AudioGlyph.Visibility = video ? Visibility.Collapsed : Visibility.Visible;
        _duration = TimeSpan.Zero;
        Seek.Maximum = 1;
        _updatingSlider = true; Seek.Value = 0; _updatingSlider = false;
        TimeText.Text = "0:00";
        SetPlaying(false);
        Media.Source = new Uri(item.FullPath);
        MediaView.Visibility = Visibility.Visible;
    }

    private void Media_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (Media.NaturalDuration.HasTimeSpan)
        {
            _duration = Media.NaturalDuration.TimeSpan;
            Seek.Maximum = Math.Max(0.1, _duration.TotalSeconds);
        }
        TimeText.Text = $"0:00 / {Fmt(_duration)}";
        if (_mediaIsVideo) { Media.Play(); Media.Pause(); }   // render the first frame as a poster
    }

    private void Media_MediaEnded(object sender, RoutedEventArgs e)
    {
        Media.Pause();
        Media.Position = TimeSpan.Zero;
        _updatingSlider = true; Seek.Value = 0; _updatingSlider = false;
        SetPlaying(false);
        _mediaTimer?.Stop();
    }

    private void Media_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        StopMedia();
        ShowMessage("Can't play this file", _current?.IconKey ?? "film");
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (Media.Source is null) return;
        if (_isPlaying) { Media.Pause(); SetPlaying(false); _mediaTimer?.Stop(); }
        else
        {
            Media.Play();
            SetPlaying(true);
            EnsureTimer();
            _mediaTimer!.Start();
        }
    }

    private void Seek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSlider || Media.Source is null) return;
        Media.Position = TimeSpan.FromSeconds(Seek.Value);
        TimeText.Text = $"{Fmt(Media.Position)} / {Fmt(_duration)}";
    }

    private void EnsureTimer()
    {
        if (_mediaTimer is not null) return;
        _mediaTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _mediaTimer.Tick += (_, _) =>
        {
            if (Media.Source is null) return;
            _updatingSlider = true;
            Seek.Value = Media.Position.TotalSeconds;
            _updatingSlider = false;
            TimeText.Text = $"{Fmt(Media.Position)} / {Fmt(_duration)}";
        };
    }

    private void SetPlaying(bool playing)
    {
        _isPlaying = playing;
        PlayIcon.Data = (Geometry)FindResource(playing ? "Ic.pause" : "Ic.play");
    }

    private void StopMedia()
    {
        _mediaTimer?.Stop();
        try { Media.Stop(); } catch { }
        Media.Source = null;
        _isPlaying = false;
        MediaView.Visibility = Visibility.Collapsed;
    }

    /// <summary>Stop playback and clear content (called when the pane is hidden).</summary>
    public void Clear()
    {
        _cts?.Cancel();
        HideContent();
        NameText.Text = string.Empty;
        MetaText.Text = string.Empty;
    }

    private static string Fmt(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    // ---- Images (native decode) --------------------------------------------
    private async Task<bool> TryShowImage(FileItem item, CancellationToken ct)
    {
        var bmp = await Task.Run<BitmapSource?>(() =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bi.UriSource = new Uri(item.FullPath);
                if (item.Size > FullDecodeCap) bi.DecodePixelWidth = 2560;   // cap huge images
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch { return null; }
        }, ct);

        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
        if (bmp is null) return false;

        ImageView.Source = bmp;
        ImageView.Visibility = Visibility.Visible;
        MetaText.Text = $"{bmp.PixelWidth}×{bmp.PixelHeight} · {BuildMeta(item)}";
        return true;
    }

    // ---- Text / code (read the head) ---------------------------------------
    private async Task<bool> TryShowText(FileItem item, CancellationToken ct)
    {
        var text = await Task.Run<string?>(() =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var fs = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                int len = (int)Math.Min(TextPreviewCap, fs.Length);
                var buf = new byte[len];
                int read = fs.Read(buf, 0, len);

                // Binary guard: a NUL in the head means this isn't really text.
                for (int i = 0; i < read; i++) if (buf[i] == 0) return null;

                using var ms = new MemoryStream(buf, 0, read);
                using var sr = new StreamReader(ms, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                return sr.ReadToEnd();
            }
            catch { return null; }
        }, ct);

        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
        if (text is null) return false;

        TextView.Text = item.Size > TextPreviewCap ? text + "\n\n… (preview truncated)" : text;
        TextScroll.Visibility = Visibility.Visible;
        return true;
    }

    // ---- PDF (true WinRT render, first page + navigation) ------------------
    private string? _pdfPath;
    private int _pdfIndex, _pdfCount;

    private async Task<bool> TryShowPdf(FileItem item, CancellationToken ct)
    {
        var (img, count) = await PdfRenderService.RenderAsync(item.FullPath, 0, PdfRenderWidth, ct);
        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
        if (img is null || count == 0) return false;

        _pdfPath = item.FullPath;
        _pdfIndex = 0;
        _pdfCount = count;
        ImageView.Source = img;
        ImageView.Visibility = Visibility.Visible;
        PdfBar.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        UpdatePdfBar();
        MetaText.Text = $"PDF · {count} page{(count == 1 ? "" : "s")}  ·  {BuildMeta(item)}";
        return true;
    }

    private void PdfPrev_Click(object sender, RoutedEventArgs e)
    {
        if (_pdfPath is null || _pdfIndex == 0) return;
        _pdfIndex--;
        _ = RenderPdfPage();
    }

    private void PdfNext_Click(object sender, RoutedEventArgs e)
    {
        if (_pdfPath is null || _pdfIndex >= _pdfCount - 1) return;
        _pdfIndex++;
        _ = RenderPdfPage();
    }

    private async Task RenderPdfPage()
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var (img, _) = await PdfRenderService.RenderAsync(_pdfPath!, _pdfIndex, PdfRenderWidth, cts.Token);
        if (cts.IsCancellationRequested || img is null) return;
        ImageView.Source = img;
        UpdatePdfBar();
    }

    private void UpdatePdfBar()
    {
        PdfPage.Text = $"{_pdfIndex + 1} / {_pdfCount}";
        PdfPrev.IsEnabled = _pdfIndex > 0;
        PdfNext.IsEnabled = _pdfIndex < _pdfCount - 1;
    }

    // ---- Shell thumbnail (video / Office / anything Explorer knows) ---------
    private async Task<bool> TryShowThumbnail(FileItem item, CancellationToken ct)
    {
        BitmapSource? src;
        try { src = await ShellThumbnailService.GetThumbnailAsync(item.FullPath, 512, ct); }
        catch (OperationCanceledException) { throw; }
        catch { return false; }

        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
        if (src is null) return false;

        ImageView.Source = src;
        ImageView.Visibility = Visibility.Visible;
        return true;
    }

    // ---- Helpers -----------------------------------------------------------
    private static string BuildMeta(FileItem item)
    {
        var parts = new List<string> { item.TypeLabel };
        if (!string.IsNullOrEmpty(item.SizeText)) parts.Add(item.SizeText);
        if (!string.IsNullOrEmpty(item.ModifiedText)) parts.Add(item.ModifiedText);
        return string.Join("  ·  ", parts);
    }

    private void ShowMessage(string message, string iconKey)
    {
        EmptyIcon.Data = TryFindResource($"Ic.{iconKey}") as Geometry
                         ?? (Geometry)FindResource("Ic.file");
        EmptyText.Text = message;
        EmptyView.Visibility = Visibility.Visible;
    }

    private void HideContent()
    {
        ImageView.Source = null;
        ImageView.Visibility = Visibility.Collapsed;
        TextView.Text = string.Empty;
        TextScroll.Visibility = Visibility.Collapsed;
        EmptyView.Visibility = Visibility.Collapsed;
        PdfBar.Visibility = Visibility.Collapsed;
        _pdfPath = null;
        StopMedia();
    }
}
