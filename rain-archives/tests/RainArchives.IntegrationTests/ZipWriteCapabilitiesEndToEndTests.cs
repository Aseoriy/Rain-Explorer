using System.IO.Compression;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class ZipWriteCapabilitiesEndToEndTests
{
    [TestMethod]
    [Timeout(120_000)]
    public async Task RealWorkerReportsGlobalEligibleAndIneligibleZipWriteCapabilities()
    {
        using var root = new TemporaryDirectory();
        var eligiblePath = Path.Combine(root.Path, "eligible.zip");
        var ineligiblePath = Path.Combine(root.Path, "duplicates.zip");
        CreateZip(eligiblePath, ("entry.txt", "eligible"u8.ToArray()));
        CreateZip(
            ineligiblePath,
            ("same.txt", "first"u8.ToArray()),
            ("same.txt", "second"u8.ToArray()));

        await using var client = await ArchiveClient.StartAsync(GetWorkerPath());
        Assert.IsTrue(client.SupportsFeature(ProtocolFeatures.ZipWriteCapabilities));

        var global = await client.GetZipWriteCapabilitiesAsync();
        AssertCapabilities(global, canRebuild: false);

        var eligible = await client.OpenArchiveAsync(eligiblePath);
        var eligibleCapabilities = await client.GetZipWriteCapabilitiesAsync(eligible.Archive.SessionId);
        AssertCapabilities(eligibleCapabilities, canRebuild: true);
        Assert.IsTrue((await client.CloseArchiveAsync(eligible.Archive.SessionId)).Closed);

        var ineligible = await client.OpenArchiveAsync(ineligiblePath);
        Assert.IsFalse(ineligible.Archive.Capabilities.HasFlag(ArchiveCapability.UpdateByRewrite));
        var ineligibleCapabilities = await client.GetZipWriteCapabilitiesAsync(ineligible.Archive.SessionId);
        AssertCapabilities(ineligibleCapabilities, canRebuild: false);
        Assert.IsTrue((await client.CloseArchiveAsync(ineligible.Archive.SessionId)).Closed);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task MissingCapabilitySessionIsAStableStructuredRefusal()
    {
        await using var client = await ArchiveClient.StartAsync(GetWorkerPath());
        var missing = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await client.GetZipWriteCapabilitiesAsync(ArchiveSessionId.New()));
        Assert.AreEqual(ArchiveErrorCategory.InvalidRequest, missing.Error.Category);
        Assert.AreEqual("session.not_found", missing.Error.Code);
    }

    private static void AssertCapabilities(
        GetZipWriteCapabilitiesResponse response,
        bool canRebuild)
    {
        Assert.IsTrue(response.CanCreate);
        Assert.AreEqual(canRebuild, response.CanRebuild);
        Assert.IsTrue(response.SupportsAdd);
        Assert.IsTrue(response.SupportsReplace);
        Assert.IsTrue(response.SupportsDelete);
        Assert.IsTrue(response.SupportsFileRename);
        Assert.IsTrue(response.SupportsTreeEdit);
        Assert.IsTrue(response.SupportsMetadata);
        CollectionAssert.AreEqual(
            Enum.GetValues<ArchiveCompressionPreset>(),
            response.CompressionPresets.ToArray());
        Assert.AreEqual(ProtocolConstants.MaximumZipCreationEntries, response.MaximumEntries);
        Assert.AreEqual(ProtocolConstants.MaximumZipCreationSourceBytes, response.MaximumSourceBytes);
        Assert.IsFalse(response.SupportsEncryption);
        Assert.IsFalse(response.SupportsFilenameEncryption);
        Assert.IsFalse(response.SupportsSplitOutput);
        Assert.IsFalse(response.SupportsExistingDestinationReplacement);
        Assert.IsFalse(response.SupportsInPlaceUpdate);
    }

    private static void CreateZip(string path, params (string Path, byte[] Content)[] entries)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var item in entries)
        {
            var entry = archive.CreateEntry(item.Path, CompressionLevel.NoCompression);
            using var output = entry.Open();
            output.Write(item.Content);
        }
    }

    private static string GetWorkerPath()
    {
        var worker = Path.Combine(AppContext.BaseDirectory, "RainArchives.Worker.exe");
        if (!File.Exists(worker)) throw new FileNotFoundException("RainArchives.Worker.exe was not found.", worker);
        return worker;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainArchives.ZipWriteCapabilitiesEndToEndTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
