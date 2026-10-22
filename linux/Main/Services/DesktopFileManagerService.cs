using System.Diagnostics;
using Tmds.DBus.Protocol;

namespace RainExplorer.Services;

/// <summary>Desktop registration requested explicitly from Settings.</summary>
internal static class DesktopFileManagerService
{
    internal const string DesktopId = "rain-explorer.desktop";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task SetDefaultAsync()
    {
        string directories = Environment.GetEnvironmentVariable("XDG_DATA_DIRS") is { Length: > 0 } value
            ? value : "/usr/local/share:/usr/share";
        if (!HasDesktopEntry(new[] { XdgDirs.DataHome }.Concat(directories.Split(':'))))
            throw new IOException("Install Rain first so the desktop can find its app-menu entry, then try again.");
        string executable = ProcessLauncher.FindExecutable("xdg-mime")
            ?? throw new IOException("Your desktop needs xdg-utils to choose a default file manager.");
        await SetDefaultAsync(executable);
    }

    internal static bool HasDesktopEntry(IEnumerable<string> directories) => directories
        .Where(Path.IsPathRooted).Any(dir => File.Exists(Path.Combine(dir, "applications", DesktopId)));

    internal static async Task SetDefaultAsync(string executable)
    {
        await RunAsync(executable, ["default", DesktopId, "inode/directory"]);
        string current = await RunAsync(executable, ["query", "default", "inode/directory"]);
        if (!string.Equals(current.Trim(), DesktopId, StringComparison.Ordinal))
            throw new IOException("The desktop did not confirm Rain as the folder default. Choose Rain in your desktop's Default Applications settings.");
    }

    private static async Task<string> RunAsync(string executable, string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Could not start the desktop settings helper.");
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(Timeout); }
        catch (TimeoutException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new IOException("The desktop settings helper timed out. Try again.");
        }
        string details = (await error).Trim();
        if (process.ExitCode != 0)
            throw new IOException(details.Length > 0 ? details : "The desktop could not change the folder default.");
        return await output;
    }

    public static async Task ReloadActivationAsync(string? address = null)
    {
        using var connection = await ConnectAsync(address);
        await connection.CallMethodAsync(Message(connection, "org.freedesktop.DBus", "/org/freedesktop/DBus",
            "org.freedesktop.DBus", "ReloadConfig")).WaitAsync(Timeout);
    }

    public static async Task ReleaseBackgroundDolphinAsync(string? address = null)
    {
        using var connection = await ConnectAsync(address);
        string owner;
        try
        {
            owner = await connection.CallMethodAsync(Message(connection, "org.freedesktop.DBus", "/org/freedesktop/DBus",
                "org.freedesktop.DBus", "GetNameOwner", FileManagerService.BusName),
                (reply, _) => reply.GetBodyReader().ReadString()).WaitAsync(Timeout);
        }
        catch (DBusErrorReplyException ex) when (ex.ErrorName == "org.freedesktop.DBus.Error.NameHasNoOwner") { return; }
        uint pid = await connection.CallMethodAsync(Message(connection, "org.freedesktop.DBus", "/org/freedesktop/DBus",
            "org.freedesktop.DBus", "GetConnectionUnixProcessID", owner),
            (reply, _) => reply.GetBodyReader().ReadUInt32()).WaitAsync(Timeout);
        try
        {
            string[] args = File.ReadAllText($"/proc/{pid}/cmdline").Split('\0', StringSplitOptions.RemoveEmptyEntries);
            string executable = File.ResolveLinkTarget($"/proc/{pid}/exe", returnFinalTarget: true)?.FullName ?? "";
            if (!IsBackgroundDolphin(executable, args)) return;
        }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        // Address the unique connection we checked, never a name that could have changed owners.
        await connection.CallMethodAsync(Message(connection, owner, "/MainApplication",
            "org.qtproject.Qt.QCoreApplication", "quit")).WaitAsync(Timeout);
    }

    internal static bool IsBackgroundDolphin(string executable, IReadOnlyList<string> arguments) =>
        Path.GetFileName(executable) == "dolphin" && arguments.Count == 2
        && Path.GetFileName(arguments[0]) == "dolphin" && arguments[1] == "--daemon";

    private static async Task<DBusConnection> ConnectAsync(string? address)
    {
        var connection = new DBusConnection(address ?? DBusAddress.Session
            ?? throw new IOException("The desktop session bus is unavailable."));
        try { await connection.ConnectAsync().AsTask().WaitAsync(Timeout); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private static MessageBuffer Message(DBusConnection connection, string destination, string path,
        string @interface, string member, string? argument = null)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: destination, path: path, @interface: @interface,
            member: member, signature: argument is null ? null : "s");
        if (argument is not null) writer.WriteString(argument);
        return writer.CreateMessage();
    }
}
