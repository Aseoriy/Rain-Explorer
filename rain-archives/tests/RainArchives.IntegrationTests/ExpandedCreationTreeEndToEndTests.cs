using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Client;
using RainArchives.Contracts;
using RainArchives.Core;
using RainArchives.Providers.Zip;
using RainArchives.Security;

namespace RainArchives.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class ExpandedCreationTreeEndToEndTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task Minor24WithoutBudgetFeatureStillRefusesExpandedTree()
    {
        using var fixture = new TreeFixture(4_200);
        await using var harness = await WorkerProtocolIntegrationTests.RawWorkerHarness.StartUnnegotiatedAsync(
            writerProviders: [new BuiltInZipArchiveWriterProvider()],
            creationRecoveryRoot: Path.Combine(fixture.Root, "recovery"),
            extractionRecoveryRoot: Path.Combine(fixture.Root, "extraction-recovery"));
        await harness.SendWelcomeAsync(24, ProtocolFeatures.SupportedForMinorVersion(24)
            .Where(feature => feature != ProtocolFeatures.ExpandedCreationTreeBudget).ToArray());
        var destination = Path.Combine(fixture.Root, "unnegotiated.zip");
        var started = await harness.SendAsync<StartZipTreeCreationRequest, StartOperationResponse>(
            ProtocolMessageKinds.StartZipTreeCreation, new(destination, [], [new(fixture.Tree, "tree")]));
        OperationEvent? terminal = null;
        for (var index = 0; index < 32; index++)
        {
            var frame = await harness.ReadFrameAsync();
            if (frame.Kind == ProtocolMessageKinds.OperationEvent)
            {
                var operationEvent = frame.ReadPayload<OperationEvent>();
                if (operationEvent.OperationId == started.OperationId && operationEvent.Kind == OperationEventKind.Terminal)
                {
                    terminal = operationEvent;
                    break;
                }
            }
        }
        Assert.AreEqual("creation.entry_count_limit", terminal?.Error?.Code);
        Assert.IsFalse(File.Exists(destination));
        fixture.AssertSourcesUnchanged();
        AssertNoStaging(fixture.Root);
    }

    [TestMethod]
    [DataRow(23, false)]
    [DataRow(24, true)]
    [Timeout(15_000)]
    public async Task BudgetFeatureRejectsOldMinorOrMissingCancellation(int minor, bool omitCancellation)
    {
        await using var harness = await WorkerProtocolIntegrationTests.RawWorkerHarness.StartUnnegotiatedAsync();
        var features = ProtocolFeatures.RequiredVersion1.Append(ProtocolFeatures.ExpandedCreationTreeBudget);
        if (!omitCancellation) features = features.Append(ProtocolFeatures.RequestCancellation);
        await harness.SendWelcomeAsync(minor, features.ToArray());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await harness.WorkerRunTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    [DataRow("zip", 20_000)]
    [DataRow("tar", 4_200)]
    [DataRow("tar.gz", 4_200)]
    [DataRow("tar.bz2", 4_200)]
    [Timeout(360_000)]
    public async Task RealWorkerCreatesAndReopensExpandedTree(string format, int entries)
    {
        using var fixture = new TreeFixture(entries);
        await using var client = await ArchiveClient.StartWithTemporaryRootAsync(WorkerPath, fixture.Root);
        Assert.AreEqual(65_536, client.MaximumCreationTreeEntries);
        await AssertLegacyCapabilitiesAsync(client);
        var destination = Path.Combine(fixture.Root, "created." + format);
        var started = await StartTreeAsync(client, fixture.Tree, destination, format);
        await CompletedAsync(client, started.OperationId);

        var opened = await client.OpenArchiveAsync(destination);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int? offset = 0; offset is not null;)
        {
            var page = await client.ListEntriesAsync(opened.Archive.SessionId, offset.Value,
                ProtocolConstants.MaximumPageSize);
            Assert.AreEqual(entries, page.TotalCount);
            foreach (var entry in page.Entries)
            {
                Assert.IsTrue(seen.Add(entry.RawPath));
                Assert.IsTrue(fixture.Expected.ContainsKey(entry.RawPath.TrimEnd('/')));
            }
            offset = page.NextOffset;
        }
        Assert.AreEqual(entries, seen.Count);
        var integrity = await client.StartIntegrityTestAsync(opened.Archive.SessionId, []);
        await CompletedAsync(client, integrity.OperationId);
        await client.CloseArchiveAsync(opened.Archive.SessionId);

        if (format == "zip")
        {
            // Independent ZIP readback checks every payload, including empty directories.
            using (var zip = ZipFile.OpenRead(destination))
            {
                Assert.AreEqual(entries, zip.Entries.Count);
                foreach (var entry in zip.Entries)
                {
                    var expected = fixture.Expected[entry.FullName.TrimEnd('/')];
                    using var stream = entry.Open();
                    using var bytes = new MemoryStream();
                    await stream.CopyToAsync(bytes);
                    CollectionAssert.AreEqual(expected ?? [], bytes.ToArray(), entry.FullName);
                }
            }

            var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(destination));
            var refused = await StartTreeAsync(client, Path.Combine(fixture.Tree, "d00000"), destination, format);
            var refusal = await client.WaitForTerminalAsync(refused.OperationId);
            Assert.AreEqual(ArchiveErrorCategory.DestinationConflict, refusal.Error?.Category);
            CollectionAssert.AreEqual(originalHash, SHA256.HashData(await File.ReadAllBytesAsync(destination)));

            var cancellation = new TaskCompletionSource<Task<CancelOperationResponse>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            void CancelOnValidating(object? sender, OperationEvent operationEvent)
            {
                if (operationEvent.Phase == OperationPhase.Validating && !cancellation.Task.IsCompleted)
                {
                    cancellation.TrySetResult(client.CancelOperationAsync(operationEvent.OperationId));
                }
            }
            client.OperationEventReceived += CancelOnValidating;
            try
            {
                var cancelledPath = Path.Combine(fixture.Root, "cancelled.zip");
                var cancelStarted = await StartTreeAsync(client, fixture.Tree, cancelledPath, format);
                var cancelResponse = await (await cancellation.Task.WaitAsync(TimeSpan.FromSeconds(90)));
                Assert.IsTrue(cancelResponse.CancellationRequested);
                var terminal = await client.WaitForTerminalAsync(cancelStarted.OperationId);
                Assert.AreEqual(OperationPhase.Cancelled, terminal.Phase);
                Assert.IsFalse(File.Exists(cancelledPath));
                CollectionAssert.AreEqual(originalHash, SHA256.HashData(await File.ReadAllBytesAsync(destination)));
            }
            finally
            {
                client.OperationEventReceived -= CancelOnValidating;
            }
        }
        fixture.AssertSourcesUnchanged();
        AssertNoStaging(fixture.Root);
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task ExpandedPolicyCoversAllDirectoryFormatsAndRejectsFinalSourceMutation()
    {
        using var fixture = new TreeFixture(4_200);
        var policy = new ArchiveCreationPolicy(maximumTreeEntries: 65_536);
        ArchiveCreationPlan? plan = null;
        foreach (var format in new[] { ArchiveCreationOutputFormat.Zip, ArchiveCreationOutputFormat.Tar,
                     ArchiveCreationOutputFormat.TarGZip, ArchiveCreationOutputFormat.TarBZip2,
                     ArchiveCreationOutputFormat.Rar })
        {
            plan = await policy.PlanTreeAsync([], [new ArchiveCreationDirectory(fixture.Tree, "tree")],
                Path.Combine(fixture.Root, "planned"), format, CancellationToken.None);
            Assert.AreEqual(4_200, plan.TotalEntries);
            await policy.ValidateSourceTreesAsync(plan, CancellationToken.None);
        }
        var path = Directory.EnumerateFiles(fixture.Tree, "*", SearchOption.AllDirectories).First();
        File.WriteAllBytes(path, [1, 2, 3]);
        var error = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await policy.ValidateSourceTreesAsync(plan!, CancellationToken.None));
        Assert.AreEqual("source.directory_tree_changed", error.Code);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "planned")));
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task CurrentClientWithRetained123WorkerKeeps4096TreeBudget()
    {
        using var fixture = new TreeFixture(4_200);
        var package = FindPackage();
        AssertHash(Path.Combine(package, "package-manifest.json"),
            "DF47FAD343B6DCBDE2B1FFB42489C7CF7D19C154CA9D2CB6398C07AE2F442ADB");
        AssertHash(Path.Combine(package, "app", "worker", "RainArchives.Worker.dll"),
            "42406932E8EF96F56DDAA1BBD0E82D595442E2D4815E6274547FFC9BEFB53C2B");
        await using var client = await ArchiveClient.StartWithTemporaryRootAsync(
            Path.Combine(package, "app", "worker", "RainArchives.Worker.exe"), fixture.Root);
        Assert.AreEqual(4096, client.MaximumCreationTreeEntries);
        Assert.IsFalse(client.SupportsFeature(ProtocolFeatures.ExpandedCreationTreeBudget));
        await AssertLegacyCapabilitiesAsync(client);
        var destination = Path.Combine(fixture.Root, "refused.zip");
        var started = await StartTreeAsync(client, fixture.Tree, destination, "zip");
        var terminal = await client.WaitForTerminalAsync(started.OperationId);
        Assert.AreEqual(ArchiveErrorCategory.ResourceLimit, terminal.Error?.Category);
        Assert.AreEqual("creation.entry_count_limit", terminal.Error?.Code);
        Assert.IsFalse(File.Exists(destination));
        fixture.AssertSourcesUnchanged();
        AssertNoStaging(fixture.Root);
    }

    private static async Task AssertLegacyCapabilitiesAsync(ArchiveClient client)
    {
        Assert.AreEqual(4096, (await client.GetZipWriteCapabilitiesAsync()).MaximumEntries);
        Assert.AreEqual(4096, (await client.GetTarWriteCapabilitiesAsync()).MaximumEntries);
        Assert.AreEqual(4096, (await client.GetRarWriteCapabilitiesAsync()).MaximumEntries);
        foreach (var capability in (await client.GetManagedWriteCapabilitiesAsync()).Capabilities)
        {
            Assert.AreEqual(capability.SupportsTreeCreation ? 4096 : 1, capability.MaximumEntries);
        }
    }

    private static Task<StartOperationResponse> StartTreeAsync(
        ArchiveClient client, string tree, string destination, string format) => format switch
        {
            "zip" => client.StartZipTreeCreationAsync(destination, [], [new(tree, "tree")]),
            "tar" => client.StartTarTreeCreationAsync(destination, [], [new(tree, "tree")]),
            "tar.gz" => client.StartTarGZipTreeCreationAsync(destination, [], [new(tree, "tree")]),
            "tar.bz2" => client.StartTarBZip2TreeCreationAsync(destination, [], [new(tree, "tree")]),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

    private static async Task CompletedAsync(ArchiveClient client, OperationId operationId)
    {
        var terminal = await client.WaitForTerminalAsync(operationId).WaitAsync(TimeSpan.FromSeconds(150));
        Assert.AreEqual(OperationPhase.Completed, terminal.Phase,
            $"{terminal.Error?.Code}: {terminal.Error?.Message}");
    }

    private static void AssertNoStaging(string root) => Assert.AreEqual(0,
        Directory.EnumerateFiles(root, ".rainarchives-*", SearchOption.TopDirectoryOnly).Count());

    private static string WorkerPath => Environment.GetEnvironmentVariable("RAIN_ARCHIVES_WORKER_PATH")
        ?? Path.Combine(AppContext.BaseDirectory, "RainArchives.Worker.exe");

    private static string FindPackage()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RainArchives.slnx")))
        {
            directory = directory.Parent;
        }
        return Path.Combine(directory!.FullName, "artifacts", "RainArchives.M7DRarPackage.1.23.0D84243C");
    }

    private static void AssertHash(string path, string expected) =>
        Assert.AreEqual(expected, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

    private sealed class TreeFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "RainArchives.ExpandedTree." + Guid.NewGuid().ToString("N"));
        public string Tree => Path.Combine(Root, "source");
        public Dictionary<string, byte[]?> Expected { get; } = new(StringComparer.Ordinal);

        public TreeFixture(int entries)
        {
            Directory.CreateDirectory(Tree);
            Expected.Add("tree", null);
            var remaining = entries - 1;
            var index = 0;
            while (remaining > 0)
            {
                var relative = $"d{index:D5}";
                var directory = Directory.CreateDirectory(Path.Combine(Tree, relative)).FullName;
                Expected.Add("tree/" + relative, null);
                remaining--;
                for (var file = 0; file < 99 && remaining > 0; file++, remaining--)
                {
                    var name = $"f{file:D3}.bin";
                    var bytes = BitConverter.GetBytes(index * 100 + file);
                    File.WriteAllBytes(Path.Combine(directory, name), bytes);
                    Expected.Add($"tree/{relative}/{name}", bytes);
                }
                index++;
            }
        }

        public void AssertSourcesUnchanged()
        {
            Assert.AreEqual(Expected.Count - 1,
                Directory.EnumerateFileSystemEntries(Tree, "*", SearchOption.AllDirectories).Count());
            foreach (var (path, bytes) in Expected)
            {
                var source = path == "tree" ? Tree : Path.Combine(Tree, path[5..].Replace('/', Path.DirectorySeparatorChar));
                if (bytes is null)
                {
                    Assert.IsTrue(Directory.Exists(source));
                }
                else
                {
                    CollectionAssert.AreEqual(bytes, File.ReadAllBytes(source), path);
                }
            }
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
