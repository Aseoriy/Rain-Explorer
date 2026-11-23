using System.Globalization;
using System.Text.RegularExpressions;

namespace RainExplorer.Services;

public enum NameCase { Keep, Lower, Upper, Title }
public enum Numbering { None, Before, After }

/// <summary>What "Rename several files" should do. Steps apply in this order: find &amp; replace, case,
/// numbering, extension.</summary>
public sealed record BatchRenameOptions
{
    public string Find { get; init; } = "";
    public string Replace { get; init; } = "";
    public bool MatchCase { get; init; }
    public bool UseRegex { get; init; }
    /// <summary>Find &amp; replace and case also touch the extension (off: only the part before the last dot).</summary>
    public bool IncludeExtension { get; init; }
    public NameCase Case { get; init; } = NameCase.Keep;
    public Numbering Numbering { get; init; } = Numbering.None;
    public int StartAt { get; init; } = 1;
    public int Digits { get; init; } = 1;
    public string Separator { get; init; } = " ";
    /// <summary>New extension without the dot; null keeps it, "" removes it. Folders are never given one.</summary>
    public string? Extension { get; init; }
}

/// <summary>One line of the preview: the item, its new name, and why it can't be renamed (if so).</summary>
public sealed record RenamePlanItem(string Path, string OldName, string NewName, bool IsDirectory, string? Problem)
{
    public bool Changes => Problem is null && OldName != NewName;
}

/// <summary>
/// The logic behind "Rename several files": builds a preview and renames safely. Names are checked for
/// clashes with each other and with other items in the folder; renaming runs in two passes through
/// temporary names so swaps (a→b, b→a) work, and nothing is ever overwritten. Undo is one step.
/// </summary>
public static class BatchRenameService
{
    public static List<RenamePlanItem> Plan(IReadOnlyList<(string Path, bool IsDirectory)> items, BatchRenameOptions options)
    {
        Regex? regex = null;
        string? regexError = null;
        if (options.UseRegex && options.Find.Length > 0)
        {
            try
            {
                regex = new Regex(options.Find, options.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase,
                    TimeSpan.FromMilliseconds(200));
            }
            catch (ArgumentException ex) { regexError = "The pattern isn't valid: " + ex.Message; }
        }

        var plan = new List<RenamePlanItem>();
        int number = options.StartAt;
        foreach (var (path, isDir) in items)
        {
            string oldName = Path.GetFileName(path.TrimEnd('/'));
            if (regexError is not null) { plan.Add(new(path, oldName, oldName, isDir, regexError)); continue; }
            string newName;
            try { newName = Apply(oldName, isDir, options, regex, number); }
            catch (RegexMatchTimeoutException) { plan.Add(new(path, oldName, oldName, isDir, "The pattern took too long.")); continue; }
            number++;
            plan.Add(new(path, oldName, newName, isDir, Check(newName)));
        }

        // Clashes: two items getting the same name, or a name already used by something not being renamed.
        var result = new List<RenamePlanItem>(plan.Count);
        foreach (var item in plan)
        {
            if (item.Problem is not null || item.OldName == item.NewName) { result.Add(item); continue; }
            string folder = Path.GetDirectoryName(item.Path.TrimEnd('/')) ?? "";
            var sameFolder = plan.Where(p => (Path.GetDirectoryName(p.Path.TrimEnd('/')) ?? "") == folder).ToList();
            bool duplicate = sameFolder.Count(p => p.Problem is null && p.NewName == item.NewName) > 1;
            string target = Path.Combine(folder, item.NewName);
            bool beingRenamed = sameFolder.Any(p => p.Path.TrimEnd('/') == target);
            if (duplicate) result.Add(item with { Problem = "Two items would get this name." });
            else if (!beingRenamed && Exists(target)) result.Add(item with { Problem = $"“{item.NewName}” already exists here." });
            else result.Add(item);
        }
        return result;
    }

    internal static string Apply(string name, bool isDir, BatchRenameOptions o, Regex? regex, int number)
    {
        string stem = name, ext = "";
        if (!isDir && !o.IncludeExtension)
        {
            int dot = name.LastIndexOf('.');
            if (dot > 0) { stem = name[..dot]; ext = name[dot..]; }
        }

        if (o.Find.Length > 0)
        {
            stem = regex is not null
                ? regex.Replace(stem, o.Replace)
                : ReplacePlain(stem, o.Find, o.Replace, o.MatchCase);
        }

        stem = o.Case switch
        {
            NameCase.Lower => stem.ToLower(CultureInfo.CurrentCulture),
            NameCase.Upper => stem.ToUpper(CultureInfo.CurrentCulture),
            NameCase.Title => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(stem.ToLower(CultureInfo.CurrentCulture)),
            _ => stem,
        };
        if (o.Case != NameCase.Keep && ext.Length > 0 && o.IncludeExtension == false)
            ext = o.Case == NameCase.Upper ? ext.ToUpper(CultureInfo.CurrentCulture) : ext.ToLower(CultureInfo.CurrentCulture);

        if (o.Numbering != Numbering.None)
        {
            string n = number.ToString(new string('0', Math.Clamp(o.Digits, 1, 9)), CultureInfo.InvariantCulture);
            stem = o.Numbering == Numbering.Before ? n + o.Separator + stem : stem + o.Separator + n;
        }

        if (!isDir && o.Extension is { } newExt)
        {
            if (o.IncludeExtension)
            {
                int dot = stem.LastIndexOf('.');
                if (dot > 0) stem = stem[..dot];
            }
            newExt = newExt.Trim().TrimStart('.');
            ext = newExt.Length == 0 ? "" : "." + newExt;
        }
        return (stem + ext).Trim();
    }

