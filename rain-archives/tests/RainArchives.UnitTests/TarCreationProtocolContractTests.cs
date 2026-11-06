using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class TarCreationProtocolContractTests
{
    [TestMethod]
    public void MinorSeventeenAddsOnlyTarCreationFeaturesAfterMinorSixteen()
    {
        var minorSixteen = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.ZipWriteCapabilitiesMinorVersion)
            .ToArray();
        var minorSeventeen = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.TarWriteCapabilitiesMinorVersion)
            .ToArray();

        CollectionAssert.DoesNotContain(minorSixteen, ProtocolFeatures.TarCreation);
        CollectionAssert.DoesNotContain(minorSixteen, ProtocolFeatures.TarDirectoryCreation);
        CollectionAssert.DoesNotContain(minorSixteen, ProtocolFeatures.TarWriteCapabilities);
        CollectionAssert.AreEqual(
            minorSixteen,
            minorSeventeen
                .Where(feature => feature is not ProtocolFeatures.TarCreation
                    and not ProtocolFeatures.TarDirectoryCreation
                    and not ProtocolFeatures.TarWriteCapabilities)
                .ToArray());
        CollectionAssert.Contains(minorSeventeen, ProtocolFeatures.TarCreation);
        CollectionAssert.Contains(minorSeventeen, ProtocolFeatures.TarDirectoryCreation);
        CollectionAssert.Contains(minorSeventeen, ProtocolFeatures.TarWriteCapabilities);
    }

    [TestMethod]
    public void TarFeatureDependenciesAreExplicitAndCreationOnly()
    {
        CollectionAssert.AreEqual(
            new[] { ProtocolFeatures.RequestCancellation },
            ProtocolFeatures.TarCreationDependencies.ToArray());
        CollectionAssert.AreEqual(
            new[] { ProtocolFeatures.TarCreation, ProtocolFeatures.RequestCancellation },
            ProtocolFeatures.TarDirectoryCreationDependencies.ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                ProtocolFeatures.TarCreation,
                ProtocolFeatures.TarDirectoryCreation,
                ProtocolFeatures.RequestCancellation,
            },
            ProtocolFeatures.TarWriteCapabilitiesDependencies.ToArray());
    }

    [TestMethod]
    public void TarCreationRequestsHaveStableTypedJsonShapes()
    {
        var explicitRequest = new StartTarCreationRequest(
            @"C:\created.tar",
            [new ArchiveCreationFile(@"C:\source.txt", "source.txt")]);
        Assert.AreEqual(
            "{\"destinationPath\":\"C:\\\\created.tar\",\"files\":[{\"sourcePath\":\"C:\\\\source.txt\",\"archivePath\":\"source.txt\"}],\"compressionPreset\":0}",
            JsonSerializer.Serialize(explicitRequest, ProtocolJson.Options));
        var explicitRoundTrip = JsonSerializer.Deserialize<StartTarCreationRequest>(
            JsonSerializer.Serialize(explicitRequest, ProtocolJson.Options),
            ProtocolJson.Options);
        Assert.IsNotNull(explicitRoundTrip);
        Assert.AreEqual(explicitRequest.DestinationPath, explicitRoundTrip!.DestinationPath);
        Assert.AreEqual(explicitRequest.CompressionPreset, explicitRoundTrip.CompressionPreset);
        CollectionAssert.AreEqual(explicitRequest.Files.ToArray(), explicitRoundTrip.Files.ToArray());

        var treeRequest = new StartTarTreeCreationRequest(
            @"C:\created.tar",
            [],
            [new ArchiveCreationDirectory(@"C:\source", "source")]);
        Assert.AreEqual(
            "{\"destinationPath\":\"C:\\\\created.tar\",\"files\":[],\"directories\":[{\"sourcePath\":\"C:\\\\source\",\"archivePath\":\"source\"}],\"compressionPreset\":0}",
            JsonSerializer.Serialize(treeRequest, ProtocolJson.Options));
        var treeRoundTrip = JsonSerializer.Deserialize<StartTarTreeCreationRequest>(
            JsonSerializer.Serialize(treeRequest, ProtocolJson.Options),
            ProtocolJson.Options);
        Assert.IsNotNull(treeRoundTrip);
        Assert.AreEqual(treeRequest.DestinationPath, treeRoundTrip!.DestinationPath);
        Assert.AreEqual(treeRequest.CompressionPreset, treeRoundTrip.CompressionPreset);
        CollectionAssert.AreEqual(treeRequest.Files.ToArray(), treeRoundTrip.Files.ToArray());
        CollectionAssert.AreEqual(treeRequest.Directories.ToArray(), treeRoundTrip.Directories.ToArray());
    }

    [TestMethod]
    public void TarCapabilitiesHaveStableCreationOnlyShapeAndClosedUnsafeFlags()
    {
        var request = new GetTarWriteCapabilitiesRequest();
        Assert.AreEqual("{}", JsonSerializer.Serialize(request, ProtocolJson.Options));

        var response = new GetTarWriteCapabilitiesResponse(
            CanCreate: true,
            CanRebuild: false,
            SupportsTreeCreation: true,
            CompressionPresets: [ArchiveCompressionPreset.Store],
            MaximumEntries: ProtocolConstants.MaximumTarCreationEntries,
            MaximumSourceBytes: ProtocolConstants.MaximumTarCreationSourceBytes,
            SupportsEncryption: false,
            SupportsFilenameEncryption: false,
            SupportsSplitOutput: false,
            SupportsExistingDestinationReplacement: false,
            SupportsInPlaceUpdate: false);
        var json = JsonSerializer.Serialize(response, ProtocolJson.Options);
        Assert.AreEqual(
            "{\"canCreate\":true,\"canRebuild\":false,\"supportsTreeCreation\":true,\"compressionPresets\":[0],\"maximumEntries\":4096,\"maximumSourceBytes\":68719476736,\"supportsEncryption\":false,\"supportsFilenameEncryption\":false,\"supportsSplitOutput\":false,\"supportsExistingDestinationReplacement\":false,\"supportsInPlaceUpdate\":false}",
            json);

        var roundTrip = JsonSerializer.Deserialize<GetTarWriteCapabilitiesResponse>(
            json,
            ProtocolJson.Options);
        Assert.IsNotNull(roundTrip);
        Assert.AreEqual(response.CanCreate, roundTrip!.CanCreate);
        Assert.AreEqual(response.CanRebuild, roundTrip.CanRebuild);
        Assert.AreEqual(response.SupportsTreeCreation, roundTrip.SupportsTreeCreation);
        Assert.AreEqual(response.MaximumEntries, roundTrip.MaximumEntries);
        Assert.AreEqual(response.MaximumSourceBytes, roundTrip.MaximumSourceBytes);
        CollectionAssert.AreEqual(
            new[] { ArchiveCompressionPreset.Store },
            roundTrip.CompressionPresets.ToArray());
        Assert.IsFalse(roundTrip.CanRebuild);
        Assert.IsFalse(roundTrip.SupportsEncryption);
        Assert.IsFalse(roundTrip.SupportsFilenameEncryption);
        Assert.IsFalse(roundTrip.SupportsSplitOutput);
        Assert.IsFalse(roundTrip.SupportsExistingDestinationReplacement);
        Assert.IsFalse(roundTrip.SupportsInPlaceUpdate);
    }

    [TestMethod]
    public void TarPayloadsRejectUnknownMembers()
    {
        Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<StartTarCreationRequest>(
                "{\"destinationPath\":\"C:\\\\created.tar\",\"files\":[],\"unexpected\":true}",
                ProtocolJson.Options));
        Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<GetTarWriteCapabilitiesRequest>(
                "{\"unexpected\":true}",
                ProtocolJson.Options));
    }

    [TestMethod]
    public async Task TarMessagesUseDistinctBoundedProtocolKinds()
    {
        await using var stream = new MemoryStream();
        var request = new StartTarCreationRequest(
            @"C:\created.tar",
            [new ArchiveCreationFile(@"C:\source.txt", "source.txt")]);
        await ProtocolStream.WriteJsonAsync(
            stream,
            ProtocolMessageKinds.StartTarCreation,
            Guid.NewGuid(),
            request,
            1);
        stream.Position = 0;

        var frame = await ProtocolStream.ReadAsync(stream);
        Assert.AreEqual(ProtocolFrameType.Json, frame.Type);
        Assert.AreEqual(ProtocolMessageKinds.StartTarCreation, frame.Json!.Kind);
        var payload = frame.Json.ReadPayload<StartTarCreationRequest>();
        Assert.AreEqual(request.DestinationPath, payload.DestinationPath);
        Assert.AreEqual(request.CompressionPreset, payload.CompressionPreset);
        CollectionAssert.AreEqual(request.Files.ToArray(), payload.Files.ToArray());
    }

    [TestMethod]
    public void TarRequestsRemainBoundedByTheControlFrameLimit()
    {
        var files = Enumerable.Range(0, ProtocolConstants.MaximumTarCreationFiles)
            .Select(index => new ArchiveCreationFile(
                $@"C:\source\{index:D4}-{new string('s', 220)}.bin",
                $"archive/{index:D4}-{new string('a', 220)}.bin"))
            .ToArray();
        var request = new StartTarCreationRequest(@"C:\created.tar", files);

        Assert.ThrowsExactly<InvalidDataException>(() =>
            ProtocolStream.ValidateJsonFrameSize(ProtocolMessageKinds.StartTarCreation, request));
    }
}
