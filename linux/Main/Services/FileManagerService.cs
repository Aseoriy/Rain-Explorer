using Tmds.DBus.Protocol;

namespace RainExplorer.Services;

/// <summary>The FreeDesktop file-manager interface, on a dedicated session-bus connection.</summary>
public sealed class FileManagerService : IDisposable
{
    public const string BusName = "org.freedesktop.FileManager1";
    public const string ObjectPath = "/org/freedesktop/FileManager1";
    private readonly Func<FileManagerRequest, Task> _show;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _requests = new(1);
    private DBusConnection? _connection;
    private int _disposed;
    private volatile bool _isOwner;
    public bool IsOwner => _isOwner;
    public event Action<bool>? OwnershipChanged;

    public FileManagerService(Func<FileManagerRequest, Task> show) => _show = show;

    public async Task StartAsync(string? address = null)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_connection is not null) throw new InvalidOperationException("The desktop service has already started.");
        var connection = new DBusConnection(address ?? DBusAddress.Session
            ?? throw new InvalidOperationException("A desktop session bus is not available."));
        _connection = connection;
        try
        {
            await connection.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), _stop.Token).ConfigureAwait(false);
            connection.AddMethodHandler(new Handler(this));
            // Never use RequestNameOptions.Default: it replaces an existing owner.
            await connection.QueueNameRequestAsync(BusName, RequestNameOptions.None,
                onAcquired: (_, _) => SetOwner(true), onLost: null,
                emitOnCapturedContext: false).ConfigureAwait(false);
        }
        catch { Dispose(); throw; }
    }

    private void SetOwner(bool owner)
    {
        if (_stop.IsCancellationRequested) return;
        _isOwner = owner;
        OwnershipChanged?.Invoke(owner);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _connection?.Dispose();
        _connection = null;
        _isOwner = false;
        _stop.Dispose();
    }

    public async Task WaitForDisconnectAsync()
    {
        if (_connection is not { } connection) return;
        try { await connection.DisconnectedAsync().ConfigureAwait(false); }
        catch { }
    }

    private sealed class Handler(FileManagerService service) : IPathMethodHandler
    {
        public string Path => ObjectPath;
        public bool HandlesChildPaths => false;
        private static readonly ReadOnlyMemory<byte> InterfaceXml = """
            <interface name="org.freedesktop.FileManager1">
              <method name="ShowItems"><arg name="URIs" type="as" direction="in"/><arg name="StartupId" type="s" direction="in"/></method>
              <method name="ShowFolders"><arg name="URIs" type="as" direction="in"/><arg name="StartupId" type="s" direction="in"/></method>
              <method name="ShowItemProperties"><arg name="URIs" type="as" direction="in"/><arg name="StartupId" type="s" direction="in"/></method>
            </interface>
            """u8.ToArray();

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            if (context.IsDBusIntrospectRequest)
            {
                context.ReplyIntrospectXml([InterfaceXml]);
                return default;
            }
            var message = context.Request;
            FileManagerAction? action = message.MemberAsString switch
            {
                "ShowItems" => FileManagerAction.ShowItems,
                "ShowFolders" => FileManagerAction.ShowFolders,
                "ShowItemProperties" => FileManagerAction.ShowItemProperties,
                _ => null,
            };
            if (message.InterfaceAsString != BusName || action is null) return default;
            if (message.SignatureAsString != "ass")
            {
                context.ReplyError("org.freedesktop.DBus.Error.InvalidArgs", "Expected an array of file URIs and a startup ID.");
                return default;
            }
            var reader = message.GetBodyReader();
            string[] uris = reader.ReadArrayOfString();
            string startupId = reader.ReadString();
            context.DisposesAsynchronously = true;
            _ = HandleRequest(context, action.Value, uris, startupId);
            return default;
        }

        private async Task HandleRequest(MethodContext context, FileManagerAction action, string[] uris, string startupId)
        {
            using (context)
            {
                bool entered = false;
                try
                {
                    await service._requests.WaitAsync(service._stop.Token).ConfigureAwait(false);
                    entered = true;
                    var request = await Task.Run(() => FileManagerRequest.Resolve(action, uris, startupId), service._stop.Token).ConfigureAwait(false);
                    service._stop.Token.ThrowIfCancellationRequested();
                    await service._show(request).ConfigureAwait(false);
                    using var writer = context.CreateReplyWriter(null);
                    context.Reply(writer.CreateMessage());
                }
                catch (Exception ex)
                {
                    if (!service._stop.IsCancellationRequested)
                    {
                        try
                        {
                            context.ReplyError(ex is ArgumentException ? "org.freedesktop.DBus.Error.InvalidArgs"
                                : "org.freedesktop.DBus.Error.Failed", ex.Message);
                        }
                        catch (DBusConnectionClosedException) { }
                        catch (ObjectDisposedException) { }
                    }
                }
                finally { if (entered) service._requests.Release(); }
            }
        }
    }
}
