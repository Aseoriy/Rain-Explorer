using System.Reflection;
using System.Security.Cryptography;
using RainArchives.Client;
using RainArchives.Contracts;

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: RainArchives.LargeTreeCompatibilityHarness <current-worker.exe>");
    return 2;
}

RequireHash(typeof(ArchiveClient).Assembly.Location,
    "38FF68D7EB7043F8200F4821C43B0289D41AA8009632129EE598AAD5B35A9AFB");
RequireHash(typeof(ProtocolConstants).Assembly.Location,
    "E71B945555031A9933EB0631BE260CFCA5CF9AA0257BEEF658BAD9319CB398EF");
var root = Path.Combine(Path.GetTempPath(), "RainArchives.LegacyTree." + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var tree = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
    for (var index = 0; index < 4096; index++)
    {
        File.WriteAllBytes(Path.Combine(tree, $"{index:D4}.bin"), BitConverter.GetBytes(index));
    }
    // This existing internal entrypoint isolates all recovery/temporary state to the fixture.
    var start = typeof(ArchiveClient).GetMethod("StartWithTemporaryRootAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
    await using var client = await (Task<ArchiveClient>)start.Invoke(null,
        [Path.GetFullPath(args[0]), root, CancellationToken.None])!;
    Require(!client.SupportsFeature("creation-expanded-tree-budget-v1"), "1.23 negotiated a future feature");
    Require((await client.GetZipWriteCapabilitiesAsync()).MaximumEntries == 4096, "ZIP legacy cap changed");
    Require((await client.GetTarWriteCapabilitiesAsync()).MaximumEntries == 4096, "TAR legacy cap changed");
    Require((await client.GetRarWriteCapabilitiesAsync()).MaximumEntries == 4096, "RAR legacy cap changed");
    _ = await client.GetManagedWriteCapabilitiesAsync(); // Retained strict validators must accept every old shape.

    var destination = Path.Combine(root, "refused.zip");
    var started = await client.StartZipTreeCreationAsync(destination, [], [new(tree, "tree")]);
    var terminal = await client.WaitForTerminalAsync(started.OperationId).WaitAsync(TimeSpan.FromSeconds(60));
    Require(terminal.Phase == OperationPhase.Failed
        && terminal.Error?.Category == ArchiveErrorCategory.ResourceLimit
        && terminal.Error.Code == "creation.entry_count_limit", "1.23 failed to retain its expanded-tree cap");
    Require(!File.Exists(destination), "legacy refusal published an output");

    var small = Directory.CreateDirectory(Path.Combine(root, "small")).FullName;
    File.WriteAllText(Path.Combine(small, "payload.txt"), "retained 1.23 client payload");
    var created = Path.Combine(root, "small.zip");
    var successful = await client.StartZipTreeCreationAsync(created, [], [new(small, "small")]);
    terminal = await client.WaitForTerminalAsync(successful.OperationId).WaitAsync(TimeSpan.FromSeconds(30));
    Require(terminal.Phase == OperationPhase.Completed, "legacy small-tree creation failed: " + terminal.Error?.Code);
    var opened = await client.OpenArchiveAsync(created);
    Require((await client.ListEntriesAsync(opened.Archive.SessionId)).TotalCount == 2, "legacy reopen failed");
    await client.CloseArchiveAsync(opened.Archive.SessionId);
    for (var index = 0; index < 4096; index++)
    {
        Require(File.ReadAllBytes(Path.Combine(tree, $"{index:D4}.bin")).SequenceEqual(BitConverter.GetBytes(index)),
            "legacy operation changed a source");
    }
    Console.WriteLine("PASS retained exact 1.23 Client/Contracts -> current Worker: legacy capabilities; 4097-entry refusal/no output; small-tree create/reopen; 4096 sources unchanged.");
}
finally
{
    Directory.Delete(root, recursive: true);
}
return 0;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void RequireHash(string path, string expected) => Require(
    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) == expected,
    "Retained 1.23 assembly hash mismatch: " + Path.GetFileName(path));
