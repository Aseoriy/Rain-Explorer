using System.Globalization;
using System.IO;
using System.Text;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainExplorer.Services;

public enum ArchiveEntryPreviewState
{
    Loading,
    Text,
    Binary,
    Oversize,
    Unsupported,
    PasswordRequired,
    Offline,
    Stale,
    Cancelled,
    Error,
}

public enum ArchiveEntryPreviewEncoding
{
    None,
    Utf8,
    Utf16LittleEndian,
    Utf16BigEndian,
    Utf32LittleEndian,
    Utf32BigEndian,
}

public sealed record ArchiveEntryPreviewResult(
    ArchiveEntryPreviewState State,
    string Status,
    string Text = "",
    ArchiveEntryPreviewEncoding Encoding = ArchiveEntryPreviewEncoding.None,
    int? ByteLength = null)
{
    public static ArchiveEntryPreviewResult Fixed(ArchiveEntryPreviewState state) =>
        new(state, FixedStatus(state));

    public static string FixedStatus(ArchiveEntryPreviewState state) => state switch
    {
        ArchiveEntryPreviewState.Loading => "Loading archive preview…",
        ArchiveEntryPreviewState.Text => "Text preview",
        ArchiveEntryPreviewState.Binary => "Binary content can't be previewed safely.",
        ArchiveEntryPreviewState.Oversize => "Preview is larger than 1 MiB.",
        ArchiveEntryPreviewState.Unsupported => "This archive entry can't be previewed safely.",
        ArchiveEntryPreviewState.PasswordRequired => "This archive entry requires a password.",
        ArchiveEntryPreviewState.Offline => "The Rain Archives Worker is unavailable.",
        ArchiveEntryPreviewState.Stale => "The archive or selection changed.",
        ArchiveEntryPreviewState.Cancelled => "Preview cancelled.",
        _ => "Archive preview couldn't be loaded safely.",
    };
}

internal interface IArchiveEntryPreviewLease : IAsyncDisposable
{
    ArchiveSessionId SessionId { get; }
    EntryId EntryId { get; }
    long? DeclaredLength { get; }
    Stream Content { get; }
}

internal sealed class ClientArchiveEntryPreviewLease(ArchiveEntryStreamLease lease)
    : IArchiveEntryPreviewLease
{
    public ArchiveSessionId SessionId => lease.SessionId;
    public EntryId EntryId => lease.EntryId;
    public long? DeclaredLength => lease.DeclaredLength;
    public Stream Content => lease.Content;
    public ValueTask DisposeAsync() => lease.DisposeAsync();
}

internal delegate Task<IArchiveEntryPreviewLease> ArchiveEntryPreviewLeaseFactory(
    IArchiveClient client,
    ArchiveSessionId sessionId,
    EntryId entryId,
    long maximumBytes,
    CancellationToken cancellationToken);

internal static class ArchiveEntryPreviewReader
{
    public const int MaximumTextBytes = 1024 * 1024;
    public const int StreamLimitBytes = MaximumTextBytes + 1;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UnicodeEncoding StrictUtf16LittleEndian = new(false, false, true);
    private static readonly UnicodeEncoding StrictUtf16BigEndian = new(true, false, true);
    private static readonly UTF32Encoding StrictUtf32LittleEndian = new(false, false, true);
    private static readonly UTF32Encoding StrictUtf32BigEndian = new(true, false, true);
    private static readonly byte[] Utf32BigEndianPreamble = [0x00, 0x00, 0xFE, 0xFF];
    private static readonly byte[] Utf32LittleEndianPreamble = [0xFF, 0xFE, 0x00, 0x00];
    private static readonly byte[] Utf8Preamble = [0xEF, 0xBB, 0xBF];
    private static readonly byte[] Utf16BigEndianPreamble = [0xFE, 0xFF];
    private static readonly byte[] Utf16LittleEndianPreamble = [0xFF, 0xFE];

    public static async Task<ArchiveEntryPreviewResult> ReadAsync(
        IArchiveEntryPreviewLease lease,
        CancellationToken cancellationToken)
    {
        if (lease.DeclaredLength is > MaximumTextBytes)
            return ArchiveEntryPreviewResult.Fixed(ArchiveEntryPreviewState.Oversize);

        byte[] bytes = GC.AllocateUninitializedArray<byte>(StreamLimitBytes);
        int total = 0;
        while (total < bytes.Length)
        {
            int read = await lease.Content.ReadAsync(bytes.AsMemory(total, bytes.Length - total),
                cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total = checked(total + read);
        }

        if (total > MaximumTextBytes)
            return ArchiveEntryPreviewResult.Fixed(ArchiveEntryPreviewState.Oversize);
        return Decode(bytes.AsSpan(0, total));
    }

    internal static ArchiveEntryPreviewResult Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumTextBytes)
            return ArchiveEntryPreviewResult.Fixed(ArchiveEntryPreviewState.Oversize);

