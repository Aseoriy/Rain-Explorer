using RainArchives.Contracts;

namespace RainArchives.App.Services;

public interface IEntryPreviewService : IAsyncDisposable
{
    Task<EntryPreviewResult> LoadTextAsync(
        ArchiveSessionId sessionId,
        ArchiveEntryDescriptor entry,
        Guid? passwordSlotId = null,
        CancellationToken cancellationToken = default);
}

public enum EntryPreviewOutcome
{
    Text,
    Binary,
    Oversize,
    UnsupportedEncoding,
    UnsupportedEntry,
}

public enum EntryPreviewTextEncoding
{
    Utf8,
    Utf16LittleEndian,
    Utf16BigEndian,
    Utf32LittleEndian,
    Utf32BigEndian,
}

public sealed record EntryPreviewResult(
    EntryPreviewOutcome Outcome,
    bool SourceWasRead,
    string Text,
    EntryPreviewTextEncoding? Encoding,
    int? ByteLength)
{
    public bool IsAvailable => Outcome == EntryPreviewOutcome.Text;

    public static EntryPreviewResult Available(
        string text,
        EntryPreviewTextEncoding encoding,
        int byteLength) =>
        new(EntryPreviewOutcome.Text, true, text, encoding, byteLength);

    public static EntryPreviewResult Unavailable(
        EntryPreviewOutcome outcome,
        bool sourceWasRead = false,
        int? byteLength = null)
    {
        if (outcome == EntryPreviewOutcome.Text)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outcome),
                "Unavailable preview results cannot use the text outcome.");
        }

        return new(outcome, sourceWasRead, string.Empty, null, byteLength);
    }
}