    private static string ReplacePlain(string text, string find, string replace, bool matchCase) =>
        matchCase ? text.Replace(find, replace, StringComparison.Ordinal)
                  : text.Replace(find, replace, StringComparison.CurrentCultureIgnoreCase);

    private static string? Check(string name)
    {
        if (name.Length == 0) return "The new name would be empty.";
        if (!PathUtil.IsValidFileName(name)) return "Names can't contain \"/\" or be \".\" or \"..\".";
        if (System.Text.Encoding.UTF8.GetByteCount(name) > 255) return "The new name is too long.";
        return null;
    }

    private static bool Exists(string path) =>
        File.Exists(path) || Directory.Exists(path) || FileOperationsService.IsSymlink(path);

    /// <summary>
    /// Rename every changing item (the plan must have no problems). Returns the (old, new) pairs that were
    /// renamed and an error if something went wrong; a failure part-way puts already-moved items back.
    /// </summary>
    public static (List<(string From, string To)> Done, string? Error) Execute(IEnumerable<RenamePlanItem> plan)
    {
        var changing = plan.Where(p => p.Changes).ToList();
        if (plan.Any(p => p.Problem is not null)) return ([], "Fix the names marked in red first.");
        return Move(changing.Select(p => (p.Path.TrimEnd('/'), Path.Combine(Path.GetDirectoryName(p.Path.TrimEnd('/'))!, p.NewName))).ToList());
    }

    /// <summary>Two-pass rename: everything to a temporary name, then to its final name.</summary>
    internal static (List<(string From, string To)> Done, string? Error) Move(List<(string From, string To)> moves)
    {
        var temps = new List<(string From, string Temp, string To)>();
        try
        {
            foreach (var (from, to) in moves)
            {
                string dir = Path.GetDirectoryName(from)!;
                string temp = Path.Combine(dir, $".rain-rename-{Guid.NewGuid():N}");
                FileOperationsService.MoveEntry(from, temp);
                temps.Add((from, temp, to));
            }
            var done = new List<(string From, string To)>();
            foreach (var (from, temp, to) in temps)
            {
                if (Exists(to)) throw new IOException($"“{Path.GetFileName(to)}” appeared while renaming.");
                FileOperationsService.MoveEntry(temp, to);
                done.Add((from, to));
            }
            return (done, null);
        }
        catch (Exception ex)
        {
            // Put everything still under a temporary name back where it was.
            foreach (var (from, temp, _) in temps)
            {
                try { if (Exists(temp) && !Exists(from)) FileOperationsService.MoveEntry(temp, from); } catch { }
            }
            var finished = temps.Where(t => Exists(t.To) && !Exists(t.Temp)).Select(t => (t.From, t.To)).ToList();
            return (finished, ex.Message);
        }
    }
}

/// <summary>Undo a batch rename in one step (and redo it again).</summary>
public sealed class BatchRenameAction : UndoAction
{
    private readonly List<(string Cur, string Home, FileIdentity? Identity)> _items;

    public BatchRenameAction(IEnumerable<(string Home, string Cur)> renamed) =>
        _items = renamed.Select(r => (r.Cur, r.Home, FileIdentityService.TryGet(r.Cur))).ToList();

    public override string Label => _items.Count == 1 ? "Rename" : $"Rename ({_items.Count} items)";

    public override (string?, UndoAction?) Invoke()
    {
        var curSet = _items.Select(i => i.Cur).ToHashSet(StringComparer.Ordinal);
        foreach (var (cur, home, identity) in _items)
        {
            if (!PathExists(cur)) return ("A renamed item no longer exists, so undo was skipped.", null);
            if (identity is null || !FileIdentityService.Matches(cur, identity.Value))
                return ("A renamed item changed, so undo was skipped.", null);
            if (PathExists(home) && !curSet.Contains(home))
                return ("Something now uses one of the original names, so undo was skipped.", null);
        }
        var (done, error) = BatchRenameService.Move(_items.Select(i => (i.Cur, i.Home)).ToList());
        return (error, done.Count > 0 ? new BatchRenameAction(done.Select(d => (d.To, d.From))) : null);
    }
}
