using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class ZipRebuildMetadataProtocolContractTests
{
    [TestMethod]
    public void MinorFifteenAddsMetadataAfterTheCompleteMinorFourteenSurface()
    {
        var minorFourteen = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.ZipRebuildTreeEditMinorVersion)
            .ToArray();
        var minorFifteen = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.ZipRebuildMetadataMinorVersion)
            .ToArray();

        CollectionAssert.DoesNotContain(minorFourteen, ProtocolFeatures.ZipRebuildMetadata);
        CollectionAssert.Contains(minorFifteen, ProtocolFeatures.ZipRebuildMetadata);
        CollectionAssert.AreEqual(
            minorFourteen,
            minorFifteen.Where(feature => feature != ProtocolFeatures.ZipRebuildMetadata).ToArray());
    }

    [TestMethod]
    public void MetadataRequestRoundTripsTypedTargetsAndRejectsUnknownMembers()
    {
        var request = new StartZipRebuildMetadataRequest(
            new ArchiveSessionId(new Guid("00112233-4455-6677-8899-aabbccddeeff")),
            @"C:\rebuilt.zip",
            [],
            [],
            [],
            [],
            [],
            [
                new ArchiveRebuildMetadataDirective(
                    new ArchiveRebuildMetadataTarget(
                        ArchiveRebuildMetadataTargetKind.RetainedEntryKey,
                        new ArchiveEntryKey("docs/readme.txt", 1)),
                    new ArchiveDosLocalTimestamp(2026, 8, 24, 12, 34, 56),
                    0x0001),
                new ArchiveRebuildMetadataDirective(
                    new ArchiveRebuildMetadataTarget(
                        ArchiveRebuildMetadataTargetKind.MappedFinalPath,
                        MappedFinalArchivePath: "added/folder",
                        MappedKind: ArchiveEntryKind.Directory),
                    AttributesLowWord: 0x0020),
            ],
            ArchiveCompressionPreset.Fast);

        var json = JsonSerializer.Serialize(request, ProtocolJson.Options);
        Assert.AreEqual(
            "{\"sessionId\":{\"value\":\"00112233-4455-6677-8899-aabbccddeeff\"},\"destinationPath\":\"C:\\\\rebuilt.zip\",\"entriesToDelete\":[],\"files\":[],\"renames\":[],\"directories\":[],\"treeActions\":[],\"metadata\":[{\"target\":{\"kind\":0,\"retainedEntryKey\":{\"normalizedPath\":\"docs/readme.txt\",\"duplicateOrdinal\":1}},\"lastWriteTime\":{\"year\":2026,\"month\":8,\"day\":24,\"hour\":12,\"minute\":34,\"second\":56},\"attributesLowWord\":1},{\"target\":{\"kind\":1,\"mappedFinalArchivePath\":\"added/folder\",\"mappedKind\":2},\"attributesLowWord\":32}],\"compressionPreset\":1}",
            json);
        var roundTrip = JsonSerializer.Deserialize<StartZipRebuildMetadataRequest>(json, ProtocolJson.Options);
        Assert.IsNotNull(roundTrip);
        CollectionAssert.AreEqual(request.Metadata.ToArray(), roundTrip!.Metadata.ToArray());

        var unknown = json.TrimEnd('}') + ",\"unexpected\":true}";
        Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<StartZipRebuildMetadataRequest>(unknown, ProtocolJson.Options));
    }

    [TestMethod]
    public async Task OversizedMetadataFrameIsRefusedBeforeWritingAnyBytes()
    {
        await using var stream = new MemoryStream();
        var request = new StartZipRebuildMetadataRequest(
            ArchiveSessionId.New(),
            @"C:\rebuilt.zip",
            [], [], [], [], [],
            [new ArchiveRebuildMetadataDirective(
                new ArchiveRebuildMetadataTarget(
                    ArchiveRebuildMetadataTargetKind.MappedFinalPath,
                    MappedFinalArchivePath: new string('x', ProtocolConstants.MaximumControlFrameBytes),
                    MappedKind: ArchiveEntryKind.File),
                AttributesLowWord: 0x0001)]);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await ProtocolStream.WriteJsonAsync(
                stream,
                ProtocolMessageKinds.StartZipRebuildMetadata,
                Guid.NewGuid(),
                request,
                1));

        Assert.AreEqual(0L, stream.Length);
    }

    [TestMethod]
    public void MetadataErrorUsesTheExistingStableErrorShape()
    {
        var response = new ErrorResponse(new ArchiveError(
            "zip.metadata_timestamp_invalid",
            ArchiveErrorCategory.InvalidRequest,
            "The ZIP DOS timestamp is not a valid calendar date."));

        var json = JsonSerializer.Serialize(response, ProtocolJson.Options);
        Assert.AreEqual(
            "{\"error\":{\"code\":\"zip.metadata_timestamp_invalid\",\"category\":1,\"message\":\"The ZIP DOS timestamp is not a valid calendar date.\",\"isRetryable\":false}}",
            json);
        var roundTrip = JsonSerializer.Deserialize<ErrorResponse>(json, ProtocolJson.Options);
        Assert.AreEqual(response, roundTrip);
    }
}
