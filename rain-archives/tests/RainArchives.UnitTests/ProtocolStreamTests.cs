using System.Buffers.Binary;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class ProtocolStreamTests
{
    [TestMethod]
    public async Task DirectJsonWriterRejectsOversizedPayloadBeforeWritingAFrame()
    {
        await using var stream = new MemoryStream();
        var oversized = new string('x', ProtocolConstants.MaximumControlFrameBytes + 1);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await ProtocolStream.WriteJsonAsync(
                stream,
                "test.oversized",
                Guid.NewGuid(),
                new OpenArchiveRequest(oversized, null),
                1));

        Assert.AreEqual(0, stream.Length);
    }

    [TestMethod]
    public async Task JsonFrameRoundTrips()
    {
        await using var stream = new MemoryStream();
        var requestId = Guid.NewGuid();
        await ProtocolStream.WriteJsonAsync(
            stream,
            ProtocolMessageKinds.ListEntries,
            requestId,
            new ListEntriesRequest(ArchiveSessionId.New(), 12, 25),
            1);
        stream.Position = 0;

        var incoming = await ProtocolStream.ReadAsync(stream);

        Assert.AreEqual(ProtocolFrameType.Json, incoming.Type);
        Assert.AreEqual(requestId, incoming.Json!.RequestId);
        Assert.AreEqual(1, incoming.Json.Sequence);
        var payload = incoming.Json.ReadPayload<ListEntriesRequest>();
        Assert.AreEqual(12, payload.Offset);
        Assert.AreEqual(25, payload.Limit);
    }

    [TestMethod]
    public async Task RecoveryInspectionFrameRoundTrips()
    {
        await using var stream = new MemoryStream();
        var recoveryId = new string('a', 64);
        var response = new InspectArchiveCreationRecoveryResponse(
            new ArchiveCreationRecoverySummary(
                recoveryId,
                OperationId.New(),
                ArchiveCreationRecoveryPhase.CommitIntent,
                4,
                DateTimeOffset.UtcNow,
                HasIncompleteTail: false),
            @"C:\created.zip",
            $".rainarchives-{recoveryId}.zip.staging",
            JournalBoundLength: 42,
            IsRemoteDestination: false,
            ArchiveCreationRecoveryParentState.MatchesJournal,
            new ArchiveCreationRecoveryArtifactInspection(
                ArchiveCreationRecoveryArtifactState.Absent),
            new ArchiveCreationRecoveryArtifactInspection(
                ArchiveCreationRecoveryArtifactState.ExactMatch,
                42,
                1),
            ArchiveCreationRecoveryAssessment.OwnedFinalObserved,
            DateTimeOffset.UtcNow);
        await ProtocolStream.WriteJsonAsync(
            stream,
            $"{ProtocolMessageKinds.InspectArchiveCreationRecovery}.response",
            Guid.NewGuid(),
            response,
            1);
        stream.Position = 0;

        var incoming = await ProtocolStream.ReadAsync(stream);
        var roundTrip = incoming.Json!.ReadPayload<InspectArchiveCreationRecoveryResponse>();

        Assert.AreEqual(response, roundTrip);
    }

    [TestMethod]
    public async Task RecoveryDispositionFrameRoundTripsWithExpectedRevision()
    {
        await using var stream = new MemoryStream();
        var request = new ApplyArchiveCreationRecoveryDispositionRequest(
            new string('b', 64),
            ArchiveCreationRecoveryPhase.CommitIntent,
            4,
            new DateTimeOffset(2026, 8, 23, 12, 34, 56, TimeSpan.Zero),
            ArchiveCreationRecoveryDispositionAction.CommitOwnedStaging);
        await ProtocolStream.WriteJsonAsync(
            stream,
            ProtocolMessageKinds.ApplyArchiveCreationRecoveryDisposition,
            Guid.NewGuid(),
            request,
            1);
        stream.Position = 0;

        var incoming = await ProtocolStream.ReadAsync(stream);
        var roundTrip = incoming.Json!
            .ReadPayload<ApplyArchiveCreationRecoveryDispositionRequest>();

        Assert.AreEqual(request, roundTrip);
    }

    [TestMethod]
    public async Task MaximumRecoveryInventoryFitsTheControlFrameBudget()
    {
        var summaries = Enumerable.Range(
                0,
                ProtocolConstants.MaximumArchiveCreationRecoveryRecords)
            .Select(index => new ArchiveCreationRecoverySummary(
                index.ToString("x64", System.Globalization.CultureInfo.InvariantCulture),
                OperationId.New(),
                ArchiveCreationRecoveryPhase.Admitted,
                1,
                DateTimeOffset.UnixEpoch,
                HasIncompleteTail: false))
            .ToArray();
        await using var stream = new MemoryStream();

        await ProtocolStream.WriteJsonAsync(
            stream,
            $"{ProtocolMessageKinds.ListArchiveCreationRecoveries}.response",
            Guid.NewGuid(),
            new ListArchiveCreationRecoveriesResponse(
                summaries,
                0,
                0,
                0,
                false,
                false),
            1);

        Assert.IsLessThanOrEqualTo(
            ProtocolConstants.MaximumControlFrameBytes + sizeof(int) + 1,
            stream.Length);
    }

    [TestMethod]
    public async Task SecretFrameRoundTripsWithoutJson()
    {
        await using var stream = new MemoryStream();
        var slot = Guid.NewGuid();
        var bytes = "p@ssword"u8.ToArray();
        await ProtocolStream.WriteSecretAsync(stream, slot, bytes);
        stream.Position = 0;

        var incoming = await ProtocolStream.ReadAsync(stream);

        Assert.AreEqual(ProtocolFrameType.Secret, incoming.Type);
        Assert.IsNull(incoming.Json);
        Assert.AreEqual(slot, incoming.Secret!.SlotId);
        CollectionAssert.AreEqual(bytes, incoming.Secret.SecretBytes);
        Array.Clear(incoming.Secret.SecretBytes);
        Array.Clear(bytes);
    }

    [TestMethod]
    public async Task RejectsOversizedDeclaredFrameBeforeAllocation()
    {
        await using var stream = new MemoryStream();
        stream.WriteByte((byte)ProtocolFrameType.Json);
        stream.Write(BitConverter.GetBytes(ProtocolConstants.MaximumControlFrameBytes + 1));
        stream.Position = 0;

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
        {
            _ = await ProtocolStream.ReadAsync(stream);
        });
    }

    [TestMethod]
    public async Task RejectsOversizedSecretBeforeReadingPayload()
    {
        await using var stream = new MemoryStream();
        stream.WriteByte((byte)ProtocolFrameType.Secret);
        stream.Write(BitConverter.GetBytes(17 + ProtocolConstants.MaximumSecretBytes));
        stream.Position = 0;

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
        {
            _ = await ProtocolStream.ReadAsync(stream);
        });
    }

    [TestMethod]
    public async Task RejectsUnknownFrameTypeBeforeReadingPayload()
    {
        await using var stream = new MemoryStream();
        stream.WriteByte(0x7F);
        stream.Write(BitConverter.GetBytes(128));
        stream.Position = 0;

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
        {
            _ = await ProtocolStream.ReadAsync(stream);
        });
    }

    [TestMethod]
    public async Task RejectsEmptyJsonFrame()
    {
        await using var stream = new MemoryStream();
        stream.WriteByte((byte)ProtocolFrameType.Json);
        stream.Write(BitConverter.GetBytes(0));
        stream.Position = 0;

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
        {
            _ = await ProtocolStream.ReadAsync(stream);
        });
    }

    [TestMethod]
    public async Task RejectsUnknownJsonMembers()
    {
        const string json = """
            {"kind":"entries.list","requestId":"00000000-0000-0000-0000-000000000001","sequence":1,"payload":{},"unexpected":true}
            """;
        var payload = Encoding.UTF8.GetBytes(json);
        await using var stream = new MemoryStream();
        stream.WriteByte((byte)ProtocolFrameType.Json);
        var length = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        stream.Write(length);
        stream.Write(payload);
        stream.Position = 0;

        await Assert.ThrowsExactlyAsync<System.Text.Json.JsonException>(async () =>
        {
            _ = await ProtocolStream.ReadAsync(stream);
        });
    }

    [TestMethod]
    public async Task RecoveryRequestRejectsUnknownPayloadMembers()
    {
        const string json = """
            {"kind":"archive.creation.recovery.inspect","requestId":"00000000-0000-0000-0000-000000000001","sequence":1,"payload":{"recoveryId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","unexpected":true}}
            """;
        var payload = Encoding.UTF8.GetBytes(json);
        await using var stream = new MemoryStream();
        stream.WriteByte((byte)ProtocolFrameType.Json);
        var length = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        stream.Write(length);
        stream.Write(payload);
        stream.Position = 0;
        var incoming = await ProtocolStream.ReadAsync(stream);

        await Assert.ThrowsExactlyAsync<System.Text.Json.JsonException>(async () =>
        {
            _ = incoming.Json!.ReadPayload<InspectArchiveCreationRecoveryRequest>();
            await Task.CompletedTask;
        });
    }

    [TestMethod]
    public async Task RecoveryListResponseRejectsOmittedScalarFields()
    {
        const string json = """
            {"kind":"archive.creation.recovery.list.response","requestId":"00000000-0000-0000-0000-000000000001","sequence":1,"payload":{"recoveries":[],"suspiciousOperationCount":0,"incompleteJournalCount":0,"isTruncated":false,"inspectionIncomplete":false}}
            """;
        var payload = Encoding.UTF8.GetBytes(json);
        await using var stream = new MemoryStream();
        stream.WriteByte((byte)ProtocolFrameType.Json);
        var length = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        stream.Write(length);
        stream.Write(payload);
        stream.Position = 0;
        var incoming = await ProtocolStream.ReadAsync(stream);

        await Assert.ThrowsExactlyAsync<System.Text.Json.JsonException>(async () =>
        {
            _ = incoming.Json!.ReadPayload<ListArchiveCreationRecoveriesResponse>();
            await Task.CompletedTask;
        });
    }

    [TestMethod]
    public async Task RecoveryDispositionResponseRejectsOmittedScalarFields()
    {
        const string json = """
            {"kind":"archive.creation.recovery.dispose.response","requestId":"00000000-0000-0000-0000-000000000001","sequence":1,"payload":{"recoveryId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","action":"commitOwnedStaging","result":"completed","resolvedAtUtc":"2026-08-23T12:34:56+00:00"}}
            """;
        var payload = Encoding.UTF8.GetBytes(json);
        await using var stream = new MemoryStream();
        stream.WriteByte((byte)ProtocolFrameType.Json);
        var length = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, payload.Length);
        stream.Write(length);
        stream.Write(payload);
        stream.Position = 0;
        var incoming = await ProtocolStream.ReadAsync(stream);

        await Assert.ThrowsExactlyAsync<System.Text.Json.JsonException>(async () =>
        {
            _ = incoming.Json!
                .ReadPayload<ApplyArchiveCreationRecoveryDispositionResponse>();
            await Task.CompletedTask;
        });
    }
}
