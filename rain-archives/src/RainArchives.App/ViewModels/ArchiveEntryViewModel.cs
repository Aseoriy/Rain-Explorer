using RainArchives.Contracts;

namespace RainArchives.App.ViewModels;

public sealed class ArchiveEntryViewModel
{
    public ArchiveEntryViewModel(ArchiveEntryDescriptor descriptor)
    {
        Descriptor = descriptor;
    }

    public ArchiveEntryDescriptor Descriptor { get; }
    public EntryId EntryId => Descriptor.EntryId;
    public string Name => UntrustedDisplayText.Escape(Descriptor.DisplayName);
    public string AutomationName =>
        $"{Name}, {Kind}, archive occurrence {OccurrenceNumber:N0}, "
        + $"security status {Descriptor.SafetyStatus}, "
        + (Descriptor.IsEncrypted ? "encrypted" : "not encrypted");
    public string Occurrence => $"#{OccurrenceNumber:N0}";
    public string RawPath => UntrustedDisplayText.Escape(Descriptor.RawPath);
    public bool IsDirectory => Descriptor.Kind == ArchiveEntryKind.Directory;
    public string Kind => Descriptor.Kind.ToString();
    public string OriginalSize => IsDirectory ? string.Empty : FormatSize(Descriptor.OriginalSize);
    public string CompressedSize => IsDirectory ? string.Empty : FormatSize(Descriptor.CompressedSize);
    public string Modified => Descriptor.ModifiedTimeUtc?.ToLocalTime().ToString("g") ?? string.Empty;
    public string Encryption => Descriptor.IsEncrypted ? "Encrypted" : string.Empty;
    public string Safety => Descriptor.SafetyStatus == EntrySafetyStatus.Safe
        ? string.Empty
        : Descriptor.SafetyStatus.ToString();

    private long OccurrenceNumber => (long)Descriptor.DuplicateOrdinal + 1L;

    private static string FormatSize(long? bytes)
    {
        if (bytes is null or < 0)
        {
            return "?";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        var value = (double)bytes.Value;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes.Value:N0} B" : $"{value:N1} {units[unit]}";
    }
}
