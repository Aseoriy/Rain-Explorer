using System.IO;
using System.IO.Compression;
using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Git.Tests;

// Ported from the Linux FontImportTests. The import folder is a temp folder and nothing is registered
// with Windows, so these tests never touch the real per-user fonts. Zip extraction belongs to the Rain
// Archives worker; here a stand-in extractor plays its part.
[Collection(nameof(FontImportTests))]
public sealed class FontImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rain-fonts-" + Guid.NewGuid().ToString("N"));
    private readonly string _folder;
    private readonly string _originalFolder = FontImportService.ImportFolder;
    private readonly bool _originalRegister = FontImportService.RegisterWithWindows;
    private readonly Func<string, string, CancellationToken, Task<bool>> _originalExtract = FontImportService.ExtractZip;

    public FontImportTests()
    {
        _folder = Path.Combine(_root, "Fonts");
        Directory.CreateDirectory(_root);
        FontImportService.ImportFolder = _folder;
        FontImportService.RegisterWithWindows = false;
        FontImportService.ExtractZip = (zip, parent, _) =>
        {
            ZipFile.ExtractToDirectory(zip, Path.Combine(parent, Path.GetFileNameWithoutExtension(zip)));
            return Task.FromResult(true);
        };
        ImportedFontCatalog.Invalidate();
    }

    public void Dispose()
    {
        FontImportService.ImportFolder = _originalFolder;
        FontImportService.RegisterWithWindows = _originalRegister;
        FontImportService.ExtractZip = _originalExtract;
        ImportedFontCatalog.Invalidate();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static string RealFont() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf");

    [Fact]
    public async Task ARealFontIsInstalledAndUsableWithoutRestart()
    {
        string source = Path.Combine(_root, "MyFont.ttf");
        File.Copy(RealFont(), source);

        FontImportResult result = await FontImportService.ImportFilesAsync([source]);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(1, result.FilesInstalled);
        Assert.Equal(["Segoe UI"], result.Families);
        Assert.True(File.Exists(Path.Combine(_folder, "MyFont.ttf")));
        Assert.True(ImportedFontCatalog.TryGet("Segoe UI", out var font));
        Assert.EndsWith("MyFont.ttf", font!.BaseUri.LocalPath);
    }

    [Fact]
    public async Task ImportingTheSameFileTwiceDoesNotDuplicateIt()
    {
        string source = Path.Combine(_root, "Same.ttf");
        File.Copy(RealFont(), source);
        await FontImportService.ImportFilesAsync([source]);
        FontImportResult again = await FontImportService.ImportFilesAsync([source]);

        Assert.True(again.Succeeded, again.Error);
        Assert.Equal(0, again.FilesInstalled);
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task FontsInsideAZipAreInstalledFlatAndFakesAreSkipped()
    {
        string zipPath = Path.Combine(_root, "family.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(RealFont(), "static/Family-Regular.ttf");
            using var fake = new StreamWriter(zip.CreateEntry("fake.ttf").Open());
            fake.Write("this is not a font");
        }

        FontImportResult result = await FontImportService.ImportFilesAsync([zipPath]);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(["Family-Regular.ttf"], Directory.GetFiles(_folder).Select(Path.GetFileName));
        Assert.Contains("fake.ttf", result.Skipped);
        Assert.Empty(Directory.GetDirectories(Path.GetTempPath(), "rain-font-zip-*")
            .Where(d => Directory.GetCreationTimeUtc(d) > DateTime.UtcNow.AddMinutes(-1)));
    }

    [Fact]
    public async Task AZipTheWorkerRefusesIsReportedNotOpened()
    {
        FontImportService.ExtractZip = (_, _, _) => Task.FromResult(false);
        string zipPath = Path.Combine(_root, "bad.zip");
        File.WriteAllText(zipPath, "PK not really");

        FontImportResult result = await FontImportService.ImportFilesAsync([zipPath]);

        Assert.False(result.Succeeded);
        Assert.Contains("Rain Archives couldn't open it", result.Error);
    }

    [Fact]
    public async Task NonFontsAreRejectedWithAnExplanation()
    {
        string notAFont = Path.Combine(_root, "notes.ttf");
        File.WriteAllText(notAFont, "hello");

        FontImportResult result = await FontImportService.ImportFilesAsync([notAFont]);

        Assert.False(result.Succeeded);
        Assert.Contains("No usable fonts found", result.Error);
        Assert.False(Directory.Exists(_folder) && Directory.EnumerateFiles(_folder).Any());
    }

    [Fact]
    public void FontTablesAreReadWithoutWpf()
    {
        byte[] real = File.ReadAllBytes(RealFont());
        Assert.Equal("Segoe UI", FontImportService.ReadFamilyName(real));
        Assert.Null(FontImportService.ReadFamilyName(real.AsSpan(0, 400)));         // truncated
        Assert.Null(FontImportService.ReadFamilyName("not a font at all"u8));
        Assert.Null(FontImportService.ReadFamilyName(new byte[64]));
    }

    [Fact]
    public void FontCollectionsUseTheirFirstFace()
    {
        string ttc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "cambria.ttc");
        if (!File.Exists(ttc)) return;
        Assert.False(string.IsNullOrWhiteSpace(FontImportService.ReadFamilyName(File.ReadAllBytes(ttc))));
    }

    [Theory]
    [InlineData("https://fonts.example.com/Inter.zip", true)]
    [InlineData("  http://example.com/a.ttf ", true)]
    [InlineData("ftp://example.com/a.ttf", false)]
    [InlineData("C:\\fonts\\a.ttf", false)]
    [InlineData("not a link", false)]
    public void OnlyWebLinksAreAccepted(string text, bool ok) =>
        Assert.Equal(ok, FontImportService.TryParseLink(text, out _));

    [Theory]
    [InlineData("..\\..\\evil.ttf", ".ttf", "evil.ttf")]
    [InlineData("a/b/c.otf", ".ttf", "c.otf")]
    [InlineData("download", ".ttf", "download.ttf")]
    [InlineData("", ".ttf", "font.ttf")]
    public void FileNamesCanNeverPickTheFolder(string name, string fallback, string expected) =>
        Assert.Equal(expected, FontImportService.SafeFileName(name, fallback));
}
