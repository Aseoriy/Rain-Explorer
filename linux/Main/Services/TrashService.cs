using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace RainExplorer.Services;

/// <summary>
/// Moves items to the desktop trash and restores them, following the FreeDesktop.org
/// Trash specification (the same trash Dolphin, Nautilus and the KDE/GNOME trash
/// views use). Items on the home filesystem go to ~/.local/share/Trash; items on
/// other drives go to that drive's own .Trash/$uid or .Trash-$uid folder so a delete
/// never turns into a slow cross-device copy.
/// </summary>
public static class TrashService
{
    [DllImport("libc")]
    private static extern uint getuid();

    private static readonly Lazy<uint> Uid = new(() =>
    {
        try { return getuid(); } catch { return 0; }
    });

    public static string HomeTrash => Path.Combine(XdgDirs.DataHome, "Trash");

    /// <summary>Move one item to the trash. Returns an error message, or null on success.</summary>
    public static string? Trash(string path)
    {
        try
        {
            string full = PathUtil.Normalize(path);
            if (!File.Exists(full) && !Directory.Exists(full) && new FileInfo(full).LinkTarget is null)
                return $"“{PathUtil.DisplayName(full)}” no longer exists.";

            var (trashDir, topDir) = ResolveTrashFor(full);
            if (trashDir is null)
                return $"There is no trash available on the drive that holds “{PathUtil.DisplayName(full)}”.";

            string filesDir = Path.Combine(trashDir, "files");
            string infoDir = Path.Combine(trashDir, "info");
            Directory.CreateDirectory(filesDir);
            Directory.CreateDirectory(infoDir);

            string recordedPath = topDir is null ? full : Path.GetRelativePath(topDir, full);
            string baseName = PathUtil.DisplayName(full);
            string info = BuildInfo(recordedPath, DateTime.Now);

            // Claim a unique name by creating the .trashinfo file exclusively first, as
            // the spec requires, so two programs trashing the same name cannot collide.
            for (int attempt = 1; attempt < 10_000; attempt++)
            {
                string name = attempt == 1 ? baseName : UniqueName(baseName, attempt);
                string infoPath = Path.Combine(infoDir, name + ".trashinfo");
                string target = Path.Combine(filesDir, name);
                if (Exists(target)) continue;

                FileStream stream;
                try { stream = new FileStream(infoPath, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
                catch (IOException) when (File.Exists(infoPath)) { continue; }

                using (stream)
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    writer.Write(info);

                try
                {
                    FileOperationsService.MoveEntry(full, target);
                    return null;
                }
                catch (Exception ex)
                {
                    try { File.Delete(infoPath); } catch { }
                    return ex.Message;
                }
            }
            return "Couldn't find a free name in the trash.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Restore the most recently trashed item whose original location matches
    /// <paramref name="originalPath"/>. Returns (error, finalRestoredPath).</summary>
    public static (string? error, string? restoredPath) Restore(string originalPath)
    {
        try
        {
            string full = PathUtil.Normalize(originalPath);
            string? bestInfo = null, bestPayload = null;
            DateTime bestTime = DateTime.MinValue;

            foreach (var (trashDir, topDir) in CandidateTrashesFor(full))
            {
                string infoDir = Path.Combine(trashDir, "info");
                if (!Directory.Exists(infoDir)) continue;
                foreach (string infoFile in SafeEnumerate(infoDir, "*.trashinfo"))
                {
                    var (recorded, deleted) = ParseInfo(infoFile);
                    if (recorded is null) continue;
                    string absolute = recorded.StartsWith('/') || topDir is null
                        ? recorded
                        : Path.Combine(topDir, recorded);
                    if (!PathUtil.Same(absolute, full)) continue;

                    string payload = Path.Combine(trashDir, "files",
                        Path.GetFileNameWithoutExtension(infoFile));
                    if (!Exists(payload)) continue;
                    if (deleted >= bestTime)
                    {
                        bestTime = deleted;
                        bestInfo = infoFile;
                        bestPayload = payload;
                    }
                }
            }

            if (bestPayload is null) return ("Item not found in the trash.", null);

            string target = Exists(full) ? FileOperationsService.UniquePath(full) : full;
            string? parent = Path.GetDirectoryName(target);
            if (parent is not null) Directory.CreateDirectory(parent);

            FileOperationsService.MoveEntry(bestPayload, target);
            try { if (bestInfo is not null) File.Delete(bestInfo); } catch { }
            return (null, target);
        }
        catch (Exception ex)
        {
            return (ex.Message, null);
        }
    }

    // ---- Trash folder view (home trash) ----------------------------------------

    /// <summary>Original location recorded for an item in the home trash's files folder.</summary>
    public static string? OriginalPathOf(string payloadPath)
    {
        string infoFile = InfoFileFor(payloadPath);
        var (recorded, _) = ParseInfo(infoFile);
        return recorded;
    }

    /// <summary>Restore one item shown in the trash view to where it was deleted from.
    /// Returns (error, restored path).</summary>
    public static (string? error, string? restoredPath) RestoreEntry(string payloadPath)
    {
        try
        {
            string? original = OriginalPathOf(payloadPath);
            if (original is null) return ("Rain couldn't find where this item came from.", null);
            string target = Exists(original) ? FileOperationsService.UniquePath(original) : original;
            string? parent = Path.GetDirectoryName(target);
            if (parent is not null) Directory.CreateDirectory(parent);
            FileOperationsService.MoveEntry(payloadPath, target);
            try { File.Delete(InfoFileFor(payloadPath)); } catch { }
            return (null, target);
        }
        catch (Exception ex)
        {
            return (ex.Message, null);
        }
    }

    /// <summary>Permanently remove one item from the trash view, with its metadata.</summary>
    public static string? DeleteEntry(string payloadPath)
    {
        try
        {
            if (Directory.Exists(payloadPath) && new DirectoryInfo(payloadPath).LinkTarget is null)
                Directory.Delete(payloadPath, recursive: true);
            else if (Exists(payloadPath))
                File.Delete(payloadPath);   // unlink(2): removes a link, never its target
            try { File.Delete(InfoFileFor(payloadPath)); } catch { }
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Permanently delete everything in the home trash. Returns an error or null.</summary>
    public static string? EmptyHomeTrash()
    {
        string files = Path.Combine(HomeTrash, "files");
        if (!Directory.Exists(files)) return null;
        string? firstError = null;
        foreach (string entry in Directory.EnumerateFileSystemEntries(files).ToList())
        {
            string? error = DeleteEntry(entry);
            firstError ??= error;
        }
        try
        {
            string sizes = Path.Combine(HomeTrash, "directorysizes");
            if (File.Exists(sizes)) File.Delete(sizes);
        }
        catch { }
        return firstError;
    }

    public static bool HomeTrashIsEmpty()
    {
        string files = Path.Combine(HomeTrash, "files");
        try { return !Directory.Exists(files) || !Directory.EnumerateFileSystemEntries(files).Any(); }
        catch { return true; }
    }

    private static string InfoFileFor(string payloadPath)
    {
        string trashDir = Path.GetDirectoryName(Path.GetDirectoryName(payloadPath) ?? "") ?? HomeTrash;
        return Path.Combine(trashDir, "info", Path.GetFileName(payloadPath) + ".trashinfo");
    }

    /// <summary>Where an item at <paramref name="path"/> should be trashed: (trash dir, top dir
    /// for relative info paths or null for the home trash).</summary>
    private static (string? TrashDir, string? TopDir) ResolveTrashFor(string path)
    {
        string home = HomeTrash;
        string homeProbe = Directory.Exists(home) ? home : XdgDirs.DataHome;
        Directory.CreateDirectory(XdgDirs.DataHome);
        string parent = Path.GetDirectoryName(path) ?? "/";
        if (UnixStat.SameDevice(homeProbe, parent)) return (home, null);

        string topDir = MountService.GetMountPoint(path);
        string shared = Path.Combine(topDir, ".Trash");
        if (IsUsableSharedTrash(shared))
        {
            string perUser = Path.Combine(shared, Uid.Value.ToString(CultureInfo.InvariantCulture));
            try
            {
                Directory.CreateDirectory(perUser);
                return (perUser, topDir);
            }
            catch { /* fall through to .Trash-$uid */ }
        }

        string own = Path.Combine(topDir, ".Trash-" + Uid.Value.ToString(CultureInfo.InvariantCulture));
        try
        {
            if (!Directory.Exists(own))
            {
                Directory.CreateDirectory(own);
                File.SetUnixFileMode(own, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            return (own, topDir);
        }
        catch
        {
            return (null, null);
        }
    }

    private static IEnumerable<(string TrashDir, string? TopDir)> CandidateTrashesFor(string originalPath)
    {
        yield return (HomeTrash, null);
        string topDir = MountService.GetMountPoint(originalPath);
        if (topDir == "/") yield break;
        string uid = Uid.Value.ToString(CultureInfo.InvariantCulture);
        yield return (Path.Combine(topDir, ".Trash", uid), topDir);
        yield return (Path.Combine(topDir, ".Trash-" + uid), topDir);
    }

    /// <summary>The spec only allows $topdir/.Trash when it is a real directory with the
    /// sticky bit set (so users cannot remove each other's entries).</summary>
    private static bool IsUsableSharedTrash(string dir)
    {
        try
        {
            var info = new DirectoryInfo(dir);
            if (!info.Exists || info.LinkTarget is not null) return false;
            return (File.GetUnixFileMode(dir) & UnixFileMode.StickyBit) != 0;
        }
        catch { return false; }
    }

    private static string BuildInfo(string recordedPath, DateTime deletedLocal) =>
        "[Trash Info]\n" +
        $"Path={EncodePath(recordedPath)}\n" +
        $"DeletionDate={deletedLocal.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)}\n";

    private static (string? Path, DateTime Deleted) ParseInfo(string infoFile)
    {
        try
        {
            string? path = null;
            DateTime deleted = DateTime.MinValue;
            foreach (string line in File.ReadLines(infoFile))
            {
                if (line.StartsWith("Path=", StringComparison.Ordinal))
                    path = Uri.UnescapeDataString(line[5..]);
                else if (line.StartsWith("DeletionDate=", StringComparison.Ordinal))
                    DateTime.TryParse(line[13..], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out deleted);
            }
            return (string.IsNullOrEmpty(path) ? null : path, deleted);
        }
        catch { return (null, DateTime.MinValue); }
    }

    /// <summary>Percent-encode as the spec asks (RFC 2396), keeping "/" readable.</summary>
    private static string EncodePath(string path) =>
        string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private static string UniqueName(string name, int n)
    {
        string ext = Path.GetExtension(name);
        string stem = Path.GetFileNameWithoutExtension(name);
        if (string.IsNullOrEmpty(stem)) { stem = name; ext = string.Empty; }   // dotfiles
        return $"{stem}.{n}{ext}";
    }

    private static IEnumerable<string> SafeEnumerate(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    private static bool Exists(string path) =>
        File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null;
}
