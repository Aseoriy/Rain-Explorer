using System.Text;
using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Tests;

/// <summary>PDF/media previews, network locations and the update checker (the 2.0.0 parity services).</summary>
public sealed class ParityServicesTests
{
    // ---- PDF ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Title:          x\nPages:          12\nEncrypted:      no\n", 12)]
    [InlineData("Pages: 1", 1)]
    [InlineData("Title: no pages here", 0)]
    [InlineData("Pages: abc", 0)]
    public void PdfPageCountIsReadFromPdfinfo(string output, int expected) =>
        Assert.Equal(expected, PdfRenderService.ParsePageCount(output));

    // ---- Media -------------------------------------------------------------------------------------

    [Fact]
    public void ProbeReadsDurationTagsAndVideoSizeButSkipsCoverArt()
    {
        const string json = """
            {"streams":[
               {"codec_type":"video","width":600,"height":600,"disposition":{"attached_pic":1}},
               {"codec_type":"video","width":1920,"height":1080,"disposition":{"attached_pic":0}},
               {"codec_type":"audio"}],
             "format":{"duration":"125.5","tags":{"TITLE":"Song","artist":"Band","album":"Record"}}}
            """;
        var info = MediaPreviewService.ParseProbe(json);
        Assert.NotNull(info);
        Assert.Equal(TimeSpan.FromSeconds(125.5), info.Duration);
        Assert.Equal((1920, 1080), (info.Width, info.Height));
        Assert.Equal(("Song", "Band", "Record"), (info.Title, info.Artist, info.Album));
        Assert.Equal("2:05", MediaPreviewService.FormatDuration(info.Duration!.Value));
    }

    [Fact]
    public void ProbeOfGarbageIsNull() => Assert.Null(MediaPreviewService.ParseProbe("not json"));

    [Theory]
    [InlineData("/home/a/My Video.mp4", "file:///home/a/My%20Video.mp4")]
    [InlineData("/tmp/x#1?.mkv", "file:///tmp/x%231%3F.mkv")]
    [InlineData("/tmp/é.ogg", "file:///tmp/%C3%A9.ogg")]
    [InlineData("/tmp/a(1),b'c.mp3", "file:///tmp/a(1),b'c.mp3")]
    public void ThumbnailUriIsEscapedLikeGlib(string path, string uri) =>
        Assert.Equal(uri, MediaPreviewService.FileUri(path));

    [Fact]
    public void ThumbnailNameIsMd5OfUri()
    {
        // md5("file:///home/jens/photos/me.png") from the FreeDesktop thumbnail spec.
        Assert.Equal("c6ee772d9e49320e97ec29a7eb5b1697.png",
            MediaPreviewService.ThumbnailName("/home/jens/photos/me.png"));
    }

    [Fact]
    public void PngTextChunkIsRead()
    {
        byte[] png = BuildPng(("Thumb::URI", "file:///x"), ("Thumb::MTime", "1700000000"));
        Assert.Equal("1700000000", MediaPreviewService.ReadPngText(png, "Thumb::MTime"));
        Assert.Null(MediaPreviewService.ReadPngText(png, "Thumb::Size"));
        Assert.Null(MediaPreviewService.ReadPngText([1, 2, 3], "Thumb::MTime"));
    }

