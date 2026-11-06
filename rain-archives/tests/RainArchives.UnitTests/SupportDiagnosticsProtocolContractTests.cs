using System.Text.Json;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class SupportDiagnosticsProtocolContractTests
{
    [TestMethod]
    public void MinorTwentyTwoAddsOnlySupportDiagnosticsAfterMinorTwentyOne()
    {
        var minorTwentyOne = ProtocolFeatures
            .SupportedForMinorVersion(
                ProtocolConstants.NestedWorkspaceIndependentReopenMinorVersion)
            .ToArray();
        var minorTwentyTwo = ProtocolFeatures
            .SupportedForMinorVersion(ProtocolConstants.SupportDiagnosticsMinorVersion)
            .ToArray();

        CollectionAssert.AreEqual(
            minorTwentyTwo,
            ProtocolFeatures.SupportedVersion1.Take(minorTwentyTwo.Length).ToArray());
        CollectionAssert.DoesNotContain(minorTwentyOne, ProtocolFeatures.SupportDiagnostics);
        CollectionAssert.AreEqual(
            minorTwentyOne,
            minorTwentyTwo
                .Where(feature => feature != ProtocolFeatures.SupportDiagnostics)
                .ToArray());
        Assert.AreEqual(ProtocolFeatures.SupportDiagnostics, minorTwentyTwo[^1]);
        CollectionAssert.AreEqual(
            new[]
            {
                ProtocolFeatures.OperationActivity,
                ProtocolFeatures.RequestCancellation,
            },
            ProtocolFeatures.SupportDiagnosticsDependencies.ToArray());
    }

    [TestMethod]
    public void SupportDiagnosticsWireShapeIsFixedPathFreeAndNonAuthoritative()
    {
        var request = new GetSupportDiagnosticsRequest();
        Assert.AreEqual("{}", JsonSerializer.Serialize(request, ProtocolJson.Options));

        var response = Response();
        const string expected =
            "{\"protocolMajor\":1,\"protocolMinor\":22,\"enabledFeatureCount\":31,\"containmentModel\":0,\"isSameUserSecuritySandbox\":false,\"maximumActiveAndQueuedOperations\":32,\"maximumConcurrentOperations\":1,\"queueOrdering\":0,\"maximumRetainedTerminalOperations\":64,\"activeAndQueuedOperationCount\":2,\"retainedTerminalOperationCount\":3,\"operationHistoryTruncated\":false,\"supportsQueueReordering\":false,\"supportsPause\":false,\"supportsRetry\":false,\"supportsAutomaticRecoveryDisposition\":false,\"persistsOperationActivityAcrossWorkerRestart\":false}";
        var json = JsonSerializer.Serialize(response, ProtocolJson.Options);

        Assert.AreEqual(expected, json);
        foreach (var forbidden in new[]
                 {
                     "path",
                     "name",
                     "message",
                     "exception",
                     "diagnosticId",
                     "sessionId",
                     "operationId",
                     "recoveryId",
                     "secret",
                 })
        {
            Assert.IsFalse(json.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }

        CollectionAssert.AreEqual(
            new[] { WorkerContainmentModel.SameUserWorkerProcess },
            Enum.GetValues<WorkerContainmentModel>());
        var roundTrip = JsonSerializer.Deserialize<GetSupportDiagnosticsResponse>(
            json,
            ProtocolJson.Options);
        Assert.AreEqual(response, roundTrip);
    }

    [TestMethod]
    public void SupportDiagnosticsPayloadExposesNoVariableTextOrIdentifiers()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                "ProtocolMajor",
                "ProtocolMinor",
                "EnabledFeatureCount",
                "ContainmentModel",
                "IsSameUserSecuritySandbox",
                "MaximumActiveAndQueuedOperations",
                "MaximumConcurrentOperations",
                "QueueOrdering",
                "MaximumRetainedTerminalOperations",
                "ActiveAndQueuedOperationCount",
                "RetainedTerminalOperationCount",
                "OperationHistoryTruncated",
                "SupportsQueueReordering",
                "SupportsPause",
                "SupportsRetry",
                "SupportsAutomaticRecoveryDisposition",
                "PersistsOperationActivityAcrossWorkerRestart",
            },
            typeof(GetSupportDiagnosticsResponse)
                .GetProperties()
                .Select(property => property.Name)
                .ToArray());
        Assert.IsFalse(typeof(GetSupportDiagnosticsResponse).GetProperties().Any(property =>
            property.PropertyType == typeof(string)
            || property.PropertyType == typeof(Guid)
            || typeof(System.Collections.IEnumerable).IsAssignableFrom(property.PropertyType)));
    }

    [TestMethod]
    public void SupportDiagnosticsPayloadsRejectUnknownAndMissingRequiredMembers()
    {
        Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<GetSupportDiagnosticsRequest>(
                "{\"unexpected\":true}",
                ProtocolJson.Options));

        var json = JsonSerializer.Serialize(Response(), ProtocolJson.Options);
        Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<GetSupportDiagnosticsResponse>(
                json.Replace(
                    "\"supportsRetry\":false,",
                    string.Empty,
                    StringComparison.Ordinal),
                ProtocolJson.Options));
        Assert.ThrowsExactly<JsonException>(() =>
            JsonSerializer.Deserialize<GetSupportDiagnosticsResponse>(
                json[..^1] + ",\"unexpected\":true}",
                ProtocolJson.Options));
    }

    [TestMethod]
    public async Task SupportDiagnosticsUsesOneBoundedProtocolKind()
    {
        var values = typeof(ProtocolMessageKinds)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();
        Assert.AreEqual(
            1,
            values.Count(value => value == ProtocolMessageKinds.GetSupportDiagnostics));

        await using var stream = new MemoryStream();
        var requestId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        await ProtocolStream.WriteJsonAsync(
            stream,
            ProtocolMessageKinds.GetSupportDiagnostics,
            requestId,
            new GetSupportDiagnosticsRequest(),
            1);
        Assert.AreEqual(
            "{\"kind\":\"worker.support.diagnostics.get\",\"requestId\":\"11111111-2222-3333-4444-555555555555\",\"sequence\":1,\"payload\":{}}",
            Encoding.UTF8.GetString(stream.ToArray().AsSpan(5)));
        stream.Position = 0;
        var frame = await ProtocolStream.ReadAsync(stream);
        Assert.AreEqual(ProtocolFrameType.Json, frame.Type);
        Assert.AreEqual(ProtocolMessageKinds.GetSupportDiagnostics, frame.Json!.Kind);
        Assert.AreEqual(requestId, frame.Json.RequestId);
        _ = frame.Json.ReadPayload<GetSupportDiagnosticsRequest>();

        await using var oversized = new MemoryStream();
        oversized.WriteByte((byte)ProtocolFrameType.Json);
        oversized.Write(BitConverter.GetBytes(ProtocolConstants.MaximumControlFrameBytes + 1));
        oversized.Position = 0;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            _ = await ProtocolStream.ReadAsync(oversized));
    }

    private static GetSupportDiagnosticsResponse Response() =>
        new(
            ProtocolMajor: 1,
            ProtocolMinor: 22,
            EnabledFeatureCount: 31,
            WorkerContainmentModel.SameUserWorkerProcess,
            IsSameUserSecuritySandbox: false,
            MaximumActiveAndQueuedOperations: ProtocolConstants.MaximumQueuedOperations,
            MaximumConcurrentOperations: ProtocolConstants.MaximumConcurrentOperations,
            OperationQueueOrdering.Unspecified,
            MaximumRetainedTerminalOperations:
                ProtocolConstants.MaximumRetainedOperationActivity,
            ActiveAndQueuedOperationCount: 2,
            RetainedTerminalOperationCount: 3,
            OperationHistoryTruncated: false,
            SupportsQueueReordering: false,
            SupportsPause: false,
            SupportsRetry: false,
            SupportsAutomaticRecoveryDisposition: false,
            PersistsOperationActivityAcrossWorkerRestart: false);
}
