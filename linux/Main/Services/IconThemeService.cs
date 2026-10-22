using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using RainExplorer.Models;
using SkiaSharp;

namespace RainExplorer.Services;

/// <summary>
/// Real desktop icons for files and folders — the Linux counterpart of the Windows
/// build's ShellIconService. Follows the FreeDesktop icon-theme specification: the
/// current theme (KDE's kdeglobals or GNOME's gsettings), its Inherits chain, then
/// hicolor. SVG icons are rasterised with Svg.Skia; results are cached per icon name and
/// size, so a folder full of folders decodes the folder icon once.
/// </summary>
public static class IconThemeService
{
    private sealed record IconDir(string Path, int Size, int Scale, string Type, int MinSize, int MaxSize, int Threshold);
    private sealed record Theme(string Name, IReadOnlyList<IconDir> Dirs, IReadOnlyList<string> Inherits);

    private static readonly ConcurrentDictionary<string, Theme?> Themes = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, HashSet<string>> DirListings = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<(string Key, int Px), Bitmap?> Cache = new();
    private static readonly ConcurrentDictionary<(string Key, int Px), Task<Bitmap?>> Pending = new();
    private static readonly SemaphoreSlim Throttle = new(3);
    private static readonly Lazy<string> CurrentThemeName = new(DetectTheme);

    /// <summary>Load the icon for <paramref name="item"/> at <paramref name="px"/> device pixels,
    /// then call <paramref name="onLoaded"/> on the UI thread (synchronously if cached).</summary>
    public static void Load(FileItem item, int px, Action<Bitmap> onLoaded)
    {
        string key = CacheKey(item);
        if (Cache.TryGetValue((key, px), out Bitmap? cached))
        {
            if (cached is not null) onLoaded(cached);
            return;
        }

        var names = IconNamesFor(item);
        var task = Pending.GetOrAdd((key, px), k => Task.Run(async () =>
        {
            await Throttle.WaitAsync().ConfigureAwait(false);
            try
            {
                Bitmap? bitmap = null;
                foreach (string name in names)
                {
                    if (FindIcon(name, px) is { } path && Render(path, px) is { } rendered)
                    {
                        bitmap = rendered;
                        break;
                    }
                }
                Cache[k] = bitmap;
                return bitmap;
            }
            finally
            {
                Throttle.Release();
                Pending.TryRemove(k, out _);
            }
        }));

        task.ContinueWith(t =>
        {
            if (t.Result is { } bmp) Dispatcher.UIThread.Post(() => onLoaded(bmp), DispatcherPriority.Background);
        }, TaskScheduler.Default);
    }

