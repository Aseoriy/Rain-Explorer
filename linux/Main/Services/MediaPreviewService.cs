using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Media.Imaging;

namespace RainExplorer.Services;

/// <summary>Duration and tags of an audio or video file.</summary>
public sealed record MediaInfo(TimeSpan? Duration, int Width, int Height, string? Title, string? Artist, string? Album);

/// <summary>
/// Poster frames and details for audio/video previews. Avalonia has no media element, so instead of
/// in-pane playback (Windows build) the pane shows a still and plays in the desktop's media player.
/// Poster frames come from the shared FreeDesktop thumbnail cache (already filled by Nautilus, Dolphin
/// and friends) and otherwise from <c>ffmpeg</c>; details come from <c>ffprobe</c>. Both are optional.
/// </summary>
public static class MediaPreviewService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static readonly string[] CacheSizes = ["xx-large", "x-large", "large", "normal"];

    /// <summary>A still for the file: cached desktop thumbnail first, then an ffmpeg frame or cover art.</summary>
    public static async Task<Bitmap?> PosterAsync(string path, int width, bool video, CancellationToken ct)
    {
        Bitmap? cached = await Task.Run(() => FromThumbnailCache(path), ct).ConfigureAwait(false);
        if (cached is not null) return cached;

        // Video: a frame a second in (skips black lead-in). Audio: the embedded cover picture, if any.
        List<string> args = ["-hide_banner", "-loglevel", "error", "-nostdin"];
        if (video) args.AddRange(["-ss", "1"]);
        args.AddRange(["-i", path, "-an", "-frames:v", "1",
            "-vf", $"scale='min({width},iw)':-2", "-f", "image2pipe", "-c:v", "png", "-"]);
        var result = await ToolRunner.RunAsync("ffmpeg", args, Timeout, ct).ConfigureAwait(false);
        if ((!result.Succeeded || result.Output.Length == 0) && video)
        {
            // Clips shorter than a second: take the first frame instead.
            args.RemoveRange(4, 2);
            result = await ToolRunner.RunAsync("ffmpeg", args, Timeout, ct).ConfigureAwait(false);
        }
        if (!result.Succeeded || result.Output.Length == 0) return null;
        try
        {
            using var stream = new MemoryStream(result.Output);
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    public static async Task<MediaInfo?> ProbeAsync(string path, CancellationToken ct)
    {
        var result = await ToolRunner.RunAsync("ffprobe",
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", "--", path],
            Timeout, ct).ConfigureAwait(false);
        return result.Succeeded ? ParseProbe(result.Text) : null;
    }

    internal static MediaInfo? ParseProbe(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            TimeSpan? duration = null;
            string? title = null, artist = null, album = null;
            if (root.TryGetProperty("format", out var format))
            {
                if (format.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.String
                    && double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                    && seconds > 0)
                    duration = TimeSpan.FromSeconds(seconds);
                if (format.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
                {
                    title = Tag(tags, "title");
                    artist = Tag(tags, "artist");
                    album = Tag(tags, "album");
                }
            }
            int width = 0, height = 0;
            if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in streams.EnumerateArray())
                {
                    if (s.TryGetProperty("codec_type", out var type) && type.GetString() == "video"
                        && !(s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var pic)
                             && pic.ValueKind == JsonValueKind.Number && pic.GetInt32() == 1)
                        && s.TryGetProperty("width", out var w) && s.TryGetProperty("height", out var h))
                    {
                        width = w.GetInt32();
                        height = h.GetInt32();
                        break;
                    }
                }
            }
            return new MediaInfo(duration, width, height, title, artist, album);
        }
        catch
        {
            return null;
        }
    }

    private static string? Tag(JsonElement tags, string name)
    {
        foreach (var prop in tags.EnumerateObject())
            if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.String)
            {
                string? value = prop.Value.GetString()?.Trim();
                return string.IsNullOrEmpty(value) ? null : value;
            }
        return null;
    }

    public static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    // ---- FreeDesktop thumbnail cache --------------------------------------------------------------

    private static Bitmap? FromThumbnailCache(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            long mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();
            string name = ThumbnailName(path);
            string cacheRoot = Path.Combine(
                Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } c ? c : Path.Combine(XdgDirs.Home, ".cache"),
                "thumbnails");
            foreach (string size in CacheSizes)
            {
                string candidate = Path.Combine(cacheRoot, size, name);
                if (!File.Exists(candidate)) continue;
                byte[] png = File.ReadAllBytes(candidate);
                // A thumbnail for an older version of the file is stale (spec: Thumb::MTime must match).
                if (ReadPngText(png, "Thumb::MTime") is { } stamp
                    && long.TryParse(stamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out long stamped)
                    && stamped != mtime)
                    continue;
                using var stream = new MemoryStream(png);
                return new Bitmap(stream);
            }
        }
        catch { }
        return null;
    }

    /// <summary>MD5 of the file URI, as the thumbnail spec names cache entries.</summary>
    internal static string ThumbnailName(string path) =>
        Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(FileUri(path)))) + ".png";

    /// <summary>The file:// URI escaped the way GLib's g_filename_to_uri does (what Nautilus/Dolphin hash).</summary>
    internal static string FileUri(string path)
    {
        var sb = new StringBuilder("file://");
        foreach (byte b in Encoding.UTF8.GetBytes(path))
        {
            char ch = (char)b;
            bool keep = b < 0x80 && (char.IsAsciiLetterOrDigit(ch) || "-._~!$&'()*+,;=:@/".Contains(ch));
            if (keep) sb.Append(ch);
            else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>Value of a PNG tEXt chunk, or null.</summary>
    internal static string? ReadPngText(byte[] png, string key)
    {
        int pos = 8;
        while (pos + 12 <= png.Length)
        {
            int length = (png[pos] << 24) | (png[pos + 1] << 16) | (png[pos + 2] << 8) | png[pos + 3];
            if (length < 0 || pos + 12 + length > png.Length) return null;
            string type = Encoding.ASCII.GetString(png, pos + 4, 4);
            if (type == "IDAT" || type == "IEND") return null;   // text chunks for thumbnails come first
            if (type == "tEXt")
            {
                int data = pos + 8;
                int sep = Array.IndexOf(png, (byte)0, data, length);
                if (sep > data && Encoding.Latin1.GetString(png, data, sep - data) == key)
                    return Encoding.Latin1.GetString(png, sep + 1, data + length - sep - 1);
            }
            pos += 12 + length;
        }
        return null;
    }
}
