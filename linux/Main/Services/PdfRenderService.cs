using System.Globalization;
using Avalonia.Media.Imaging;

namespace RainExplorer.Services;

/// <summary>
/// Renders PDF pages with poppler's <c>pdfinfo</c>/<c>pdftoppm</c> (package poppler-utils, present on most
/// desktops). The Windows build uses the built-in WinRT renderer; Linux has no equivalent in .NET, and the
/// command-line tools avoid bundling a native PDF library. Without poppler the preview falls back to the
/// desktop thumbnail or the type icon.
/// </summary>
public static class PdfRenderService
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public static bool IsAvailable => ToolRunner.IsAvailable("pdftoppm") && ToolRunner.IsAvailable("pdfinfo");

    /// <summary>Page count from <c>pdfinfo</c>, or 0 when the file can't be read.</summary>
    public static async Task<int> PageCountAsync(string path, CancellationToken ct)
    {
        var result = await ToolRunner.RunAsync("pdfinfo", ["--", path], Timeout, ct).ConfigureAwait(false);
        return result.Succeeded ? ParsePageCount(result.Text) : 0;
    }

    internal static int ParsePageCount(string pdfinfoOutput)
    {
        foreach (string line in pdfinfoOutput.Split('\n'))
        {
            if (!line.StartsWith("Pages:", StringComparison.Ordinal)) continue;
            return int.TryParse(line["Pages:".Length..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int n)
                ? n : 0;
        }
        return 0;
    }

    /// <summary>Render one page (0-based) at the given pixel width. Null on failure.</summary>
    public static async Task<Bitmap?> RenderPageAsync(string path, int pageIndex, int targetWidth, CancellationToken ct)
    {
        string page = (pageIndex + 1).ToString(CultureInfo.InvariantCulture);
        // Without an output root pdftoppm writes the single page to stdout.
        var result = await ToolRunner.RunAsync("pdftoppm",
            ["-f", page, "-l", page, "-singlefile", "-png",
             "-scale-to-x", targetWidth.ToString(CultureInfo.InvariantCulture), "-scale-to-y", "-1", "--", path],
            Timeout, ct).ConfigureAwait(false);
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
}
