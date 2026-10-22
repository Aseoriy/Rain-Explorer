using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace RainExplorer.Services;

/// <summary>
/// Decodes small photo thumbnails off the UI thread. Avalonia's Skia decoder handles
/// PNG, JPEG, GIF, WebP and BMP; anything it cannot read simply keeps the vector icon.
/// Decoding is throttled so scrolling a large photo folder can't starve the thread pool.
/// </summary>
public static class ThumbnailService
{
    private const long MaxSourceBytes = 64L * 1024 * 1024;
    private static readonly SemaphoreSlim Throttle = new(Math.Clamp(Environment.ProcessorCount / 2, 2, 4));

    public static void LoadAsync(string path, int width, Action<Bitmap> onLoaded)
    {
        _ = Task.Run(async () =>
        {
            await Throttle.WaitAsync().ConfigureAwait(false);
            try
            {
                Bitmap? bitmap = Decode(path, width);
                if (bitmap is not null)
                    Dispatcher.UIThread.Post(() => onLoaded(bitmap), DispatcherPriority.Background);
            }
            finally
            {
                Throttle.Release();
            }
        });
    }

    /// <summary>Decode at most <paramref name="width"/> pixels wide (never upscaled).</summary>
    public static Bitmap? Decode(string path, int width)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaxSourceBytes) return null;
            var size = ReadPixelSize(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int target = size is { Width: > 0 } s ? Math.Min(width, s.Width) : width;
            return Bitmap.DecodeToWidth(stream, target, BitmapInterpolationMode.MediumQuality);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Image dimensions from the file header (no full decode).</summary>
    public static (int Width, int Height)? ReadPixelSize(string path)
    {
        try
        {
            using var codec = SkiaSharp.SKCodec.Create(path);
            return codec is null ? null : (codec.Info.Width, codec.Info.Height);
        }
        catch
        {
            return null;
        }
    }
}
