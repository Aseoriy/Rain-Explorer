using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class ZipWriteCapabilitiesProtocolContractTests
{
    [TestMethod]
    public void MinorSixteenAddsCapabilitiesAfterMinorFifteen()
    {
        var minorFifteen = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.ZipRebuildMetadataMinorVersion)
            .ToArray();
        var minorSixteen = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.ZipWriteCapabilitiesMinorVersion)
            .ToArray();

        CollectionAssert.DoesNotContain(minorFifteen, ProtocolFeatures.ZipWriteCapabilities);
        CollectionAssert.Contains(minorSixteen, ProtocolFeatures.ZipWriteCapabilities);
        CollectionAssert.AreEqual(
            minorFifteen,
            minorSixteen.Where(feature => feature != ProtocolFeatures.ZipWriteCapabilities).ToArray());
    }

    [TestMethod]
    public void CapabilityRequestAndResponseHaveStableTypedJsonShapes()
    {
        var request = new GetZipWriteCapabilitiesRequest(
            new ArchiveSessionId(new Guid("00112233-4455-6677-8899-aabbccddeeff")));
        var requestJson = JsonSerializer.Serialize(request, ProtocolJson.Options);
        Assert.AreEqual(
            "{\"sessionId\":{\"value\":\"00112233-4455-6677-8899-aabbccddeeff\"}}",
            requestJson);
        var requestRoundTrip = JsonSerializer.Deserialize<GetZipWriteCapabilitiesRequest>(
            requestJson,
            ProtocolJson.Options);
        Assert.AreEqual(request, requestRoundTrip);

        var response = new GetZipWriteCapabilitiesResponse(
            CanCreate: true,
            CanRebuild: true,
            SupportsAdd: true,
            SupportsReplace: true,
            SupportsDelete: true,
            SupportsFileRename: true,
            SupportsTreeEdit: true,
            SupportsMetadata: true,
            CompressionPresets: Enum.GetValues<ArchiveCompressionPreset>(),
            MaximumEntries: 4096,
            MaximumSourceBytes: 68_719_476_736,
            SupportsEncryption: false,
            SupportsFilenameEncryption: false,
            SupportsSplitOutput: false,
            SupportsExistingDestinationReplacement: false,
            SupportsInPlaceUpdate: false);
        var responseJson = JsonSerializer.Serialize(response, ProtocolJson.Options);
        Assert.AreEqual(
            "{\"canCreate\":true,\"canRebuild\":true,\"supportsAdd\":true,\"supportsReplace\":true,\"supportsDelete\":true,\"supportsFileRename\":true,\"supportsTreeEdit\":true,\"supportsMetadata\":true,\"compressionPresets\":[0,1,2,3],\"maximumEntries\":4096,\"maximumSourceBytes\":68719476736,\"supportsEncryption\":false,\"supportsFilenameEncryption\":false,\"supportsSplitOutput\":false,\"supportsExistingDestinationReplacement\":false,\"supportsInPlaceUpdate\":false}",
            responseJson);
        var responseRoundTrip = JsonSerializer.Deserialize<GetZipWriteCapabilitiesResponse>(
            responseJson,
            ProtocolJson.Options);
        Assert.IsNotNull(responseRoundTrip);
        Assert.AreEqual(response.CanCreate, responseRoundTrip!.CanCreate);
        Assert.AreEqual(response.CanRebuild, responseRoundTrip.CanRebuild);
        Assert.AreEqual(response.MaximumEntries, responseRoundTrip.MaximumEntries);
        Assert.AreEqual(response.MaximumSourceBytes, responseRoundTrip.MaximumSourceBytes);
        CollectionAssert.AreEqual(
            response.CompressionPresets.ToArray(),
            responseRoundTrip.CompressionPresets.ToArray());
    }

    [TestMethod]
    public void CapabilityPayloadRejectsUnknownMembersAndPreservesNullGlobalSession()
    {
        var requestJson = JsonSerializer.Serialize(
            new GetZipWriteCapabilitiesRequest(),
            ProtocolJson.Options);
        Assert.AreEqual("{}", requestJson);
        var unknown = requestJson.TrimEnd('}') + "\"unexpected\":true}";
        Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<GetZipWriteCapabilitiesRequest>(unknown, ProtocolJson.Options));
    }

    [TestMethod]
    public async Task CapabilityRequestUsesTheBoundedJsonFrameProtocol()
    {
        await using var stream = new MemoryStream();
        await ProtocolStream.WriteJsonAsync(
            stream,
            ProtocolMessageKinds.GetZipWriteCapabilities,
            Guid.NewGuid(),
            new GetZipWriteCapabilitiesRequest(),
            1);
        stream.Position = 0;
        var frame = await ProtocolStream.ReadAsync(stream);
        Assert.AreEqual(ProtocolFrameType.Json, frame.Type);
        Assert.AreEqual(ProtocolMessageKinds.GetZipWriteCapabilities, frame.Json!.Kind);
        Assert.AreEqual("{}", frame.Json.Payload.GetRawText());
    }
}