    /// <summary>Resolve a named icon (e.g. "folder", "text-plain") to a file path, or null.</summary>
    public static string? FindIcon(string name, int size)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? found = FindInTheme(CurrentThemeName.Value, name, size, visited);
        found ??= FindInTheme("hicolor", name, size, visited);
        if (found is not null) return found;
        foreach (string ext in new[] { ".svg", ".png" })
        {
            string pixmap = Path.Combine("/usr/share/pixmaps", name + ext);
            if (File.Exists(pixmap)) return pixmap;
        }
        return null;
    }

    private static string CacheKey(FileItem item) =>
        item.IsDirectory ? "dir:" + FolderIconName(item.FullPath)
        : "mime:" + MimeService.ForFileName(item.Name) + (IsExecutable(item) ? ":x" : "");

    private static List<string> IconNamesFor(FileItem item)
    {
        var names = new List<string>();
        if (item.IsDirectory)
        {
            names.Add(FolderIconName(item.FullPath));
            names.Add("folder");
            return names;
        }

        string mime = MimeService.ForFileName(item.Name);
        if (mime == MimeService.Unknown && IsExecutable(item)) names.Add("application-x-executable");
        names.AddRange(MimeService.IconNames(mime));
        names.Add("text-x-generic");
        names.Add("unknown");
        return names;
    }

    private static bool IsExecutable(FileItem item)
    {
        if (item.IsDirectory || item.Extension.Length > 0) return false;
        try { return (File.GetUnixFileMode(item.FullPath) & UnixFileMode.UserExecute) != 0; }
        catch { return false; }
    }

    /// <summary>Special folders get their themed icons, like Dolphin and the Windows shell.</summary>
    public static string FolderIconName(string path)
    {
        if (PathUtil.Same(path, XdgDirs.Home)) return "user-home";
        if (PathUtil.Same(path, XdgDirs.Desktop)) return "user-desktop";
        if (PathUtil.Same(path, XdgDirs.Documents)) return "folder-documents";
        if (PathUtil.Same(path, XdgDirs.Downloads)) return "folder-download";
        if (PathUtil.Same(path, XdgDirs.Music)) return "folder-music";
        if (PathUtil.Same(path, XdgDirs.Pictures)) return "folder-pictures";
        if (PathUtil.Same(path, XdgDirs.Videos)) return "folder-videos";
        if (PathUtil.Same(path, FileSystemService.TrashToken)) return "user-trash";
        return "folder";
    }

    // ---- Theme lookup -------------------------------------------------------

    private static string? FindInTheme(string themeName, string iconName, int size, HashSet<string> visited)
    {
        if (!visited.Add(themeName)) return null;
        var theme = LoadTheme(themeName);
        if (theme is null) return null;

        string? best = null;
        int bestDistance = int.MaxValue;
        foreach (var dir in theme.Dirs)
        {
            var listing = Listing(dir.Path);
            string? file = listing.Contains(iconName + ".svg") ? iconName + ".svg"
                : listing.Contains(iconName + ".png") ? iconName + ".png"
                : null;
            if (file is null) continue;
            int distance = SizeDistance(dir, size);
            if (distance == 0) return Path.Combine(dir.Path, file);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = Path.Combine(dir.Path, file);
            }
        }
        if (best is not null) return best;

        foreach (string parent in theme.Inherits)
        {
            string? inherited = FindInTheme(parent, iconName, size, visited);
            if (inherited is not null) return inherited;
        }
        return null;
    }

    private static int SizeDistance(IconDir dir, int size)
    {
        int scaled = size;
        int dirSize = dir.Size * dir.Scale;
        return dir.Type switch
        {
            "Scalable" => scaled < dir.MinSize * dir.Scale ? dir.MinSize * dir.Scale - scaled
                : scaled > dir.MaxSize * dir.Scale ? scaled - dir.MaxSize * dir.Scale
                : 0,
            "Threshold" => Math.Abs(dirSize - scaled) <= dir.Threshold ? 0 : Math.Abs(dirSize - scaled),
            _ => Math.Abs(dirSize - scaled) + (dirSize < scaled ? 1 : 0),   // prefer downscaling
        };
    }

    private static HashSet<string> Listing(string dir) => DirListings.GetOrAdd(dir, d =>
    {
        try { return Directory.EnumerateFiles(d).Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.Ordinal); }
        catch { return new HashSet<string>(StringComparer.Ordinal); }
    });

    private static IEnumerable<string> IconBaseDirs()
    {
        yield return Path.Combine(XdgDirs.DataHome, "icons");
        yield return Path.Combine(XdgDirs.Home, ".icons");
        string dirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS") is { Length: > 0 } d ? d : "/usr/local/share:/usr/share";
        foreach (string dir in dirs.Split(':', StringSplitOptions.RemoveEmptyEntries))
            yield return Path.Combine(dir, "icons");
    }

    private static Theme? LoadTheme(string name) => Themes.GetOrAdd(name, n =>
    {
        var roots = IconBaseDirs().Select(b => Path.Combine(b, n)).Where(Directory.Exists).Distinct().ToList();
        string? index = roots.Select(r => Path.Combine(r, "index.theme")).FirstOrDefault(File.Exists);
        if (index is null) return null;

        var sections = ParseIni(index);
        if (!sections.TryGetValue("Icon Theme", out var header)) return null;
        var dirNames = Split(header.GetValueOrDefault("Directories")).Concat(Split(header.GetValueOrDefault("ScaledDirectories")));
        var inherits = Split(header.GetValueOrDefault("Inherits")).Where(i => i != "hicolor").ToList();

        var dirs = new List<IconDir>();
        foreach (string dirName in dirNames.Distinct())
        {
            if (!sections.TryGetValue(dirName, out var props)) continue;
            int size = ParseInt(props.GetValueOrDefault("Size"), 0);
            if (size <= 0) continue;
            int scale = ParseInt(props.GetValueOrDefault("Scale"), 1);
            string type = props.GetValueOrDefault("Type") ?? "Threshold";
            int min = ParseInt(props.GetValueOrDefault("MinSize"), size);
            int max = ParseInt(props.GetValueOrDefault("MaxSize"), size);
            int threshold = ParseInt(props.GetValueOrDefault("Threshold"), 2);
            // Symbolic icons are monochrome glyphs meant for recolouring; skip them for files.
            if (dirName.Contains("symbolic", StringComparison.Ordinal)) continue;
            foreach (string root in roots)
            {
                string full = Path.Combine(root, dirName);
                if (Directory.Exists(full)) dirs.Add(new IconDir(full, size, scale, type, min, max, threshold));
            }
        }
        return new Theme(n, dirs, inherits);
    });

    private static IEnumerable<string> Split(string? value) =>
        (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static int ParseInt(string? value, int fallback) => int.TryParse(value, out int v) ? v : fallback;

    private static Dictionary<string, Dictionary<string, string>> ParseIni(string file)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        Dictionary<string, string>? current = null;
        foreach (string raw in File.ReadLines(file))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                current = new Dictionary<string, string>(StringComparer.Ordinal);
                sections[line[1..^1]] = current;
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq > 0 && current is not null) current[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return sections;
    }

    private static string DetectTheme()
    {
        try
        {
            string kdeglobals = Path.Combine(XdgDirs.ConfigHome, "kdeglobals");
            if (File.Exists(kdeglobals)
                && ParseIni(kdeglobals).TryGetValue("Icons", out var icons)
                && icons.TryGetValue("Theme", out string? kde) && !string.IsNullOrWhiteSpace(kde))
                return kde;
        }
        catch { }

        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("gsettings")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("get");
            start.ArgumentList.Add("org.gnome.desktop.interface");
            start.ArgumentList.Add("icon-theme");
            using var process = System.Diagnostics.Process.Start(start);
            if (process is not null)
            {
                string value = process.StandardOutput.ReadToEnd().Trim().Trim('\'');
                process.WaitForExit(1500);
                if (value.Length > 0) return value;
            }
        }
        catch { }

        string desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty;
        return desktop.Contains("KDE", StringComparison.OrdinalIgnoreCase) ? "breeze-dark" : "Adwaita";
    }

    // ---- Rasterising -----------------------------------------------------------

    private static Bitmap? Render(string path, int px)
    {
        try
        {
            if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                using var svg = new Svg.Skia.SKSvg();
                if (svg.Load(path) is not { } picture) return null;
                SKRect bounds = picture.CullRect;
                if (bounds.Width <= 0 || bounds.Height <= 0) return null;
                float scale = Math.Min(px / bounds.Width, px / bounds.Height);
                using var surface = SKSurface.Create(new SKImageInfo(px, px, SKColorType.Bgra8888, SKAlphaType.Premul));
                var canvas = surface.Canvas;
                canvas.Clear(SKColors.Transparent);
                canvas.Translate((px - bounds.Width * scale) / 2f, (px - bounds.Height * scale) / 2f);
                canvas.Scale(scale);
                canvas.Translate(-bounds.Left, -bounds.Top);
                canvas.DrawPicture(picture);
                canvas.Flush();
                using var image = surface.Snapshot();
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                using var stream = data.AsStream();
                return new Bitmap(stream);
            }

            using var file = File.OpenRead(path);
            return Bitmap.DecodeToWidth(file, px, BitmapInterpolationMode.HighQuality);
        }
        catch
        {
            return null;
        }
    }
}
