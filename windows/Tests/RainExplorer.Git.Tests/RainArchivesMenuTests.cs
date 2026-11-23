using System.Xml.Linq;
using RainExplorer.Models;
using Xunit;

namespace RainExplorer.Git.Tests;

/// <summary>The file list's "Rain Archives" submenu and the sharp-text rules for menus and dialogs.</summary>
public sealed class RainArchivesMenuTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData(@"C:\Downloads\Photos.zip", "Extract to \"Photos\\\"")]
    [InlineData(@"C:\Downloads\backup.tar.gz", "Extract to \"backup\\\"")]
    [InlineData(@"C:\Downloads\my_files.7z", "Extract to \"my__files\\\"")]
    [InlineData(@"C:\Downloads\.zip", "Extract to new folder")]
    public void ExtractHeaderNamesTheFolderLikeWinRar(string path, string expected)
    {
        Assert.Equal(expected, ArchiveFileTypes.ExtractToOwnFolderHeader([path]));
    }

    [Fact]
    public void ExtractHeaderShortensLongNamesAndCoversSeveralArchives()
    {
        string header = ArchiveFileTypes.ExtractToOwnFolderHeader([@"C:\x\" + new string('a', 80) + ".rar"]);
        Assert.EndsWith("…\\\"", header, StringComparison.Ordinal);
        Assert.True(header.Length < 60);

        Assert.Equal("Extract each to its own folder",
            ArchiveFileTypes.ExtractToOwnFolderHeader([@"C:\a.zip", @"C:\b.rar"]));
    }

    [Fact]
    public void CompressNamesFollowTheSelectionLikeWinRar()
    {
        var folder = new ArchiveCreationSource(ArchiveCreationSourceKind.Directory, @"C:\Work\v1.2", "v1.2");
        var file = new ArchiveCreationSource(ArchiveCreationSourceKind.File, @"C:\Work\report.docx", "report.docx");

        Assert.Equal("v1.2", ArchiveFileTypes.SuggestedArchiveStem([folder], @"C:\Work", ArchiveWriteFormat.Zip));
        Assert.Equal("report", ArchiveFileTypes.SuggestedArchiveStem([file], @"C:\Work", ArchiveWriteFormat.TarGZip));
        Assert.Equal("report.docx", ArchiveFileTypes.SuggestedArchiveStem([file], @"C:\Work", ArchiveWriteFormat.GZip));
        Assert.Equal("Work", ArchiveFileTypes.SuggestedArchiveStem([folder, file], @"C:\Work\", ArchiveWriteFormat.Zip));
        Assert.Equal("Archive", ArchiveFileTypes.SuggestedArchiveStem([folder, file], @"C:\", ArchiveWriteFormat.Zip));
    }

    [Fact]
    public void SevenZipFormatsGetTheRightNamesAndExtensions()
    {
        var file = new ArchiveCreationSource(ArchiveCreationSourceKind.File, @"C:\Work\notes.txt", "notes.txt");
        Assert.Equal("notes.txt", ArchiveFileTypes.SuggestedArchiveStem([file], @"C:\Work", ArchiveWriteFormat.Xz));
        Assert.Equal("notes", ArchiveFileTypes.SuggestedArchiveStem([file], @"C:\Work", ArchiveWriteFormat.SevenZip));
        Assert.Equal(".7z", RainExplorer.Services.ArchiveWriteValidation.ExtensionFor(ArchiveWriteFormat.SevenZip));
        Assert.Equal(".tar.xz", RainExplorer.Services.ArchiveWriteValidation.ExtensionFor(ArchiveWriteFormat.TarXz));
        Assert.Equal(".xz", RainExplorer.Services.ArchiveWriteValidation.ExtensionFor(ArchiveWriteFormat.Xz));
        Assert.Equal("Compress to \"Photos.7z\"", ArchiveFileTypes.CompressToHeader("Photos", ".7z"));
    }

    [Fact]
    public void CompressMenuListsZipThenSevenZipFirst()
    {
        string source = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "Main", "Controls", "PaneView.xaml.cs"));
        int zip = source.IndexOf("ArchiveWriteFormat.Zip, ArchiveWriteFormat.SevenZip,", StringComparison.Ordinal);
        Assert.True(zip > 0, "The Compress-to order should start with ZIP then 7z.");
    }

    [Fact]
    public void FreeArchivePathKeepsDoubleExtensionsTogether()
    {
        string folder = Directory.CreateTempSubdirectory("rain-compress-").FullName;
        try
        {
            Assert.Equal(Path.Combine(folder, "Photos.tar.gz"),
                ArchiveFileTypes.FreeArchivePath(folder, "Photos", ".tar.gz"));
            File.WriteAllText(Path.Combine(folder, "Photos.tar.gz"), "");
            Assert.Equal(Path.Combine(folder, "Photos (2).tar.gz"),
                ArchiveFileTypes.FreeArchivePath(folder, "Photos", ".tar.gz"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void CompressHeaderShowsTheArchiveName()
    {
        Assert.Equal("Compress to \"Photos.zip\"", ArchiveFileTypes.CompressToHeader("Photos", ".zip"));
        Assert.Equal("Compress to \"my__stuff.tar.gz\"", ArchiveFileTypes.CompressToHeader("my_stuff", ".tar.gz"));
        string longHeader = ArchiveFileTypes.CompressToHeader(new string('b', 90), ".7z");
        Assert.EndsWith("….7z\"", longHeader, StringComparison.Ordinal);
    }

    [Fact]
    public void ArchiveItemsLiveOnlyInsideTheRainArchivesSubmenu()
    {
        var pane = XDocument.Load(Path.Combine(FindRepositoryRoot(), "Main", "Controls", "PaneView.xaml"));
        XElement submenu = FindMenu(pane, "MenuRainArchives");

        string[] archiveItems =
        [
            "MenuExtract", "MenuExtractTo", "MenuCreateArchive",
            "MenuArchiveIntegrity", "MenuArchiveExtract", "MenuArchiveRebuildCopy",
        ];
        foreach (string name in archiveItems)
        {
            XElement item = FindMenu(pane, name);
            Assert.Same(submenu, item.Parent);
        }
        Assert.Equal("Add to archive…", (string?)FindMenu(pane, "MenuCreateArchive").Attribute("Header"));
    }

    [Fact]
    public void ExtractItemsAreOfferedOnlyForArchiveFiles()
    {
        string source = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "Main", "Controls", "PaneView.xaml.cs"));
        Assert.Contains("items.All(item => ArchiveFileTypes.IsSupportedPath(item.FullPath))", source, StringComparison.Ordinal);
        Assert.Contains("MenuExtract.Visibility = Show(allArchiveFiles);", source, StringComparison.Ordinal);
        Assert.Contains("MenuExtractTo.Visibility = Show(allArchiveFiles);", source, StringComparison.Ordinal);
        Assert.Contains("MenuRainArchives.Visibility = Show(allArchiveFiles || canAdd || insideArchive);", source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NoTextSitsUnderAShadowEffect()
    {
        // A DropShadowEffect on an element that contains text makes the text render soft, so shadows
        // live on their own empty layer. The only effect left on an element with children is the
        // tab underline, which holds no text.
        string main = Path.Combine(FindRepositoryRoot(), "Main");
        var offenders = new List<string>();
        foreach (string file in Directory.EnumerateFiles(main, "*.xaml", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            foreach (XElement effect in XDocument.Load(file).Descendants()
                         .Where(element => element.Name.LocalName.EndsWith(".Effect", StringComparison.Ordinal)))
            {
                XElement owner = effect.Parent!;
                if ((string?)owner.Attribute(Xaml + "Name") == "ul") continue;
                if (owner.Elements().Any(child => !child.Name.LocalName.Contains('.')))
                    offenders.Add($"{Path.GetFileName(file)}: {owner.Name.LocalName} {(string?)owner.Attribute(Xaml + "Name")}");
            }
        }
        Assert.Empty(offenders);
    }

    private static XElement FindMenu(XDocument document, string name) =>
        document.Descendants(Presentation + "MenuItem").Single(element =>
            string.Equals((string?)element.Attribute(Xaml + "Name"), name, StringComparison.Ordinal));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Main", "Controls", "PaneView.xaml")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("windows/ folder not found.");
    }
}
