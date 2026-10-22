using Tmds.DBus.Protocol;

namespace RainExplorer.Services;

public enum DriveAction { Mount, Unmount, SafelyRemove }
public sealed record DriveActionResult(string? MountPoint = null, string? Error = null);

/// <summary>Removable-drive actions through the same UDisks2 service used by KDE and GNOME.</summary>
public static class DriveActions
{
    internal static Func<IUDisksClient> CreateClient { get; set; } = () => new UDisksClient();
    private static readonly SemaphoreSlim Operations = new(1);

    internal static async Task<IReadOnlyList<MountEntry>> DiscoverAsync()
    {
        using var client = CreateClient();
        return ToEntries(await client.GetObjectsAsync().ConfigureAwait(false));
    }

    internal static IReadOnlyList<MountEntry> ToEntries(IReadOnlyList<UDisksObject> objects)
    {
        var result = new List<MountEntry>();
        foreach (var block in objects.Where(o => o.Has("Filesystem") && o.Has("Block")))
        {
            var drive = FindDrive(objects, block);
            if (drive is null || !Removable(drive) || block.Flag("Block", "HintIgnore")
                || block.Flag("Block", "HintSystem") || block.Text("Block", "IdUsage") != "filesystem") continue;
            string label = block.Text("Block", "HintName");
            if (label.Length == 0) label = block.Text("Block", "IdLabel");
            if (label.Length == 0) label = drive.Text("Drive", "Model").Trim();
            if (label.Length == 0) label = Path.GetFileName(block.Device);
            string[] mounts = block.MountPoints;
            if (mounts.Any(IsSystemMount)) continue;
            result.Add(new MountEntry(mounts.FirstOrDefault() ?? "", block.Device, block.Text("Block", "IdType"),
                label, false, true, false)
            {
                UDisksPath = block.Path, DrivePath = drive.Path,
                Identity = Identity(block, drive),
                CanPowerOff = drive.Flag("Drive", "CanPowerOff"), CanEject = drive.Flag("Drive", "Ejectable"),
            });
        }
        return result;
    }

    private static string Identity(UDisksObject block, UDisksObject drive) =>
        drive.Text("Drive", "Id") + "|" + block.Text("Block", "IdUUID") + "|" + block.Device;

    private static bool Removable(UDisksObject drive) => drive.Flag("Drive", "Removable")
        || drive.Flag("Drive", "MediaRemovable") || drive.Text("Drive", "ConnectionBus") == "usb";

    private static UDisksObject? FindDrive(IReadOnlyList<UDisksObject> objects, UDisksObject block)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (seen.Add(block.Path))
        {
            var drive = objects.FirstOrDefault(o => o.Path == block.Text("Block", "Drive") && o.Has("Drive"));
            if (drive is not null) return drive;
            var backing = objects.FirstOrDefault(o => o.Path == block.Text("Block", "CryptoBackingDevice"));
            if (backing is null) break;
            block = backing;
        }
        return null;
    }

    internal static bool IsSystemMount(string path) => path == "/" || path == "/home"
        || new[] { "/boot", "/usr", "/var", "/etc", "/run", "/opt" }
            .Any(root => PathUtil.IsSameOrInside(root, path) && !path.StartsWith("/run/media/", StringComparison.Ordinal));

    public static async Task<DriveActionResult> RunAsync(MountEntry entry, DriveAction action)
    {
        // Serialize across every window, including mount versus remove on sibling partitions.
        if (!await Operations.WaitAsync(0).ConfigureAwait(false))
            return new(Error: "Another drive operation is still running.");
        try
        {
            using var client = CreateClient();
            var objects = await client.GetObjectsAsync().ConfigureAwait(false);
            var current = ToEntries(objects).FirstOrDefault(d => d.UDisksPath == entry.UDisksPath && d.Identity == entry.Identity);
            if (entry.UDisksPath is null || current is null) return new(Error: "That removable drive is no longer available. Refresh and try again.");
            var block = objects.First(o => o.Path == current.UDisksPath);
            var drive = FindDrive(objects, block)!;
            if (action == DriveAction.Mount)
            {
                if (current.IsMounted) return new(current.MountPoint);
                return new(await client.CallAsync(block.Path, "Filesystem", "Mount").ConfigureAwait(false));
            }
            if (action == DriveAction.Unmount)
            {
                if (!current.IsMounted) return new(Error: "That drive is no longer mounted.");
                await client.CallAsync(block.Path, "Filesystem", "Unmount").ConfigureAwait(false);
                return new();
            }
            if (!current.CanSafelyRemove) return new(Error: "This device does not support safe removal. Use Unmount instead.");

            // PowerOff can affect other drives sharing a USB enclosure/card reader.
            string siblings = current.CanEject ? "" : drive.Text("Drive", "SiblingId");
            var affected = objects.Where(o => o.Has("Block") && FindDrive(objects, o) is { } owner
                && (owner.Path == drive.Path || (siblings.Length > 0 && owner.Text("Drive", "SiblingId") == siblings))).ToList();
            if (affected.Any(o => o.Flag("Block", "HintSystem") || o.MountPoints.Any(IsSystemMount)))
                return new(Error: "This device contains a system volume and cannot be safely removed here.");
            foreach (var volume in affected.Where(o => o.Has("Filesystem") && o.MountPoints.Length > 0))
                await client.CallAsync(volume.Path, "Filesystem", "Unmount").ConfigureAwait(false);
            // A failed or newly mounted sibling must prevent power-off/ejection.
            var latest = await client.GetObjectsAsync().ConfigureAwait(false);
            if (!ToEntries(latest).Any(d => d.UDisksPath == current.UDisksPath && d.Identity == current.Identity))
                return new(Error: "The device disconnected or changed before it could be safely removed. Refresh and try again.");
            if (latest.Any(o => o.Has("Block") && FindDrive(latest, o) is { } owner
                && (owner.Path == drive.Path || (siblings.Length > 0 && owner.Text("Drive", "SiblingId") == siblings))
                && (o.Flag("Block", "HintSystem") || o.MountPoints.Length > 0)))
                return new(Error: "The device is still in use. Close files on it and try again.");
            await client.CallAsync(drive.Path, "Drive", current.CanEject ? "Eject" : "PowerOff").ConfigureAwait(false);
            return new();
        }
        catch (DBusErrorReplyException ex)
        {
            string error = ex.ErrorName.Split('.').Last() switch
            {
                "DeviceBusy" => "The device is in use. Close files and stop transfers on it, then try again.",
                "NotAuthorized" or "NotAuthorizedCanObtain" or "NotAuthorizedDismissed" => "Drive access wasn't authorized. Try again and approve the desktop permission prompt.",
                "ServiceUnknown" or "NameHasNoOwner" => "UDisks2 is unavailable. Install or start udisks2 to manage removable drives.",
                _ => ex.Message,
            };
            return new(Error: error);
        }
        catch (Exception ex) { return new(Error: ex.Message); }
        finally { Operations.Release(); }
    }
}
