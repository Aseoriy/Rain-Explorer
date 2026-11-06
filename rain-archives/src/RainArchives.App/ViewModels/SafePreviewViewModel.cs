using System.Globalization;
using System.IO;
using RainArchives.App.Infrastructure;
using RainArchives.App.Services;
using RainArchives.Contracts;

namespace RainArchives.App.ViewModels;

public enum SafePreviewState
{
    Idle,
    Loading,
    Text,
    Binary,
    Oversize,
    Unsupported,
    Offline,
    Stale,
    Cancelled,
    Error,
}

/// <summary>
/// Fixed-field presentation state for the existing bounded text preview service. It retains no
/// provider error, cache path, secret, recovery identifier, source path, or external-launch action.
/// </summary>
public sealed class SafePreviewViewModel : ObservableObject
{
    public const string Limits =
        "Read-only text only · 1 MiB display limit · 1 MiB + 1 detection byte per stream · "
        + "one active and one newest pending request · four cache reservations · delete-on-close";

    private SafePreviewState _state = SafePreviewState.Idle;
    private string _entryTitle = "Safe preview";
    private string _stateTitle = "No preview loaded";
    private string _stateMessage = "Select a regular file and press Enter to request a bounded preview.";
    private string _details = Limits;
    private string _text = string.Empty;
    private ArchiveSessionId? _sessionId;
    private EntryId? _entryId;

