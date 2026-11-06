using System.Reflection;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class RarCreationProtocolContractTests
{
    [TestMethod]
    public void MinorTwentyTwoRemainsTheGoldenPreRarFeatureSet()
    {
        var minorTwentyOne = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.NestedWorkspaceIndependentReopenMinorVersion)
            .ToArray();
        var minorTwentyTwo = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.SupportDiagnosticsMinorVersion)
            .ToArray();

        CollectionAssert.AreEqual(
            minorTwentyOne.Append(ProtocolFeatures.SupportDiagnostics).ToArray(),
            minorTwentyTwo);
        CollectionAssert.DoesNotContain(minorTwentyTwo, ProtocolFeatures.RarCreation);
        CollectionAssert.DoesNotContain(minorTwentyTwo, ProtocolFeatures.RarDirectoryCreation);
        CollectionAssert.DoesNotContain(minorTwentyTwo, ProtocolFeatures.RarWriteCapabilities);
    }

    [TestMethod]
    public void MinorTwentyThreeAddsOnlyTheRarFeatureSet()
    {
        var minorTwentyTwo = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.SupportDiagnosticsMinorVersion)
            .ToArray();
        var minorTwentyThree = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.RarCreationMinorVersion)
            .ToArray();

        CollectionAssert.AreEqual(
            minorTwentyTwo
                .Concat(
                [
                    ProtocolFeatures.RarCreation,
                    ProtocolFeatures.RarDirectoryCreation,
                    ProtocolFeatures.RarWriteCapabilities,
                ])
                .ToArray(),
            minorTwentyThree);
        var minorTwentyFour = minorTwentyThree.Append(ProtocolFeatures.ExpandedCreationTreeBudget).ToArray();
        CollectionAssert.AreEqual(minorTwentyFour, ProtocolFeatures.SupportedVersion1.ToArray());
        CollectionAssert.AreEqual(
            minorTwentyFour,
            ProtocolFeatures.SupportedForMinorVersion(ProtocolConstants.MinorVersion).ToArray());
    }

    [TestMethod]
    public void RarFeatureDependenciesAreExplicit()
    {
        CollectionAssert.AreEqual(
            new[] { ProtocolFeatures.RequestCancellation },
            ProtocolFeatures.RarCreationDependencies.ToArray());
        CollectionAssert.AreEqual(
            new[] { ProtocolFeatures.RarCreation, ProtocolFeatures.RequestCancellation },
            ProtocolFeatures.RarDirectoryCreationDependencies.ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                ProtocolFeatures.RarCreation,
                ProtocolFeatures.RarDirectoryCreation,
                ProtocolFeatures.RequestCancellation,
            },
            ProtocolFeatures.RarWriteCapabilitiesDependencies.ToArray());
    }

    [TestMethod]
    public void RarRequestsAndCapabilitiesHaveStableTypedJsonShapes()
    {
        var request = new StartRarCreationRequest(
            @"C:\created.rar",
            [new ArchiveCreationFile(@"C:\source.txt", "source.txt")]);
        Assert.AreEqual(
            "{\"destinationPath\":\"C:\\\\created.rar\",\"files\":[{\"sourcePath\":\"C:\\\\source.txt\",\"archivePath\":\"source.txt\"}],\"compressionPreset\":2}",
            JsonSerializer.Serialize(request, ProtocolJson.Options));

        var treeRequest = new StartRarTreeCreationRequest(
            @"C:\created.rar",
            [],
            [new ArchiveCreationDirectory(@"C:\source", "source")],
            ArchiveCompressionPreset.Maximum);
        Assert.AreEqual(
            "{\"destinationPath\":\"C:\\\\created.rar\",\"files\":[],\"directories\":[{\"sourcePath\":\"C:\\\\source\",\"archivePath\":\"source\"}],\"compressionPreset\":3}",
            JsonSerializer.Serialize(treeRequest, ProtocolJson.Options));

        Assert.AreEqual(
            "{}",
            JsonSerializer.Serialize(new GetRarWriteCapabilitiesRequest(), ProtocolJson.Options));
        var response = new GetRarWriteCapabilitiesResponse(
            CanCreate: true,
            CanRebuild: false,
            SupportsTreeCreation: true,
            CompressionPresets: Enum.GetValues<ArchiveCompressionPreset>(),
            MaximumEntries: ProtocolConstants.MaximumRarCreationEntries,
            MaximumSourceBytes: ProtocolConstants.MaximumRarCreationSourceBytes,
            Availability: RarEncoderAvailability.Available,
            SupportsEncryption: false,
            SupportsFilenameEncryption: false,
            SupportsSplitOutput: false,
            SupportsExistingDestinationReplacement: false,
            SupportsInPlaceUpdate: false);
        Assert.AreEqual(
            "{\"canCreate\":true,\"canRebuild\":false,\"supportsTreeCreation\":true,\"compressionPresets\":[0,1,2,3],\"maximumEntries\":4096,\"maximumSourceBytes\":8589934592,\"availability\":0,\"supportsEncryption\":false,\"supportsFilenameEncryption\":false,\"supportsSplitOutput\":false,\"supportsExistingDestinationReplacement\":false,\"supportsInPlaceUpdate\":false}",
            JsonSerializer.Serialize(response, ProtocolJson.Options));
    }

    [TestMethod]
    public async Task RarMessagesUseDistinctBoundedProtocolKinds()
    {
        await using var stream = new MemoryStream();
        var request = new StartRarTreeCreationRequest(
            @"C:\created.rar",
            [],
            [new ArchiveCreationDirectory(@"C:\source", "source")]);
        await ProtocolStream.WriteJsonAsync(
            stream,
            ProtocolMessageKinds.StartRarTreeCreation,
            Guid.NewGuid(),
            request,
            1);
        stream.Position = 0;

        var frame = await ProtocolStream.ReadAsync(stream);
        Assert.AreEqual(ProtocolFrameType.Json, frame.Type);
        Assert.AreEqual(ProtocolMessageKinds.StartRarTreeCreation, frame.Json!.Kind);
        var payload = frame.Json.ReadPayload<StartRarTreeCreationRequest>();
        Assert.AreEqual(request.DestinationPath, payload.DestinationPath);
        CollectionAssert.AreEqual(request.Directories.ToArray(), payload.Directories.ToArray());
    }

    [TestMethod]
    public void RarCreationRequestsRemainBoundedByTheControlFrameLimit()
    {
        var files = Enumerable.Range(0, ProtocolConstants.MaximumRarCreationFiles)
            .Select(index => new ArchiveCreationFile(
                $@"C:\source\{index:D4}-{new string('s', 220)}.bin",
                $"archive/{index:D4}-{new string('a', 220)}.bin"))
            .ToArray();
        var request = new StartRarCreationRequest(@"C:\created.rar", files);

        Assert.ThrowsExactly<InvalidDataException>(() =>
            ProtocolStream.ValidateJsonFrameSize(ProtocolMessageKinds.StartRarCreation, request));
    }

    [TestMethod]
    public void RarOperationKindsAreIncludedInClientDispatch()
    {
        var method = typeof(ArchiveClient).GetMethod(
            "IsOperationStartKind",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method);
        Assert.IsTrue((bool)method!.Invoke(null, [ProtocolMessageKinds.StartRarCreation])!);
        Assert.IsTrue((bool)method.Invoke(null, [ProtocolMessageKinds.StartRarTreeCreation])!);
    }

    [TestMethod]
    public void RarCapabilitiesRejectUnsafeOrMismatchedResponses()
    {
        var method = typeof(ArchiveClient).GetMethod(
            "ValidateRarWriteCapabilitiesResponse",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method);

        var valid = new GetRarWriteCapabilitiesResponse(
            CanCreate: true,
            CanRebuild: false,
            SupportsTreeCreation: true,
            CompressionPresets: Enum.GetValues<ArchiveCompressionPreset>(),
            MaximumEntries: ProtocolConstants.MaximumRarCreationEntries,
            MaximumSourceBytes: ProtocolConstants.MaximumRarCreationSourceBytes,
            Availability: RarEncoderAvailability.Available,
            SupportsEncryption: false,
            SupportsFilenameEncryption: false,
            SupportsSplitOutput: false,
            SupportsExistingDestinationReplacement: false,
            SupportsInPlaceUpdate: false);
        method.Invoke(null, [valid]);

        method.Invoke(null,
        [
            valid with
            {
                CanCreate = false,
                SupportsTreeCreation = false,
                Availability = RarEncoderAvailability.RestartRequired,
            },
        ]);

        var unsafeResponse = valid with { SupportsEncryption = true };
        var exception = Assert.ThrowsExactly<TargetInvocationException>(() =>
            method.Invoke(null, [unsafeResponse]));
        Assert.IsInstanceOfType<InvalidDataException>(exception.InnerException);

        var mismatch = valid with
        {
            CanCreate = false,
            Availability = RarEncoderAvailability.Available,
        };
        exception = Assert.ThrowsExactly<TargetInvocationException>(() =>
            method.Invoke(null, [mismatch]));
        Assert.IsInstanceOfType<InvalidDataException>(exception.InnerException);
    }
}
