using System.IO;
using Microsoft.VisualBasic.FileIO;

namespace RainExplorer.Services;

/// <summary>Outcome of a mutating file operation, distinguishing a user cancellation (e.g.
/// clicking "No" on the Windows confirm dialog) from a genuine success or failure — so the
/// activity center never reports "done" for something that didn't actually happen.</summary>
public enum OpOutcome { Ok, Canceled, Failed }

public readonly record struct OpResult(OpOutcome Outcome, string? Error)
{
    public bool Ok => Outcome == OpOutcome.Ok;
    public bool Canceled => Outcome == OpOutcome.Canceled;
    public static readonly OpResult Success = new(OpOutcome.Ok, null);
    public static readonly OpResult Cancelled = new(OpOutcome.Canceled, null);
    public static OpResult Fail(string? error) => new(OpOutcome.Failed, error);
}

/// <summary>
/// Mutating file operations. These intentionally delegate to the Windows shell
/// engine via <see cref="Microsoft.VisualBasic.FileIO.FileSystem"/>, which gives
/// us native progress dialogs, the standard overwrite/conflict prompts, undo
/// support, and — critically — Recycle Bin deletes instead of permanent ones.
///
/// Every method returns null on success or an error message on failure. User
/// cancellation (clicking Cancel in a Windows dialog) is treated as success.
/// </summary>
public sealed class FileOperationsService
{
    /// <summary>Send paths to the Recycle Bin (never a permanent delete). Shows the OS confirm + progress.
    /// Uses ThrowException so cancelling the confirm dialog is reported as a real cancellation
    /// (rather than being silently treated as success).</summary>
    public OpResult Delete(IEnumerable<string> paths)
    {
        try
        {
            foreach (string p in paths)
            {
                if (Directory.Exists(p))
                    FileSystem.DeleteDirectory(p, UIOption.AllDialogs, RecycleOption.SendToRecycleBin,
                        UICancelOption.ThrowException);
                else if (File.Exists(p))
                    FileSystem.DeleteFile(p, UIOption.AllDialogs, RecycleOption.SendToRecycleBin,
                        UICancelOption.ThrowException);
            }
            return OpResult.Success;
        }
        catch (OperationCanceledException) { return OpResult.Cancelled; }
        catch (Exception ex) { return OpResult.Fail(ex.Message); }
    }

    /// <summary>Permanently delete paths (bypasses the Recycle Bin — not undoable). Shows the OS confirm + progress.
    /// Cancelling the confirm dialog is reported as a cancellation, not a success.</summary>
    public OpResult DeletePermanent(IEnumerable<string> paths)
    {
        try
        {
            foreach (string p in paths)
            {
                if (Directory.Exists(p))
                    FileSystem.DeleteDirectory(p, UIOption.AllDialogs, RecycleOption.DeletePermanently,
                        UICancelOption.ThrowException);
                else if (File.Exists(p))
                    FileSystem.DeleteFile(p, UIOption.AllDialogs, RecycleOption.DeletePermanently,
                        UICancelOption.ThrowException);
            }
            return OpResult.Success;
        }
        catch (OperationCanceledException) { return OpResult.Cancelled; }
        catch (Exception ex) { return OpResult.Fail(ex.Message); }
    }

    public string? CopyInto(IEnumerable<string> sources, string destDir) => Transfer(sources, destDir, move: false);
    public string? MoveInto(IEnumerable<string> sources, string destDir) => Transfer(sources, destDir, move: true);

    private static string? Transfer(IEnumerable<string> sources, string destDir, bool move)
    {
        try
        {
            foreach (string src in sources)
            {
                string name = Path.GetFileName(src.TrimEnd(Path.DirectorySeparatorChar));
                string dest = Path.Combine(destDir, name);

                // Don't copy/move a folder into itself or its own subtree.
                if (Directory.Exists(src) &&
                    (string.Equals(src, dest, StringComparison.OrdinalIgnoreCase) ||
                     IsSubPath(src, destDir)))
                    return "Can't move a folder into itself.";

                if (Directory.Exists(src))
                {
                    if (move) FileSystem.MoveDirectory(src, dest, UIOption.AllDialogs);
                    else FileSystem.CopyDirectory(src, dest, UIOption.AllDialogs);
                }
                else if (File.Exists(src))
                {
                    if (move) FileSystem.MoveFile(src, dest, UIOption.AllDialogs, UICancelOption.DoNothing);
                    else FileSystem.CopyFile(src, dest, UIOption.AllDialogs, UICancelOption.DoNothing);
                }
            }
            return null;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Rename within the same folder. Refuses invalid names and existing targets (no overwrite).</summary>
    public string? Rename(string path, string newName)
    {
        newName = newName.Trim();
        if (string.IsNullOrEmpty(newName)) return "Name can't be empty.";
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "Name contains invalid characters.";

        string? dir = Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar));
        if (dir is null) return "Can't rename this item.";
        string target = Path.Combine(dir, newName);

        if (string.Equals(path.TrimEnd(Path.DirectorySeparatorChar), target,
                StringComparison.OrdinalIgnoreCase))
            return null; // unchanged

        if (File.Exists(target) || Directory.Exists(target))
            return $"\"{newName}\" already exists here.";

        try
        {
            if (Directory.Exists(path)) Directory.Move(path, target);
            else File.Move(path, target);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Create a folder, auto-uniquifying the name. Returns (error, createdPath).</summary>
    public (string? error, string? created) CreateFolder(string parentDir, string desiredName)
    {
        desiredName = desiredName.Trim();
        if (string.IsNullOrEmpty(desiredName)) desiredName = "New folder";
        if (desiredName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return ("Name contains invalid characters.", null);

        try
        {
            string target = Path.Combine(parentDir, desiredName);
            int n = 2;
            while (Directory.Exists(target) || File.Exists(target))
                target = Path.Combine(parentDir, $"{desiredName} ({n++})");

            Directory.CreateDirectory(target);
            return (null, target);
        }
        catch (Exception ex) { return (ex.Message, null); }
    }

    /// <summary>Create an empty file, auto-uniquifying the name. Returns (error, createdPath).</summary>
    public (string? error, string? created) CreateFile(string parentDir, string desiredName)
    {
        desiredName = desiredName.Trim();
        if (string.IsNullOrEmpty(desiredName)) desiredName = "New file";
        if (desiredName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return ("Name contains invalid characters.", null);

        try
        {
            string ext = Path.GetExtension(desiredName);
            string stem = Path.GetFileNameWithoutExtension(desiredName);
            string target = Path.Combine(parentDir, desiredName);
            int n = 2;
            while (File.Exists(target) || Directory.Exists(target))
                target = Path.Combine(parentDir, $"{stem} ({n++}){ext}");

            using (File.Create(target)) { }
            return (null, target);
        }
        catch (Exception ex) { return (ex.Message, null); }
    }

    /// <summary>Return a non-colliding path by appending " (n)" before the extension.</summary>
    public static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path) ?? "";
        string stem = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        int n = 2;
        string candidate;
        do { candidate = Path.Combine(dir, $"{stem} ({n++}){ext}"); }
        while (File.Exists(candidate) || Directory.Exists(candidate));
        return candidate;
    }

    private static bool IsSubPath(string parent, string child)
    {
        string p = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
        string c = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar);
        return c.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
