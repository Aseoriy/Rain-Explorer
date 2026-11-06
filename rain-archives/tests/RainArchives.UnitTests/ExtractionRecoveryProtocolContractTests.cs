using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class ExtractionRecoveryProtocolContractTests
{
    private static readonly DateTimeOffset RecordedAtUtc =
        new(2026, 8, 27, 1, 2, 3, TimeSpan.Zero);
    private static readonly string RecoveryId = new('a', 64);

    [TestMethod]
    public void MinorTwentyAddsOnlyExtractionRecoveryAfterMinorNineteen()
    {
        var minorNineteen = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.OperationActivityMinorVersion)
            .ToArray();
        var minorTwenty = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.ExtractionRecoveryInspectionMinorVersion)
            .ToArray();
        CollectionAssert.DoesNotContain(
            minorNineteen,
            ProtocolFeatures.ExtractionRecoveryInspection);
        CollectionAssert.AreEqual(
            minorNineteen,
            minorTwenty
                .Where(feature => feature != ProtocolFeatures.ExtractionRecoveryInspection)
                .ToArray());
        Assert.AreEqual(ProtocolFeatures.ExtractionRecoveryInspection, minorTwenty[^1]);
    }

    [TestMethod]
    public void ExtractionRecoveryDependenciesAndBoundsAreExplicit()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                ProtocolFeatures.NewDestinationExtraction,
                ProtocolFeatures.SourceFingerprintValidation,
                ProtocolFeatures.RequestCancellation,
            },
            ProtocolFeatures.ExtractionRecoveryInspectionDependencies.ToArray());
        CollectionAssert.AreEqual(
            new long[] { 1024, 2_000_000, 64L * 1024 * 1024 * 1024 },
            new long[]
            {
                ProtocolConstants.MaximumExtractionRecoveryRecords,
                ProtocolConstants.MaximumExtractionRecoveryObjects,
                ProtocolConstants.MaximumExtractionRecoveryBytes,
            });
    }

    [TestMethod]
    public void ListPayloadIsPathFreeAndContainsOnlyOpaqueConservativeState()
    {
        var response = new ListExtractionRecoveriesResponse(
            [Summary(ExtractionRecoveryPhase.Writing)],
            ActiveOperationCount: 1,
            SuspiciousOperationCount: 2,
            IncompleteJournalCount: 0,
            IsTruncated: false,
            InspectionIncomplete: false);
        var json = JsonSerializer.Serialize(response, ProtocolJson.Options);

        Assert.AreEqual(
            $"{{\"recoveries\":[{{\"recoveryId\":\"{RecoveryId}\",\"state\":0,\"phase\":1,\"sequence\":2,\"recordedAtUtc\":\"2026-08-27T01:02:03+00:00\",\"hasIncompleteTail\":false}}],\"activeOperationCount\":1,\"suspiciousOperationCount\":2,\"incompleteJournalCount\":0,\"isTruncated\":false,\"inspectionIncomplete\":false}}",
            json);
        StringAssert.Contains(json, RecoveryId);
        Assert.IsFalse(json.Contains("destination", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("staging", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("operationId", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("path", StringComparison.OrdinalIgnoreCase));
        CollectionAssert.AreEqual(
            new[]
            {
                "RecoveryId",
                "State",
                "Phase",
                "Sequence",
                "RecordedAtUtc",
                "HasIncompleteTail",
            },
            typeof(ExtractionRecoverySummary)
                .GetProperties()
                .Select(property => property.Name)
                .ToArray());
    }

    [TestMethod]
    public void ExplicitInspectionIsTypedReadOnlyAndRoundTrips()
    {
        var response = new InspectExtractionRecoveryResponse(
            Summary(ExtractionRecoveryPhase.Finalized),
            @"C:\Rain\output",
            IsRemoteDestination: false,
            JournalFileCount: 1,
            JournalDirectoryCount: 1,
            JournalTotalBytes: 13,
            JournalNamedStreamCount: 0,
            JournalRevision: new string('b', 64),
            ExtractionRecoveryParentState.MatchesJournal,
            new ExtractionRecoveryArtifactInspection(
                ExtractionRecoveryArtifactState.ExactMatch,
                1,
                1,
                13,
                0,
                new string('b', 64)),
            new ExtractionRecoveryArtifactInspection(
                ExtractionRecoveryArtifactState.Absent),
            ExtractionRecoveryAssessment.OwnedFinalizedStagingObserved,
            RecordedAtUtc.AddMinutes(1),
            SupportsMutation: false,
            SupportsRetry: false,
            SupportsCleanup: false);

        var json = JsonSerializer.Serialize(response, ProtocolJson.Options);
        Assert.AreEqual(
            $"{{\"recovery\":{{\"recoveryId\":\"{RecoveryId}\",\"state\":1,\"phase\":2,\"sequence\":3,\"recordedAtUtc\":\"2026-08-27T01:02:03+00:00\",\"hasIncompleteTail\":false}},\"destinationPath\":\"C:\\\\Rain\\\\output\",\"isRemoteDestination\":false,\"journalFileCount\":1,\"journalDirectoryCount\":1,\"journalTotalBytes\":13,\"journalNamedStreamCount\":0,\"journalRevision\":\"{new string('b', 64)}\",\"destinationParent\":0,\"stagingArtifact\":{{\"state\":4,\"observedFileCount\":1,\"observedDirectoryCount\":1,\"observedTotalBytes\":13,\"observedNamedStreamCount\":0,\"observedRevision\":\"{new string('b', 64)}\"}},\"finalArtifact\":{{\"state\":2}},\"assessment\":2,\"inspectedAtUtc\":\"2026-08-27T01:03:03+00:00\",\"supportsMutation\":false,\"supportsRetry\":false,\"supportsCleanup\":false}}",
            json);
        StringAssert.Contains(json, "\"destinationPath\":\"C:\\\\Rain\\\\output\"");
        StringAssert.Contains(json, "\"supportsMutation\":false");
        StringAssert.Contains(json, "\"supportsRetry\":false");
        StringAssert.Contains(json, "\"supportsCleanup\":false");
        Assert.IsFalse(json.Contains("stagingDirectoryName", StringComparison.OrdinalIgnoreCase));
        var roundTrip = JsonSerializer.Deserialize<InspectExtractionRecoveryResponse>(
            json,
            ProtocolJson.Options);
        Assert.AreEqual(response, roundTrip);
    }

    [TestMethod]
    public void ExtractionRecoveryPayloadsRejectUnknownAndMissingRequiredMembers()
    {
        Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<ListExtractionRecoveriesRequest>(
                "{\"unexpected\":true}",
                ProtocolJson.Options));
        Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<InspectExtractionRecoveryRequest>(
                "{}",
                ProtocolJson.Options));
        Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<InspectExtractionRecoveryRequest>(
                $"{{\"recoveryId\":\"{RecoveryId}\",\"unexpected\":true}}",
                ProtocolJson.Options));

        var valid = JsonNode.Parse(JsonSerializer.Serialize(
            new ListExtractionRecoveriesResponse([], 0, 0, 0, false, false),
            ProtocolJson.Options))!.AsObject();
        valid.Remove("inspectionIncomplete");
        Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<ListExtractionRecoveriesResponse>(
                valid.ToJsonString(),
                ProtocolJson.Options));

        var inspection = JsonNode.Parse(JsonSerializer.Serialize(
            CreateInspectionResponse(),
            ProtocolJson.Options))!.AsObject();
        inspection.Remove("supportsCleanup");
        Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<InspectExtractionRecoveryResponse>(
                inspection.ToJsonString(),
                ProtocolJson.Options));
        inspection["supportsCleanup"] = false;
        inspection["unexpected"] = true;
        Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<InspectExtractionRecoveryResponse>(
                inspection.ToJsonString(),
                ProtocolJson.Options));
        valid["inspectionIncomplete"] = false;
        valid["unexpected"] = true;
        Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<ListExtractionRecoveriesResponse>(
                valid.ToJsonString(),
                ProtocolJson.Options));
    }

    [TestMethod]
    public async Task ExtractionRecoveryUsesTwoDistinctBoundedProtocolKinds()
    {
        var values = typeof(ProtocolMessageKinds)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();
        Assert.AreEqual(1, values.Count(value => value == ProtocolMessageKinds.ListExtractionRecoveries));
        Assert.AreEqual(1, values.Count(value => value == ProtocolMessageKinds.InspectExtractionRecovery));
        Assert.AreEqual(
            2,
            new[]
            {
                ProtocolMessageKinds.ListExtractionRecoveries,
                ProtocolMessageKinds.InspectExtractionRecovery,
            }.Distinct(StringComparer.Ordinal).Count());

        await AssertFrameAsync(
            ProtocolMessageKinds.ListExtractionRecoveries,
            new ListExtractionRecoveriesRequest());
        await AssertFrameAsync(
            ProtocolMessageKinds.InspectExtractionRecovery,
            new InspectExtractionRecoveryRequest(RecoveryId));
    }

    [TestMethod]
    public void ExtractionRecoveryListCannotExceedTheControlFrameLimit()
    {
        var recoveries = Enumerable.Range(0, 8_000)
            .Select(index => new ExtractionRecoverySummary(
                index.ToString("x64"),
                ExtractionRecoveryInventoryState.Incomplete,
                ExtractionRecoveryPhase.Writing,
                Sequence: 2,
                RecordedAtUtc.AddSeconds(index),
                HasIncompleteTail: false))
            .ToArray();

        Assert.ThrowsExactly<InvalidDataException>(() =>
            ProtocolStream.ValidateJsonFrameSize(
                ProtocolMessageKinds.ListExtractionRecoveries,
                new ListExtractionRecoveriesResponse(
                    recoveries,
                    0,
                    0,
                    0,
                    false,
                    false)));

        var oversizedInspection = CreateInspectionResponse() with
        {
            DestinationPath = @"C:\" + new string('x', ProtocolConstants.MaximumControlFrameBytes),
        };
        Assert.ThrowsExactly<InvalidDataException>(() =>
            ProtocolStream.ValidateJsonFrameSize(
                ProtocolMessageKinds.InspectExtractionRecovery,
                oversizedInspection));
    }

    private static InspectExtractionRecoveryResponse CreateInspectionResponse() =>
        new(
            Summary(ExtractionRecoveryPhase.Finalized),
            @"C:\Rain\output",
            IsRemoteDestination: false,
            JournalFileCount: 1,
            JournalDirectoryCount: 1,
            JournalTotalBytes: 13,
            JournalNamedStreamCount: 0,
            JournalRevision: new string('b', 64),
            ExtractionRecoveryParentState.MatchesJournal,
            new ExtractionRecoveryArtifactInspection(
                ExtractionRecoveryArtifactState.ExactMatch,
                1,
                1,
                13,
                0,
                new string('b', 64)),
            new ExtractionRecoveryArtifactInspection(
                ExtractionRecoveryArtifactState.Absent),
            ExtractionRecoveryAssessment.OwnedFinalizedStagingObserved,
            RecordedAtUtc.AddMinutes(1),
            SupportsMutation: false,
            SupportsRetry: false,
            SupportsCleanup: false);

    private static ExtractionRecoverySummary Summary(ExtractionRecoveryPhase phase) =>
        new(
            RecoveryId,
            phase switch
            {
                ExtractionRecoveryPhase.Admitted or ExtractionRecoveryPhase.Writing =>
                    ExtractionRecoveryInventoryState.Incomplete,
                ExtractionRecoveryPhase.Finalized => ExtractionRecoveryInventoryState.Finalized,
                ExtractionRecoveryPhase.CommitIntent =>
                    ExtractionRecoveryInventoryState.PublicationUncertain,
                _ => ExtractionRecoveryInventoryState.PublishedWithoutTerminal,
            },
            phase,
            (int)phase + 1,
            RecordedAtUtc,
            HasIncompleteTail: false);

    private static async Task AssertFrameAsync<T>(string kind, T payload)
    {
        await using var stream = new MemoryStream();
        var requestId = Guid.NewGuid();
        await ProtocolStream.WriteJsonAsync(stream, kind, requestId, payload, 1);
        stream.Position = 0;
        var frame = await ProtocolStream.ReadAsync(stream);
        Assert.AreEqual(ProtocolFrameType.Json, frame.Type);
        Assert.AreEqual(kind, frame.Json!.Kind);
        Assert.AreEqual(requestId, frame.Json.RequestId);
        _ = frame.Json.ReadPayload<T>();
    }
}
