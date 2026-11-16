using System.IO.Compression;
using System.Net.Http;
using SkiaSharp;

namespace RainExplorer.Services;

/// <summary>Result of importing fonts: the families now available, or why nothing was imported.</summary>
public sealed record FontImportResult(IReadOnlyList<string> Families, int FilesInstalled, string? Error)
{
    public bool Succeeded => Error is null && Families.Count > 0;
}

/// <summary>
/// "Import font…" in Settings. Installs font files for the current user in
/// <c>~/.local/share/fonts/Rain Explorer</c> (the standard per-user font folder, so other apps can use them
/// too) and refreshes fontconfig's cache. Accepts .ttf/.otf/.ttc/.otc files and .zip archives of them
/// (what Google Fonts and most font sites hand out), from a file on disk or a direct download link.
/// Every file is opened with Skia first: anything that isn't a readable font is skipped, never installed.
/// </summary>
public static class FontImportService
{
    public static readonly string[] FontExtensions = [".ttf", ".otf", ".ttc", ".otc"];
    private const long MaxDownloadBytes = 60L * 1024 * 1024;
    private const long MaxFontBytes = 40L * 1024 * 1024;
    private const int MaxFontsPerArchive = 200;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RainExplorer-FontImport/1.0");
        return client;
    }

    /// <summary>Where imported fonts go ($XDG_DATA_HOME/fonts/Rain Explorer).</summary>
    public static string ImportFolder => Path.Combine(XdgDirs.DataHome, "fonts", "Rain Explorer");

    public static bool IsImportable(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return FontExtensions.Contains(ext) || ext == ".zip";
    }

    /// <summary>Import font files and/or zip archives from disk.</summary>
    public static Task<FontImportResult> ImportFilesAsync(IEnumerable<string> paths, CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            var families = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
            int installed = 0;
            var skipped = new List<string>();
            foreach (string path in paths)
            {
                ct.ThrowIfCancellationRequested();
                string ext = Path.GetExtension(path).ToLowerInvariant();
                try
                {
                    if (ext == ".zip")
                    {
                        await using var stream = File.OpenRead(path);
                        installed += ImportArchive(stream, families, skipped, ct);
                    }
                    else if (FontExtensions.Contains(ext))
                    {
                        if (new FileInfo(path).Length > MaxFontBytes) { skipped.Add($"{Path.GetFileName(path)} (too large)"); continue; }
                        if (Install(File.ReadAllBytes(path), Path.GetFileName(path), families)) installed++;
                        else skipped.Add(Path.GetFileName(path));
                    }
                    else skipped.Add(Path.GetFileName(path));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    skipped.Add($"{Path.GetFileName(path)} ({ex.Message})");
                }
            }
            return await Finish(families, installed, skipped, ct).ConfigureAwait(false);
        }, ct);

    /// <summary>Download a font file or a zip of fonts from a direct link (http/https).</summary>
    public static async Task<FontImportResult> ImportFromUrlAsync(string url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return new([], 0, "Paste a full web link starting with https://.");

        byte[] data;
        string name;
        try
        {
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxDownloadBytes)
                return new([], 0, "That download is larger than 60 MB, which is too big for a font.");
            string? media = response.Content.Headers.ContentType?.MediaType;
            if (media is "text/html" or "application/xhtml+xml")
                return new([], 0, "That link opens a web page, not a font file. On the font's page, copy the link of its "
                                  + "Download button (a .ttf, .otf or .zip file), or download it and use From a file.");
            name = response.Content.Headers.ContentDisposition?.FileNameStar
                   ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                   ?? Path.GetFileName(uri.LocalPath);
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxDownloadBytes) return new([], 0, "That download is larger than 60 MB, which is too big for a font.");
                buffer.Write(chunk, 0, read);
            }
            data = buffer.ToArray();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new([], 0, "The download took too long.");
        }
        catch (HttpRequestException ex)
        {
            return new([], 0, $"Couldn't download it: {ex.Message}");
        }

        return await Task.Run(async () =>
        {
            var families = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
            var skipped = new List<string>();
            int installed;
            if (IsZip(data))
            {
                try
                {
                    using var stream = new MemoryStream(data);
                    installed = ImportArchive(stream, families, skipped, ct);
                }
                catch (InvalidDataException)
                {
                    return new FontImportResult([], 0, "The downloaded zip file is damaged.");
                }
            }
            else
            {
                string fileName = SafeFileName(name, fallbackExtension: ".ttf");
                installed = Install(data, fileName, families) ? 1 : 0;
                if (installed == 0) skipped.Add(fileName);
            }
            return await Finish(families, installed, skipped, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>The family name stored in a font file, or null when it isn't a font Skia can read.</summary>
    internal static string? ReadFamilyName(byte[] data)
    {
        try
        {
            using var skData = SKData.CreateCopy(data);
            using var typeface = SKTypeface.FromData(skData);
            string? family = typeface?.FamilyName;
            return string.IsNullOrWhiteSpace(family) ? null : family.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static int ImportArchive(Stream stream, SortedSet<string> families, List<string> skipped, CancellationToken ct)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        int installed = 0, considered = 0;
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (!FontExtensions.Contains(Path.GetExtension(entry.Name).ToLowerInvariant())) continue;
            if (++considered > MaxFontsPerArchive) break;
            // Only the file name is used: folder structure inside the zip can never pick the destination.
            if (entry.Length > MaxFontBytes) { skipped.Add($"{entry.Name} (too large)"); continue; }
            using var input = entry.Open();
            using var buffer = new MemoryStream();
            input.CopyTo(buffer);
            if (Install(buffer.ToArray(), entry.Name, families)) installed++;
            else skipped.Add(entry.Name);
        }
        if (considered == 0) skipped.Add("the zip contained no .ttf, .otf or .ttc files");
        return installed;
    }

    /// <summary>Validate and copy one font. Identical files already installed count as installed.</summary>
    private static bool Install(byte[] data, string originalName, SortedSet<string> families)
    {
        string? family = ReadFamilyName(data);
        if (family is null) return false;
        Directory.CreateDirectory(ImportFolder);
        string fileName = SafeFileName(originalName, Path.GetExtension(originalName));
        string target = Path.Combine(ImportFolder, fileName);
        for (int i = 2; File.Exists(target); i++)
        {
            if (File.ReadAllBytes(target).AsSpan().SequenceEqual(data)) { families.Add(family); return true; }
            target = Path.Combine(ImportFolder, $"{Path.GetFileNameWithoutExtension(fileName)} ({i}){Path.GetExtension(fileName)}");
        }
        string temp = target + ".part";
        File.WriteAllBytes(temp, data);
        File.Move(temp, target);
        families.Add(family);
        return true;
    }

    private static async Task<FontImportResult> Finish(SortedSet<string> families, int installed, List<string> skipped, CancellationToken ct)
    {
        if (families.Count == 0)
        {
            string what = skipped.Count > 0 ? $" ({string.Join(", ", skipped.Take(3))})" : "";
            return new([], 0, $"No usable fonts found{what}. Rain accepts .ttf, .otf, .ttc and .zip files.");
        }
        // Let fontconfig (and through it, Rain and other apps) see the new files.
        await ToolRunner.RunAsync("fc-cache", ["-f", ImportFolder], TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        return new(families.ToList(), installed, null);
    }

    private static bool IsZip(byte[] data) => data.Length > 4 && data[0] == 'P' && data[1] == 'K' && data[2] == 3 && data[3] == 4;

    private static string SafeFileName(string name, string fallbackExtension)
    {
        string file = Path.GetFileName(name.Replace('\\', '/'));
        file = new string(file.Where(c => c != '/' && c != '\0' && !char.IsControl(c)).ToArray()).Trim().TrimStart('.');
        if (file.Length == 0) file = "font";
        if (!FontExtensions.Contains(Path.GetExtension(file).ToLowerInvariant())) file += fallbackExtension;
        return file.Length > 120 ? file[..100] + Path.GetExtension(file) : file;
    }
}