    private static byte[] BuildPng(params (string Key, string Value)[] texts)
    {
        var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        void Chunk(string type, byte[] data)
        {
            ms.Write([(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length]);
            ms.Write(Encoding.ASCII.GetBytes(type));
            ms.Write(data);
            ms.Write([0, 0, 0, 0]);   // CRC is not checked by the reader
        }
        Chunk("IHDR", new byte[13]);
        foreach (var (key, value) in texts)
            Chunk("tEXt", [.. Encoding.Latin1.GetBytes(key), 0, .. Encoding.Latin1.GetBytes(value)]);
        Chunk("IDAT", [0]);
        Chunk("IEND", []);
        return ms.ToArray();
    }

    // ---- Network locations -------------------------------------------------------------------------

    [Theory]
    [InlineData("smb://nas/media", true)]
    [InlineData("SMB://nas", true)]
    [InlineData("sftp://me@host/home/me", true)]
    [InlineData("dav://cloud.example/remote.php", true)]
    [InlineData("http://example.com", false)]
    [InlineData("file:///home", false)]
    [InlineData("smb://", false)]
    [InlineData("/home/me", false)]
    [InlineData("nas/share", false)]
    public void NetworkUrisAreRecognized(string text, bool expected) =>
        Assert.Equal(expected, NetworkLocationService.IsNetworkUri(text));

    [Theory]
    [InlineData("smb://nas", true)]
    [InlineData("smb://nas/", true)]
    [InlineData("smb://nas/media", false)]
    [InlineData("sftp://nas", false)]
    public void SmbServerRootIsDetected(string uri, bool expected) =>
        Assert.Equal(expected, NetworkLocationService.IsSmbServerRoot(uri));

    [Theory]
    [InlineData("smb-share:server=nas,share=media", "media on nas")]
    [InlineData("smb-share:domain=WORK,server=nas.local,share=my%20files,user=bob", "my files on nas.local")]
    [InlineData("sftp:host=example.com,user=me", "me@example.com (SFTP)")]
    [InlineData("ftp:host=ftp.example.com", "ftp.example.com (FTP)")]
    [InlineData("dav:host=cloud.example,ssl=true", "cloud.example (WebDAV (secure))")]
    [InlineData("not-a-mount", null)]
    [InlineData("smb-share:share=x", null)]
    public void GvfsMountFoldersGetReadableNames(string folder, string? expected) =>
        Assert.Equal(expected, NetworkLocationService.DescribeGvfsMount(folder));

    [Fact]
    public void GioInfoFieldsAreRead()
    {
        const string info = "display name: media on nas\nuri: smb://nas/media/\nlocal path: /run/user/1000/gvfs/smb-share:server=nas,share=media\n";
        Assert.Equal("/run/user/1000/gvfs/smb-share:server=nas,share=media", NetworkLocationService.ReadField(info, "local path"));
        Assert.Equal("smb://nas/media/", NetworkLocationService.ReadField(info, "uri"));
        Assert.Null(NetworkLocationService.ReadField(info, "type"));
    }

    [Fact]
    public void FoldersInsideAShareFindTheirShare()
    {
        string root = NetworkLocationService.GvfsRoot;
        string share = Path.Combine(root, "smb-share:server=192.168.1.89,share=media");
        var inside = NetworkLocationService.FindGvfsMount(Path.Combine(share, "Movies", "2026"));
        Assert.NotNull(inside);
        Assert.Equal(share, inside.MountPoint);
        Assert.Equal("media on 192.168.1.89", inside.Label);
        Assert.Equal(share, NetworkLocationService.FindGvfsMount(share)?.MountPoint);
        Assert.Null(NetworkLocationService.FindGvfsMount(root));
        Assert.Null(NetworkLocationService.FindGvfsMount("/home/someone/Documents"));
    }

    [Fact]
    public void GvfsMountsAreNetworkDrivesWithDisconnect()
    {
        var entry = new MountEntry("/run/user/1/gvfs/smb-share:server=a,share=b", "x", "gvfs", "b on a",
            IsRoot: false, IsRemovable: false, IsNetwork: true) { IsGvfs = true };
        Assert.True(entry.CanUnmount);
        Assert.True(entry.HasAction);
        Assert.Equal("Disconnect", entry.ActionText);
    }

    // ---- Updates -----------------------------------------------------------------------------------

    private const string Releases = """
        [
          {"tag_name":"2.0.1","prerelease":false,"draft":false,"body":"Windows only","html_url":"https://github.com/Aseoriy/Rain-Explorer/releases/tag/2.0.1",
           "assets":[{"name":"RainExplorer-Setup-2.0.1.exe","browser_download_url":"https://github.com/Aseoriy/Rain-Explorer/releases/download/2.0.1/RainExplorer-Setup-2.0.1.exe"}]},
          {"tag_name":"2.0.0","prerelease":false,"draft":false,"body":"Stable","html_url":"https://github.com/Aseoriy/Rain-Explorer/releases/tag/2.0.0",
           "assets":[
             {"name":"RainExplorer-Setup-2.0.0.exe","browser_download_url":"https://github.com/Aseoriy/Rain-Explorer/releases/download/2.0.0/RainExplorer-Setup-2.0.0.exe"},
             {"name":"rain-explorer-2.0.0-linux-x64.AppImage","browser_download_url":"https://github.com/Aseoriy/Rain-Explorer/releases/download/2.0.0/rain-explorer-2.0.0-linux-x64.AppImage"},
             {"name":"rain-explorer_2.0.0_amd64.deb","browser_download_url":"https://github.com/Aseoriy/Rain-Explorer/releases/download/2.0.0/rain-explorer_2.0.0_amd64.deb"},
             {"name":"rain-explorer-2.0.0-linux-x64.sha256","browser_download_url":"https://github.com/Aseoriy/Rain-Explorer/releases/download/2.0.0/rain-explorer-2.0.0-linux-x64.sha256"}]},
          {"tag_name":"2.0.0-beta-3","prerelease":true,"draft":false,"body":"Beta","html_url":"h",
           "assets":[{"name":"rain-explorer-2.0.0-beta-3-linux-x64.AppImage","browser_download_url":"https://github.com/Aseoriy/Rain-Explorer/releases/download/2.0.0-beta-3/rain-explorer-2.0.0-beta-3-linux-x64.AppImage"}]},
          {"tag_name":"0.1.0-linux-alpha","prerelease":true,"draft":false,"body":"","html_url":"h",
           "assets":[{"name":"rain-explorer-0.1.0-linux-alpha-linux-x64.AppImage","browser_download_url":"https://github.com/Aseoriy/Rain-Explorer/releases/download/0.1.0-linux-alpha/rain-explorer-0.1.0-linux-alpha-linux-x64.AppImage"}]}
        ]
        """;

    [Fact]
    public void NewestReleaseWithALinuxPackageIsOffered()
    {
        var info = UpdateService.PickUpdate(Releases, "2.0.0-beta-2", includeBeta: true, InstallKind.AppImage, "x64", "amd64");
        Assert.NotNull(info);
        Assert.Equal("2.0.0", info.Version);   // 2.0.1 has no Linux build
        Assert.Equal("rain-explorer-2.0.0-linux-x64.AppImage", info.AssetName);
        Assert.EndsWith(".sha256", info.ChecksumUrl);
    }

    [Fact]
    public void DebInstallsGetTheDeb()
    {
        var info = UpdateService.PickUpdate(Releases, "2.0.0-beta-2", includeBeta: false, InstallKind.Deb, "x64", "amd64");
        Assert.Equal("rain-explorer_2.0.0_amd64.deb", info?.AssetName);
    }

    [Fact]
    public void ManualInstallsGetOnlyTheReleasePage()
    {
        var info = UpdateService.PickUpdate(Releases, "2.0.0-beta-2", includeBeta: false, InstallKind.Manual, "x64", "amd64");
        Assert.NotNull(info);
        Assert.Null(info.DownloadUrl);
        Assert.Contains("/releases/tag/2.0.0", info.HtmlUrl);
    }

    [Fact]
    public void UpToDateAndWrongArchitectureOfferNothing()
    {
        Assert.Null(UpdateService.PickUpdate(Releases, "2.0.0", true, InstallKind.AppImage, "x64", "amd64"));
        Assert.Null(UpdateService.PickUpdate(Releases, "2.0.0-beta-2", true, InstallKind.AppImage, "arm64", "arm64"));
    }

    [Fact]
    public void BetasNeedTheBetaToggleAndAlphaTagsAreNeverOffered()
    {
        const string onlyBeta = """
            [{"tag_name":"2.0.0-beta-3","prerelease":true,"html_url":"h","assets":[{"name":"rain-explorer-2.0.0-beta-3-linux-x64.AppImage","browser_download_url":"https://github.com/Aseoriy/Rain-Explorer/releases/download/a/b"}]},
             {"tag_name":"9.0.0-linux-alpha","prerelease":true,"html_url":"h","assets":[{"name":"rain-explorer-9.0.0-linux-alpha-linux-x64.AppImage","browser_download_url":"https://github.com/Aseoriy/Rain-Explorer/releases/download/a/c"}]}]
            """;
        Assert.Null(UpdateService.PickUpdate(onlyBeta, "2.0.0-beta-2", false, InstallKind.AppImage, "x64", "amd64"));
        Assert.Equal("2.0.0-beta-3",
            UpdateService.PickUpdate(onlyBeta, "2.0.0-beta-2", true, InstallKind.AppImage, "x64", "amd64")?.Version);
        Assert.Null(UpdateService.PickUpdate(onlyBeta, "2.0.0-beta-3", true, InstallKind.AppImage, "x64", "amd64"));
    }

    [Fact]
    public void ChecksumLinesMatchGithubRenamedDebs()
    {
        string hash = new string('a', 64);
        string sums = $"{hash}  rain-explorer_2.0.0~beta.2_amd64.deb\n{new string('b', 64)}  rain-explorer-2.0.0-beta-2-linux-x64.AppImage\n";
        Assert.Equal(hash, UpdateService.ExpectedHash(sums, "rain-explorer_2.0.0.beta.2_amd64.deb"));
        Assert.Equal(new string('b', 64), UpdateService.ExpectedHash(sums, "rain-explorer-2.0.0-beta-2-linux-x64.AppImage"));
        Assert.Null(UpdateService.ExpectedHash(sums, "other.tar.gz"));
    }

    [Fact]
    public void InstallKindFollowsTheInstallLocation()
    {
        string appImage = Path.Combine(TestEnvironment.NewFolder("appimage"), "Rain.AppImage");
        File.WriteAllText(appImage, "");
        Assert.Equal(InstallKind.AppImage, UpdateService.DetectInstallKind(appImage, "/tmp/.mount_x/usr/lib/rain-explorer/"));
        Assert.Equal(InstallKind.Deb, UpdateService.DetectInstallKind(null, "/opt/rain-explorer/"));
        Assert.Equal(InstallKind.Manual, UpdateService.DetectInstallKind(null, "/home/me/.local/share/rain-explorer/"));
        Assert.Equal(InstallKind.Manual, UpdateService.DetectInstallKind("/does/not/exist.AppImage", "/opt/rain-explorer-old/"));
    }
}
