using System.Diagnostics;
using RainExplorer.Services;
using Tmds.DBus.Protocol;
using Xunit;

namespace RainExplorer.Tests;

internal sealed class PrivateSessionBus : IDisposable
{
    private readonly Process _process;
    public string Address { get; }
    public PrivateSessionBus(string? configuration = null)
    {
        var start = new ProcessStartInfo("dbus-daemon") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(configuration is null ? "--session" : "--config-file=" + configuration);
        start.ArgumentList.Add("--nofork"); start.ArgumentList.Add("--print-address=1");
        _process = Process.Start(start)!;
        Address = _process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("The private D-Bus session did not start.");
    }
    public void Dispose()
    {
        if (!_process.HasExited) { _process.Kill(entireProcessTree: true); _process.WaitForExit(); }
        _process.Dispose();
    }
}

public sealed class FileManagerTests
{
    internal static MessageBuffer RevealMessage(DBusConnection connection, string method, string[] uris, string? signature = "ass")
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: FileManagerService.BusName, path: FileManagerService.ObjectPath,
            @interface: FileManagerService.BusName, member: method, signature: signature);
        if (signature == "ass") { writer.WriteArray(uris); writer.WriteString("test-startup-id"); }
        return writer.CreateMessage();
    }

    private static MessageBuffer IntrospectMessage(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: FileManagerService.BusName, path: FileManagerService.ObjectPath,
            @interface: "org.freedesktop.DBus.Introspectable", member: "Introspect");
        return writer.CreateMessage();
    }

    private static async Task WaitForOwner(FileManagerService service)
    {
        var until = DateTime.UtcNow.AddSeconds(3);
        while (!service.IsOwner && DateTime.UtcNow < until) await Task.Delay(10);
        Assert.True(service.IsOwner);
    }

    [Fact]
    public void RevealUrisPreserveLiteralNamesCaseAndLinksAndGroupByParent()
    {
        string root = TestEnvironment.NewFolder("reveal");
        string[] names = ["Case.txt", "case.txt", "space # percent% & é.txt", ".hidden", "%23.txt", "back\\slash.txt", "trailing space "];
        var paths = names.Select(name => Path.Combine(root, name)).ToList();
        foreach (string path in paths) File.WriteAllText(path, "source");
        string folder = Path.Combine(root, "Folder"); Directory.CreateDirectory(folder); paths.Add(folder);
        string link = Path.Combine(root, "broken-link"); File.CreateSymbolicLink(link, "missing"); paths.Add(link);
        string other = TestEnvironment.NewFolder("reveal-other");
        string second = Path.Combine(other, "other.txt"); File.WriteAllText(second, "second"); paths.Add(second);
        var uris = paths.Select(path => PathUtil.ToFileUri(path)).Append(PathUtil.ToFileUri(paths[0])).ToArray();
        var request = FileManagerRequest.Resolve(FileManagerAction.ShowItems, uris, "startup");
        Assert.Equal(paths, request.Paths);
        var targets = request.Targets(); Assert.Equal(2, targets.Count);
        Assert.Equal(root, targets[0].Folder);
        Assert.Equal(paths.Take(paths.Count - 1), targets[0].Selection);
        Assert.Equal(new[] { second }, targets[1].Selection);
        Assert.Equal("startup", request.StartupId);
        var directory = FileManagerRequest.Resolve(FileManagerAction.ShowFolders, [PathUtil.ToFileUri(folder)], "");
        Assert.Equal(folder, Assert.Single(directory.Targets()).Folder);
        Assert.Empty(directory.Targets()[0].Selection);
    }

    [Theory]
    [InlineData("https://example.com/a")]
    [InlineData("smb://server/share/a")]
    [InlineData("file://remote-host/etc/passwd")]
    [InlineData("file:///etc/passwd#fragment")]
    [InlineData("file:///etc/passwd?query")]
    [InlineData("relative.txt")]
    [InlineData("file:///tmp/%00bad")]
    [InlineData("file:///tmp/%gg")]
    [InlineData("file:///tmp/%")]
    [InlineData("/etc/passwd")]
    public void UnsupportedUrisFailInsteadOfOpeningSomethingElse(string uri) =>
        Assert.ThrowsAny<ArgumentException>(() => FileManagerRequest.Resolve(FileManagerAction.ShowItems, [uri], ""));

    [Fact]
    public void MissingItemsFailAndRootCanBeRevealedWithoutSelectingItself()
    {
        string folder = TestEnvironment.NewFolder("reveal-missing");
        Assert.Throws<FileNotFoundException>(() => FileManagerRequest.Resolve(FileManagerAction.ShowItems,
            [PathUtil.ToFileUri(Path.Combine(folder, "missing"))], ""));
        var root = FileManagerRequest.Resolve(FileManagerAction.ShowItems, ["file:///"], "");
        Assert.Equal("/", Assert.Single(root.Targets()).Folder); Assert.Empty(root.Targets()[0].Selection);
        File.WriteAllText(Path.Combine(folder, "file.txt"), "file");
        Assert.Throws<ArgumentException>(() => FileManagerRequest.Resolve(FileManagerAction.ShowFolders,
            [PathUtil.ToFileUri(Path.Combine(folder, "file.txt"))], ""));
    }

    [Fact]
    public void LocalhostAndCommandLinePathsPreserveLiteralNamesAndRejectRemoteUris()
    {
        string path = Path.Combine(TestEnvironment.NewFolder("cli-reveal"), "# %23 é\\.txt");
        File.WriteAllText(path, "literal");
        string uri = PathUtil.ToFileUri(path);
        string localhost = "file://localhost" + uri[7..];
        Assert.Equal(path, Assert.Single(FileManagerRequest.Resolve(FileManagerAction.ShowItems, [localhost], "").Paths));
        Assert.Equal(path, SingleInstance.ToLocalPath(path));
        Assert.Equal(path, SingleInstance.ToLocalPath(uri));
        Assert.Equal(path, SingleInstance.ToLocalPath(localhost));
        foreach (string invalid in new[] { "smb://server/file", "https://example.com/file", "file://server/etc/passwd", "file:///etc/passwd#fragment", "file:///tmp/%gg", "--select" })
            Assert.Null(SingleInstance.ToLocalPath(invalid));
    }

    [Fact]
    public void ActivationIsOptInAtomicAndDoesNotReplaceAnotherUserService()
    {
        Assert.False(new RainExplorer.Models.AppSettings().HandleShowInFolder);
        string path = FileManagerActivation.ServicePath;
        FileManagerActivation.Disable();
        FileManagerActivation.Enable();
        Assert.StartsWith(FileManagerActivation.Marker, File.ReadAllText(path));
        Assert.Contains("--file-manager-service", File.ReadAllText(path));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        FileManagerActivation.Disable(); Assert.False(File.Exists(path));
        File.WriteAllText(path, "[D-BUS Service]\nName=org.freedesktop.FileManager1\nExec=other-app\n");
        Assert.Throws<IOException>(FileManagerActivation.Enable);
        FileManagerActivation.Disable(); Assert.Contains("other-app", File.ReadAllText(path));
        File.Delete(path);
        string outside = Path.Combine(TestEnvironment.NewFolder("activation-link"), "service");
        File.WriteAllText(outside, FileManagerActivation.Marker);
        File.CreateSymbolicLink(path, outside);
        Assert.Throws<IOException>(FileManagerActivation.Enable); FileManagerActivation.Disable();
        Assert.NotNull(new FileInfo(path).LinkTarget); Assert.Equal(FileManagerActivation.Marker, File.ReadAllText(outside));
        File.Delete(path);
        string contents = FileManagerActivation.CreateContents(["/tmp/Rain folder/quo\"te\\RainExplorer"]);
        Assert.Contains("\"/tmp/Rain folder/quo\\\"te\\\\RainExplorer\"", contents);
        Assert.Throws<ArgumentException>(() => FileManagerActivation.CreateContents(["/tmp/bad\npath"]));
    }

    [Fact]
    public async Task RealBusDispatchesAllMethodsAndRejectsInvalidCalls()
    {
        using var bus = new PrivateSessionBus();
        var requests = new List<FileManagerRequest>();
        using var service = new FileManagerService(request => { requests.Add(request); return Task.CompletedTask; });
        await service.StartAsync(bus.Address); await WaitForOwner(service);
        using var client = new DBusConnection(bus.Address); await client.ConnectAsync();
        string folder = TestEnvironment.NewFolder("bus-reveal");
        string path = Path.Combine(folder, "file #é.txt"); File.WriteAllText(path, "literal");
        foreach (var action in Enum.GetValues<FileManagerAction>())
        {
            string target = action == FileManagerAction.ShowFolders ? folder : path;
            await client.CallMethodAsync(RevealMessage(client, action.ToString(), [PathUtil.ToFileUri(target)]));
            Assert.Equal(action, requests.Last().Action);
            Assert.Equal(target, Assert.Single(requests.Last().Paths));
        }
        Assert.Equal(3, requests.Count);
        var badUri = await Assert.ThrowsAnyAsync<DBusErrorReplyException>(() =>
            client.CallMethodAsync(RevealMessage(client, "ShowItems", ["smb://host/share"])));
        Assert.Equal("org.freedesktop.DBus.Error.InvalidArgs", badUri.ErrorName);
        var badSignature = await Assert.ThrowsAnyAsync<DBusErrorReplyException>(() =>
            client.CallMethodAsync(RevealMessage(client, "ShowItems", [], null)));
        Assert.Equal("org.freedesktop.DBus.Error.InvalidArgs", badSignature.ErrorName);
        var missing = await Assert.ThrowsAnyAsync<DBusErrorReplyException>(() =>
            client.CallMethodAsync(RevealMessage(client, "ShowItems", [PathUtil.ToFileUri(path + ".missing")])));
        Assert.Equal("org.freedesktop.DBus.Error.Failed", missing.ErrorName);
        Assert.Equal(3, requests.Count);
        string xml = await client.CallMethodAsync(IntrospectMessage(client), (message, _) => message.GetBodyReader().ReadString());
        Assert.Contains("ShowItems", xml); Assert.Contains("ShowItemProperties", xml);
        service.Dispose(); service.Dispose();
    }

    [Fact]
    public async Task ExistingBusOwnerIsPreservedAndRainAcquiresNameWhenItCloses()
    {
        using var bus = new PrivateSessionBus();
        using var other = new DBusConnection(bus.Address); await other.ConnectAsync();
        await other.RequestNameAsync(FileManagerService.BusName, RequestNameOptions.None);
        using var service = new FileManagerService(_ => Task.CompletedTask);
        await service.StartAsync(bus.Address);
        Assert.False(service.IsOwner);
        await other.ReleaseNameAsync(FileManagerService.BusName);
        await WaitForOwner(service);
        service.Dispose();
        Assert.True(await other.TryRequestNameAsync(FileManagerService.BusName, RequestNameOptions.None));
    }
}
