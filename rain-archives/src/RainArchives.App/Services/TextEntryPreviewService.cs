using System.Globalization;
using System.IO;
using System.Text;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.App.Services;

public sealed class TextEntryPreviewService : IEntryPreviewService
{
    public const int MaximumTextPreviewBytes = 1024 * 1024;
    private const int StreamLimitBytes = MaximumTextPreviewBytes + 1;
    private const int MaximumConcurrentMaterializations = 4;
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
    private readonly ArchivePreviewCache _cache;

    public TextEntryPreviewService(IArchiveClient client, string cacheDirectory)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        _cache = new ArchivePreviewCache(
            client,
            cacheDirectory,
            (long)StreamLimitBytes * MaximumConcurrentMaterializations,
            MaximumConcurrentMaterializations);
    }

    public static TextEntryPreviewService CreateDefault(IArchiveClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException("Windows did not provide a local application-data directory.");
        }

        var cacheDirectory = Path.Combine(
            localApplicationData,
            "SailHub",
            "Rain Archives",
            "Preview Cache");
        Directory.CreateDirectory(cacheDirectory);
        return new TextEntryPreviewService(client, cacheDirectory);
    }

    public async Task<EntryPreviewResult> LoadTextAsync(
        ArchiveSessionId sessionId,
        ArchiveEntryDescriptor entry,
        Guid? passwordSlotId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Kind != ArchiveEntryKind.File)
        {
            return EntryPreviewResult.Unavailable(EntryPreviewOutcome.UnsupportedEntry);
        }

        if (entry.OriginalSize is > MaximumTextPreviewBytes)
        {
            return TooLarge(sourceWasRead: false);
        }

        try
        {
            await using var lease = await _cache.MaterializeAsync(
                    sessionId,
                    entry.EntryId,
                    StreamLimitBytes,
                    passwordSlotId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (lease.Length > MaximumTextPreviewBytes)
            {
                return TooLarge(
                    sourceWasRead: true,
                    byteLength: checked((int)lease.Length));
            }

            var bytes = GC.AllocateUninitializedArray<byte>(checked((int)lease.Length));
            await lease.Content.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            return Decode(bytes);
        }
        catch (ArchiveClientException exception) when (
            exception.Error.Code is "entry.stream_declared_size_limit" or "entry.stream_byte_limit")
        {
            return TooLarge(sourceWasRead: false);
        }
    }

    public ValueTask DisposeAsync() => _cache.DisposeAsync();

    internal static EntryPreviewResult Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumTextPreviewBytes)
        {
            return TooLarge(sourceWasRead: true, byteLength: bytes.Length);
        }

        var encoding = SelectEncoding(bytes, out var preambleBytes, out var encodingKind);
        var payload = bytes[preambleBytes..];
        if (preambleBytes == 0 && payload.Contains((byte)0))
        {
            return Binary(bytes.Length);
        }

        string decoded;
        try
        {
            decoded = encoding.GetString(payload);
        }
        catch (DecoderFallbackException)
        {
            return EntryPreviewResult.Unavailable(
                EntryPreviewOutcome.UnsupportedEncoding,
                sourceWasRead: true,
                byteLength: bytes.Length);
        }

        var disallowedControls = 0;
        foreach (var character in decoded)
        {
            if (character == '\0')
            {
                return Binary(bytes.Length);
            }

            if (character is not ('\r' or '\n' or '\t')
                && char.GetUnicodeCategory(character) == UnicodeCategory.Control)
            {
                disallowedControls++;
            }
        }

        if (disallowedControls > Math.Max(8, decoded.Length / 100))
        {
            return Binary(bytes.Length);
        }

        return EntryPreviewResult.Available(
            EscapeMultiline(decoded),
            encodingKind,
            bytes.Length);
    }

    private static Encoding SelectEncoding(
        ReadOnlySpan<byte> bytes,
        out int preambleBytes,
        out EntryPreviewTextEncoding kind)
    {
        if (bytes.StartsWith(Utf32BigEndianPreamble))
        {
            preambleBytes = 4;
            kind = EntryPreviewTextEncoding.Utf32BigEndian;
            return StrictUtf32BigEndian;
        }

        if (bytes.StartsWith(Utf32LittleEndianPreamble))
        {
            preambleBytes = 4;
            kind = EntryPreviewTextEncoding.Utf32LittleEndian;
            return StrictUtf32LittleEndian;
        }

        if (bytes.StartsWith(Utf8Preamble))
        {
            preambleBytes = 3;
            kind = EntryPreviewTextEncoding.Utf8;
            return StrictUtf8;
        }

        if (bytes.StartsWith(Utf16BigEndianPreamble))
        {
            preambleBytes = 2;
            kind = EntryPreviewTextEncoding.Utf16BigEndian;
            return StrictUtf16BigEndian;
        }

        if (bytes.StartsWith(Utf16LittleEndianPreamble))
        {
            preambleBytes = 2;
            kind = EntryPreviewTextEncoding.Utf16LittleEndian;
            return StrictUtf16LittleEndian;
        }

        preambleBytes = 0;
        kind = EntryPreviewTextEncoding.Utf8;
        return StrictUtf8;
    }

    private static string EscapeMultiline(string value)
    {
        var result = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
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
                && Rune.TryCreate(current, value[index + 1], out var rune))
            {
                if (MustEscape(Rune.GetUnicodeCategory(rune)))
                {
                    result.Append("\\U").Append(rune.Value.ToString("X8", CultureInfo.InvariantCulture));
                }
                else
                {
                    result.Append(current).Append(value[index + 1]);
                }

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

    private static EntryPreviewResult Binary(int byteLength) =>
        EntryPreviewResult.Unavailable(
            EntryPreviewOutcome.Binary,
            sourceWasRead: true,
            byteLength);

    private static EntryPreviewResult TooLarge(bool sourceWasRead, int? byteLength = null) =>
        EntryPreviewResult.Unavailable(
            EntryPreviewOutcome.Oversize,
            sourceWasRead,
            byteLength);
}
