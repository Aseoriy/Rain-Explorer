using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class ZipRebuildTreeEditProtocolContractTests
{
    [TestMethod]
    public void MinorFourteenAddsTreeEditAfterMinorThirteenAndDirectoryCreation()
    {
        var minorThirteen = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.ZipRebuildRenameMinorVersion)
            .ToArray();
        var minorFourteen = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.ZipRebuildTreeEditMinorVersion)
            .ToArray();

        CollectionAssert.DoesNotContain(minorThirteen, ProtocolFeatures.ZipRebuildTreeEdit);
        CollectionAssert.Contains(minorFourteen, ProtocolFeatures.ZipRebuildTreeEdit);
        CollectionAssert.Contains(minorFourteen, ProtocolFeatures.ZipRebuildRename);
        CollectionAssert.Contains(minorFourteen, ProtocolFeatures.ZipDirectoryCreation);
        CollectionAssert.AreEqual(
            minorThirteen,
            minorFourteen
                .Where(feature => feature != ProtocolFeatures.ZipRebuildTreeEdit
                    && feature != ProtocolFeatures.ZipRebuildSourceFolderReview)
                .ToArray());
        CollectionAssert.Contains(minorFourteen, ProtocolFeatures.ZipRebuildSourceFolderReview);
        CollectionAssert.DoesNotContain(minorThirteen, ProtocolFeatures.ZipRebuildSourceFolderReview);
    }

    [TestMethod]
    public void SourceFolderReviewRequestHasStableJsonShapeAndRejectsUnknownMembers()
    {
        var request = new GetZipRebuildSourceFolderReviewRequest(
            new ArchiveSessionId(new Guid("00112233-4455-6677-8899-aabbccddeeff")),
            [new ArchiveCreationDirectory(@"C:\folder", "Imported/")]);

        var json = JsonSerializer.Serialize(request, ProtocolJson.Options);
        Assert.AreEqual(
            "{\"sessionId\":{\"value\":\"00112233-4455-6677-8899-aabbccddeeff\"},\"directories\":[{\"sourcePath\":\"C:\\\\folder\",\"archivePath\":\"Imported/\"}]}",
            json);
        var roundTrip = JsonSerializer.Deserialize<GetZipRebuildSourceFolderReviewRequest>(json, ProtocolJson.Options);
        Assert.IsNotNull(roundTrip);
        CollectionAssert.AreEqual(request.Directories.ToArray(), roundTrip!.Directories.ToArray());

        const string unknown = "{\"sessionId\":{\"value\":\"00112233-4455-6677-8899-aabbccddeeff\"},\"directories\":[],\"unexpected\":true}";
        Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<GetZipRebuildSourceFolderReviewRequest>(unknown, ProtocolJson.Options));
    }

    [TestMethod]
    public async Task OversizedSourceFolderReviewFrameIsRefusedBeforeWritingAnyBytes()
    {
        await using var stream = new MemoryStream();
        var request = new GetZipRebuildSourceFolderReviewRequest(
            ArchiveSessionId.New(),
            [new ArchiveCreationDirectory(
                @"C:\folder",
                new string('x', ProtocolConstants.MaximumControlFrameBytes))]);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await ProtocolStream.WriteJsonAsync(
                stream,
                ProtocolMessageKinds.GetZipRebuildSourceFolderReview,
                Guid.NewGuid(),
                request,
                1));

        Assert.AreEqual(0L, stream.Length);
    }

    [TestMethod]
    public void TreeEditRequestHasStableJsonShape()
    {
        var request = new StartZipRebuildTreeEditRequest(
            new ArchiveSessionId(new Guid("00112233-4455-6677-8899-aabbccddeeff")),
            @"C:\rebuilt.zip",
            [new ArchiveEntryKey("docs/readme.txt", 1)],
            [new ArchiveRebuildCopyFile(@"C:\replacement.txt", "docs/readme.txt")],
            [new ArchiveRebuildRename(new ArchiveEntryKey("docs/guide.txt", 0), "manual/guide.txt")],
            [new ArchiveCreationDirectory(@"C:\folder", "added")],
            [new ArchiveRebuildTreeAction(
                "docs",
                new string('a', 64),
                ArchiveRebuildTreeActionKind.Move,
                "manual")],
            ArchiveCompressionPreset.Maximum);

        var json = JsonSerializer.Serialize(request, ProtocolJson.Options);
        Assert.AreEqual(
            "{\"sessionId\":{\"value\":\"00112233-4455-6677-8899-aabbccddeeff\"},\"destinationPath\":\"C:\\\\rebuilt.zip\",\"entriesToDelete\":[{\"normalizedPath\":\"docs/readme.txt\",\"duplicateOrdinal\":1}],\"files\":[{\"sourcePath\":\"C:\\\\replacement.txt\",\"archivePath\":\"docs/readme.txt\"}],\"renames\":[{\"entryKey\":{\"normalizedPath\":\"docs/guide.txt\",\"duplicateOrdinal\":0},\"finalArchivePath\":\"manual/guide.txt\"}],\"directories\":[{\"sourcePath\":\"C:\\\\folder\",\"archivePath\":\"added\"}],\"treeActions\":[{\"normalizedPath\":\"docs\",\"expectedRevision\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"kind\":0,\"finalArchivePath\":\"manual\"}],\"compressionPreset\":3}",
            json);

        var roundTrip = JsonSerializer.Deserialize<StartZipRebuildTreeEditRequest>(json, ProtocolJson.Options);
        Assert.IsNotNull(roundTrip);
        CollectionAssert.AreEqual(request.EntriesToDelete.ToArray(), roundTrip.EntriesToDelete.ToArray());
        CollectionAssert.AreEqual(request.Files.ToArray(), roundTrip.Files.ToArray());
        CollectionAssert.AreEqual(request.Renames.ToArray(), roundTrip.Renames.ToArray());
        CollectionAssert.AreEqual(request.Directories.ToArray(), roundTrip.Directories.ToArray());
        CollectionAssert.AreEqual(request.TreeActions.ToArray(), roundTrip.TreeActions.ToArray());
    }

    [TestMethod]
    public void TreeEditPayloadRejectsUnknownMembers()
    {
        const string json = """
            {
              "sessionId":{"value":"00112233-4455-6677-8899-aabbccddeeff"},
              "destinationPath":"C:\\rebuilt.zip",
              "entriesToDelete":[],
              "files":[],
              "renames":[],
              "directories":[],
              "treeActions":[],
              "compressionPreset":2,
              "unexpected":true
            }
            """;

        Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<StartZipRebuildTreeEditRequest>(json, ProtocolJson.Options));
    }

    [TestMethod]
    public async Task OversizedTreeEditFrameIsRefusedBeforeWritingAnyBytes()
    {
        await using var stream = new MemoryStream();
        var request = new StartZipRebuildTreeEditRequest(
            ArchiveSessionId.New(),
            @"C:\rebuilt.zip",
            [],
            [],
            [],
            [],
            [new ArchiveRebuildTreeAction(
                new string('t', ProtocolConstants.MaximumControlFrameBytes),
                new string('a', 64),
                ArchiveRebuildTreeActionKind.Delete)]);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await ProtocolStream.WriteJsonAsync(
                stream,
                ProtocolMessageKinds.StartZipRebuildTreeEdit,
                Guid.NewGuid(),
                request,
                1));

        Assert.AreEqual(0L, stream.Length);
    }
}
