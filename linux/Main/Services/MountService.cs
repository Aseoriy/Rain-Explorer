using System.Text;

namespace RainExplorer.Services;

/// <summary>A mounted filesystem shown in the sidebar and on the Drives page.</summary>
public sealed record MountEntry(
    string MountPoint,
    string Device,
    string FileSystem,
    string Label,
    bool IsRoot,
    bool IsRemovable,
    bool IsNetwork)
{
    public string? UDisksPath { get; init; }
    public string? DrivePath { get; init; }
    public string Identity { get; init; } = "";
    public bool CanPowerOff { get; init; }
    public bool CanEject { get; init; }
    public bool IsMounted => MountPoint.Length > 0;
    public bool CanMount => UDisksPath is not null && !IsMounted;
    public bool CanUnmount => UDisksPath is not null && IsMounted && IsRemovable && !IsRoot;
    public bool CanSafelyRemove => UDisksPath is not null && IsRemovable && (CanPowerOff || CanEject);
    public bool HasAction => CanMount || CanUnmount || CanSafelyRemove;
    public string ActionText => CanMount ? "Mount" : CanSafelyRemove ? (CanEject ? "Eject" : "Safely remove") : "Unmount";
    public DriveAction DefaultAction => CanMount ? DriveAction.Mount : CanSafelyRemove ? DriveAction.SafelyRemove : DriveAction.Unmount;
}

/// <summary>
/// Lists the mounts a person would think of as "drives": the root filesystem, a
/// separate /home, removable media under /media or /run/media, and anything mounted
/// under /mnt (including network shares). Pseudo filesystems, snaps, the EFI
/// partition and per-session FUSE helpers are hidden.
/// </summary>
public static class MountService
{
    private static IReadOnlyList<MountEntry> _devices = [];

    internal static bool UpdateDevices(IReadOnlyList<MountEntry> devices)
    {
        if (_devices.SequenceEqual(devices)) return false;
        _devices = devices;
        return true;
    }

