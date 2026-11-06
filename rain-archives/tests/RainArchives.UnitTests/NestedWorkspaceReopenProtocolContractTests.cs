using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;
using RainArchives.Core;
using RainArchives.Security;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class NestedWorkspaceReopenProtocolContractTests
{
    private static readonly string RecoveryId = new('a', 64);
    private static readonly string SourceId = new('b', 64);

    [TestMethod]
    public void MinorTwentyOneAddsOnlyIndependentReopenAfterMinorTwenty()
    {
        var minorTwenty = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.ExtractionRecoveryInspectionMinorVersion)
            .ToArray();
        var minorTwentyOne = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.NestedWorkspaceIndependentReopenMinorVersion)
            .ToArray();

        CollectionAssert.DoesNotContain(
            minorTwenty,
            ProtocolFeatures.NestedWorkspaceIndependentReopen);
        CollectionAssert.AreEqual(
            minorTwenty,
            minorTwentyOne
                .Where(feature => feature != ProtocolFeatures.NestedWorkspaceIndependentReopen)
                .ToArray());
        Assert.AreEqual(ProtocolFeatures.NestedWorkspaceIndependentReopen, minorTwentyOne[^1]);
        CollectionAssert.AreEqual(
            new[]
            {
                ProtocolFeatures.NestedWorkspaceRecovery,
                ProtocolFeatures.NestedArchiveOpen,
                ProtocolFeatures.SecretSlots,
                ProtocolFeatures.RequestCancellation,
                ProtocolFeatures.SourceFingerprintValidation,
            },
            ProtocolFeatures.NestedWorkspaceIndependentReopenDependencies.ToArray());
    }

    [TestMethod]
    public void ReopenWireShapeIsPathFreeTypedAndStrict()
    {
        var hash = new string('c', 64);
        var request = new ReopenNestedWorkspaceRecoveryRequest(
            RecoveryId,
            new string('d', 64),
            1,
            3,
            SourceId,
            3,
            hash,
            NestedWorkspaceRecoveryReopenMode.IndependentRoot,
            Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var json = JsonSerializer.Serialize(request, ProtocolJson.Options);

        StringAssert.Contains(json, $"\"recoveryId\":\"{RecoveryId}\"");
        StringAssert.Contains(json, $"\"sourceId\":\"{SourceId}\"");
        StringAssert.Contains(json, $"\"expectedContentSha256\":\"{hash}\"");
        Assert.IsFalse(json.Contains("path", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("parentSession", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("sourceEntry", StringComparison.OrdinalIgnoreCase));

        Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<InspectNestedWorkspaceReopenRequest>(
                "{}",
                ProtocolJson.Options));
        Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<InspectNestedWorkspaceReopenRequest>(
                $"{{\"recoveryId\":\"{RecoveryId}\",\"unexpected\":true}}",
                ProtocolJson.Options));
        Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<ReopenNestedWorkspaceRecoveryRequest>(
                json[..^1] + ",\"unexpected\":true}",
                ProtocolJson.Options));
    }

    [TestMethod]
    public void ReopenInspectionCannotExceedControlFrameLimit()
    {
        var sources = Enumerable.Range(0, 8_000)
            .Select(index => new NestedWorkspaceRecoverySourceEvidence(
                index.ToString("x64"),
                1,
                new string('c', 64)))
            .ToArray();
        Assert.ThrowsExactly<InvalidDataException>(() =>
            ProtocolStream.ValidateJsonFrameSize(
                ProtocolMessageKinds.InspectNestedWorkspaceReopen,
                new InspectNestedWorkspaceReopenResponse(
                    RecoveryId,
                    NestedWorkspaceRecoveryAssessment.VerifiedDisposable,
                    sources.Length,
                    sources.Length,
                    new string('d', 64),
                    sources,
                    DateTimeOffset.UtcNow,
                    SupportsIndependentReopen: true)));
    }

    [TestMethod]
    public async Task ExactSourceIsFullHashedCopiedAndStaleSameLengthEvidenceIsRejected()
    {
        using var root = new TemporaryDirectory();
        var workspace = Path.Combine(
            root.Path,
            OwnedNestedTemporaryWorkspace.WorkspacePrefix + RecoveryId);
        Directory.CreateDirectory(workspace);
        File.WriteAllBytes(
            Path.Combine(workspace, OwnedNestedTemporaryWorkspace.OwnerFileName),
            OwnedNestedTemporaryWorkspace.CreateOwnerRecord(
                OwnedNestedTemporaryWorkspace.WorkspacePrefix + RecoveryId));
        var sourceDirectory = Path.Combine(
            workspace,
            OwnedTemporaryArchiveSource.SourceDirectoryPrefix + SourceId);
        Directory.CreateDirectory(sourceDirectory);
        var sourcePath = Path.Combine(sourceDirectory, OwnedTemporaryArchiveSource.SourceFileName);
        var original = new byte[] { 1, 2, 3, 4 };
        File.WriteAllBytes(sourcePath, original);
        var store = new NestedTemporaryWorkspaceRecoveryStore(root.Path);

        var inspection = await store.InspectForReopenAsync(RecoveryId);

        Assert.AreEqual(NestedWorkspaceRecoveryAssessment.VerifiedDisposable, inspection.Assessment);
        Assert.IsTrue(inspection.SupportsIndependentReopen);
        var sources = inspection.Sources
            ?? throw new AssertFailedException("Verified reopen inspection omitted sources.");
        Assert.HasCount(1, sources);
        Assert.AreEqual(SourceId, sources[0].SourceId);
        Assert.AreEqual(
            Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant(),
            sources[0].ContentSha256);
        await using var copied = new MemoryStream();
        await store.CopySourceForIndependentReopenAsync(
            RecoveryId,
            inspection.Revision!,
            inspection.SourceCount!.Value,
            inspection.TotalSourceBytes!.Value,
            SourceId,
            original.Length,
            sources[0].ContentSha256,
            copied);
        CollectionAssert.AreEqual(original, copied.ToArray());

        File.WriteAllBytes(sourcePath, new byte[] { 4, 3, 2, 1 });
        await using var rejectedCopy = new MemoryStream();
        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(() =>
            store.CopySourceForIndependentReopenAsync(
                RecoveryId,
                inspection.Revision!,
                inspection.SourceCount.Value,
                inspection.TotalSourceBytes.Value,
                SourceId,
                original.Length,
                sources[0].ContentSha256,
                rejectedCopy));
        Assert.AreEqual("nested_recovery.changed", exception.Code);
        Assert.AreEqual(0, rejectedCopy.Length);
        Assert.IsTrue(File.Exists(sourcePath));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainArchives.NestedWorkspaceReopenProtocolContractTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
