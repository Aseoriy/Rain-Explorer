using System.Runtime.InteropServices;

namespace RainExplorer.Services;

/// <summary>Device + inode pair: the Linux equivalent of the Windows volume serial + file index.</summary>
public readonly record struct FileIdentity(ulong Device, ulong Inode);

/// <summary>Metadata from <c>statx(2)</c> that .NET does not expose directly.</summary>
public readonly record struct UnixStatInfo(
    ulong Device, ulong Inode, uint Uid, uint Gid, uint Mode, ulong LinkCount, DateTime? BirthTimeUtc,
    ulong Blocks = 0);

/// <summary>
/// Thin wrapper over glibc's <c>statx</c>. Its result struct has the same layout on
/// every architecture, which makes it safe to read by fixed offsets without a native
/// helper library. Used for undo identity checks, same-device detection (fast rename
/// vs copy+delete) and the Properties dialog.
/// </summary>
public static class UnixStat
{
    private const int AtFdCwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxBasicStats = 0x7ff;
    private const uint StatxBtime = 0x800;
    private const int StatxSize = 256;

    [DllImport("libc", SetLastError = true)]
    private static extern int rename(string oldpath, string newpath);

    /// <summary>
    /// rename(2) on the entry itself. .NET's File.Move/Directory.Move resolve symbolic links
    /// first (a link to a folder is "not a file", a dangling link "does not exist"), so links
    /// are moved with this instead. Same-filesystem only; throws IOException on failure.
    /// </summary>
    public static void RenameEntry(string from, string to)
    {
        if (rename(from, to) != 0)
            throw new IOException(Marshal.GetLastPInvokeErrorMessage());
    }

    [DllImport("libc", SetLastError = true)]
    private static extern unsafe int statx(int dirfd, string pathname, int flags, uint mask, byte* buffer);

    public static unsafe UnixStatInfo? TryGet(string path, bool followLinks = false)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        byte* buffer = stackalloc byte[StatxSize];
        try
        {
            int rc = statx(AtFdCwd, path, followLinks ? 0 : AtSymlinkNoFollow,
                StatxBasicStats | StatxBtime, buffer);
            if (rc != 0) return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }

        uint mask = *(uint*)(buffer + 0);
        uint nlink = *(uint*)(buffer + 16);
        uint uid = *(uint*)(buffer + 20);
        uint gid = *(uint*)(buffer + 24);
        ushort mode = *(ushort*)(buffer + 28);
        ulong ino = *(ulong*)(buffer + 32);
        ulong blocks = *(ulong*)(buffer + 48);   // 512-byte units
        long btimeSec = *(long*)(buffer + 80);
        uint btimeNsec = *(uint*)(buffer + 88);
        uint devMajor = *(uint*)(buffer + 136);
        uint devMinor = *(uint*)(buffer + 140);

        DateTime? birth = (mask & StatxBtime) != 0 && btimeSec > 0
            ? DateTime.UnixEpoch.AddSeconds(btimeSec).AddTicks(btimeNsec / 100)
            : null;
        ulong dev = ((ulong)devMajor << 32) | devMinor;
        return new UnixStatInfo(dev, ino, uid, gid, mode, nlink, birth, blocks);
    }

    public static FileIdentity? TryGetIdentity(string path) =>
        TryGet(path) is { } info ? new FileIdentity(info.Device, info.Inode) : null;

    public static bool Matches(string path, FileIdentity expected) =>
        TryGetIdentity(path) is { } current && current == expected;

    /// <summary>
    /// True when both paths live on the same filesystem, so a rename(2) can move between
    /// them. For a destination that does not exist yet, pass its parent folder.
    /// </summary>
    public static bool SameDevice(string left, string right)
    {
        var a = TryGet(left, followLinks: true);
        var b = TryGet(right, followLinks: true);
        return a is not null && b is not null && a.Value.Device == b.Value.Device;
    }
}
