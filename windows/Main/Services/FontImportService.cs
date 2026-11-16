using System.Buffers.Binary;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;

namespace RainExplorer.Services;

/// <summary>Result of importing fonts: the families now available, or why nothing was imported.</summary>
public sealed record FontImportResult(IReadOnlyList<string> Families, int FilesInstalled, string? Error,
    IReadOnlyList<string> Skipped)
{
    public bool Succeeded => Error is null && Families.Count > 0;
}

/// <summary>
/// "Import font…" in Settings. Installs font files for the current user the way Windows' own "Install for
/// me" does: into <c>%LOCALAPPDATA%\Microsoft\Windows\Fonts</c>, registered under HKCU so other apps can use
/// them too, then tells running apps the font list changed. Accepts .ttf/.otf/.ttc/.otc files and .zip
/// archives of them (what Google Fonts and most font sites hand out), from disk or a direct download link.
/// Port of the Linux FontImportService, with two Windows differences:
/// <list type="bullet">
/// <item>Zip files are opened only by the isolated Rain Archives worker (the app itself never reads archives),
/// which extracts them into a fresh temporary folder; Rain then imports the font files found there.</item>
/// <item>Files are checked by reading their font tables directly. WPF isn't used for this because it
/// mis-handles a font file that is deleted after it has read it.</item>
/// </list>
/// </summary>
public static class FontImportService
{
    public static readonly string[] FontExtensions = [".ttf", ".otf", ".ttc", ".otc"];
    private const long MaxDownloadBytes = 60L * 1024 * 1024;
    private const long MaxFontBytes = 40L * 1024 * 1024;
    private const int MaxFontsPerArchive = 200;
    private const string FontsKey = @"Software\Microsoft\Windows NT\CurrentVersion\Fonts";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RainExplorer-FontImport/1.0");
        return client;
    }

    /// <summary>The per-user font folder. Tests point this at a temp folder.</summary>
    internal static string ImportFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");

    /// <summary>Register installed fonts with Windows (HKCU + font-change notice). Off in tests.</summary>
    internal static bool RegisterWithWindows { get; set; } = true;

    /// <summary>Extracts one zip into a new folder under the given (empty) parent; true on success.
    /// The Rain Archives worker by default; tests substitute their own.</summary>
    internal static Func<string, string, CancellationToken, Task<bool>> ExtractZip { get; set; } = ExtractWithWorkerAsync;

    public static bool IsImportable(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return FontExtensions.Contains(ext) || ext == ".zip";
    }

    /// <summary>Import font files and/or zip archives from disk.</summary>
    public static async Task<FontImportResult> ImportFilesAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        var batch = new Batch();
        foreach (string path in paths)
        {
            ct.ThrowIfCancellationRequested();
            string ext = Path.GetExtension(path).ToLowerInvariant();
            try
            {
                if (ext == ".zip") await ImportZipAsync(path, batch, ct).ConfigureAwait(false);
                else if (FontExtensions.Contains(ext)) await Task.Run(() => ImportFile(path, batch), ct).ConfigureAwait(false);
                else batch.Skipped.Add(Path.GetFileName(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                batch.Skipped.Add($"{Path.GetFileName(path)} ({ex.Message})");
            }
        }
        return Finish(batch);
    }

    /// <summary>Download a font file or a zip of fonts from a direct link (http/https).</summary>
    public static async Task<FontImportResult> ImportFromUrlAsync(string url, CancellationToken ct = default)
    {
        if (!TryParseLink(url, out Uri? uri))
            return Error("Paste a full web link starting with https://.");

        byte[] data;
        string name;
        try
        {
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxDownloadBytes)
                return Error("That download is larger than 60 MB, which is too big for a font.");
            string? media = response.Content.Headers.ContentType?.MediaType;
            if (media is "text/html" or "application/xhtml+xml")
                return Error("That link opens a web page, not a font file. On the font's page, copy the link of its "
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
                if (buffer.Length + read > MaxDownloadBytes) return Error("That download is larger than 60 MB, which is too big for a font.");
                buffer.Write(chunk, 0, read);
            }
            data = buffer.ToArray();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Error("The download took too long.");
        }
        catch (HttpRequestException ex)
        {
            return Error($"Couldn't download it: {ex.Message}");
        }

        var batch = new Batch();
        if (IsZip(data))
        {
            string folder = NewTempFolder("rain-font-download");
            try
            {
                string zip = Path.Combine(folder, Path.ChangeExtension(SafeFileName(name, ".zip"), ".zip"));
                await File.WriteAllBytesAsync(zip, data, ct).ConfigureAwait(false);
                await ImportZipAsync(zip, batch, ct).ConfigureAwait(false);
            }
            finally { TryDeleteFolder(folder); }
        }
        else
        {
            string fileName = SafeFileName(name, fallbackExtension: ".ttf");
            await Task.Run(() => { if (!Install(data, fileName, batch)) batch.Skipped.Add(fileName); }, ct).ConfigureAwait(false);
        }
        return Finish(batch);
    }

    internal static bool TryParseLink(string text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Uri? uri) =>
        Uri.TryCreate(text.Trim(), UriKind.Absolute, out uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static FontImportResult Error(string message) => new([], 0, message, []);

    private sealed class Batch
    {
        public readonly List<string> Installed = [];       // paths in the font folder
        public readonly SortedSet<string> ParsedNames = new(StringComparer.CurrentCultureIgnoreCase);
        public readonly List<string> Skipped = [];
        public int NewFiles;
    }

    // ---- Zip files: opened only by the Rain Archives worker --------------------------------------

    private static async Task ImportZipAsync(string zipPath, Batch batch, CancellationToken ct)
    {
        string parent = NewTempFolder("rain-font-zip");
        try
        {
            if (!await ExtractZip(zipPath, parent, ct).ConfigureAwait(false))
            {
                batch.Skipped.Add($"{Path.GetFileName(zipPath)} (Rain Archives couldn't open it)");
                return;
            }
            await Task.Run(() =>
            {
                int considered = 0;
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                };
                foreach (string file in Directory.EnumerateFiles(parent, "*", options))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!FontExtensions.Contains(Path.GetExtension(file).ToLowerInvariant())) continue;
                    if (++considered > MaxFontsPerArchive) break;
                    ImportFile(file, batch);
                }
                if (considered == 0) batch.Skipped.Add($"{Path.GetFileName(zipPath)} (no .ttf, .otf or .ttc files inside)");
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteFolder(parent);
        }
    }

    private static async Task<bool> ExtractWithWorkerAsync(string zipPath, string parent, CancellationToken ct)
    {
        await using var extraction = new PhysicalArchiveExtractionService(ArchiveClientRuntime.Shared);
        // Font zips are never password protected; no prompt is offered.
        PhysicalArchiveExtractionBatchResult result =
            await extraction.ExtractToAsync([zipPath], parent, _ => null, ct).ConfigureAwait(false);
        return result.CompletedCount == 1;
    }

    // ---- Installing one font ---------------------------------------------------------------------

    private static void ImportFile(string path, Batch batch)
    {
        try
        {
            if (new FileInfo(path).Length > MaxFontBytes) { batch.Skipped.Add($"{Path.GetFileName(path)} (too large)"); return; }
            if (!Install(File.ReadAllBytes(path), Path.GetFileName(path), batch)) batch.Skipped.Add(Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            batch.Skipped.Add($"{Path.GetFileName(path)} ({ex.Message})");
        }
    }

    /// <summary>Validate and install one font. Identical files already installed count as installed.</summary>
    private static bool Install(byte[] data, string originalName, Batch batch)
    {
        string? family = ReadFamilyName(data);
        if (family is null) return false;
        Directory.CreateDirectory(ImportFolder);
        string fileName = SafeFileName(originalName, Path.GetExtension(originalName));
        string target = Path.Combine(ImportFolder, fileName);
        for (int i = 2; File.Exists(target); i++)
        {
            if (File.ReadAllBytes(target).AsSpan().SequenceEqual(data))
            {
                batch.Installed.Add(target);
                batch.ParsedNames.Add(family);
                return true;
            }
            target = Path.Combine(ImportFolder, $"{Path.GetFileNameWithoutExtension(fileName)} ({i}){Path.GetExtension(fileName)}");
        }
        string part = target + ".part";
        File.WriteAllBytes(part, data);
        File.Move(part, target);
        if (RegisterWithWindows) Register(target, family);
        batch.Installed.Add(target);
        batch.ParsedNames.Add(family);
        batch.NewFiles++;
        return true;
    }

    private static FontImportResult Finish(Batch batch)
    {
        if (batch.Installed.Count == 0)
        {
            string what = batch.Skipped.Count > 0 ? $" ({string.Join(", ", batch.Skipped.Take(3))})" : "";
            return new([], 0, $"No usable fonts found{what}. Rain accepts .ttf, .otf, .ttc and .zip files.", batch.Skipped);
        }
        if (RegisterWithWindows && batch.NewFiles > 0) NotifyFontsChanged();
        ImportedFontCatalog.Invalidate();
        // Report the names WPF will use for these files, so the picker can select them directly.
        var families = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (string file in batch.Installed)
            foreach (string name in ImportedFontCatalog.FamiliesIn(file)) families.Add(name);
        if (families.Count == 0) families.UnionWith(batch.ParsedNames);
        return new(families.ToList(), batch.NewFiles, null, batch.Skipped);
    }

    // ---- Reading the font's own tables -----------------------------------------------------------

    /// <summary>
    /// The family name stored in a TrueType/OpenType file (or the first face of a collection), or null when
    /// the data isn't a well-formed font: it needs a known signature, in-bounds table records, and the
    /// cmap, head and name tables plus outlines (glyf or CFF).
    /// </summary>
    internal static string? ReadFamilyName(ReadOnlySpan<byte> data)
    {
        try
        {
            if (data.Length < 12) return null;
            int font = 0;
            if (Tag(data, 0) == "ttcf")
            {
                if (BinaryPrimitives.ReadUInt32BigEndian(data[8..]) == 0) return null;
                font = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[12..]));
            }
            uint version = BinaryPrimitives.ReadUInt32BigEndian(data[font..]);
            if (version is not (0x00010000 or 0x4F54544F /* OTTO */ or 0x74727565 /* true */)) return null;
            int tables = BinaryPrimitives.ReadUInt16BigEndian(data[(font + 4)..]);
            if (tables is 0 or > 200) return null;

            var found = new Dictionary<string, (int Offset, int Length)>();
            for (int i = 0; i < tables; i++)
            {
                int record = font + 12 + i * 16;
                string tag = Tag(data, record);
                long offset = BinaryPrimitives.ReadUInt32BigEndian(data[(record + 8)..]);
                long length = BinaryPrimitives.ReadUInt32BigEndian(data[(record + 12)..]);
                if (offset + length > data.Length) return null;
                found[tag] = ((int)offset, (int)length);
            }
            if (!found.ContainsKey("cmap") || !found.ContainsKey("head")
                || !(found.ContainsKey("glyf") || found.ContainsKey("CFF ") || found.ContainsKey("CFF2"))
                || !found.TryGetValue("name", out var name))
                return null;
            return ReadName(data.Slice(name.Offset, name.Length));
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static string? ReadName(ReadOnlySpan<byte> table)
    {
        int count = BinaryPrimitives.ReadUInt16BigEndian(table[2..]);
        int strings = BinaryPrimitives.ReadUInt16BigEndian(table[4..]);
        string? best = null;
        int bestScore = -1;
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> r = table.Slice(6 + i * 12, 12);
            int platform = BinaryPrimitives.ReadUInt16BigEndian(r);
            int language = BinaryPrimitives.ReadUInt16BigEndian(r[4..]);
            int id = BinaryPrimitives.ReadUInt16BigEndian(r[6..]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(r[8..]);
            int offset = BinaryPrimitives.ReadUInt16BigEndian(r[10..]);
            if (id is not (1 or 16) || platform is not (0 or 1 or 3)) continue;
            // Typographic family (16) over legacy family (1); Windows/Unicode over Mac; US English first.
            int score = (id == 16 ? 8 : 0) + (platform != 1 ? 4 : 0) + (language is 0x409 or 0 ? 2 : 0);
            if (score <= bestScore) continue;
            ReadOnlySpan<byte> raw = table.Slice(strings + offset, length);
            string text = (platform == 1 ? Encoding.Latin1.GetString(raw) : Encoding.BigEndianUnicode.GetString(raw)).Trim();
            if (text.Length == 0) continue;
            best = text;
            bestScore = score;
        }
        return best;
    }

    private static string Tag(ReadOnlySpan<byte> data, int at) => Encoding.ASCII.GetString(data.Slice(at, 4));

    // ---- Windows registration ("Install for me") -------------------------------------------------

    private static void Register(string path, string family)
    {
        try
        {
            string kind = Path.GetExtension(path).ToLowerInvariant() is ".otf" or ".otc" ? "OpenType" : "TrueType";
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(FontsKey, writable: true);
            string valueName = $"{family} ({kind})";
            for (int i = 2; key.GetValue(valueName) is string existing
                            && !string.Equals(existing, path, StringComparison.OrdinalIgnoreCase); i++)
                valueName = $"{family} {i} ({kind})";
            key.SetValue(valueName, path, RegistryValueKind.String);
            AddFontResourceW(path);
        }
        catch { /* The file is still usable in Rain; other apps see it after the next sign-in at worst. */ }
    }

    private static void NotifyFontsChanged()
    {
        try { SendMessageTimeoutW(HwndBroadcast, WmFontChange, 0, 0, SmtoAbortIfHung, 1000, out _); }
        catch { }
    }

    private static readonly nint HwndBroadcast = 0xffff;
    private const uint WmFontChange = 0x001D;
    private const uint SmtoAbortIfHung = 0x0002;

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int AddFontResourceW(string fileName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageTimeoutW(nint hWnd, uint msg, nint wParam, nint lParam,
        uint flags, uint timeout, out nint result);

    // ---- helpers --------------------------------------------------------------------------------

    private static bool IsZip(byte[] data) => data.Length > 4 && data[0] == 'P' && data[1] == 'K' && data[2] == 3 && data[3] == 4;

    private static string NewTempFolder(string prefix)
    {
        string folder = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void TryDeleteFolder(string folder)
    {
        try { Directory.Delete(folder, recursive: true); } catch { }
    }

    internal static string SafeFileName(string name, string fallbackExtension)
    {
        string file = Path.GetFileName(name.Replace('/', '\\'));
        var invalid = Path.GetInvalidFileNameChars();
        file = new string(file.Where(c => !invalid.Contains(c) && !char.IsControl(c)).ToArray()).Trim().TrimStart('.');
        if (file.Length == 0) file = "font";
        if (!FontExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()) && Path.GetExtension(file) != fallbackExtension)
            file += fallbackExtension;
        return file.Length > 120 ? file[..100] + Path.GetExtension(file) : file;
    }
}

/// <summary>
/// Fonts in the per-user font folder, loaded straight from their files so Rain can use a font the moment
/// it's imported (and fonts installed "for me" that WPF's system list doesn't show). Port of the Linux
/// ImportedFontCollection.
/// </summary>
public static class ImportedFontCatalog
{
    private static readonly object Gate = new();
    private static Snapshot? _snapshot;

    private sealed record Snapshot(
        Dictionary<string, FontFamily> Families,
        Dictionary<string, List<string>> NamesByFile);

    /// <summary>Forget the cached list so the next lookup rescans (after an import).</summary>
    public static void Invalidate()
    {
        lock (Gate) _snapshot = null;
    }

    public static IReadOnlyDictionary<string, FontFamily> Families => Current.Families;

    public static bool TryGet(string family, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out FontFamily? font) =>
        Current.Families.TryGetValue(family, out font);

    /// <summary>The family names WPF reads from one file in the font folder.</summary>
    public static IReadOnlyList<string> FamiliesIn(string file) =>
        Current.NamesByFile.TryGetValue(file, out var names) ? names : [];

    private static Snapshot Current
    {
        get
        {
            lock (Gate) return _snapshot ??= Scan();
        }
    }

    private static Snapshot Scan()
    {
        var families = new Dictionary<string, FontFamily>(StringComparer.CurrentCultureIgnoreCase);
        var byFile = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string folder = FontImportService.ImportFolder;
        try
        {
            if (!Directory.Exists(folder)) return new(families, byFile);
            foreach (string file in Directory.EnumerateFiles(folder))
            {
                if (!FontImportService.FontExtensions.Contains(Path.GetExtension(file).ToLowerInvariant())) continue;
                try
                {
                    var names = new List<string>();
                    foreach (FontFamily family in Fonts.GetFontFamilies(new Uri(file)))
                    {
                        if (FamilyName(family) is not { Length: > 0 } name) continue;
                        names.Add(name);
                        families.TryAdd(name, family);
                    }
                    byFile[file] = names;
                }
                catch { /* one unreadable file doesn't hide the others */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return new(families, byFile);
    }

    internal static string? FamilyName(FontFamily family)
    {
        if (family.FamilyNames.TryGetValue(XmlLanguage.GetLanguage("en-us"), out string? english)) return english.Trim();
        string? any = family.FamilyNames.Values.FirstOrDefault();
        if (any is not null) return any.Trim();
        int hash = family.Source.LastIndexOf('#');
        return hash >= 0 ? family.Source[(hash + 1)..].Trim() : null;
    }
}
