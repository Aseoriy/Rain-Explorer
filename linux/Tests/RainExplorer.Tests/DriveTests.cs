using System.Text;
using RainExplorer.Models;
using RainExplorer.Services;
using Tmds.DBus.Protocol;
using Xunit;

namespace RainExplorer.Tests;

internal sealed class FakeUDisks : IUDisksClient
{
    internal const string Root = "/org/freedesktop/UDisks2";
    internal const string DrivePath = Root + "/drives/test_usb";
    internal const string BlockPath = Root + "/block_devices/test_usb1";
    public List<UDisksObject> Objects { get; } = [];
    public List<(string Path, string Interface, string Method)> Calls { get; } = [];
    public string MountPoint { get; set; } = TestEnvironment.NewFolder("usb-mount-é space");
    public string? FailPath { get; set; }
    public string? FailMethod { get; set; }
    public Action? BeforeFinalRead { get; set; }
    private int _reads;

    public FakeUDisks(bool mounted = false, bool powerOff = true, bool eject = false)
    {
        Objects.Add(Object(DrivePath, "Drive", new()
        {
            ["Id"] = "test-usb-serial", ["Removable"] = true, ["ConnectionBus"] = "usb",
            ["CanPowerOff"] = powerOff, ["Ejectable"] = eject, ["Model"] = "Test USB",
        }));
        AddVolume(BlockPath, mounted ? MountPoint : "", "My USB");
    }

    internal static UDisksObject Object(string path, string @interface, Dictionary<string, VariantValue> properties) =>
        new(path, new(StringComparer.Ordinal) { ["org.freedesktop.UDisks2." + @interface] = properties });

    internal static VariantValue Bytes(string text) => new Tmds.DBus.Protocol.Array<byte>(Encoding.UTF8.GetBytes(text + "\0"));
    internal static VariantValue Mounts(string path) => new Tmds.DBus.Protocol.Array<Tmds.DBus.Protocol.Array<byte>>(
        path.Length == 0 ? [] : [new(Encoding.UTF8.GetBytes(path + "\0"))]);

    public void AddVolume(string path, string mount, string label, bool system = false, bool ignore = false, string? drive = null)
    {
        var block = Object(path, "Block", new()
        {
            ["PreferredDevice"] = Bytes("/dev/" + path.Split('/').Last()),
            ["Drive"] = new ObjectPath(drive ?? DrivePath), ["IdUsage"] = "filesystem", ["IdType"] = "ext4",
            ["IdLabel"] = label, ["IdUUID"] = path.Split('/').Last(), ["HintIgnore"] = ignore, ["HintSystem"] = system,
        });
        block.Interfaces["org.freedesktop.UDisks2.Filesystem"] = new() { ["MountPoints"] = Mounts(mount) };
        Objects.Add(block);
    }

    public Task<IReadOnlyList<UDisksObject>> GetObjectsAsync()
    {
        if (++_reads == 2) BeforeFinalRead?.Invoke();
        return Task.FromResult<IReadOnlyList<UDisksObject>>(Objects.ToArray());
    }

    public Task<string?> CallAsync(string path, string @interface, string method)
    {
        Calls.Add((path, @interface, method));
        if (path == FailPath && method == FailMethod)
            throw new DBusErrorReplyException("org.freedesktop.UDisks2.Error.DeviceBusy", "busy");
        if (method is "Mount" or "Unmount")
            Objects.First(o => o.Path == path).Interfaces["org.freedesktop.UDisks2.Filesystem"]["MountPoints"] =
                Mounts(method == "Mount" ? MountPoint : "");
        else Objects.RemoveAll(o => o.Path != Root);
        return Task.FromResult<string?>(method == "Mount" ? MountPoint : null);
    }
    public MountEntry Entry => DriveActions.ToEntries(Objects).First(e => e.UDisksPath == BlockPath);
    public void Dispose() { }
}

public sealed class DriveTests
{
    private static async Task<DriveActionResult> Run(FakeUDisks fake, DriveAction action, MountEntry? entry = null)
    {
        var original = DriveActions.CreateClient;
        DriveActions.CreateClient = () => fake;
        try { return await DriveActions.RunAsync(entry ?? fake.Entry, action); }
        finally { DriveActions.CreateClient = original; }
    }

