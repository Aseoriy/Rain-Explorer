using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.IntegrationTests;

[TestClass]
public sealed class FramedEntryReadStreamTests
{
    [TestMethod]
    public async Task DataAndConsistentCompletionProduceAReadOnlyStream()
    {
        await using var pipe = new MemoryStream();
        await EntryStreamPipeProtocol.WriteDataAsync(pipe, "first"u8.ToArray());
        await EntryStreamPipeProtocol.WriteDataAsync(pipe, "-second"u8.ToArray());
        await EntryStreamPipeProtocol.WriteCompleteAsync(pipe, 12);
        pipe.Position = 0;
        var closed = 0;
        var aborted = 0;
        await using var stream = new FramedEntryReadStream(
            pipe,
            EntryId.New(),
            maximumBytes: 64,
            exception =>
            {
                Interlocked.Increment(ref aborted);
                return ProtocolFailure(exception);
            },
            _ => Interlocked.Increment(ref closed));

        using var destination = new MemoryStream();
        await stream.CopyToAsync(destination);

        CollectionAssert.AreEqual("first-second"u8.ToArray(), destination.ToArray());
        Assert.AreEqual(1, closed);
        Assert.AreEqual(0, aborted);
        Assert.IsTrue(stream.CanRead);
        Assert.IsFalse(stream.CanSeek);
        Assert.IsFalse(stream.CanWrite);
    }

    [TestMethod]
    public async Task WorkerByteLimitViolationAbortsTheControlTransport()
    {
        await using var pipe = new MemoryStream();
        await EntryStreamPipeProtocol.WriteDataAsync(pipe, "four"u8.ToArray());
        await EntryStreamPipeProtocol.WriteCompleteAsync(pipe, 4);
        pipe.Position = 0;
        Exception? protocolCause = null;
        var closed = 0;
        await using var stream = new FramedEntryReadStream(
            pipe,
            EntryId.New(),
            maximumBytes: 3,
            exception =>
            {
                protocolCause = exception;
                return ProtocolFailure(exception);
            },
            _ => Interlocked.Increment(ref closed));

        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            _ = await stream.ReadAsync(new byte[8]));

        Assert.AreEqual(ArchiveErrorCategory.ProtocolMismatch, failure.Error.Category);
        Assert.IsInstanceOfType<InvalidDataException>(protocolCause);
        Assert.AreEqual(1, closed);
    }

    [TestMethod]
    public async Task StructuredTerminalErrorFailsOnlyTheLease()
    {
        var entryId = EntryId.New();
        var expectedError = new ArchiveError(
            "archive.password_required",
            ArchiveErrorCategory.PasswordRequired,
            "A password is required.",
            IsRetryable: true,
            EntryId: entryId);
        await using var pipe = new MemoryStream();
        await EntryStreamPipeProtocol.WriteErrorAsync(pipe, expectedError);
        pipe.Position = 0;
        var aborted = 0;
        await using var stream = new FramedEntryReadStream(
            pipe,
            entryId,
            maximumBytes: 64,
            exception =>
            {
                Interlocked.Increment(ref aborted);
                return ProtocolFailure(exception);
            },
            _ => { });

        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            _ = await stream.ReadAsync(new byte[8]));

        Assert.AreEqual(expectedError, failure.Error);
        Assert.AreEqual(0, aborted);
        var repeated = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            _ = await stream.ReadAsync(new byte[8]));
        Assert.AreEqual(expectedError, repeated.Error);
    }

    private static ArchiveClientException ProtocolFailure(Exception exception) =>
        new(new ArchiveError(
            "protocol.invalid_worker_frame",
            ArchiveErrorCategory.ProtocolMismatch,
            exception.Message));
}
