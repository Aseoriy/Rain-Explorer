using System.Net.Sockets;
using System.Text;
using RainExplorer.Services;

namespace RainExplorer;

/// <summary>
/// Keeps one Rain Explorer process per user session. Only one process may own
/// settings.json at a time: two processes racing on it could read a half-written file,
/// fall back to defaults and save over the user's pins. A later launch hands its folder
/// to the running instance (which opens it in a new tab) and exits.
/// </summary>
/// <remarks>
/// Uses a Unix domain socket in $XDG_RUNTIME_DIR (a private, per-user tmpfs), the
/// Linux counterpart of the Windows build's named mutex + named pipe.
/// </remarks>
internal static class SingleInstance
{
    private const string SocketName = "rain-explorer.sock";
    private static Socket? _listener;

    /// <summary>Raised on a background thread with the arguments of a forwarded launch.</summary>
    public static event Action<string[]>? ArgumentsReceived;

    private static string SocketPath
    {
        get
        {
            string? runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            string dir = !string.IsNullOrEmpty(runtime) && Directory.Exists(runtime)
                ? runtime
                : Path.Combine(Path.GetTempPath(), $"rain-explorer-{Environment.UserName}");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, SocketName);
        }
    }

    /// <summary>Send <paramref name="args"/> to an already-running instance. True when it accepted them.</summary>
    public static bool TryForwardToRunningInstance(string[] args)
    {
        string path = SocketPath;
        if (!File.Exists(path)) return false;
        try
        {
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(new UnixDomainSocketEndPoint(path));
            string cwd = Environment.CurrentDirectory;
            // Resolve relative paths here: the running instance has its own working directory.
            var resolved = args.Select(a => a.StartsWith('-') ? a : ResolveArg(a, cwd));
            byte[] payload = Encoding.UTF8.GetBytes(string.Join('\0', resolved));
            client.Send(payload);
            client.Shutdown(SocketShutdown.Send);
            return true;
        }
        catch (SocketException)
        {
            // A stale socket left by a crashed instance — this process becomes the owner.
            return false;
        }
    }

    /// <summary>Become the primary instance and start accepting forwarded launches.</summary>
    public static void StartListening()
    {
        string path = SocketPath;
        try
        {
            if (File.Exists(path)) File.Delete(path);
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen(4);
            _listener = listener;
        }
        catch
        {
            return;   // Forwarding is a convenience; the app still runs without it.
        }

        var thread = new Thread(AcceptLoop) { IsBackground = true, Name = "Rain single-instance" };
        thread.Start();
    }

    public static void Stop()
    {
        try
        {
            _listener?.Close();
            _listener = null;
            File.Delete(SocketPath);
        }
        catch { }
    }

    private static void AcceptLoop()
    {
        while (_listener is { } listener)
        {
            try
            {
                using Socket connection = listener.Accept();
                using var stream = new NetworkStream(connection);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string payload = reader.ReadToEnd();
                string[] args = payload.Length == 0 ? [] : payload.Split('\0');
                ArgumentsReceived?.Invoke(args);
            }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) when (_listener is null) { return; }
            catch { /* one bad client must not stop the listener */ }
        }
    }

    private static string ResolveArg(string arg, string cwd)
    {
        if (arg.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            try { return FileManagerRequest.LocalPathFromUri(arg); }
            catch (ArgumentException) { return arg; }
        }
        if (arg.Contains("://", StringComparison.Ordinal)) return arg;
        try { return Path.GetFullPath(arg, cwd); }
        catch { return arg; }
    }

    /// <summary>Turn a command-line argument (path or file:// URI) into a local path.</summary>
    public static string? ToLocalPath(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg) || arg.StartsWith('-')) return null;
        if (Path.IsPathFullyQualified(arg)) return Path.GetFullPath(arg);
        if (arg.Contains("://", StringComparison.Ordinal) || arg.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            try { return FileManagerRequest.LocalPathFromUri(arg); }
            catch (ArgumentException) { return null; }
        }
        return ResolveArg(arg, Environment.CurrentDirectory);
    }
}
