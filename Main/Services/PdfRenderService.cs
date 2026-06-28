using System.IO;
using System.Threading;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace RainExplorer.Services;

/// <summary>
/// Renders a PDF page to a WPF bitmap using the built-in WinRT
/// <see cref="Windows.Data.Pdf"/> renderer (no third-party library). Available
/// because the project targets an OS-versioned TFM that enables WinRT projections.
/// </summary>
public static class PdfRenderService
{
    /// <summary>Render one page (0-based) at the given pixel width.
    /// Returns the page image and the document's page count (0 image on failure).</summary>
    public static async Task<(BitmapSource? image, int pageCount)> RenderAsync(
        string path, int pageIndex, uint targetWidth, CancellationToken ct)
    {
        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            ct.ThrowIfCancellationRequested();

            PdfDocument doc = await PdfDocument.LoadFromFileAsync(file);
            int count = (int)doc.PageCount;
            if (count == 0) return (null, 0);

            int idx = Math.Clamp(pageIndex, 0, count - 1);
            using PdfPage page = doc.GetPage((uint)idx);

            using var stream = new InMemoryRandomAccessStream();
            var options = new PdfPageRenderOptions { DestinationWidth = targetWidth };
            await page.RenderToStreamAsync(stream, options);
            ct.ThrowIfCancellationRequested();

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = stream.AsStream();
            bmp.EndInit();
            bmp.Freeze();
            return (bmp, count);
        }
        catch (OperationCanceledException) { throw; }
        catch { return (null, 0); }
    }
}
