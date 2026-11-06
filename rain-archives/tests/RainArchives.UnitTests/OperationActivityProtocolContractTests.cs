using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class OperationActivityProtocolContractTests
{
    private static readonly DateTimeOffset CreatedAtUtc =
        new(2026, 8, 26, 12, 34, 56, TimeSpan.Zero);

    [TestMethod]
    public void MinorNineteenAddsOnlyOperationActivityAfterMinorEighteen()
    {
        var minorEighteen = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.ManagedWriteCapabilitiesMinorVersion)
            .ToArray();
        var minorNineteen = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.OperationActivityMinorVersion)
            .ToArray();

        CollectionAssert.DoesNotContain(minorEighteen, ProtocolFeatures.OperationActivity);
        CollectionAssert.AreEqual(
            minorEighteen,
            minorNineteen.Where(feature => feature != ProtocolFeatures.OperationActivity).ToArray());
        Assert.AreEqual(ProtocolFeatures.OperationActivity, minorNineteen[^1]);
    }

    [TestMethod]
    public void OperationActivityDependenciesAndBoundsAreExplicit()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                ProtocolFeatures.OperationEvents,
                ProtocolFeatures.RequestCancellation,
            },
            ProtocolFeatures.OperationActivityDependencies.ToArray());
        CollectionAssert.AreEqual(
            new[] { 32, 1, 64, 96 },
            new[]
            {
                ProtocolConstants.MaximumQueuedOperations,
                ProtocolConstants.MaximumConcurrentOperations,
                ProtocolConstants.MaximumRetainedOperationActivity,
                ProtocolConstants.MaximumOperationActivityRecords,
            });
        CollectionAssert.AreEqual(
            new[] { OperationQueueOrdering.Unspecified },
            Enum.GetValues<OperationQueueOrdering>());
    }

    [TestMethod]
    public void ActivityPayloadHasStablePathFreeTypedJsonShape()
    {
        var request = new GetOperationActivityRequest();
        Assert.AreEqual("{}", JsonSerializer.Serialize(request, ProtocolJson.Options));

        var response = Response(
        [
            new OperationActivitySnapshot(
                new OperationId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
                OperationKind.CreateArchive,
                OperationPhase.Running,
                LastEventSequence: 3,
                CreatedAtUtc,
                CreatedAtUtc.AddSeconds(2),
                CompletedAtUtc: null,
                new OperationActivityProgress(
                    BytesProcessed: 13,
                    TotalBytes: 26,
                    EntriesProcessed: 1,
                    TotalEntries: 2,
                    Elapsed: TimeSpan.FromSeconds(2),
                    BytesPerSecond: 6.5,
                    CompressionRatio: 0.5),
                WarningCount: 0,
                CanCancel: true),
        ]);
        var json = JsonSerializer.Serialize(response, ProtocolJson.Options);

        StringAssert.Contains(json, "\"maximumActiveAndQueuedOperations\":32");
        StringAssert.Contains(json, "\"maximumConcurrentOperations\":1");
        StringAssert.Contains(json, "\"maximumRetainedTerminalOperations\":64");
        StringAssert.Contains(json, "\"supportsReordering\":false");
        StringAssert.Contains(json, "\"supportsPause\":false");
        StringAssert.Contains(json, "\"supportsRetry\":false");
        StringAssert.Contains(json, "\"persistsAcrossWorkerRestart\":false");
        Assert.IsFalse(json.Contains("sourcePath", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("destinationPath", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("currentEntryId", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("currentEntryName", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("\"warning\":", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("\"error\":", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("\"message\":", StringComparison.Ordinal));

        var roundTrip = JsonSerializer.Deserialize<GetOperationActivityResponse>(
            json,
            ProtocolJson.Options);
        Assert.IsNotNull(roundTrip);
        Assert.HasCount(1, roundTrip.Operations);
        Assert.AreEqual(response.Operations[0], roundTrip.Operations[0]);
    }

    [TestMethod]
    public void ActivitySnapshotsExposeOnlyBoundedLifecycleFields()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                "OperationId",
                "Kind",
                "Phase",
                "LastEventSequence",
                "CreatedAtUtc",
                "UpdatedAtUtc",
                "CompletedAtUtc",
                "Progress",
                "WarningCount",
                "CanCancel",
            },
            typeof(OperationActivitySnapshot).GetProperties().Select(property => property.Name).ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                "BytesProcessed",
                "TotalBytes",
                "EntriesProcessed",
                "TotalEntries",
                "Elapsed",
                "BytesPerSecond",
                "CompressionRatio",
            },
            typeof(OperationActivityProgress).GetProperties().Select(property => property.Name).ToArray());
    }

    [TestMethod]
    public void ActivityPayloadsRejectUnknownAndMissingRequiredMembers()
    {
        Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<GetOperationActivityRequest>(
                "{\"unexpected\":true}",
                ProtocolJson.Options));
        Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<GetOperationActivityResponse>(
                "{\"snapshotAtUtc\":\"2026-08-26T12:34:56+00:00\",\"maximumActiveAndQueuedOperations\":32,\"maximumConcurrentOperations\":1,\"queueOrdering\":0,\"maximumRetainedTerminalOperations\":64,\"supportsReordering\":false,\"supportsPause\":false,\"persistsAcrossWorkerRestart\":false,\"historyTruncated\":false,\"operations\":[]}",
                ProtocolJson.Options));
        Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<GetOperationActivityResponse>(
                "{\"snapshotAtUtc\":\"2026-08-26T12:34:56+00:00\",\"maximumActiveAndQueuedOperations\":32,\"maximumConcurrentOperations\":1,\"queueOrdering\":0,\"maximumRetainedTerminalOperations\":64,\"supportsReordering\":false,\"supportsPause\":false,\"supportsRetry\":false,\"persistsAcrossWorkerRestart\":false,\"historyTruncated\":false,\"operations\":[],\"unexpected\":true}",
                ProtocolJson.Options));
    }

    [TestMethod]
    public async Task OperationActivityUsesOneDistinctBoundedProtocolKind()
    {
        var values = typeof(ProtocolMessageKinds)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();
        Assert.AreEqual(1, values.Count(value => value == ProtocolMessageKinds.GetOperationActivity));

        await using var stream = new MemoryStream();
        var requestId = Guid.NewGuid();
        await ProtocolStream.WriteJsonAsync(
            stream,
            ProtocolMessageKinds.GetOperationActivity,
            requestId,
            new GetOperationActivityRequest(),
            1);
        stream.Position = 0;
        var frame = await ProtocolStream.ReadAsync(stream);
        Assert.AreEqual(ProtocolFrameType.Json, frame.Type);
        Assert.AreEqual(ProtocolMessageKinds.GetOperationActivity, frame.Json!.Kind);
        Assert.AreEqual(requestId, frame.Json.RequestId);
        _ = frame.Json.ReadPayload<GetOperationActivityRequest>();
    }

    [TestMethod]
    public void ActivityResponseRemainsBoundedByTheControlFrameLimit()
    {
        var operations = Enumerable.Range(0, 5_000)
            .Select(index => new OperationActivitySnapshot(
                new OperationId(Guid.NewGuid()),
                OperationKind.CreateArchive,
                OperationPhase.Completed,
                LastEventSequence: 3,
                CreatedAtUtc.AddSeconds(index),
                CreatedAtUtc.AddSeconds(index + 1),
                CreatedAtUtc.AddSeconds(index + 1),
                Progress: null,
                WarningCount: 0,
                CanCancel: false))
            .ToArray();

        Assert.ThrowsExactly<InvalidDataException>(() =>
            ProtocolStream.ValidateJsonFrameSize(
                ProtocolMessageKinds.GetOperationActivity,
                Response(operations)));
    }

    private static GetOperationActivityResponse Response(
        IReadOnlyList<OperationActivitySnapshot> operations) =>
        new(
            CreatedAtUtc.AddHours(2),
            MaximumActiveAndQueuedOperations: ProtocolConstants.MaximumQueuedOperations,
            MaximumConcurrentOperations: ProtocolConstants.MaximumConcurrentOperations,
            QueueOrdering: OperationQueueOrdering.Unspecified,
            MaximumRetainedTerminalOperations:
                ProtocolConstants.MaximumRetainedOperationActivity,
            SupportsReordering: false,
            SupportsPause: false,
            SupportsRetry: false,
            PersistsAcrossWorkerRestart: false,
            HistoryTruncated: false,
            Operations: operations);
}
