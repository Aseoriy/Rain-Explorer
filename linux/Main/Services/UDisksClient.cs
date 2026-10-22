using System.Text;
using Tmds.DBus.Protocol;

namespace RainExplorer.Services;

internal sealed record UDisksObject(string Path, Dictionary<string, Dictionary<string, VariantValue>> Interfaces)
{
    public bool Has(string name) => Interfaces.ContainsKey("org.freedesktop.UDisks2." + name);
    public VariantValue Value(string name, string property) =>
        Interfaces.TryGetValue("org.freedesktop.UDisks2." + name, out var properties)
        && properties.TryGetValue(property, out var value) ? value : default;
    public string Text(string name, string property)
    {
        var value = Value(name, property);
        return value.Type == VariantValueType.String ? value.GetString()
            : value.Type == VariantValueType.ObjectPath ? value.GetObjectPathAsString() : "";
    }
    public bool Flag(string name, string property) => Value(name, property) is var v
        && v.Type == VariantValueType.Bool && v.GetBool();
    public string Device => Decode(Value("Block", "PreferredDevice"));
    public string[] MountPoints
    {
        get
        {
            var value = Value("Filesystem", "MountPoints");
            return value.Type == VariantValueType.Array
                ? Enumerable.Range(0, value.Count).Select(i => Decode(value.GetItem(i))).Where(p => p.Length > 0).ToArray() : [];
        }
    }
    private static string Decode(VariantValue value) => value.Type == VariantValueType.Array
        ? Encoding.UTF8.GetString(value.GetArray<byte>()).TrimEnd('\0') : "";
}

internal interface IUDisksClient : IDisposable
{
    Task<IReadOnlyList<UDisksObject>> GetObjectsAsync();
    Task<string?> CallAsync(string path, string @interface, string method);
}

/// <summary>Small UDisks2 client; desktop PolicyKit handles authorization and busy-device checks.</summary>
internal sealed class UDisksClient(string? address = null) : IUDisksClient
{
    private const string Destination = "org.freedesktop.UDisks2";
    private DBusConnection? _connection;

    private async Task<DBusConnection> ConnectAsync()
    {
        if (_connection is { } existing) return existing;
        var connection = new DBusConnection(address ?? DBusAddress.System
            ?? throw new IOException("The system D-Bus is unavailable."));
        _connection = connection;
        await connection.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        return connection;
    }

    public async Task<IReadOnlyList<UDisksObject>> GetObjectsAsync()
    {
        var connection = await ConnectAsync().ConfigureAwait(false);
        return await connection.CallMethodAsync(Message(connection, "/org/freedesktop/UDisks2",
            "org.freedesktop.DBus.ObjectManager", "GetManagedObjects", options: false),
            static (message, _) => ReadObjects(message), null).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }

    private static IReadOnlyList<UDisksObject> ReadObjects(Message message)
    {
        var result = new List<UDisksObject>();
        var reader = message.GetBodyReader();
        var objects = reader.ReadDictionaryStart();
        while (reader.HasNext(objects))
        {
            reader.AlignStruct();
            string path = reader.ReadObjectPathAsString();
            var interfaces = new Dictionary<string, Dictionary<string, VariantValue>>(StringComparer.Ordinal);
            var entries = reader.ReadDictionaryStart();
            while (reader.HasNext(entries))
            {
                reader.AlignStruct();
                string name = reader.ReadString();
                interfaces[name] = reader.ReadDictionaryOfStringToVariantValue();
            }
            result.Add(new(path, interfaces));
        }
        return result;
    }

    public async Task<string?> CallAsync(string path, string @interface, string method)
    {
        var connection = await ConnectAsync().ConfigureAwait(false);
        var message = Message(connection, path, "org.freedesktop.UDisks2." + @interface, method, options: true);
        // Let authorization and normal unmount complete; timing out a write could leave its outcome unknown.
        if (method == "Mount")
            return await connection.CallMethodAsync(message, static (reply, _) => reply.GetBodyReader().ReadString(), null).ConfigureAwait(false);
        await connection.CallMethodAsync(message).ConfigureAwait(false);
        return null;
    }

    private static MessageBuffer Message(DBusConnection connection, string path, string @interface, string member, bool options)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: Destination, path: path, @interface: @interface,
            member: member, signature: options ? "a{sv}" : null);
        if (options) writer.WriteDictionary(new Dictionary<string, VariantValue>());
        return writer.CreateMessage();
    }

    public void Dispose() => _connection?.Dispose();
}
