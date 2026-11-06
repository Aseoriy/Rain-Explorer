using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Security;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class DownloadProvenanceServiceTests
{
    [TestMethod]
    public async Task ReadsValidatedZoneAndBuildsCanonicalPayload()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows alternate data streams are required for this test.");
        }

        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.zip");
        await File.WriteAllTextAsync(source, "archive");
        await File.WriteAllTextAsync(
            source + ":Zone.Identifier",
            "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://untrusted.invalid/value\r\n");
        var provenance = await DownloadProvenanceService.TryReadAsync(source, CancellationToken.None);
        Assert.IsNotNull(provenance);
        Assert.AreEqual(3, provenance.ZoneId);

        CollectionAssert.AreEqual(
            Encoding.ASCII.GetBytes("[ZoneTransfer]\r\nZoneId=3\r\n"),
            DownloadProvenanceService.CreateCanonicalPayload(provenance));
    }

    [TestMethod]
    public async Task IgnoresOutOfRangeZone()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows alternate data streams are required for this test.");
        }

        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.zip");
        await File.WriteAllTextAsync(source, "archive");
        await File.WriteAllTextAsync(source + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=99\r\n");

        Assert.IsNull(await DownloadProvenanceService.TryReadAsync(source, CancellationToken.None));
    }

    [TestMethod]
    public async Task IgnoresZoneOutsideZoneTransferSection()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows alternate data streams are required for this test.");
        }

        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.zip");
        await File.WriteAllTextAsync(source, "archive");
        await File.WriteAllTextAsync(source + ":Zone.Identifier", "ZoneId=3\r\n");

        Assert.IsNull(await DownloadProvenanceService.TryReadAsync(source, CancellationToken.None));
    }

    [TestMethod]
    public async Task IgnoresDuplicateOrConflictingZoneValues()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows alternate data streams are required for this test.");
        }

        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.zip");
        await File.WriteAllTextAsync(source, "archive");

        await File.WriteAllTextAsync(
            source + ":Zone.Identifier",
            "[ZoneTransfer]\r\nZoneId=3\r\nZoneId=3\r\n");
        Assert.IsNull(await DownloadProvenanceService.TryReadAsync(source, CancellationToken.None));

        await File.WriteAllTextAsync(
            source + ":Zone.Identifier",
            "[ZoneTransfer]\r\nZoneId=3\r\nZoneId=4\r\n");
        Assert.IsNull(await DownloadProvenanceService.TryReadAsync(source, CancellationToken.None));
    }

    [TestMethod]
    public async Task ReadsUtf8BomAndIgnoresZoneValuesInOtherSections()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows alternate data streams are required for this test.");
        }

        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.zip");
        await File.WriteAllTextAsync(source, "archive");
        await File.WriteAllTextAsync(
            source + ":Zone.Identifier",
            "[Other]\r\nZoneId=4\r\n[ZoneTransfer]\r\nZoneId=3\r\n",
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var provenance = await DownloadProvenanceService.TryReadAsync(source, CancellationToken.None);
        Assert.IsNotNull(provenance);
        Assert.AreEqual(3, provenance.ZoneId);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainArchives.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