        Encoding encoding = SelectEncoding(bytes, out int preambleBytes, out var encodingKind);
        ReadOnlySpan<byte> payload = bytes[preambleBytes..];
        if (preambleBytes == 0 && payload.Contains((byte)0))
            return ArchiveEntryPreviewResult.Fixed(ArchiveEntryPreviewState.Binary);

        string decoded;
        try
        {
            decoded = encoding.GetString(payload);
        }
        catch (DecoderFallbackException)
        {
            return ArchiveEntryPreviewResult.Fixed(ArchiveEntryPreviewState.Binary);
        }

        int disallowedControls = 0;
        foreach (char character in decoded)
        {
            if (character == '\0')
                return ArchiveEntryPreviewResult.Fixed(ArchiveEntryPreviewState.Binary);
            if (character is not ('\r' or '\n' or '\t')
                && char.GetUnicodeCategory(character) == UnicodeCategory.Control)
                disallowedControls++;
        }

        if (disallowedControls > Math.Max(8, decoded.Length / 100))
            return ArchiveEntryPreviewResult.Fixed(ArchiveEntryPreviewState.Binary);

        return new ArchiveEntryPreviewResult(
            ArchiveEntryPreviewState.Text,
            ArchiveEntryPreviewResult.FixedStatus(ArchiveEntryPreviewState.Text),
            EscapeMultiline(decoded),
            encodingKind,
            bytes.Length);
    }

    public static string EncodingLabel(ArchiveEntryPreviewEncoding encoding) => encoding switch
    {
        ArchiveEntryPreviewEncoding.Utf8 => "UTF-8",
        ArchiveEntryPreviewEncoding.Utf16LittleEndian => "UTF-16 LE",
        ArchiveEntryPreviewEncoding.Utf16BigEndian => "UTF-16 BE",
        ArchiveEntryPreviewEncoding.Utf32LittleEndian => "UTF-32 LE",
        ArchiveEntryPreviewEncoding.Utf32BigEndian => "UTF-32 BE",
        _ => string.Empty,
    };

    private static Encoding SelectEncoding(
        ReadOnlySpan<byte> bytes,
        out int preambleBytes,
        out ArchiveEntryPreviewEncoding kind)
    {
        if (bytes.StartsWith(Utf32BigEndianPreamble))
        {
            preambleBytes = 4;
            kind = ArchiveEntryPreviewEncoding.Utf32BigEndian;
            return StrictUtf32BigEndian;
        }
        if (bytes.StartsWith(Utf32LittleEndianPreamble))
        {
            preambleBytes = 4;
            kind = ArchiveEntryPreviewEncoding.Utf32LittleEndian;
            return StrictUtf32LittleEndian;
        }
        if (bytes.StartsWith(Utf8Preamble))
        {
            preambleBytes = 3;
            kind = ArchiveEntryPreviewEncoding.Utf8;
            return StrictUtf8;
        }
        if (bytes.StartsWith(Utf16BigEndianPreamble))
        {
            preambleBytes = 2;
            kind = ArchiveEntryPreviewEncoding.Utf16BigEndian;
            return StrictUtf16BigEndian;
        }
        if (bytes.StartsWith(Utf16LittleEndianPreamble))
        {
            preambleBytes = 2;
            kind = ArchiveEntryPreviewEncoding.Utf16LittleEndian;
            return StrictUtf16LittleEndian;
        }

        preambleBytes = 0;
        kind = ArchiveEntryPreviewEncoding.Utf8;
        return StrictUtf8;
    }

    private static string EscapeMultiline(string value)
    {
        var result = new StringBuilder(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (current is '\r' or '\n' or '\t')
            {
                result.Append(current);
                continue;
            }
            if (current == '\\')
            {
                result.Append("\\\\");
                continue;
            }
            if (char.IsHighSurrogate(current)
                && index + 1 < value.Length
                && Rune.TryCreate(current, value[index + 1], out Rune rune))
            {
                if (MustEscape(Rune.GetUnicodeCategory(rune)))
                    result.Append("\\U").Append(rune.Value.ToString("X8", CultureInfo.InvariantCulture));
                else
                    result.Append(current).Append(value[index + 1]);
                index++;
                continue;
            }
            if (char.IsSurrogate(current) || MustEscape(char.GetUnicodeCategory(current)))
            {
                result.Append("\\u").Append(((int)current).ToString("X4", CultureInfo.InvariantCulture));
                continue;
            }
            result.Append(current);
        }
        return result.ToString();
    }

    private static bool MustEscape(UnicodeCategory category) =>
        category is UnicodeCategory.Control
            or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator;
}
