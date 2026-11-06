using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RainArchives.Contracts;

public enum ProtocolFrameType : byte
{
    Json = 0,
    Secret = 1,
}

public sealed record ProtocolFrame(string Kind, Guid RequestId, long Sequence, JsonElement Payload);

public sealed record SecretFrame(Guid SlotId, byte[] SecretBytes);

public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new(ProtocolJsonSerializerContext.Default.Options)
    {
        MaxDepth = ProtocolConstants.MaximumJsonDepth,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static T ReadPayload<T>(this ProtocolFrame frame) =>
        frame.Payload.Deserialize<T>(Options)
        ?? throw new InvalidDataException($"Frame '{frame.Kind}' had an empty payload.");
}

public static class ProtocolStream
{
    private const int HeaderLength = 5;

    public static async ValueTask WriteJsonAsync<T>(
        Stream stream,
        string kind,
        Guid requestId,
        T payload,
        long sequence,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            throw new InvalidDataException("Protocol frame kind was missing.");
        }

        var serialized = SerializeJsonFrame(kind, requestId, payload, sequence);
        await WriteFrameAsync(
            stream,
            ProtocolFrameType.Json,
            serialized,
            cancellationToken).ConfigureAwait(false);
    }

    public static void ValidateJsonFrameSize<T>(string kind, T payload)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            throw new InvalidDataException("Protocol frame kind was missing.");
        }

        _ = SerializeJsonFrame(
            kind,
            Guid.Empty,
            payload,
            ProtocolConstants.MaximumRequestsPerConnection);
    }

    public static async ValueTask WriteSecretAsync(
        Stream stream,
        Guid slotId,
        ReadOnlyMemory<byte> secret,
        CancellationToken cancellationToken = default)
    {
        if (secret.Length > ProtocolConstants.MaximumSecretBytes)
        {
            throw new InvalidDataException("Secret frame exceeded the protocol limit.");
        }

        var payload = new byte[16 + secret.Length];
        slotId.TryWriteBytes(payload);
        secret.CopyTo(payload.AsMemory(16));
        try
        {
            await WriteFrameAsync(stream, ProtocolFrameType.Secret, payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    public static async ValueTask<(ProtocolFrameType Type, ProtocolFrame? Json, SecretFrame? Secret)> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        var header = new byte[HeaderLength];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        var type = (ProtocolFrameType)header[0];
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
        if (length < 0 || length > ProtocolConstants.MaximumControlFrameBytes)
        {
            throw new InvalidDataException("Protocol frame length was outside the allowed range.");
        }

        if (type is not ProtocolFrameType.Json and not ProtocolFrameType.Secret)
        {
            throw new InvalidDataException("Unknown protocol frame type.");
        }

        if (type == ProtocolFrameType.Json && length == 0)
        {
            throw new InvalidDataException("JSON protocol frames cannot be empty.");
        }

        if (type == ProtocolFrameType.Secret
            && (length < 16 || length > 16 + ProtocolConstants.MaximumSecretBytes))
        {
            throw new InvalidDataException("Secret frame length was outside the allowed range.");
        }

        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);

        if (type == ProtocolFrameType.Json)
        {
            var frame = JsonSerializer.Deserialize<ProtocolFrame>(payload, ProtocolJson.Options)
                ?? throw new InvalidDataException("Protocol frame contained no JSON value.");
            if (string.IsNullOrWhiteSpace(frame.Kind))
            {
                throw new InvalidDataException("Protocol frame kind was missing.");
            }

            return (type, frame, null);
        }

        if (type == ProtocolFrameType.Secret)
        {
            try
            {
                var slotId = new Guid(payload.AsSpan(0, 16));
                var secret = payload.AsSpan(16).ToArray();
                return (type, null, new SecretFrame(slotId, secret));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }
        throw new UnreachableException();
    }

    private static async ValueTask WriteFrameAsync(
        Stream stream,
        ProtocolFrameType type,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (payload.Length > ProtocolConstants.MaximumControlFrameBytes)
        {
            throw new InvalidDataException("Protocol frame exceeded the allowed size.");
        }

        var header = new byte[HeaderLength];
        header[0] = (byte)type;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static ReadOnlyMemory<byte> SerializeBounded(
        Action<Utf8JsonWriter> serialize,
        int maximumLength = ProtocolConstants.MaximumControlFrameBytes)
    {
        var buffer = new BoundedBufferWriter(maximumLength);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            serialize(writer);
            writer.Flush();
        }

        return buffer.WrittenMemory;
    }

    private static ReadOnlyMemory<byte> SerializeJsonFrame<T>(
        string kind,
        Guid requestId,
        T payload,
        long sequence) =>
        SerializeBounded(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("kind", kind);
            writer.WriteString("requestId", requestId);
            writer.WriteNumber("sequence", sequence);
            writer.WritePropertyName("payload");
            JsonSerializer.Serialize(writer, payload, ProtocolJson.Options);
            writer.WriteEndObject();
        });

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
                throw new EndOfStreamException("The IPC stream closed in the middle of a frame.");
            }

            offset += read;
        }
    }

    private sealed class BoundedBufferWriter : IBufferWriter<byte>
    {
        private readonly int _maximumLength;
        private byte[] _buffer;
        private int _written;

        public BoundedBufferWriter(int maximumLength)
        {
            _maximumLength = maximumLength;
            _buffer = new byte[Math.Min(4096, maximumLength)];
        }

        public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

        public void Advance(int count)
        {
            if (count < 0 || count > _buffer.Length - _written)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer.AsSpan(_written);
        }

        private void EnsureCapacity(int sizeHint)
        {
            if (sizeHint < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sizeHint));
            }

            sizeHint = Math.Max(sizeHint, 1);
            if (sizeHint > _maximumLength - _written)
            {
                throw new InvalidDataException("Protocol frame exceeded the allowed size.");
            }

            var required = _written + sizeHint;
            if (required <= _buffer.Length)
            {
                return;
            }

            var doubled = Math.Min(_maximumLength, checked(_buffer.Length * 2));
            Array.Resize(ref _buffer, Math.Max(required, doubled));
        }
    }
}
