// Avalonia 11.3 marks the IDataObject clipboard API obsolete in favour of
// DataTransfer; it is still the documented 11.x path and works on X11.
#pragma warning disable CS0618
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;

namespace RainExplorer.Services;

/// <summary>
/// File copy/cut/paste through the desktop clipboard, interoperable with Dolphin and
/// Nautilus. Files are offered as <c>text/uri-list</c> plus the two de-facto cut/copy
/// markers: KDE's <c>application/x-kde-cutselection</c> and GNOME's
/// <c>x-special/gnome-copied-files</c>. An in-process copy of the last selection backs
/// up the system clipboard so copy/paste inside Rain always works even if the
/// clipboard owner disappears.
/// </summary>
public static class ClipboardService
{
    private const string UriList = "text/uri-list";
    private const string KdeCut = "application/x-kde-cutselection";
    private const string GnomeFiles = "x-special/gnome-copied-files";

    private static IReadOnlyList<string> _lastPaths = Array.Empty<string>();
    private static bool _lastCut;
    private static string _lastUriList = string.Empty;

    /// <summary>Paths currently marked as cut by Rain (rows are drawn faded).</summary>
    public static IReadOnlyCollection<string> CutPaths => _lastCut ? _lastPaths : Array.Empty<string>();

    public static event Action? Changed;

    public static async Task SetFilesAsync(TopLevel? top, IReadOnlyList<string> paths, bool cut)
    {
        _lastPaths = paths.ToList();
        _lastCut = cut;
        _lastUriList = BuildUriList(paths);
        Changed?.Invoke();

        if (top?.Clipboard is not { } clipboard) return;
        try
        {
            var data = new DataObject();
            data.Set(UriList, Encoding.UTF8.GetBytes(_lastUriList));
            data.Set(KdeCut, Encoding.UTF8.GetBytes(cut ? "1" : "0"));
            data.Set(GnomeFiles, Encoding.UTF8.GetBytes((cut ? "cut" : "copy") + "\n" + _lastUriList.Replace("\r\n", "\n").TrimEnd('\n')));
            data.Set(DataFormats.Text, string.Join('\n', paths));
            await clipboard.SetDataObjectAsync(data);
        }
        catch
        {
            // The in-process copy above still lets Rain paste; other apps just won't see it.
        }
    }

    public static async Task SetTextAsync(TopLevel? top, string text)
    {
        if (top?.Clipboard is not { } clipboard) return;
        try { await clipboard.SetTextAsync(text); } catch { }
    }

    /// <summary>Read files from the clipboard. Returns the paths and whether they were cut.</summary>
    public static async Task<(IReadOnlyList<string> Paths, bool Cut)> GetFilesAsync(TopLevel? top)
    {
        if (top?.Clipboard is { } clipboard)
        {
            try
            {
                string[] formats = await clipboard.GetFormatsAsync();
                if (formats.Contains(GnomeFiles))
                {
                    string? gnome = Decode(await clipboard.GetDataAsync(GnomeFiles));
                    if (gnome is not null)
                    {
                        string[] lines = gnome.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
                        if (lines.Length > 1)
                            return (ParseUris(lines.Skip(1)), lines[0].Trim() == "cut");
                    }
                }
                if (formats.Contains(UriList))
                {
                    string? uris = Decode(await clipboard.GetDataAsync(UriList));
                    if (uris is not null)
                    {
                        bool cut = false;
                        if (formats.Contains(KdeCut))
                            cut = Decode(await clipboard.GetDataAsync(KdeCut))?.Trim() == "1";
                        // Our own selection: trust the in-process cut flag too.
                        if (!cut && _lastCut && string.Equals(uris, _lastUriList, StringComparison.Ordinal))
                            cut = true;
                        var paths = ParseUris(uris.Replace("\r\n", "\n").Split('\n'));
                        if (paths.Count > 0) return (paths, cut);
                    }
                }
            }
            catch
            {
                // Fall back to the in-process copy below.
            }
        }

        return (_lastPaths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList(), _lastCut);
    }

    public static async Task<bool> HasFilesAsync(TopLevel? top)
    {
        if (_lastPaths.Count > 0) return true;
        if (top?.Clipboard is not { } clipboard) return false;
        try
        {
            string[] formats = await clipboard.GetFormatsAsync();
            return formats.Contains(UriList) || formats.Contains(GnomeFiles);
        }
        catch { return false; }
    }

    /// <summary>A cut was pasted: the source paths no longer exist, so forget them.</summary>
    public static async Task ClearAfterCutAsync(TopLevel? top)
    {
        if (!_lastCut) return;
        _lastPaths = Array.Empty<string>();
        _lastCut = false;
        _lastUriList = string.Empty;
        Changed?.Invoke();
        if (top?.Clipboard is { } clipboard)
        {
            try { await clipboard.ClearAsync(); } catch { }
        }
    }

    private static string BuildUriList(IEnumerable<string> paths) =>
        string.Concat(paths.Select(p => new Uri(p).AbsoluteUri + "\r\n"));

    private static List<string> ParseUris(IEnumerable<string> lines)
    {
        var paths = new List<string>();
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (Uri.TryCreate(line, UriKind.Absolute, out Uri? uri) && uri.IsFile)
                paths.Add(uri.LocalPath);
            else if (line.StartsWith('/'))
                paths.Add(line);
        }
        return paths;
    }

    private static string? Decode(object? data) => data switch
    {
        byte[] bytes => Encoding.UTF8.GetString(bytes).TrimEnd('\0'),
        string s => s,
        _ => null,
    };
}
