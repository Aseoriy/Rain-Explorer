using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RainExplorer.Models;
using RainExplorer.Services;

namespace RainExplorer.Controls;

/// <summary>
/// Right-hand preview pane: photos, the head of text/code files, or a hint. Port of the
/// Windows build's PreviewPane (same header, footer metadata line and empty states);
/// audio/video playback and PDF pages are not ported yet.
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
            if (ImageExt.Contains(ext) && await TryShowImage(item, cts.Token)) return;
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
    }
}
