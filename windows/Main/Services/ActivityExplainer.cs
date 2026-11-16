using RainExplorer.Models;

namespace RainExplorer.Services;

/// <summary>A plain-language account of an activity: what happened and, for failures, what to try.</summary>
public sealed record ActivityExplanation(string Summary, string? Advice);

/// <summary>
/// Turns an activity and the system's error text into a readable explanation for the "View details" window.
/// When Windows can name the program holding a file, Rain's own message already says which one
/// (see <see cref="FileLockDiagnostics"/>); otherwise the wording says what Windows refused rather than
/// guessing at a culprit. Port of the Linux ActivityExplainer with Windows error texts.
/// </summary>
public static class ActivityExplainer
{
    private sealed record Rule(string[] AnyOf, string Reason, string Advice);

    private static readonly Rule[] Rules =
    [
        new(["is in use by", "being used by another process", "or something inside it is open in another program",
             "is open in another program", "locked a portion of the file"],
            "Another program is using the item, so Windows wouldn't let it change right now.",
            "Close the program named above (or the one that has the item open: an editor, a terminal whose "
            + "folder is inside it, a media player), then try again."),
        new(["Access to the path", "is denied", "Access is denied", "UnauthorizedAccess", "don't have permission"],
            "Windows refused access because your account doesn't have permission for an item involved.",
            "Check the item's Security tab in Properties, or work in a folder you own. System folders, other "
            + "users' files and some program folders are protected on purpose; Controlled folder access in "
            + "Windows Security can also block apps it doesn't know."),
        new(["write protected", "write-protected", "read-only"],
            "The drive or item is read-only, so nothing on it can be changed.",
            "If it's an SD card or USB stick, check its lock switch. For a file, clear Read-only in its Properties."),
        new(["not enough space", "There is not enough space", "disk is full", "quota"],
            "The destination drive is full.",
            "Free up space there (empty the Recycle Bin too), then try again."),
        new(["path is too long", "PathTooLong", "file name is too long", "filename or extension is too long"],
            "A name or path is longer than Windows or the program allows.",
            "Shorten the file or folder name, or use a shallower folder."),
        new(["cloud file provider", "cloud operation", "cloud provider"],
            "The cloud sync app (OneDrive, Google Drive…) that owns this folder didn't respond.",
            "Make sure the sync app is running and signed in, wait for it to finish syncing, then try again."),
        // Before the generic "not found" rule: "The network path was not found" is a network problem.
        new(["network path was not found", "network name is no longer available", "semaphore timeout",
             "timed out", "took too long", "network location", "specified network"],
            "A network location didn't answer in time.",
            "Check the connection to the server or share (and that you're signed in to it), then try again."),
        new(["Could not find", "cannot find the", "does not exist", "was not found", "no longer exists"],
            "An item was gone by the time Rain reached it. It may have been moved, renamed or deleted elsewhere.",
            "Refresh the folder and check where the item is now."),
        new(["already exists", "file exists", "directory is not empty"],
            "Something with the same name was already there.",
            "Choose a different name, or move the existing item first."),
        new(["I/O device error", "cyclic redundancy check", "data error", "device is not ready"],
            "The drive reported a read or write error. This can mean a loose cable, a drive that disconnected, "
            + "or a failing disk.",
            "Reconnect the drive and try again. If it keeps happening, back up what you can from it."),
        new(["syntax is incorrect", "invalid characters", "Illegal characters", "The parameter is incorrect"],
            "The name contains something Windows doesn't accept (characters such as \\ / : * ? \" < > |, or a "
            + "reserved name like CON or NUL).",
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
        return new($"{failed} The message below is exactly what Windows reported.",
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
