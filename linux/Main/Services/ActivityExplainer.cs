using RainExplorer.Models;

namespace RainExplorer.Services;

/// <summary>A plain-language account of an activity: what happened and, for failures, what to try.</summary>
public sealed record ActivityExplanation(string Summary, string? Advice);

/// <summary>
/// Turns an activity and the system's error text into a readable explanation for the "View details" window.
/// Linux reports why something failed (permission, disk full, busy…) but not which program objected, so
/// the wording says what the system refused rather than guessing at a culprit.
/// </summary>
public static class ActivityExplainer
{
    private sealed record Rule(string[] AnyOf, string Reason, string Advice);

    private static readonly Rule[] Rules =
    [
        new(["Access to the path", "is denied", "Permission denied", "UnauthorizedAccess", "don't have permission"],
            "The system refused access because your account doesn't have permission for an item involved.",
            "Check the folder's permissions in Properties → Permissions, or work in a folder you own. System "
            + "folders and other users' files are protected on purpose."),
        new(["Operation not permitted"],
            "The system blocked this even though the permissions may look fine. This happens with files owned by "
            + "another user inside shared folders (the \"sticky\" bit, as in /tmp) and with files marked immutable.",
            "Ask the owner of the item, or do this from an account that owns it."),
        new(["Read-only file system"],
            "The drive is mounted read-only, so nothing on it can be changed.",
            "If it's a USB drive, it may have been mounted read-only after an error. Safely remove it, check it "
            + "on the computer it came from, and plug it back in."),
        new(["No space left on device", "Disk quota exceeded", "not enough space"],
            "The destination drive is full.",
            "Free up space there (empty the Trash on that drive too), then try again."),
        new(["being used by another process", "Device or resource busy", "Text file busy", "is in use"],
            "Another program is using the item, so it couldn't be changed right now.",
            "Close the program that has it open, then try again."),
        new(["File name too long", "path is too long", "PathTooLong"],
            "A name or path is longer than the file system allows.",
            "Shorten the file or folder name, or use a shallower folder."),
        new(["Could not find", "No such file or directory", "does not exist", "was not found"],
            "An item was gone by the time Rain reached it. It may have been moved, renamed or deleted elsewhere.",
            "Refresh the folder and check where the item is now."),
        new(["already exists", "File exists", "Directory not empty"],
            "Something with the same name was already there.",
            "Choose a different name, or move the existing item first."),
        new(["timed out", "took too long", "Host is down", "No route to host", "Connection refused",
             "Network is unreachable", "Connection reset"],
            "A network location didn't answer in time.",
            "Check the connection to the server or share, then try again."),
        new(["Input/output error", "I/O error"],
            "The drive reported a read or write error. This can mean a loose cable, a drive that disconnected, "
            + "or a failing disk.",
            "Reconnect the drive and try again. If it keeps happening, back up what you can from it."),
        new(["Invalid argument", "invalid characters", "Illegal characters"],
            "The name contains something this drive doesn't accept (some drives, like FAT/exFAT USB sticks, "
            + "don't allow characters such as : ? \" < > |).",
            "Rename the item without those characters."),
    ];

    public static ActivityExplanation Explain(ActivityEntry entry)
    {
        string what = string.IsNullOrWhiteSpace(entry.BeginDetail ?? entry.Detail)
            ? $"“{entry.Title}”"
            : $"“{entry.Title}” ({entry.BeginDetail ?? entry.Detail})";
        string duration = string.IsNullOrWhiteSpace(entry.DurationText) ? "" : $" in {entry.DurationText}";

        switch (entry.Status)
        {
            case ActivityStatus.Running:
                return new($"{what} is still running.", null);
            case ActivityStatus.Success:
                return new($"{what} finished successfully{duration}.", null);
            case ActivityStatus.Canceled:
                return new($"{what} was cancelled{duration}. Anything Rain finished before you cancelled stays "
                           + "as it is; nothing after that point was changed.", null);
        }

        string error = entry.FullDetail ?? entry.Detail ?? string.Empty;
        string failed = IsFolderOpen(entry)
            ? $"Rain couldn't open the folder {entry.BeginDetail}."
            : $"Rain couldn't finish {what}.";
        foreach (Rule rule in Rules)
            if (rule.AnyOf.Any(needle => error.Contains(needle, StringComparison.OrdinalIgnoreCase)))
                return new($"{failed} {rule.Reason}", rule.Advice);
        return new($"{failed} The message below is exactly what the system reported.",
            "If it keeps happening, copy these details when asking for help.");
    }

    internal static bool IsFolderOpen(ActivityEntry entry) =>
        entry.Title == "Open folder" && !string.IsNullOrWhiteSpace(entry.BeginDetail);

    /// <summary>A copyable text version of an activity (for "Copy details" and exports).</summary>
    public static string Describe(ActivityEntry entry)
    {
        var e = Explain(entry);
        var lines = new List<string>
        {
            $"{entry.Title} — {entry.StatusText}",
            e.Summary,
        };
        if (e.Advice is not null) lines.Add($"What to try: {e.Advice}");
        lines.Add($"Started: {entry.StartedAt:yyyy-MM-dd HH:mm:ss}");
        if (entry.FinishedAt is { } finished) lines.Add($"Finished: {finished:yyyy-MM-dd HH:mm:ss}");
        if (!string.IsNullOrWhiteSpace(entry.DurationText)) lines.Add($"Took: {entry.DurationText}");
        if (!string.IsNullOrWhiteSpace(entry.BeginDetail))
            lines.Add($"{(IsFolderOpen(entry) ? "Folder" : "Items")}: {entry.BeginDetail}");
        string message = entry.FullDetail ?? (entry.IsFailed ? entry.Detail : "");
        if (!string.IsNullOrWhiteSpace(message)) lines.Add($"System message: {message}");
        return string.Join(Environment.NewLine, lines);
    }
}
