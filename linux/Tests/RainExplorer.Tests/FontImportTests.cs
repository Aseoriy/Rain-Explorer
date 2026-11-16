using System.IO.Compression;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using RainExplorer.Controls;
using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Tests;

/// <summary>Settings → Import font: files, zips, validation, and using a font without restarting.</summary>
public sealed class FontImportTests
{
    // A font that ships with kitty and isn't registered with fontconfig on most systems.
    private const string SampleFont = "/usr/lib/kitty/fonts/SymbolsNerdFontMono-Regular.ttf";
    private static string? AnyFont()
    {
        if (File.Exists(SampleFont)) return SampleFont;
        foreach (string dir in new[] { "/usr/share/fonts", "/usr/local/share/fonts" })
            if (Directory.Exists(dir))
                return Directory.EnumerateFiles(dir, "*.ttf", SearchOption.AllDirectories).FirstOrDefault();
        return null;
    }

    private static void ResetImportFolder()
    {
        if (Directory.Exists(FontImportService.ImportFolder)) Directory.Delete(FontImportService.ImportFolder, recursive: true);
    }

    [AvaloniaFact]
    public async Task ImportedFontWorksImmediately()
    {
        string? font = AnyFont();
        if (font is null) return;
        ResetImportFolder();
        var result = await FontImportService.ImportFilesAsync([font]);
        Assert.True(result.Succeeded, result.Error);
        string family = Assert.Single(result.Families);
        Assert.True(File.Exists(Path.Combine(FontImportService.ImportFolder, Path.GetFileName(font))));

        ImportedFontCollection.Register();
        ImportedFontCollection.Instance.Reload();
        Assert.True(ImportedFontCollection.Instance.Contains(family),
            $"collection has [{string.Join("|", ImportedFontCollection.Instance.FamilyNames)}]; manager {FontManager.Current.GetHashCode()}; files [{string.Join("|", Directory.GetFiles(FontImportService.ImportFolder).Select(Path.GetFileName))}]");
        Assert.True(FontManager.Current.TryGetGlyphTypeface(
            new Typeface(new FontFamily($"{ImportedFontCollection.CollectionKey}#{family}")), out var typeface));
        Assert.Equal(family, typeface.FamilyName);

        // Importing the same file again doesn't make a copy.
        var again = await FontImportService.ImportFilesAsync([font]);
        Assert.True(again.Succeeded);
        Assert.Single(Directory.GetFiles(FontImportService.ImportFolder));
    }

    [AvaloniaFact]
    public async Task ZipsInstallOnlyFontsAndIgnoreTheirFolderPaths()
    {
        string? font = AnyFont();
        if (font is null) return;
        ResetImportFolder();
        string zipPath = Path.Combine(TestEnvironment.NewFolder("font-zip"), "family.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(font, "static/Some-Regular.ttf");
            zip.CreateEntryFromFile(font, "../../escape.ttf");   // must land inside the import folder
            var readme = zip.CreateEntry("OFL.txt");
            using (var w = new StreamWriter(readme.Open())) w.Write("license");
            var fake = zip.CreateEntry("not-a-font.ttf");
            using (var w = new StreamWriter(fake.Open())) w.Write("this is text, not a font");
        }

        var result = await FontImportService.ImportFilesAsync([zipPath]);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(2, result.FilesInstalled);   // the fake .ttf is rejected after Skia can't read it
        string[] installed = Directory.GetFiles(FontImportService.ImportFolder).Select(Path.GetFileName).ToArray()!;
        Assert.Contains("Some-Regular.ttf", installed);
        Assert.Contains("escape.ttf", installed);
        Assert.DoesNotContain("not-a-font.ttf", installed);
        Assert.DoesNotContain("OFL.txt", installed);
        Assert.False(File.Exists(Path.Combine(FontImportService.ImportFolder, "..", "..", "escape.ttf")));
    }

    [AvaloniaFact]
    public async Task NonFontsAreRejectedWithAClearMessage()
    {
        ResetImportFolder();
        string fake = Path.Combine(TestEnvironment.NewFolder("fake-font"), "Pretty.ttf");
        File.WriteAllText(fake, "hello");
        var result = await FontImportService.ImportFilesAsync([fake]);
        Assert.False(result.Succeeded);
        Assert.Contains(".ttf, .otf, .ttc and .zip", result.Error);
        Assert.False(Directory.Exists(FontImportService.ImportFolder) && Directory.GetFiles(FontImportService.ImportFolder).Length > 0);
    }

    [Fact]
    public async Task BadLinksAreExplained()
    {
        Assert.Contains("https://", (await FontImportService.ImportFromUrlAsync("not a link")).Error);
        Assert.Contains("https://", (await FontImportService.ImportFromUrlAsync("file:///etc/passwd")).Error);
    }

    [Theory]
    [InlineData("font.ttf", true)]
    [InlineData("Font.OTF", true)]
    [InlineData("family.zip", true)]
    [InlineData("readme.txt", false)]
    [InlineData("font.woff2", false)]
    public void ImportableFileTypes(string name, bool expected) =>
        Assert.Equal(expected, FontImportService.IsImportable(name));

    [AvaloniaFact]
    public void FontSearchFiltersTheList()
    {
        var settings = new SettingsView();
        var window = new Window { Content = settings, Width = 1100, Height = 800 };
        window.Show();
        try
        {
            var list = settings.FindControl<ListBox>("FontList")!;
            var search = settings.FindControl<TextBox>("FontSearch")!;
            int all = list.ItemCount;
            Assert.True(all > 0);
            search.Text = "noto";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.InRange(list.ItemCount, 1, all);
            Assert.All(list.Items.OfType<FontChoice>(), f => Assert.Contains("noto", f.Name, StringComparison.OrdinalIgnoreCase));
            search.Text = "zzzz-no-such-font";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, list.ItemCount);
            Assert.Contains("No fonts match", settings.FindControl<TextBlock>("FontStatus")!.Text);
            search.Text = "";
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(all, list.ItemCount);
        }
        finally
        {
            window.Close();
        }
    }
}