    public SafePreviewState State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(IsTextVisible));
            OnPropertyChanged(nameof(IsStatusVisible));
            OnPropertyChanged(nameof(HasPresentation));
        }
    }

    public string EntryTitle
    {
        get => _entryTitle;
        private set => SetProperty(ref _entryTitle, value);
    }

    public string StateTitle
    {
        get => _stateTitle;
        private set => SetProperty(ref _stateTitle, value);
    }

    public string StateMessage
    {
        get => _stateMessage;
        private set => SetProperty(ref _stateMessage, value);
    }

    public string Details
    {
        get => _details;
        private set => SetProperty(ref _details, value);
    }

    public string Text
    {
        get => _text;
        private set => SetProperty(ref _text, value);
    }

    public bool IsLoading => State == SafePreviewState.Loading;
    public bool IsTextVisible => State == SafePreviewState.Text;
    public bool IsStatusVisible => State != SafePreviewState.Text;
    public bool HasPresentation => State != SafePreviewState.Idle;
    internal ArchiveSessionId? SessionId => _sessionId;
    internal EntryId? EntryId => _entryId;

    public void Begin(ArchiveSessionId sessionId, ArchiveEntryDescriptor entry)
    {
        ValidateEntryIdentity(sessionId, entry);
        _sessionId = sessionId;
        _entryId = entry.EntryId;
        EntryTitle = UntrustedDisplayText.Escape(entry.DisplayName);
        StateTitle = "Loading safe preview";
        StateMessage = "Reading one authenticated, bounded, delete-on-close cache lease.";
        Details = Limits;
        Text = string.Empty;
        State = SafePreviewState.Loading;
    }

    public void Apply(
        ArchiveSessionId sessionId,
        ArchiveEntryDescriptor entry,
        EntryPreviewResult result)
    {
        ValidateEntryIdentity(sessionId, entry);
        ValidateResult(result);
        _sessionId = sessionId;
        _entryId = entry.EntryId;
        EntryTitle = UntrustedDisplayText.Escape(entry.DisplayName);
        Text = result.IsAvailable ? result.Text : string.Empty;
        switch (result.Outcome)
        {
            case EntryPreviewOutcome.Text:
                StateTitle = "Read-only text";
                StateMessage = "Decoded text is inert and cannot execute active content.";
                Details = $"Read-only text · {EncodingName(result.Encoding!.Value)} · {FormatBytes(result.ByteLength!.Value)} · {Limits}";
                State = SafePreviewState.Text;
                break;
            case EntryPreviewOutcome.Binary:
                SetFixedState(
                    SafePreviewState.Binary,
                    "Binary content",
                    "This entry contains NUL or control-dense data. Text rendering is closed.",
                    result.ByteLength);
                break;
            case EntryPreviewOutcome.Oversize:
                SetFixedState(
                    SafePreviewState.Oversize,
                    "Preview too large",
                    "The entry exceeds the 1 MiB text-preview ceiling. No larger preview or implicit export is attempted.",
                    result.ByteLength);
                break;
            case EntryPreviewOutcome.UnsupportedEncoding:
                SetFixedState(
                    SafePreviewState.Unsupported,
                    "Unsupported text encoding",
                    "Only strict UTF-8 or BOM-marked UTF-16/UTF-32 is accepted.",
                    result.ByteLength);
                break;
            case EntryPreviewOutcome.UnsupportedEntry:
                SetFixedState(
                    SafePreviewState.Unsupported,
                    "Preview unsupported",
                    "Only regular files with an authorized entry-stream capability can be previewed.",
                    result.ByteLength);
                break;
            default:
                throw new InvalidDataException("The preview service returned an unknown outcome.");
        }
    }

    public void ShowUnsupported(ArchiveSessionId sessionId, ArchiveEntryDescriptor entry)
    {
        ValidateEntryIdentity(sessionId, entry);
        _sessionId = sessionId;
        _entryId = entry.EntryId;
        EntryTitle = UntrustedDisplayText.Escape(entry.DisplayName);
        Text = string.Empty;
        SetFixedState(
            SafePreviewState.Unsupported,
            "Preview unsupported",
            "The current session does not authorize bounded entry streaming for this selection.");
    }

    public void ShowOffline(ArchiveEntryDescriptor? entry = null)
    {
        SetEntryTitle(entry);
        ClearIdentity();
        Text = string.Empty;
        SetFixedState(
            SafePreviewState.Offline,
            "Preview offline",
            "The authenticated archive worker disconnected. No cached result is shown.");
    }

    public void ShowStale(ArchiveEntryDescriptor? entry = null)
    {
        SetEntryTitle(entry);
        ClearIdentity();
        Text = string.Empty;
        SetFixedState(
            SafePreviewState.Stale,
            "Archive source changed",
            "The preview session is stale. Reopen the archive before requesting content again.");
    }

    public void ShowCancelled(ArchiveEntryDescriptor? entry = null)
    {
        SetEntryTitle(entry);
        ClearIdentity();
        Text = string.Empty;
        SetFixedState(
            SafePreviewState.Cancelled,
            "Preview cancelled",
            "The request and its cache/stream ownership were cancelled and drained.");
    }

    public void ShowError(ArchiveEntryDescriptor? entry = null)
    {
        SetEntryTitle(entry);
        ClearIdentity();
        Text = string.Empty;
        SetFixedState(
            SafePreviewState.Error,
            "Preview unavailable",
            "The preview failed safely. Provider and exception text is not displayed.");
    }

    public void Reset()
    {
        ClearIdentity();
        EntryTitle = "Safe preview";
        StateTitle = "No preview loaded";
        StateMessage = "Select a regular file and press Enter to request a bounded preview.";
        Details = Limits;
        Text = string.Empty;
        State = SafePreviewState.Idle;
    }

    private void SetFixedState(
        SafePreviewState state,
        string title,
        string message,
        int? byteLength = null)
    {
        StateTitle = title;
        StateMessage = message;
        Details = byteLength is { } length
            ? $"{FormatBytes(length)} read · {Limits}"
            : Limits;
        State = state;
    }

    private void SetEntryTitle(ArchiveEntryDescriptor? entry)
    {
        if (entry is not null)
        {
            EntryTitle = UntrustedDisplayText.Escape(entry.DisplayName);
        }
    }

    private void ClearIdentity()
    {
        _sessionId = null;
        _entryId = null;
    }

    private static void ValidateEntryIdentity(
        ArchiveSessionId sessionId,
        ArchiveEntryDescriptor entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (sessionId.Value == Guid.Empty
            || entry.EntryId.Value == Guid.Empty
            || string.IsNullOrEmpty(entry.DisplayName))
        {
            throw new InvalidDataException("The preview selection identity is invalid.");
        }
    }

    private static void ValidateResult(EntryPreviewResult? result)
    {
        if (result is null
            || !Enum.IsDefined(result.Outcome)
            || result.Text is null
            || result.ByteLength is < 0 or > TextEntryPreviewService.MaximumTextPreviewBytes + 1
            || (result.Outcome == EntryPreviewOutcome.Text
                && (!result.SourceWasRead
                    || result.Encoding is null
                    || !Enum.IsDefined(result.Encoding.Value)
                    || result.ByteLength is null
                    || result.ByteLength > TextEntryPreviewService.MaximumTextPreviewBytes))
            || (result.Outcome != EntryPreviewOutcome.Text
                && (result.Text.Length != 0 || result.Encoding is not null))
            || (result.Outcome is EntryPreviewOutcome.Binary
                    or EntryPreviewOutcome.UnsupportedEncoding
                && (!result.SourceWasRead
                    || result.ByteLength is null
                    || result.ByteLength > TextEntryPreviewService.MaximumTextPreviewBytes))
            || (result.Outcome == EntryPreviewOutcome.Oversize
                && (result.SourceWasRead
                    ? result.ByteLength != TextEntryPreviewService.MaximumTextPreviewBytes + 1
                    : result.ByteLength is not null))
            || (result.Outcome == EntryPreviewOutcome.UnsupportedEntry
                && (result.SourceWasRead || result.ByteLength is not null)))
        {
            throw new InvalidDataException("The preview service returned malformed presentation data.");
        }
    }

    private static string EncodingName(EntryPreviewTextEncoding encoding) => encoding switch
    {
        EntryPreviewTextEncoding.Utf8 => "UTF-8",
        EntryPreviewTextEncoding.Utf16LittleEndian => "UTF-16 LE",
        EntryPreviewTextEncoding.Utf16BigEndian => "UTF-16 BE",
        EntryPreviewTextEncoding.Utf32LittleEndian => "UTF-32 LE",
        EntryPreviewTextEncoding.Utf32BigEndian => "UTF-32 BE",
        _ => throw new InvalidDataException("The preview text encoding is invalid."),
    };

    private static string FormatBytes(int bytes) => bytes switch
    {
        < 1024 => $"{bytes.ToString("N0", CultureInfo.CurrentCulture)} B",
        _ => $"{(bytes / 1024d).ToString("N1", CultureInfo.CurrentCulture)} KiB",
    };
}
