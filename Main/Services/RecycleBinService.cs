using System.IO;
using System.Security.Principal;
using System.Text;

namespace RainExplorer.Services;

/// <summary>
/// Restores items from the Recycle Bin by reading the hidden <c>$I…</c> metadata
/// files (original path + deletion time) next to each recycled <c>$R…</c> payload,
/// then moving the payload back. This is deterministic and locale-independent —
/// unlike invoking the shell's localized "Restore" verb.
/// </summary>
public static class RecycleBinService
{
    /// <summary>Restore the most recently deleted item whose original location matches
    /// <paramref name="originalPath"/>. Returns (error, finalRestoredPath).</summary>
    public static (string? error, string? restoredPath) Restore(string originalPath)
    {
        try
        {
            string? sid = WindowsIdentity.GetCurrent().User?.Value;
            if (sid is null) return ("Couldn't determine the current user.", null);

            string root = Path.GetPathRoot(originalPath) ?? "C:\\";
            string binDir = Path.Combine(root, "$Recycle.Bin", sid);
            if (!Directory.Exists(binDir)) return ("Item not found in the Recycle Bin.", null);

            string? bestInfo = null, bestPayload = null;
            long bestTime = -1;
            foreach (string info in EnumerateInfoFiles(binDir))
            {
                var (origPath, delTime) = ParseInfo(info);
                if (origPath is null) continue;
                if (!string.Equals(origPath, originalPath, StringComparison.OrdinalIgnoreCase)) continue;

                string payload = PayloadFor(info);
                if (!Exists(payload)) continue;
                if (delTime >= bestTime) { bestTime = delTime; bestInfo = info; bestPayload = payload; }
            }

            if (bestPayload is null) return ("Item not found in the Recycle Bin.", null);

            string target = originalPath;
            if (Exists(target)) target = FileOperationsService.UniquePath(target);
            string? parent = Path.GetDirectoryName(target);
            if (parent is not null) Directory.CreateDirectory(parent);

            if (Directory.Exists(bestPayload)) Directory.Move(bestPayload, target);
            else File.Move(bestPayload, target);
            try { if (bestInfo is not null) File.Delete(bestInfo); } catch { }

            return (null, target);
        }
        catch (Exception ex) { return (ex.Message, null); }
    }

    private static IEnumerable<string> EnumerateInfoFiles(string binDir)
    {
        try { return Directory.EnumerateFiles(binDir, "$I*"); }
        catch { return Array.Empty<string>(); }
    }

    // The $R payload sits beside the $I metadata, same suffix: $I123.txt -> $R123.txt
    private static string PayloadFor(string infoFile)
    {
        string name = Path.GetFileName(infoFile);
        string rName = "$R" + (name.Length > 2 ? name[2..] : "");
        return Path.Combine(Path.GetDirectoryName(infoFile) ?? "", rName);
    }

    // $I layout: [int64 version][int64 size][int64 deletion FILETIME][path].
    // v2 (Win10): int32 char-count then UTF-16 path. v1 (Vista–8): fixed 520-byte path.
    private static (string? origPath, long delTime) ParseInfo(string infoFile)
    {
        try
        {
            byte[] b = File.ReadAllBytes(infoFile);
            if (b.Length < 24) return (null, 0);
            long version = BitConverter.ToInt64(b, 0);
            long delTime = BitConverter.ToInt64(b, 16);

            string path;
            if (version >= 2)
            {
                if (b.Length < 28) return (null, 0);
                int chars = BitConverter.ToInt32(b, 24);              // includes the null terminator
                int bytes = Math.Max(0, (chars - 1) * 2);
                bytes = Math.Min(bytes, b.Length - 28);
                path = Encoding.Unicode.GetString(b, 28, bytes);
            }
            else
            {
                int avail = Math.Min(520, b.Length - 24);
                path = Encoding.Unicode.GetString(b, 24, avail);
            }

            int nul = path.IndexOf('\0');
            if (nul >= 0) path = path[..nul];
            return (string.IsNullOrEmpty(path) ? null : path, delTime);
        }
        catch { return (null, 0); }
    }

    private static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);
}