    [Fact]
    public void DiscoveryIncludesUnmountedUsbDisksButHidesIgnoredSystemAndNonFilesystems()
    {
        var fake = new FakeUDisks();
        // Many USB SSDs report Removable=false; ConnectionBus still identifies them correctly.
        fake.Objects[0].Interfaces["org.freedesktop.UDisks2.Drive"]["Removable"] = false;
        fake.AddVolume(FakeUDisks.Root + "/block_devices/efi", "", "EFI", system: true);
        fake.AddVolume(FakeUDisks.Root + "/block_devices/ignored", "", "Ignored", ignore: true);
        fake.AddVolume(FakeUDisks.Root + "/block_devices/root", "/", "Root");
        fake.AddVolume(FakeUDisks.Root + "/block_devices/swap", "", "Swap");
        fake.Objects[^1].Interfaces["org.freedesktop.UDisks2.Block"]["IdUsage"] = "other";
        var entry = Assert.Single(DriveActions.ToEntries(fake.Objects));
        Assert.Equal("My USB", entry.Label);
        Assert.True(entry.CanMount);
        Assert.False(entry.CanUnmount);
        var node = SidebarNode.DriveNode(entry);
        Assert.False(node.CanExpand);
        Assert.Empty(node.Children);
        Assert.Equal("Mount", node.DriveActionText);
    }

    [Fact]
    public async Task MountReturnsExactPathAndMountedOpenDoesNotMountAgain()
    {
        var fake = new FakeUDisks();
        var result = await Run(fake, DriveAction.Mount);
        Assert.Null(result.Error);
        Assert.Equal(fake.MountPoint, result.MountPoint);
        Assert.Equal("Mount", Assert.Single(fake.Calls).Method);
        Assert.True(fake.Entry.CanUnmount);
        result = await Run(fake, DriveAction.Mount);
        Assert.Equal(fake.MountPoint, result.MountPoint);
        Assert.Single(fake.Calls);
    }

    [Fact]
    public async Task UnmountLeavesDeviceDiscoverableForRemount()
    {
        var fake = new FakeUDisks(mounted: true);
        Assert.Null((await Run(fake, DriveAction.Unmount)).Error);
        Assert.Equal("Unmount", Assert.Single(fake.Calls).Method);
        Assert.True(fake.Entry.CanMount);
        Assert.Null((await Run(fake, DriveAction.Mount)).Error);
        Assert.Equal(fake.MountPoint, fake.Entry.MountPoint);
    }

    [Fact]
    public async Task SafeRemovalUnmountsEverySiblingBeforePowerOff()
    {
        var fake = new FakeUDisks(mounted: true);
        fake.AddVolume(FakeUDisks.Root + "/block_devices/test_usb2", "/media/test/second", "Second");
        Assert.Null((await Run(fake, DriveAction.SafelyRemove)).Error);
        Assert.Equal(new[] { "Unmount", "Unmount", "PowerOff" }, fake.Calls.Select(c => c.Method));
        Assert.Equal(FakeUDisks.DrivePath, fake.Calls[^1].Path);
    }

    [Fact]
    public async Task BusySiblingStopsRemovalAndReportsUsefulError()
    {
        var fake = new FakeUDisks(mounted: true);
        string sibling = FakeUDisks.Root + "/block_devices/test_usb2";
        fake.AddVolume(sibling, "/media/test/second", "Second");
        fake.FailPath = sibling; fake.FailMethod = "Unmount";
        var result = await Run(fake, DriveAction.SafelyRemove);
        Assert.Contains("in use", result.Error);
        Assert.DoesNotContain(fake.Calls, c => c.Method is "PowerOff" or "Eject");
        Assert.NotEmpty(fake.Objects.First(o => o.Path == sibling).MountPoints);
    }

    [Fact]
    public async Task RemountedSiblingPreventsPowerOff()
    {
        var fake = new FakeUDisks(mounted: true);
        fake.BeforeFinalRead = () => fake.Objects.First(o => o.Path == FakeUDisks.BlockPath)
            .Interfaces["org.freedesktop.UDisks2.Filesystem"]["MountPoints"] = FakeUDisks.Mounts(fake.MountPoint);
        Assert.Contains("still in use", (await Run(fake, DriveAction.SafelyRemove)).Error);
        Assert.DoesNotContain(fake.Calls, c => c.Method == "PowerOff");
    }

