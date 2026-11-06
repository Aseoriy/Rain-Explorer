using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class ZipRebuildCopyProtocolContractTests
{
    [TestMethod]
    public void MinorTwelveAddsRebuildCopyAndPreservesTheMinorElevenTable()
    {
        var minorEleven = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.NestedWorkspaceRecoveryMinorVersion)
            .ToArray();
        var minorTwelve = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.ZipRebuildCopyMinorVersion)
            .ToArray();

        CollectionAssert.DoesNotContain(minorEleven, ProtocolFeatures.ZipRebuildCopy);
        CollectionAssert.AreEqual(
            minorEleven,
            minorTwelve
                .Where(feature => !string.Equals(
                    feature,
                    ProtocolFeatures.ZipRebuildCopy,
                    StringComparison.Ordinal))
                .ToArray());
        CollectionAssert.Contains(minorTwelve, ProtocolFeatures.ZipRebuildCopy);

        foreach (var dependency in new[]
        {
            ProtocolFeatures.RequestCancellation,
            ProtocolFeatures.PersistentEntryKeys,
            ProtocolFeatures.VolumeSetIdentity,
            ProtocolFeatures.ZipCreation,
        })
        {
            CollectionAssert.Contains(minorTwelve, dependency);
        }
    }

    [TestMethod]
    public void RebuildCopyRequestHasTheExactStableJsonShape()
    {
        var request = new StartZipRebuildCopyRequest(
            new ArchiveSessionId(new Guid("00112233-4455-6677-8899-aabbccddeeff")),
            @"C:\rebuilt.zip",
            [new ArchiveEntryKey("docs/readme.txt", 1)],
            [new ArchiveRebuildCopyFile(@"C:\replacement.txt", "docs/readme.txt")],
            ArchiveCompressionPreset.Maximum);

        var json = JsonSerializer.Serialize(request, ProtocolJson.Options);

        Assert.AreEqual(
            "{\"sessionId\":{\"value\":\"00112233-4455-6677-8899-aabbccddeeff\"},\"destinationPath\":\"C:\\\\rebuilt.zip\",\"entriesToDelete\":[{\"normalizedPath\":\"docs/readme.txt\",\"duplicateOrdinal\":1}],\"files\":[{\"sourcePath\":\"C:\\\\replacement.txt\",\"archivePath\":\"docs/readme.txt\"}],\"compressionPreset\":3}",
            json);

        var roundTrip = JsonSerializer.Deserialize<StartZipRebuildCopyRequest>(
            json,
            ProtocolJson.Options);
        Assert.IsNotNull(roundTrip);
        Assert.AreEqual(request.SessionId, roundTrip.SessionId);
        Assert.AreEqual(request.DestinationPath, roundTrip.DestinationPath);
        CollectionAssert.AreEqual(
            request.EntriesToDelete.ToArray(),
            roundTrip.EntriesToDelete.ToArray());
        CollectionAssert.AreEqual(request.Files.ToArray(), roundTrip.Files.ToArray());
        Assert.AreEqual(request.CompressionPreset, roundTrip.CompressionPreset);
    }

    [TestMethod]
    public void RebuildCopyPayloadRejectsUnknownMembers()
    {
        const string json = """
            {
              "sessionId":{"value":"00112233-4455-6677-8899-aabbccddeeff"},
              "destinationPath":"C:\\rebuilt.zip",
              "entriesToDelete":[],
              "files":[],
              "compressionPreset":2,
              "unexpected":true
            }
            """;

        Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<StartZipRebuildCopyRequest>(json, ProtocolJson.Options));
    }

    [TestMethod]
    public async Task OversizedRebuildCopyFrameIsRefusedBeforeWritingAnyBytes()
    {
        await using var stream = new MemoryStream();
        var request = new StartZipRebuildCopyRequest(
            ArchiveSessionId.New(),
            @"C:\rebuilt.zip",
            [],
            [new ArchiveRebuildCopyFile(
                new string('s', ProtocolConstants.MaximumControlFrameBytes),
                "source.txt")]);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await ProtocolStream.WriteJsonAsync(
                stream,
                ProtocolMessageKinds.StartZipRebuildCopy,
                Guid.NewGuid(),
                request,
                1));

        Assert.AreEqual(0L, stream.Length);
    }

    [TestMethod]
    public void MinorThirteenAddsRebuildRenameAfterTheCopyFeature()
    {
        var minorTwelve = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.ZipRebuildCopyMinorVersion)
            .ToArray();
        var minorThirteen = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.ZipRebuildRenameMinorVersion)
            .ToArray();

        CollectionAssert.DoesNotContain(minorTwelve, ProtocolFeatures.ZipRebuildRename);
        CollectionAssert.Contains(minorThirteen, ProtocolFeatures.ZipRebuildCopy);
        CollectionAssert.Contains(minorThirteen, ProtocolFeatures.ZipRebuildRename);
        CollectionAssert.AreEqual(
            minorTwelve,
            minorThirteen
                .Where(feature => !string.Equals(
                    feature,
                    ProtocolFeatures.ZipRebuildRename,
                    StringComparison.Ordinal))
                .ToArray());
    }

    [TestMethod]
    public void RebuildRenameRequestHasTheExactStableJsonShape()
    {
        var request = new StartZipRebuildRenameRequest(
            new ArchiveSessionId(new Guid("00112233-4455-6677-8899-aabbccddeeff")),
            @"C:\rebuilt.zip",
            [new ArchiveEntryKey("docs/readme.txt", 1)],
            [new ArchiveRebuildCopyFile(@"C:\replacement.txt", "docs/readme.txt")],
            [new ArchiveRebuildRename(
                new ArchiveEntryKey("docs/guide.txt", 0),
                "manual/guide.txt")],
            ArchiveCompressionPreset.Maximum);

        var json = JsonSerializer.Serialize(request, ProtocolJson.Options);

        Assert.AreEqual(
            "{\"sessionId\":{\"value\":\"00112233-4455-6677-8899-aabbccddeeff\"},\"destinationPath\":\"C:\\\\rebuilt.zip\",\"entriesToDelete\":[{\"normalizedPath\":\"docs/readme.txt\",\"duplicateOrdinal\":1}],\"files\":[{\"sourcePath\":\"C:\\\\replacement.txt\",\"archivePath\":\"docs/readme.txt\"}],\"renames\":[{\"entryKey\":{\"normalizedPath\":\"docs/guide.txt\",\"duplicateOrdinal\":0},\"finalArchivePath\":\"manual/guide.txt\"}],\"compressionPreset\":3}",
            json);

        var roundTrip = JsonSerializer.Deserialize<StartZipRebuildRenameRequest>(
            json,
            ProtocolJson.Options);
        Assert.IsNotNull(roundTrip);
        Assert.AreEqual(request.SessionId, roundTrip.SessionId);
        Assert.AreEqual(request.DestinationPath, roundTrip.DestinationPath);
        CollectionAssert.AreEqual(request.EntriesToDelete.ToArray(), roundTrip.EntriesToDelete.ToArray());
        CollectionAssert.AreEqual(request.Files.ToArray(), roundTrip.Files.ToArray());
        CollectionAssert.AreEqual(request.Renames.ToArray(), roundTrip.Renames.ToArray());
        Assert.AreEqual(request.CompressionPreset, roundTrip.CompressionPreset);
    }

    [TestMethod]
    public void RebuildRenamePayloadRejectsUnknownMembers()
    {
        const string json = """
            {
              "sessionId":{"value":"00112233-4455-6677-8899-aabbccddeeff"},
              "destinationPath":"C:\\rebuilt.zip",
              "entriesToDelete":[],
              "files":[],
              "renames":[],
              "compressionPreset":2,
              "unexpected":true
            }
            """;

        Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<StartZipRebuildRenameRequest>(json, ProtocolJson.Options));
    }

    [TestMethod]
    public async Task OversizedRebuildRenameFrameIsRefusedBeforeWritingAnyBytes()
    {
        await using var stream = new MemoryStream();
        var request = new StartZipRebuildRenameRequest(
            ArchiveSessionId.New(),
            @"C:\rebuilt.zip",
            [],
            [],
            [new ArchiveRebuildRename(
                new ArchiveEntryKey("source.txt", 0),
                new string('r', ProtocolConstants.MaximumControlFrameBytes))]);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await ProtocolStream.WriteJsonAsync(
                stream,
                ProtocolMessageKinds.StartZipRebuildRename,
                Guid.NewGuid(),
                request,
                1));

        Assert.AreEqual(0L, stream.Length);
    }
}
