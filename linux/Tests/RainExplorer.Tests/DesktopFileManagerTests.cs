using RainExplorer.Services;
using Tmds.DBus.Protocol;
using Xunit;

namespace RainExplorer.Tests;

public sealed class DesktopFileManagerTests
{
    internal static string FakeMimeCommand(string folder, string query = "rain-explorer.desktop", int exitCode = 0)
    {
        Directory.CreateDirectory(folder);
        string executable = Path.Combine(folder, "xdg-mime");
        File.WriteAllText(executable, $"""
            #!/bin/sh
            printf '%s\n' "$*" >> "{folder}/calls"
            if [ "$1" = default ]; then sleep 0.2; fi
            if [ "$1" = query ]; then printf '%s\n' '{query}'; fi
            exit {exitCode}
            """);
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return executable;
    }

    [Fact]
    public async Task FolderRegistrationIsVerifiedAndDoesNotChangeFileAssociations()
    {
        string folder = TestEnvironment.NewFolder("default-helper with spaces");
        await DesktopFileManagerService.SetDefaultAsync(FakeMimeCommand(folder));
        Assert.Equal(new[] { "default rain-explorer.desktop inode/directory", "query default inode/directory" },
            File.ReadAllLines(Path.Combine(folder, "calls")));
        Assert.False(DesktopFileManagerService.HasDesktopEntry([folder]));
        Directory.CreateDirectory(Path.Combine(folder, "applications"));
        File.WriteAllText(Path.Combine(folder, "applications", DesktopFileManagerService.DesktopId), "[Desktop Entry]\nType=Application\n");
        Assert.True(DesktopFileManagerService.HasDesktopEntry([folder]));
    }

    [Fact]
    public async Task FailedAndUnconfirmedRegistrationsReportErrors()
    {
        string folder = TestEnvironment.NewFolder("default-helper-fail");
        await Assert.ThrowsAsync<IOException>(() => DesktopFileManagerService.SetDefaultAsync(FakeMimeCommand(folder, exitCode: 1)));
        Assert.Single(File.ReadAllLines(Path.Combine(folder, "calls")));
        var error = await Assert.ThrowsAsync<IOException>(() => DesktopFileManagerService.SetDefaultAsync(FakeMimeCommand(folder, "org.kde.dolphin.desktop")));
        Assert.Contains("did not confirm", error.Message);
    }

    [Theory]
    [InlineData("/usr/bin/dolphin", "/usr/bin/dolphin", "--daemon", true)]
    [InlineData("/usr/bin/dolphin", "dolphin", "--daemon", true)]
    [InlineData("/usr/bin/dolphin", "dolphin", "/tmp", false)]
    [InlineData("/usr/bin/dolphin", "dolphin", "--new-window", false)]
    [InlineData("/usr/bin/other", "dolphin", "--daemon", false)]
    public void OnlyBackgroundDolphinMayBeAskedToExit(string executable, string command, string argument, bool expected)
    {
        Assert.Equal(expected, DesktopFileManagerService.IsBackgroundDolphin(executable, [command, argument]));
        Assert.False(DesktopFileManagerService.IsBackgroundDolphin(executable, [command, "--daemon", "/tmp"]));
        Assert.False(DesktopFileManagerService.IsBackgroundDolphin(executable, [command]));
    }

    [Fact]
    public async Task DesktopSetupPreservesUnrecognizedRunningHandlers()
    {
        using var bus = new PrivateSessionBus();
        using var owner = new DBusConnection(bus.Address); await owner.ConnectAsync();
        await owner.RequestNameAsync(FileManagerService.BusName, RequestNameOptions.None);
        await DesktopFileManagerService.ReloadActivationAsync(bus.Address);
        await DesktopFileManagerService.ReleaseBackgroundDolphinAsync(bus.Address);
        using var rain = new FileManagerService(_ => Task.CompletedTask);
        await rain.StartAsync(bus.Address);
        Assert.False(rain.IsOwner);
    }

    [Fact]
    public async Task ReloadPicksUpNewServiceDirectoryAndRestoresPreviousActivationWhenRemoved()
    {
        string root = TestEnvironment.NewFolder("activation-reload");
        string user = Path.Combine(root, "user-services"), system = Path.Combine(root, "system-services");
        Directory.CreateDirectory(system);
        string service = FileManagerService.BusName + ".service";
        File.WriteAllText(Path.Combine(system, service), $"[D-BUS Service]\nName={FileManagerService.BusName}\nExec=/bin/false\n");
        string config = Path.Combine(root, "bus.conf");
        File.WriteAllText(config, $"""
            <busconfig><type>session</type><listen>unix:tmpdir=/tmp</listen><auth>EXTERNAL</auth>
            <servicedir>{user}</servicedir><servicedir>{system}</servicedir>
            <policy context="default"><allow send_destination="*"/><allow receive_sender="*"/><allow own="*"/></policy>
            </busconfig>
            """);
        using var bus = new PrivateSessionBus(config);
        using var client = new DBusConnection(bus.Address); await client.ConnectAsync();
        async Task Activate()
        {
            MessageBuffer message;
            using (var writer = client.GetMessageWriter())
            {
                writer.WriteMethodCallHeader(destination: "org.freedesktop.DBus", path: "/org/freedesktop/DBus",
                    @interface: "org.freedesktop.DBus", member: "StartServiceByName", signature: "su");
                writer.WriteString(FileManagerService.BusName); writer.WriteUInt32(0);
                message = writer.CreateMessage();
            }
            await Assert.ThrowsAnyAsync<DBusErrorReplyException>(() => client.CallMethodAsync(message));
        }
        await Activate();
        Directory.CreateDirectory(user);
        string marker = Path.Combine(root, "new-service-used");
        string command = Path.Combine(root, "activation.sh");
        File.WriteAllText(command, $"#!/bin/sh\ntouch '{marker}'\nexit 1\n");
        File.WriteAllText(Path.Combine(user, service), $"[D-BUS Service]\nName={FileManagerService.BusName}\nExec=/bin/sh {command}\n");
        await DesktopFileManagerService.ReloadActivationAsync(bus.Address);
        await Activate(); Assert.True(File.Exists(marker));
        File.Delete(Path.Combine(user, service)); File.Delete(marker);
        await DesktopFileManagerService.ReloadActivationAsync(bus.Address);
        await Activate(); Assert.False(File.Exists(marker));
    }
}