    [Theory]
    [InlineData("/", false)]
    [InlineData("/home", false)]
    [InlineData("/boot/efi", false)]
    [InlineData("", true)]
    public async Task SystemSiblingPreventsAnyRemovalCall(string mountPoint, bool system)
    {
        var fake = new FakeUDisks(mounted: true);
        fake.AddVolume(FakeUDisks.Root + "/block_devices/system", mountPoint, "System", system: system);
        Assert.Contains("system volume", (await Run(fake, DriveAction.SafelyRemove)).Error);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task EjectableMediaUsesEjectAndUnsupportedDevicesKeepUnmount()
    {
        var fake = new FakeUDisks(mounted: true, powerOff: false, eject: true);
        Assert.Null((await Run(fake, DriveAction.SafelyRemove)).Error);
        Assert.Equal(new[] { "Unmount", "Eject" }, fake.Calls.Select(c => c.Method));
        fake = new FakeUDisks(mounted: true, powerOff: false);
        Assert.Equal("Unmount", fake.Entry.ActionText);
        Assert.NotNull((await Run(fake, DriveAction.SafelyRemove)).Error);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task RemovedOrReplacedDeviceCannotBeOperatedUsingOldRow()
    {
        var fake = new FakeUDisks(mounted: true);
        var old = fake.Entry;
        fake.Objects[0].Interfaces["org.freedesktop.UDisks2.Drive"]["Id"] = "replacement-serial";
        Assert.NotNull((await Run(fake, DriveAction.SafelyRemove, old)).Error);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task SharedEnclosureIncludesOtherPhysicalDrives()
    {
        var fake = new FakeUDisks(mounted: true);
        fake.Objects[0].Interfaces["org.freedesktop.UDisks2.Drive"]["SiblingId"] = "reader";
        string other = FakeUDisks.Root + "/drives/other";
        fake.Objects.Add(FakeUDisks.Object(other, "Drive", new() { ["SiblingId"] = "reader" }));
        fake.AddVolume(FakeUDisks.Root + "/block_devices/other1", "/media/test/other", "Other", drive: other);
        Assert.Null((await Run(fake, DriveAction.SafelyRemove)).Error);
        Assert.Equal(new[] { "Unmount", "Unmount", "PowerOff" }, fake.Calls.Select(c => c.Method));
    }

    [Fact]
    public async Task UDisksProtocolRoundTripOnPrivateBus()
    {
        using var bus = new PrivateSessionBus();
        using var service = new DBusConnection(bus.Address);
        await service.ConnectAsync();
        var fake = new FakeUDisks();
        service.AddMethodHandler(new UDisksHandler(fake));
        await service.RequestNameAsync("org.freedesktop.UDisks2", RequestNameOptions.None);
        using var client = new UDisksClient(bus.Address);
        var objects = await client.GetObjectsAsync();
        var entry = Assert.Single(DriveActions.ToEntries(objects));
        Assert.Equal("My USB", entry.Label);
        Assert.True(entry.CanMount);
        Assert.Equal(fake.MountPoint, await client.CallAsync(entry.UDisksPath!, "Filesystem", "Mount"));
        Assert.Equal(fake.MountPoint, Assert.Single(DriveActions.ToEntries(await client.GetObjectsAsync())).MountPoint);
        await client.CallAsync(entry.UDisksPath!, "Filesystem", "Unmount");
        Assert.True(Assert.Single(DriveActions.ToEntries(await client.GetObjectsAsync())).CanMount);
        await client.CallAsync(FakeUDisks.DrivePath, "Drive", "PowerOff");
        Assert.Empty(await client.GetObjectsAsync());
    }

    private sealed class UDisksHandler(FakeUDisks fake) : IPathMethodHandler
    {
        public string Path => FakeUDisks.Root;
        public bool HandlesChildPaths => true;
        public ValueTask HandleMethodAsync(MethodContext context)
        {
            string method = context.Request.MemberAsString!;
            if (method == "GetManagedObjects")
            {
                using var writer = context.CreateReplyWriter("a{oa{sa{sv}}}");
                var objects = writer.WriteDictionaryStart();
                foreach (var obj in fake.Objects)
                {
                    writer.WriteDictionaryEntryStart(); writer.WriteObjectPath(new ObjectPath(obj.Path));
                    var interfaces = writer.WriteDictionaryStart();
                    foreach (var pair in obj.Interfaces)
                    {
                        writer.WriteDictionaryEntryStart(); writer.WriteString(pair.Key); writer.WriteDictionary(pair.Value);
                    }
                    writer.WriteDictionaryEnd(interfaces);
                }
                writer.WriteDictionaryEnd(objects);
                context.Reply(writer.CreateMessage());
            }
            else
            {
                Assert.Equal("a{sv}", context.Request.SignatureAsString);
                Assert.Empty(context.Request.GetBodyReader().ReadDictionaryOfStringToVariantValue());
                string? mount = fake.CallAsync(context.Request.PathAsString!, "", method).GetAwaiter().GetResult();
                using var writer = context.CreateReplyWriter(method == "Mount" ? "s" : null);
                if (method == "Mount") writer.WriteString(mount!);
                context.Reply(writer.CreateMessage());
            }
            return default;
        }
    }
}