    /// <summary>Mounted filesystems plus available removable volumes discovered through UDisks2.</summary>
    public static IReadOnlyList<MountEntry> GetAvailableDrives()
    {
        var devices = _devices;
        var result = new List<MountEntry>();
        foreach (var mounted in GetDrives())
        {
            var device = devices.FirstOrDefault(d => PathUtil.Same(d.Device, mounted.Device));
            result.Add(device is null ? mounted : device with { MountPoint = mounted.MountPoint, IsRoot = mounted.IsRoot });
        }
        foreach (var device in devices)
            if (!result.Any(d => PathUtil.Same(d.Device, device.Device))) result.Add(device);
        return result.OrderBy(d => d.IsRoot ? 0 : d.MountPoint == "/home" ? 1 : d.IsNetwork ? 3 : 2)
            .ThenBy(d => d.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static readonly HashSet<string> NetworkTypes = new(StringComparer.Ordinal)
    {
        "cifs", "smb3", "smbfs", "nfs", "nfs4", "sshfs", "fuse.sshfs", "davfs", "fuse.rclone",
    };

    private static readonly HashSet<string> PseudoTypes = new(StringComparer.Ordinal)
    {
        "proc", "sysfs", "tmpfs", "devtmpfs", "devpts", "cgroup", "cgroup2", "securityfs",
        "pstore", "bpf", "debugfs", "tracefs", "mqueue", "hugetlbfs", "fusectl", "configfs",
        "efivarfs", "binfmt_misc", "autofs", "squashfs", "nsfs", "ramfs", "rpc_pipefs",
        "overlay", "fuse.portal", "fuse.gvfsd-fuse", "fuse.kio-fuse", "tracefs", "selinuxfs",
    };

    public static IReadOnlyList<MountEntry> GetDrives()
    {
        var labels = ReadLabels();
        var byMountPoint = new Dictionary<string, MountEntry>(StringComparer.Ordinal);
        string user = Environment.UserName;

        foreach (var (device, mountPoint, type) in ReadMounts())
        {
            if (PseudoTypes.Contains(type)) continue;
            if (type.StartsWith("fuse.", StringComparison.Ordinal) && !NetworkTypes.Contains(type)
                && !mountPoint.StartsWith("/media/", StringComparison.Ordinal)
                && !mountPoint.StartsWith("/run/media/", StringComparison.Ordinal)
                && !mountPoint.StartsWith("/mnt/", StringComparison.Ordinal))
                continue;

            bool isRoot = mountPoint == "/";
            bool removable = mountPoint.StartsWith("/media/", StringComparison.Ordinal)
                             || mountPoint.StartsWith($"/run/media/{user}/", StringComparison.Ordinal);
            bool network = NetworkTypes.Contains(type) || device.StartsWith("//", StringComparison.Ordinal);
            bool interesting = isRoot || removable || mountPoint == "/home"
                               || mountPoint.StartsWith("/mnt/", StringComparison.Ordinal)
                               || mountPoint.StartsWith("/srv/", StringComparison.Ordinal)
                               || mountPoint.StartsWith("/data", StringComparison.Ordinal);
            if (!interesting) continue;

            string label = isRoot ? "File System"
                : labels.TryGetValue(device, out string? l) ? l
                : PathUtil.DisplayName(mountPoint);
            byMountPoint[mountPoint] = new MountEntry(mountPoint, device, type, label, isRoot, removable, network);
        }

        return byMountPoint.Values
            .OrderBy(m => m.IsRoot ? 0 : m.MountPoint == "/home" ? 1 : m.IsNetwork ? 3 : 2)
            .ThenBy(m => m.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>The longest mount point containing <paramref name="path"/> ("/" as a fallback).</summary>
    public static string GetMountPoint(string path)
    {
        string best = "/";
        foreach (var (_, mountPoint, _) in ReadMounts())
        {
            if (mountPoint.Length > best.Length && PathUtil.IsSameOrInside(mountPoint, path))
                best = mountPoint;
        }
        return best;
    }

    private static IEnumerable<(string Device, string MountPoint, string Type)> ReadMounts()
    {
        string[] lines;
        try { lines = File.ReadAllLines("/proc/self/mounts"); }
        catch { yield break; }

        foreach (string line in lines)
        {
            string[] parts = line.Split(' ');
            if (parts.Length < 3) continue;
            yield return (Unescape(parts[0]), Unescape(parts[1]), parts[2]);
        }
    }

    /// <summary>/proc/mounts escapes space, tab, newline and backslash as octal (\040 …).</summary>
    private static string Unescape(string value)
    {
        if (!value.Contains('\\')) return value;
        var bytes = new List<byte>(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && IsOctal(value, i + 1))
            {
                bytes.Add(Convert.ToByte(value.Substring(i + 1, 3), 8));
                i += 3;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(value[i].ToString()));
            }
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static bool IsOctal(string s, int start)
    {
        if (start + 3 > s.Length) return false;
        for (int i = start; i < start + 3; i++)
            if (s[i] < '0' || s[i] > '7') return false;
        return true;
    }

    /// <summary>udev encodes unsafe label characters as \xHH (for example \x20 for a space).</summary>
    private static string UnescapeUdev(string value)
    {
        if (!value.Contains("\\x", StringComparison.Ordinal)) return value;
        var bytes = new List<byte>(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 3 < value.Length && value[i + 1] == 'x'
                && byte.TryParse(value.AsSpan(i + 2, 2), System.Globalization.NumberStyles.HexNumber, null, out byte b))
            {
                bytes.Add(b);
                i += 3;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(value[i].ToString()));
            }
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>Map block devices to filesystem labels via the udev /dev/disk/by-label links.</summary>
    private static Dictionary<string, string> ReadLabels()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            const string dir = "/dev/disk/by-label";
            if (!Directory.Exists(dir)) return map;
            foreach (string link in Directory.EnumerateFileSystemEntries(dir))
            {
                try
                {
                    var info = new FileInfo(link);
                    if (info.LinkTarget is not { } target) continue;
                    string device = Path.GetFullPath(Path.Combine(dir, target));
                    map[device] = UnescapeUdev(Path.GetFileName(link));
                }
                catch { }
            }
        }
        catch { }
        return map;
    }
}
