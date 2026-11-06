using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class ManagedCreationProtocolContractTests
{
    [TestMethod]
    public void ManagedCreationFormatIsClosedToTheApprovedFourFormats()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                ManagedArchiveCreationFormat.TarGZip,
                ManagedArchiveCreationFormat.TarBZip2,
                ManagedArchiveCreationFormat.GZip,
                ManagedArchiveCreationFormat.BZip2,
            },
            Enum.GetValues<ManagedArchiveCreationFormat>());
    }

    [TestMethod]
    public void MinorEighteenAddsManagedCreationAfterMinorSeventeen()
    {
        var minorSeventeen = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.TarWriteCapabilitiesMinorVersion)
            .ToArray();
        var minorEighteen = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.ManagedWriteCapabilitiesMinorVersion)
            .ToArray();
        var additions = new[]
        {
            ProtocolFeatures.TarGZipCreation,
            ProtocolFeatures.TarGZipDirectoryCreation,
            ProtocolFeatures.TarBZip2Creation,
            ProtocolFeatures.TarBZip2DirectoryCreation,
            ProtocolFeatures.GZipCreation,
            ProtocolFeatures.BZip2Creation,
            ProtocolFeatures.ManagedWriteCapabilities,
        };

        CollectionAssert.AreEqual(
            minorSeventeen,
            minorEighteen.Where(feature => !additions.Contains(feature)).ToArray());
        CollectionAssert.AreEqual(
            additions,
            minorEighteen.Skip(minorSeventeen.Length).ToArray());
        CollectionAssert.DoesNotContain(minorSeventeen, ProtocolFeatures.ManagedWriteCapabilities);
    }

    [TestMethod]
    public void ManagedCreationFeatureDependenciesAreExplicit()
    {
        CollectionAssert.AreEqual(
            new[] { ProtocolFeatures.TarCreation, ProtocolFeatures.RequestCancellation },
            ProtocolFeatures.TarGZipCreationDependencies.ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                ProtocolFeatures.TarGZipCreation,
                ProtocolFeatures.TarDirectoryCreation,
                ProtocolFeatures.RequestCancellation,
            },
            ProtocolFeatures.TarGZipDirectoryCreationDependencies.ToArray());
        CollectionAssert.AreEqual(
            new[] { ProtocolFeatures.TarCreation, ProtocolFeatures.RequestCancellation },
            ProtocolFeatures.TarBZip2CreationDependencies.ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                ProtocolFeatures.TarBZip2Creation,
                ProtocolFeatures.TarDirectoryCreation,
                ProtocolFeatures.RequestCancellation,
            },
            ProtocolFeatures.TarBZip2DirectoryCreationDependencies.ToArray());
        CollectionAssert.AreEqual(
            new[] { ProtocolFeatures.RequestCancellation },
            ProtocolFeatures.GZipCreationDependencies.ToArray());
        CollectionAssert.AreEqual(
            new[] { ProtocolFeatures.RequestCancellation },
            ProtocolFeatures.BZip2CreationDependencies.ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                ProtocolFeatures.TarGZipCreation,
                ProtocolFeatures.TarGZipDirectoryCreation,
                ProtocolFeatures.TarBZip2Creation,
                ProtocolFeatures.TarBZip2DirectoryCreation,
                ProtocolFeatures.GZipCreation,
                ProtocolFeatures.BZip2Creation,
                ProtocolFeatures.TarCreation,
                ProtocolFeatures.TarDirectoryCreation,
                ProtocolFeatures.RequestCancellation,
            },
            ProtocolFeatures.ManagedWriteCapabilitiesDependencies.ToArray());
    }

    [TestMethod]
    public void ManagedCreationRequestsHaveStableTypedJsonShapes()
    {
        var tarGZip = new StartTarGZipCreationRequest(
            @"C:\created.tar.gz",
            [new ArchiveCreationFile(@"C:\source.txt", "source.txt")],
            ArchiveCompressionPreset.Maximum);
        Assert.AreEqual(
            "{\"destinationPath\":\"C:\\\\created.tar.gz\",\"files\":[{\"sourcePath\":\"C:\\\\source.txt\",\"archivePath\":\"source.txt\"}],\"compressionPreset\":3}",
            Serialize(tarGZip));

        var tarGZipTree = new StartTarGZipTreeCreationRequest(
            @"C:\created.tar.gz",
            [],
            [new ArchiveCreationDirectory(@"C:\source", "source")],
            ArchiveCompressionPreset.Fast);
        Assert.AreEqual(
            "{\"destinationPath\":\"C:\\\\created.tar.gz\",\"files\":[],\"directories\":[{\"sourcePath\":\"C:\\\\source\",\"archivePath\":\"source\"}],\"compressionPreset\":1}",
            Serialize(tarGZipTree));

        var tarBZip2 = new StartTarBZip2CreationRequest(
            @"C:\created.tar.bz2",
            [],
            ArchiveCompressionPreset.Balanced);
        Assert.AreEqual(
            "{\"destinationPath\":\"C:\\\\created.tar.bz2\",\"files\":[],\"compressionPreset\":2}",
            Serialize(tarBZip2));

        var tarBZip2Tree = new StartTarBZip2TreeCreationRequest(
            @"C:\created.tar.bz2",
            [],
            [new ArchiveCreationDirectory(@"C:\source", "source")]);
        Assert.AreEqual(
            "{\"destinationPath\":\"C:\\\\created.tar.bz2\",\"files\":[],\"directories\":[{\"sourcePath\":\"C:\\\\source\",\"archivePath\":\"source\"}],\"compressionPreset\":2}",
            Serialize(tarBZip2Tree));

        var gzip = new StartGZipCreationRequest(
            @"C:\created.gz",
            @"C:\source.txt",
            ArchiveCompressionPreset.Fast);
        Assert.AreEqual(
            "{\"destinationPath\":\"C:\\\\created.gz\",\"sourcePath\":\"C:\\\\source.txt\",\"compressionPreset\":1}",
            Serialize(gzip));

        var bzip2 = new StartBZip2CreationRequest(
            @"C:\created.bz2",
            @"C:\source.txt");
        Assert.AreEqual(
            "{\"destinationPath\":\"C:\\\\created.bz2\",\"sourcePath\":\"C:\\\\source.txt\",\"compressionPreset\":2}",
            Serialize(bzip2));

        Assert.AreEqual(tarGZip.DestinationPath, Deserialize<StartTarGZipCreationRequest>(tarGZip).DestinationPath);
        Assert.AreEqual(tarGZip.CompressionPreset, Deserialize<StartTarGZipCreationRequest>(tarGZip).CompressionPreset);
        Assert.AreEqual(gzip.SourcePath, Deserialize<StartGZipCreationRequest>(gzip).SourcePath);
    }

    [TestMethod]
    public void ManagedCapabilitiesExposePerFormatPresetsAndClosedUnsafeRoutes()
    {
        var response = new GetManagedWriteCapabilitiesResponse(
        [
            Capability(
                ManagedArchiveCreationFormat.TarGZip,
                [ArchiveCompressionPreset.Store, ArchiveCompressionPreset.Fast, ArchiveCompressionPreset.Balanced, ArchiveCompressionPreset.Maximum],
                supportsTreeCreation: true,
                maximumEntries: ProtocolConstants.MaximumTarCreationEntries),
            Capability(
                ManagedArchiveCreationFormat.TarBZip2,
                [ArchiveCompressionPreset.Balanced],
                supportsTreeCreation: true,
                maximumEntries: ProtocolConstants.MaximumTarCreationEntries),
            Capability(
                ManagedArchiveCreationFormat.GZip,
                [ArchiveCompressionPreset.Store, ArchiveCompressionPreset.Fast, ArchiveCompressionPreset.Balanced, ArchiveCompressionPreset.Maximum],
                supportsTreeCreation: false,
                maximumEntries: ProtocolConstants.MaximumRawCreationEntries),
            Capability(
                ManagedArchiveCreationFormat.BZip2,
                [ArchiveCompressionPreset.Balanced],
                supportsTreeCreation: false,
                maximumEntries: ProtocolConstants.MaximumRawCreationEntries),
        ]);
        var json = Serialize(response);
        Assert.AreEqual(
            "{\"capabilities\":[{\"format\":0,\"canCreate\":true,\"supportsTreeCreation\":true,\"canRebuild\":false,\"compressionPresets\":[0,1,2,3],\"maximumEntries\":4096,\"maximumSourceBytes\":68719476736,\"supportsEncryption\":false,\"supportsFilenameEncryption\":false,\"supportsSplitOutput\":false,\"supportsExistingDestinationReplacement\":false,\"supportsInPlaceUpdate\":false},{\"format\":1,\"canCreate\":true,\"supportsTreeCreation\":true,\"canRebuild\":false,\"compressionPresets\":[2],\"maximumEntries\":4096,\"maximumSourceBytes\":68719476736,\"supportsEncryption\":false,\"supportsFilenameEncryption\":false,\"supportsSplitOutput\":false,\"supportsExistingDestinationReplacement\":false,\"supportsInPlaceUpdate\":false},{\"format\":2,\"canCreate\":true,\"supportsTreeCreation\":false,\"canRebuild\":false,\"compressionPresets\":[0,1,2,3],\"maximumEntries\":1,\"maximumSourceBytes\":68719476736,\"supportsEncryption\":false,\"supportsFilenameEncryption\":false,\"supportsSplitOutput\":false,\"supportsExistingDestinationReplacement\":false,\"supportsInPlaceUpdate\":false},{\"format\":3,\"canCreate\":true,\"supportsTreeCreation\":false,\"canRebuild\":false,\"compressionPresets\":[2],\"maximumEntries\":1,\"maximumSourceBytes\":68719476736,\"supportsEncryption\":false,\"supportsFilenameEncryption\":false,\"supportsSplitOutput\":false,\"supportsExistingDestinationReplacement\":false,\"supportsInPlaceUpdate\":false}]}",
            json);
        StringAssert.Contains(json, "\"format\":0");
        StringAssert.Contains(json, "\"format\":3");
        var roundTrip = Deserialize<GetManagedWriteCapabilitiesResponse>(response);
        Assert.HasCount(4, roundTrip.Capabilities);
        CollectionAssert.AreEqual(
            new[]
            {
                ArchiveCompressionPreset.Store,
                ArchiveCompressionPreset.Fast,
                ArchiveCompressionPreset.Balanced,
                ArchiveCompressionPreset.Maximum,
            },
            roundTrip.Capabilities[0].CompressionPresets.ToArray());
        Assert.IsTrue(roundTrip.Capabilities[0].SupportsTreeCreation);
        Assert.IsFalse(roundTrip.Capabilities[2].SupportsTreeCreation);
        Assert.AreEqual(ProtocolConstants.MaximumRawCreationEntries, roundTrip.Capabilities[2].MaximumEntries);
        Assert.IsTrue(roundTrip.Capabilities.All(capability => !capability.CanRebuild));
        Assert.IsTrue(roundTrip.Capabilities.All(capability =>
            !capability.SupportsEncryption
            && !capability.SupportsFilenameEncryption
            && !capability.SupportsSplitOutput
            && !capability.SupportsExistingDestinationReplacement
            && !capability.SupportsInPlaceUpdate));
    }

    [TestMethod]
    public void ManagedCreationPayloadsRejectUnknownMembers()
    {
        Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<StartGZipCreationRequest>(
                "{\"destinationPath\":\"C:\\\\created.gz\",\"sourcePath\":\"C:\\\\source.txt\",\"compressionPreset\":1,\"format\":2}",
                ProtocolJson.Options));
        Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<GetManagedWriteCapabilitiesRequest>(
                "{\"unexpected\":true}",
                ProtocolJson.Options));

        Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<GetManagedWriteCapabilitiesResponse>(
                "{\"capabilities\":[{\"format\":2,\"canCreate\":true,\"supportsTreeCreation\":false,\"canRebuild\":false,\"compressionPresets\":[2],\"maximumEntries\":1,\"maximumSourceBytes\":68719476736,\"supportsEncryption\":false,\"supportsFilenameEncryption\":false,\"supportsSplitOutput\":false,\"supportsExistingDestinationReplacement\":false}]}",
                ProtocolJson.Options));
    }

    [TestMethod]
    public void RawRoutesCarryOnlyOneSourceAndNoSyntheticMemberClaim()
    {
        var request = new StartGZipCreationRequest(
            @"C:\created.gz",
            @"C:\source.txt",
            ArchiveCompressionPreset.Balanced);
        var propertyNames = typeof(StartGZipCreationRequest)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        CollectionAssert.AreEqual(
            new[] { "DestinationPath", "SourcePath", "CompressionPreset" },
            propertyNames);
        Assert.AreEqual(request.SourcePath, Deserialize(request).SourcePath);
    }

    [TestMethod]
    public async Task ManagedCreationMessagesUseDistinctBoundedFrames()
    {
        await AssertFrameAsync(
            ProtocolMessageKinds.StartTarGZipCreation,
            new StartTarGZipCreationRequest(@"C:\created.tar.gz", [], ArchiveCompressionPreset.Fast));
        await AssertFrameAsync(
            ProtocolMessageKinds.StartTarGZipTreeCreation,
            new StartTarGZipTreeCreationRequest(
                @"C:\created.tar.gz",
                [],
                [new ArchiveCreationDirectory(@"C:\source", "source")]));
        await AssertFrameAsync(
            ProtocolMessageKinds.StartTarBZip2Creation,
            new StartTarBZip2CreationRequest(@"C:\created.tar.bz2", [], ArchiveCompressionPreset.Balanced));
        await AssertFrameAsync(
            ProtocolMessageKinds.StartTarBZip2TreeCreation,
            new StartTarBZip2TreeCreationRequest(
                @"C:\created.tar.bz2",
                [],
                [new ArchiveCreationDirectory(@"C:\source", "source")]));
        await AssertFrameAsync(
            ProtocolMessageKinds.StartGZipCreation,
            new StartGZipCreationRequest(
                @"C:\created.gz",
                @"C:\source.txt"));
        await AssertFrameAsync(
            ProtocolMessageKinds.StartBZip2Creation,
            new StartBZip2CreationRequest(
                @"C:\created.bz2",
                @"C:\source.txt"));
        await AssertFrameAsync(
            ProtocolMessageKinds.GetManagedWriteCapabilities,
            new GetManagedWriteCapabilitiesRequest());
    }

    [TestMethod]
    public void ManagedCreationRequestsRemainBoundedByTheControlFrameLimit()
    {
        var files = Enumerable.Range(0, ProtocolConstants.MaximumTarCreationFiles)
            .Select(index => new ArchiveCreationFile(
                $@"C:\source\{index:D4}-{new string('s', 220)}.bin",
                $"archive/{index:D4}-{new string('a', 220)}.bin"))
            .ToArray();
        var request = new StartTarGZipCreationRequest(@"C:\created.tar.gz", files);

        Assert.ThrowsExactly<InvalidDataException>(() =>
            ProtocolStream.ValidateJsonFrameSize(ProtocolMessageKinds.StartTarGZipCreation, request));
    }

    private static ManagedWriteCapability Capability(
        ManagedArchiveCreationFormat format,
        IReadOnlyList<ArchiveCompressionPreset> presets,
        bool supportsTreeCreation,
        int maximumEntries) =>
        new(
            format,
            CanCreate: true,
            SupportsTreeCreation: supportsTreeCreation,
            CanRebuild: false,
            CompressionPresets: presets,
            MaximumEntries: maximumEntries,
            MaximumSourceBytes: format is ManagedArchiveCreationFormat.GZip
                or ManagedArchiveCreationFormat.BZip2
                    ? ProtocolConstants.MaximumRawCreationSourceBytes
                    : ProtocolConstants.MaximumTarCreationSourceBytes,
            SupportsEncryption: false,
            SupportsFilenameEncryption: false,
            SupportsSplitOutput: false,
            SupportsExistingDestinationReplacement: false,
            SupportsInPlaceUpdate: false);

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, ProtocolJson.Options);

    private static T Deserialize<T>(T value) =>
        JsonSerializer.Deserialize<T>(Serialize(value), ProtocolJson.Options)
        ?? throw new InvalidDataException("Protocol payload did not round-trip.");

    private static async Task AssertFrameAsync<T>(string kind, T payload)
    {
        await using var stream = new MemoryStream();
        await ProtocolStream.WriteJsonAsync(stream, kind, Guid.NewGuid(), payload, 1);
        stream.Position = 0;
        var frame = await ProtocolStream.ReadAsync(stream);
        Assert.AreEqual(ProtocolFrameType.Json, frame.Type);
        Assert.AreEqual(kind, frame.Json!.Kind);
    }
}
