using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RainExplorer.Models;
using RainExplorer.Services;

namespace RainExplorer.Controls;

/// <summary>
/// Right-hand preview pane: photos, PDF pages, audio/video posters, the head of text/code files, or a hint.
/// Port of the Windows build's PreviewPane (same header, footer metadata line and empty states). PDF pages
/// come from poppler; audio/video show a still and details and play in the desktop's media player
/// (Avalonia has no media element), see <see cref="MediaPreviewService"/>.
/// </summary>
public partial class PreviewPane : UserControl
{
    private const int TextPreviewBytes = 64 * 1024;
    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "gif", "bmp", "webp", "ico",
    };
    private static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase)
    {
        "txt", "md", "log", "json", "xml", "yaml", "yml", "toml", "ini", "cfg", "conf", "csv", "tsv",
        "cs", "axaml", "xaml", "csproj", "sln", "slnx", "props", "targets", "js", "ts", "jsx", "tsx", "css",
        "scss", "html", "htm", "py", "rs", "go", "c", "h", "cpp", "hpp", "java", "kt", "sh", "bash", "zsh",
        "fish", "lua", "rb", "php", "sql", "desktop", "service", "gitignore", "env", "properties", "gradle",
        "cmake", "bat", "cmd", "ps1", "iss", "svg",
    };
    private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "mkv", "mov", "avi", "webm", "wmv", "m4v", "mpg", "mpeg", "ogv", "3gp", "ts",
    };
    private static readonly HashSet<string> AudioExt = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp3", "wav", "flac", "ogg", "oga", "opus", "m4a", "aac", "wma", "aiff", "aif",
    };
    private const int PdfRenderWidth = 1400;
    private const int PosterWidth = 1280;

    private CancellationTokenSource? _cts;

    public PreviewPane() => InitializeComponent();

    private void Close_Click(object? sender, RoutedEventArgs e) =>
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

        NameText.Text = item.Name;
        MetaText.Text = BuildMeta(item);

        if (item.IsDirectory)
        {
            ShowMessage("Folder", "folder");
            return;
        }

        string ext = item.Extension.ToLowerInvariant();
        try
        {
            if (VideoExt.Contains(ext)) { await ShowMedia(item, video: true, cts.Token); return; }
            if (AudioExt.Contains(ext)) { await ShowMedia(item, video: false, cts.Token); return; }
            if (ImageExt.Contains(ext) && await TryShowImage(item, cts.Token)) return;
            if (ext == "pdf" && await TryShowPdf(item, cts.Token)) return;
            if ((TextExt.Contains(ext) || (ext.Length == 0 && item.Size < 1024 * 1024))
                && await TryShowText(item, cts.Token))
                return;
        }
        catch (OperationCanceledException) { return; }

        if (!cts.IsCancellationRequested) ShowMessage("No preview available", item.IconKey);
    }

    /// <summary>Clear content (called when the pane is hidden or the folder changes).</summary>
    public void Clear()
    {
        _cts?.Cancel();
        HideContent();
        NameText.Text = string.Empty;
        MetaText.Text = string.Empty;
    }

    private async Task<bool> TryShowImage(FileItem item, CancellationToken ct)
    {
        var (bitmap, size) = await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return (ThumbnailService.Decode(item.FullPath, 2560), ThumbnailService.ReadPixelSize(item.FullPath));
        }, ct).ConfigureAwait(true);
        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
        if (bitmap is null) return false;
        ImageView.Source = bitmap;
        ImageView.IsVisible = true;
        var (w, h) = size ?? (bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        MetaText.Text = $"{w}×{h} · {BuildMeta(item)}";
        return true;
    }

    private async Task<bool> TryShowText(FileItem item, CancellationToken ct)
    {
        string? text = await Task.Run(() =>
        {
            try
            {
                using var stream = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                byte[] buffer = new byte[TextPreviewBytes];
                int read = stream.Read(buffer, 0, buffer.Length);
                if (Array.IndexOf(buffer, (byte)0, 0, Math.Min(read, 4096)) >= 0) return null;   // binary
                string s = Encoding.UTF8.GetString(buffer, 0, read);
                return read == TextPreviewBytes ? s + "\n…" : s;
            }
            catch { return null; }
        }, ct).ConfigureAwait(true);
        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
        if (text is null) return false;
        TextView.Text = text;
        TextScroll.IsVisible = true;
        return true;
    }

    // ---- PDF (poppler, first page + navigation) ----------------------------------------------
    private string? _pdfPath;
    private int _pdfIndex, _pdfCount;

    private async Task<bool> TryShowPdf(FileItem item, CancellationToken ct)
    {
        if (!PdfRenderService.IsAvailable) return false;
        int count = await PdfRenderService.PageCountAsync(item.FullPath, ct).ConfigureAwait(true);
        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
        if (count == 0) return false;
        var page = await PdfRenderService.RenderPageAsync(item.FullPath, 0, PdfRenderWidth, ct).ConfigureAwait(true);
        if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
        if (page is null) return false;

        _pdfPath = item.FullPath;
        _pdfIndex = 0;
        _pdfCount = count;
        ImageView.Source = page;
        ImageView.IsVisible = true;
        PdfBar.IsVisible = count > 1;
        UpdatePdfBar();
        MetaText.Text = $"PDF · {count} page{(count == 1 ? "" : "s")}  ·  {BuildMeta(item)}";
        return true;
    }

    private void PdfPrev_Click(object? sender, RoutedEventArgs e)
    {
        if (_pdfPath is null || _pdfIndex == 0) return;
        _pdfIndex--;
        _ = RenderPdfPage();
    }

    private void PdfNext_Click(object? sender, RoutedEventArgs e)
    {
        if (_pdfPath is null || _pdfIndex >= _pdfCount - 1) return;
        _pdfIndex++;
        _ = RenderPdfPage();
    }

    private async Task RenderPdfPage()
    {
        string path = _pdfPath!;
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        UpdatePdfBar();
        Avalonia.Media.Imaging.Bitmap? page;
        try { page = await PdfRenderService.RenderPageAsync(path, _pdfIndex, PdfRenderWidth, cts.Token); }
        catch (OperationCanceledException) { return; }
        if (cts.IsCancellationRequested || page is null || _pdfPath != path) return;
        ImageView.Source = page;
    }

    private void UpdatePdfBar()
    {
        PdfPage.Text = $"{_pdfIndex + 1} / {_pdfCount}";
        PdfPrev.IsEnabled = _pdfIndex > 0;
        PdfNext.IsEnabled = _pdfIndex < _pdfCount - 1;
    }

    // ---- Audio / video (poster + details, plays in the desktop player) --------------------------
    private string? _mediaPath;

    private async Task ShowMedia(FileItem item, bool video, CancellationToken ct)
    {
        _mediaPath = item.FullPath;
        MediaBar.IsVisible = true;
        MediaText.Text = video ? "Video" : "Audio";
        if (!video) AudioView.IsVisible = true;

        var posterTask = MediaPreviewService.PosterAsync(item.FullPath, PosterWidth, video, ct);
        var infoTask = MediaPreviewService.ProbeAsync(item.FullPath, ct);
        var poster = await posterTask.ConfigureAwait(true);
        if (ct.IsCancellationRequested) return;
        if (poster is not null)
        {
            AudioView.IsVisible = false;
            ImageView.Source = poster;
            ImageView.IsVisible = true;
        }
        else if (video)
        {
            ShowMessage("Video", "film");
        }

        var info = await infoTask.ConfigureAwait(true);
        if (ct.IsCancellationRequested || info is null) return;
        var details = new List<string>();
        if (!video && info.Title is not null)
            details.Add(info.Artist is null ? info.Title : $"{info.Artist} – {info.Title}");
        if (info.Duration is { } duration) details.Add(MediaPreviewService.FormatDuration(duration));
        if (details.Count > 0) MediaText.Text = string.Join("  ·  ", details);
        if (video && info.Width > 0) MetaText.Text = $"{info.Width}×{info.Height} · {BuildMeta(item)}";
        else if (!video && info.Album is not null) MetaText.Text = $"{info.Album} · {BuildMeta(item)}";
    }

    private void Play_Click(object? sender, RoutedEventArgs e)
    {
        if (_mediaPath is null) return;
        try { ProcessLauncher.OpenDefault(_mediaPath); }
        catch (Exception ex) { MediaText.Text = $"⚠️ Couldn't open a media player: {ex.Message}"; }
    }

    private static string BuildMeta(FileItem item)
    {
        var parts = new List<string> { item.TypeLabel };
        if (!string.IsNullOrEmpty(item.SizeText)) parts.Add(item.SizeText);
        if (!string.IsNullOrEmpty(item.ModifiedText)) parts.Add(item.ModifiedText);
        return string.Join("  ·  ", parts);
    }

    private void ShowMessage(string message, string iconKey)
    {
        EmptyIcon.Kind = iconKey;
        EmptyText.Text = message;
        EmptyView.IsVisible = true;
    }

    private void HideContent()
    {
        ImageView.Source = null;
        ImageView.IsVisible = false;
        TextView.Text = string.Empty;
        TextScroll.IsVisible = false;
        EmptyView.IsVisible = false;
        AudioView.IsVisible = false;
        PdfBar.IsVisible = false;
        MediaBar.IsVisible = false;
        _pdfPath = null;
        _mediaPath = null;
    }
}
