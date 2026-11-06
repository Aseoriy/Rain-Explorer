using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class EntryStreamPipeProtocolTests
{
    [TestMethod]
    public async Task HelloDataCompletionAndErrorFramesRoundTrip()
    {
        await using var stream = new MemoryStream();
        var streamId = EntryStreamId.New();
        var nonce = Enumerable.Range(0, EntryStreamPipeProtocol.BootstrapNonceBytes)
            .Select(value => (byte)value)
            .ToArray();
        var data = "preview payload"u8.ToArray();
        var error = new ArchiveError(
            "fixture.error",
            ArchiveErrorCategory.ProviderFailure,
            "Checked fixture failure.",
            EntryId: EntryId.New());

        await EntryStreamPipeProtocol.WriteHelloAsync(stream, streamId, nonce);
        await EntryStreamPipeProtocol.WriteDataAsync(stream, data);
        await EntryStreamPipeProtocol.WriteCompleteAsync(stream, data.Length);
        await EntryStreamPipeProtocol.WriteErrorAsync(stream, error);
        stream.Position = 0;

        var hello = await EntryStreamPipeProtocol.ReadAsync(stream);
        EntryStreamPipeProtocol.ValidateHello(hello, streamId, nonce);
        Assert.IsTrue(hello.Payload.All(value => value == 0));

        var dataFrame = await EntryStreamPipeProtocol.ReadAsync(stream);
        Assert.AreEqual(EntryStreamFrameType.Data, dataFrame.Type);
        CollectionAssert.AreEqual(data, dataFrame.Payload);

        var complete = await EntryStreamPipeProtocol.ReadAsync(stream);
        Assert.AreEqual(data.Length, EntryStreamPipeProtocol.ReadCompletionTotal(complete));

        var errorFrame = await EntryStreamPipeProtocol.ReadAsync(stream);
        Assert.AreEqual(error, EntryStreamPipeProtocol.ReadError(errorFrame));
        CryptographicOperations.ZeroMemory(nonce);
    }

    [TestMethod]
    public async Task OversizedDeclaredDataFrameIsRejectedBeforePayloadAllocation()
    {
        await using var stream = new MemoryStream();
        stream.WriteByte((byte)EntryStreamFrameType.Data);
        var length = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(
            length,
            ProtocolConstants.MaximumEntryStreamFrameBytes + 1);
        stream.Write(length);
        stream.Position = 0;

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            _ = await EntryStreamPipeProtocol.ReadAsync(stream));
    }

    [TestMethod]
    public async Task BoundedErrorWriterRejectsOversizedErrorBeforeWriting()
    {
        await using var stream = new MemoryStream();
        var oversized = new ArchiveError(
            "fixture.oversized",
            ArchiveErrorCategory.ProviderFailure,
            new string('x', ProtocolConstants.MaximumEntryStreamErrorBytes));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await EntryStreamPipeProtocol.WriteErrorAsync(stream, oversized));
        Assert.AreEqual(0, stream.Length);
    }

    [TestMethod]
    public void HelloValidationUsesBothStreamIdentityAndNonceAndClearsPayload()
    {
        var streamId = EntryStreamId.New();
        var expectedNonce = RandomNumberGenerator.GetBytes(EntryStreamPipeProtocol.BootstrapNonceBytes);
        var wrongNonce = RandomNumberGenerator.GetBytes(EntryStreamPipeProtocol.BootstrapNonceBytes);
        var payload = new byte[16 + EntryStreamPipeProtocol.BootstrapNonceBytes];
        streamId.Value.TryWriteBytes(payload);
        wrongNonce.CopyTo(payload, 16);
        var frame = new EntryStreamFrame(EntryStreamFrameType.Hello, payload);

        Assert.ThrowsExactly<InvalidDataException>(() =>
            EntryStreamPipeProtocol.ValidateHello(frame, streamId, expectedNonce));
        Assert.IsTrue(payload.All(value => value == 0));
        CryptographicOperations.ZeroMemory(expectedNonce);
        CryptographicOperations.ZeroMemory(wrongNonce);
    }
}
