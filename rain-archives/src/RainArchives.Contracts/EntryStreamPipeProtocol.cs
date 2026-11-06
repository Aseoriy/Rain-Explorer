using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace RainArchives.Contracts;

public enum EntryStreamFrameType : byte
{
    Hello = 0,
    Data = 1,
    Complete = 2,
    Error = 3,
}

public sealed record EntryStreamFrame(EntryStreamFrameType Type, byte[] Payload);

public static class EntryStreamPipeProtocol
{
    public const int BootstrapNonceBytes = 32;
    private const int HeaderLength = 5;
    private const int HelloPayloadLength = 16 + BootstrapNonceBytes;
    private const int CompletionPayloadLength = sizeof(long);

    public static async ValueTask WriteHelloAsync(
        Stream stream,
        EntryStreamId streamId,
        ReadOnlyMemory<byte> bootstrapNonce,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (streamId.Value == Guid.Empty || bootstrapNonce.Length != BootstrapNonceBytes)
        {
            throw new InvalidDataException("Entry stream hello data was invalid.");
        }

        var payload = new byte[HelloPayloadLength];
        streamId.Value.TryWriteBytes(payload.AsSpan(0, 16));
        bootstrapNonce.CopyTo(payload.AsMemory(16));
        try
        {
            await WriteFrameAsync(
                stream,
                EntryStreamFrameType.Hello,
                payload,
                flush: true,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    public static ValueTask WriteDataAsync(
        Stream stream,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (data.IsEmpty || data.Length > ProtocolConstants.MaximumEntryStreamFrameBytes)
        {
            throw new InvalidDataException("Entry stream data frame length was outside the allowed range.");
        }

        return WriteFrameAsync(
            stream,
            EntryStreamFrameType.Data,
            data,
            flush: false,
            cancellationToken);
    }

    public static ValueTask WriteCompleteAsync(
        Stream stream,
        long totalBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (totalBytes < 0)
        {
            throw new InvalidDataException("Entry stream completion total cannot be negative.");
        }

        var payload = new byte[CompletionPayloadLength];
        BinaryPrimitives.WriteInt64LittleEndian(payload, totalBytes);
        return WriteFrameAsync(
            stream,
            EntryStreamFrameType.Complete,
            payload,
            flush: true,
            cancellationToken);
    }

    public static ValueTask WriteErrorAsync(
        Stream stream,
        ArchiveError error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(error);
        var payload = ProtocolStream.SerializeBounded(
            writer => JsonSerializer.Serialize(writer, error, ProtocolJson.Options),
            ProtocolConstants.MaximumEntryStreamErrorBytes);
        if (payload.IsEmpty)
        {
            throw new InvalidDataException("Entry stream errors cannot be empty.");
        }

        return WriteFrameAsync(
            stream,
            EntryStreamFrameType.Error,
            payload,
            flush: true,
            cancellationToken);
    }

    public static async ValueTask<EntryStreamFrame> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[HeaderLength];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var type = (EntryStreamFrameType)header[0];
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
        if (!IsValidLength(type, length))
        {
            throw new InvalidDataException("Entry stream frame type or length was invalid.");
        }

        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return new EntryStreamFrame(type, payload);
    }

    public static void ValidateHello(
        EntryStreamFrame frame,
        EntryStreamId expectedStreamId,
        ReadOnlySpan<byte> expectedBootstrapNonce)
    {
        ArgumentNullException.ThrowIfNull(frame);
        try
        {
            if (frame.Type != EntryStreamFrameType.Hello
                || frame.Payload.Length != HelloPayloadLength
                || expectedStreamId.Value == Guid.Empty
                || expectedBootstrapNonce.Length != BootstrapNonceBytes)
            {
                throw new InvalidDataException("Entry stream hello frame was invalid.");
            }

            var receivedStreamId = new Guid(frame.Payload.AsSpan(0, 16));
            if (receivedStreamId != expectedStreamId.Value
                || !CryptographicOperations.FixedTimeEquals(
                    frame.Payload.AsSpan(16),
                    expectedBootstrapNonce))
            {
                throw new InvalidDataException("Entry stream bootstrap proof did not match the control response.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(frame.Payload);
        }
    }

    public static long ReadCompletionTotal(EntryStreamFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Type != EntryStreamFrameType.Complete
            || frame.Payload.Length != CompletionPayloadLength)
        {
            throw new InvalidDataException("Entry stream completion frame was invalid.");
        }

        var total = BinaryPrimitives.ReadInt64LittleEndian(frame.Payload);
        if (total < 0)
        {
            throw new InvalidDataException("Entry stream completion total cannot be negative.");
        }

        return total;
    }

    public static ArchiveError ReadError(EntryStreamFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Type != EntryStreamFrameType.Error
            || frame.Payload.Length is <= 0 or > ProtocolConstants.MaximumEntryStreamErrorBytes)
        {
            throw new InvalidDataException("Entry stream error frame was invalid.");
        }

        return JsonSerializer.Deserialize<ArchiveError>(frame.Payload, ProtocolJson.Options)
            ?? throw new InvalidDataException("Entry stream error frame contained no value.");
    }

    private static bool IsValidLength(EntryStreamFrameType type, int length) => type switch
    {
        EntryStreamFrameType.Hello => length == HelloPayloadLength,
        EntryStreamFrameType.Data => length is > 0 and <= ProtocolConstants.MaximumEntryStreamFrameBytes,
        EntryStreamFrameType.Complete => length == CompletionPayloadLength,
        EntryStreamFrameType.Error => length is > 0 and <= ProtocolConstants.MaximumEntryStreamErrorBytes,
        _ => false,
    };

    private static async ValueTask WriteFrameAsync(
        Stream stream,
        EntryStreamFrameType type,
        ReadOnlyMemory<byte> payload,
        bool flush,
        CancellationToken cancellationToken)
    {
        if (!IsValidLength(type, payload.Length))
        {
            throw new InvalidDataException("Entry stream frame type or length was invalid.");
        }

        var header = new byte[HeaderLength];
        header[0] = (byte)type;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        if (flush)
        {
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The entry stream pipe closed in the middle of a frame.");
            }

            offset += read;
        }
    }
}
