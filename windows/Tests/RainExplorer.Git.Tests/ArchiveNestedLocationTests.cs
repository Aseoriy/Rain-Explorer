using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using RainArchives.Client;
using RainArchives.Contracts;
using RainExplorer.Models;
using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Git.Tests;

public sealed class ArchiveNestedLocationTests
{
    [Fact]
    public void NestedLocationEnvelopeIsVersionedStrictBoundedAndV1Compatible()
    {
        string source = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Rain Explorer Space", "outer.data"));
        ArchiveFingerprint root = Fingerprint(4_096, 'A');
        ArchiveFingerprint child = Fingerprint(2_048, 'B');
        ArchiveFingerprint grandchild = Fingerprint(1_024, 'C');
        var location = new ArchiveLocation(source, root, "inside/folder", 0)
        {
            NestedSegments =
            [
                new ArchiveNestedSegment(root, "child one.bin", 0, child),
                new ArchiveNestedSegment(child, "nested/child two.bin", 2, grandchild),
            ],
        };

        Assert.True(ArchiveLocationSerializer.TrySerialize(location, out string target));
        Assert.Contains("\"v\":2", target, StringComparison.Ordinal);
        Assert.DoesNotContain("session", target, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("entryId", target, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", target, StringComparison.OrdinalIgnoreCase);
        Assert.True(ArchiveLocationSerializer.TryDeserialize(target, out ArchiveLocation restored));
        Assert.Equal(source, restored.SourcePath);
        Assert.Equal("inside/folder", restored.FolderPath);
        Assert.Equal(2, restored.NestedSegments.Count);
        Assert.Equal("nested/child two.bin", restored.NestedSegments[1].SourceEntryPath);
        Assert.Equal(2, restored.NestedSegments[1].SourceEntryDuplicateOrdinal);

        var rootOnly = new ArchiveLocation(source, root, string.Empty, null);
        Assert.True(ArchiveLocationSerializer.TrySerialize(rootOnly, out string v2Root));
        string v1Root = v2Root
            .Replace("\"v\":2", "\"v\":1", StringComparison.Ordinal)
            .Replace(",\"segments\":[]", string.Empty, StringComparison.Ordinal);
        Assert.True(ArchiveLocationSerializer.TryDeserialize(v1Root, out ArchiveLocation v1Restored));
        Assert.Empty(v1Restored.NestedSegments);

        Assert.False(ArchiveLocationSerializer.TryDeserialize(
            target.Replace("\"sourceEntry\":", "\"unknown\":true,\"sourceEntry\":",
                StringComparison.Ordinal), out _));
        Assert.False(ArchiveLocationSerializer.TrySerialize(location with
        {
            NestedSegments =
            [
                .. location.NestedSegments,
                new ArchiveNestedSegment(grandchild, "third.bin", 0, Fingerprint(512, 'D')),
                new ArchiveNestedSegment(Fingerprint(512, 'D'), "fourth.bin", 0, Fingerprint(256, 'E')),
            ],
        }, out _));
        Assert.False(ArchiveLocationSerializer.TrySerialize(location with
        {
            NestedSegments =
            [new ArchiveNestedSegment(Fingerprint(4_096, 'F'), "child.bin", 0, child)],
        }, out _));
        Assert.False(ArchiveLocationSerializer.TryDeserialize(
            ArchiveLocationSerializer.Prefix
                + new string('x', ArchiveLocationSerializer.MaximumEnvelopeBytes),
            out _));
    }

    [Fact]
    public async Task ExplicitNestedDepthHistoryPersistenceAndCloseOrderingAreAuthoritative()
    {
        NestedFakeArchiveClient fake = NestedFakeArchiveClient.CreateChain(levels: 4);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var adapter = new ArchiveLocationAdapter(runtime);
        string source = Path.Combine(Path.GetTempPath(), "nested history outer.data");

        await adapter.OpenAsync(source);
        Assert.Equal(0, adapter.NestedDepth);
        for (int depth = 1; depth <= ProtocolConstants.MaximumNestedArchiveDepth; depth++)
        {
            FileItem child = Assert.Single(adapter.Items);
            Assert.True(adapter.CanOpenNestedArchive(child));
            Assert.Equal(ArchiveNestedOpenOutcome.Opened,
                await adapter.OpenNestedArchiveAsync(child));
            Assert.Equal(depth, adapter.NestedDepth);
            Assert.Equal(depth + 1, adapter.Breadcrumbs.Count);
        }

        FileItem tooDeep = Assert.Single(adapter.Items);
        Assert.False(adapter.CanOpenNestedArchive(tooDeep));
        Assert.Equal(ArchiveNestedOpenOutcome.Refused,
            await adapter.OpenNestedArchiveAsync(tooDeep));
        Assert.Equal(3, fake.NestedOpenCalls.Count);
        Assert.Equal(3, adapter.NestedDepth);

        Assert.True(adapter.TryGetPersistentTarget(out string target));
        Assert.True(ArchiveLocationSerializer.TryDeserialize(target, out ArchiveLocation saved));
        Assert.Equal(3, saved.NestedSegments.Count);
        Assert.True(adapter.GoBack());
        Assert.Equal(2, adapter.NestedDepth);
        Assert.True(adapter.GoForward());
        Assert.Equal(3, adapter.NestedDepth);
        Assert.True(adapter.GoUp());
        Assert.Equal(2, adapter.NestedDepth);
        Assert.True(adapter.GoBack());
        Assert.Equal(3, adapter.NestedDepth);

        await adapter.DisposeAsync();
        Assert.Equal(
            fake.SessionIds.Take(4).Reverse(),
            fake.ClosedSessions.Take(4));
    }

    [Fact]
    public async Task NestedAdmissionRefusesDowngradeSolidMultipartListOnlyEncryptedAndOversizeBeforeSecrets()
    {
        var cases = new[]
        {
            NestedFakeArchiveClient.CreateChain(supportsNested: false),
            NestedFakeArchiveClient.CreateChain(rootSolid: true),
            NestedFakeArchiveClient.CreateChain(rootMultipart: true),
            NestedFakeArchiveClient.CreateChain(rootCapabilities: ArchiveCapability.List),
            NestedFakeArchiveClient.CreateChain(rootEntryEncrypted: true),
            NestedFakeArchiveClient.CreateChain(rootEntrySize: ProtocolConstants.MaximumNestedArchiveBytes + 1),
        };

        foreach (NestedFakeArchiveClient fake in cases)
        {
            await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
            await using var adapter = new ArchiveLocationAdapter(runtime);
            await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".data"));
            FileItem child = Assert.Single(adapter.Items);
            char[] password = ['n', 'o', 't', '-', 'u', 's', 'e', 'd'];

            Assert.False(adapter.CanOpenNestedArchive(child));
            Assert.Equal(ArchiveNestedOpenOutcome.Refused,
                await adapter.OpenNestedArchiveAsync(child, password));
            Assert.All(password, character => Assert.Equal('\0', character));
            Assert.Empty(fake.NestedOpenCalls);
            Assert.Empty(fake.CreatedSecretIds);
        }
    }

    [Fact]
    public async Task NestedPasswordRetryUsesOnlyTheChildRoleAndClearsEveryCallerBuffer()
    {
        NestedFakeArchiveClient fake = NestedFakeArchiveClient.CreateChain(childPassword: "correct child password");
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var adapter = new ArchiveLocationAdapter(runtime);
        await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "password child.data"));
        FileItem child = Assert.Single(adapter.Items);

        Assert.Equal(ArchiveNestedOpenOutcome.PasswordRequired,
            await adapter.OpenNestedArchiveAsync(child));
        char[] wrong = "wrong child password".ToCharArray();
        Assert.Equal(ArchiveNestedOpenOutcome.PasswordRequired,
            await adapter.OpenNestedArchiveAsync(child, wrong));
        Assert.All(wrong, character => Assert.Equal('\0', character));
        Assert.Equal(0, adapter.NestedDepth);

        char[] correct = "correct child password".ToCharArray();
        Assert.Equal(ArchiveNestedOpenOutcome.Opened,
            await adapter.OpenNestedArchiveAsync(child, correct));
        Assert.All(correct, character => Assert.Equal('\0', character));
        Assert.Equal(1, adapter.NestedDepth);
        Assert.Equal(2, fake.CreatedSecretIds.Count);
        Assert.All(fake.NestedOpenCalls, call => Assert.Null(call.ParentPasswordSlotId));
        Assert.All(fake.CreatedSecretIds, id => Assert.Contains(id, fake.SettledSecretIds));
    }

    [Fact]
    public async Task NestedAdmissionRefusesChainByteOverflowBeforeClientOrSecretUse()
    {
        NestedFakeArchiveClient fake = NestedFakeArchiveClient.CreateChain(
            childMaterializedBytes:
            [
                ProtocolConstants.MaximumNestedArchiveChainBytes - 1,
                2,
            ]);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var adapter = new ArchiveLocationAdapter(runtime);
        await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "nested chain limit.data"));

        Assert.Equal(ArchiveNestedOpenOutcome.Opened,
            await adapter.OpenNestedArchiveAsync(Assert.Single(adapter.Items)));
        FileItem next = Assert.Single(adapter.Items);
        char[] password = "must not be consumed".ToCharArray();

        Assert.False(adapter.CanOpenNestedArchive(next));
        Assert.Equal(ArchiveNestedOpenOutcome.Refused,
            await adapter.OpenNestedArchiveAsync(next, password));
        Assert.All(password, character => Assert.Equal('\0', character));
        Assert.Single(fake.NestedOpenCalls);
        Assert.Empty(fake.CreatedSecretIds);
        Assert.Equal(1, adapter.NestedDepth);
    }

    [Fact]
    public async Task MalformedCancellationAndSourceChangeNeverPublishAPartialChild()
    {
        NestedFakeArchiveClient malformed = NestedFakeArchiveClient.CreateChain(malformedNestedResponse: true);
        await using (var runtime = new ArchiveClientRuntime(_ => Task.FromResult(malformed.Client)))
        await using (var adapter = new ArchiveLocationAdapter(runtime))
        {
            await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "malformed child.data"));
            FileItem child = Assert.Single(adapter.Items);
            Assert.Equal(ArchiveNestedOpenOutcome.Error, await adapter.OpenNestedArchiveAsync(child));
            Assert.Equal(0, adapter.NestedDepth);
            Assert.Single(adapter.Items);
            Assert.Equal(malformed.SessionIds[1], Assert.Single(malformed.ClosedSessions));
        }

        NestedFakeArchiveClient blocked = NestedFakeArchiveClient.CreateChain(blockNestedOpen: true);
        await using (var runtime = new ArchiveClientRuntime(_ => Task.FromResult(blocked.Client)))
        await using (var adapter = new ArchiveLocationAdapter(runtime))
        {
            await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "cancel child.data"));
            FileItem child = Assert.Single(adapter.Items);
            using var cancellation = new CancellationTokenSource();
            Task<ArchiveNestedOpenOutcome> opening = adapter.OpenNestedArchiveAsync(child, cancellationToken: cancellation.Token);
            await blocked.NestedOpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            Assert.Equal(ArchiveNestedOpenOutcome.Cancelled, await opening);
            Assert.Equal(0, adapter.NestedDepth);
            Assert.Single(adapter.Items);
            Assert.Empty(blocked.ClosedSessions);
        }

        NestedFakeArchiveClient changed = NestedFakeArchiveClient.CreateChain(sourceChanged: true);
        await using (var runtime = new ArchiveClientRuntime(_ => Task.FromResult(changed.Client)))
        await using (var adapter = new ArchiveLocationAdapter(runtime))
        {
            await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "changed parent.data"));
            Assert.Equal(ArchiveNestedOpenOutcome.Stale,
                await adapter.OpenNestedArchiveAsync(Assert.Single(adapter.Items)));
            Assert.Equal(ArchiveLocationState.Stale, adapter.State);
            Assert.Empty(adapter.Items);
            Assert.Equal(changed.SessionIds[0], Assert.Single(changed.ClosedSessions));
        }
    }

    [Fact]
    public async Task CancellationDuringChildIndexingClosesTheOwnedChildBeforeItCanBecomeVisible()
    {
        NestedFakeArchiveClient fake = NestedFakeArchiveClient.CreateChain(blockChildListing: true);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var adapter = new ArchiveLocationAdapter(runtime);
        await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "cancel child index.data"));
        FileItem child = Assert.Single(adapter.Items);
        using var cancellation = new CancellationTokenSource();

        Task<ArchiveNestedOpenOutcome> opening = adapter.OpenNestedArchiveAsync(
            child, cancellationToken: cancellation.Token);
        await fake.ChildListStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        Assert.Equal(ArchiveNestedOpenOutcome.Cancelled, await opening);
        Assert.Equal(0, adapter.NestedDepth);
        Assert.Single(adapter.Items);
        Assert.Equal(fake.SessionIds[1], Assert.Single(fake.ClosedSessions));
    }

    [Fact]
    public async Task NestedRestoreReplaysFreshEntryIdsAndFingerprintMismatchFailsStaleWithoutFallback()
    {
        string source = Path.Combine(Path.GetTempPath(), "restore nested.data");
        string target;
        NestedFakeArchiveClient original = NestedFakeArchiveClient.CreateChain();
        await using (var runtime = new ArchiveClientRuntime(_ => Task.FromResult(original.Client)))
        await using (var adapter = new ArchiveLocationAdapter(runtime))
        {
            await adapter.OpenAsync(source);
            Assert.Equal(ArchiveNestedOpenOutcome.Opened,
                await adapter.OpenNestedArchiveAsync(Assert.Single(adapter.Items)));
            Assert.True(adapter.TryGetPersistentTarget(out target));
        }

        NestedFakeArchiveClient restored = NestedFakeArchiveClient.CreateChain();
        await using (var runtime = new ArchiveClientRuntime(_ => Task.FromResult(restored.Client)))
        await using (var adapter = new ArchiveLocationAdapter(runtime))
        {
            await adapter.OpenAsync(target);
            Assert.Equal(ArchiveLocationState.Ready, adapter.State);
            Assert.Equal(1, adapter.NestedDepth);
            Assert.Single(restored.NestedOpenCalls);
        }

        NestedFakeArchiveClient changed = NestedFakeArchiveClient.CreateChain(changedChildFingerprint: true);
        await using (var runtime = new ArchiveClientRuntime(_ => Task.FromResult(changed.Client)))
        await using (var adapter = new ArchiveLocationAdapter(runtime))
        {
            await adapter.OpenAsync(target);
            Assert.Equal(ArchiveLocationState.Stale, adapter.State);
            Assert.Empty(adapter.Items);
            Assert.Equal(
                new[] { changed.SessionIds[1], changed.SessionIds[0] },
                changed.ClosedSessions);
        }
    }

    [Fact]
    public async Task NestedRestoreUsesNormalizedPathAndDuplicateOrdinalWithFreshEntryIds()
    {
        string source = Path.Combine(Path.GetTempPath(), "restore duplicate nested.data");
        string target;
        NestedFakeArchiveClient original = NestedFakeArchiveClient.CreateChain(duplicateRootEntries: true);
        EntryId originalDuplicate = original.RootEntryIds[1];
        await using (var runtime = new ArchiveClientRuntime(_ => Task.FromResult(original.Client)))
        await using (var adapter = new ArchiveLocationAdapter(runtime))
        {
            await adapter.OpenAsync(source);
            Assert.Equal(2, adapter.Items.Count);
            Assert.Equal(ArchiveNestedOpenOutcome.Opened,
                await adapter.OpenNestedArchiveAsync(adapter.Items[1]));
            Assert.True(adapter.TryGetPersistentTarget(out target));
            Assert.True(ArchiveLocationSerializer.TryDeserialize(target, out ArchiveLocation saved));
            ArchiveNestedSegment segment = Assert.Single(saved.NestedSegments);
            Assert.Equal("child-1.data", segment.SourceEntryPath);
            Assert.Equal(1, segment.SourceEntryDuplicateOrdinal);
        }

        NestedFakeArchiveClient restored = NestedFakeArchiveClient.CreateChain(duplicateRootEntries: true);
        Assert.DoesNotContain(originalDuplicate, restored.RootEntryIds);
        await using (var runtime = new ArchiveClientRuntime(_ => Task.FromResult(restored.Client)))
        await using (var adapter = new ArchiveLocationAdapter(runtime))
        {
            await adapter.OpenAsync(target);
            Assert.Equal(ArchiveLocationState.Ready, adapter.State);
            Assert.Equal(1, adapter.NestedDepth);
            NestedOpenCall call = Assert.Single(restored.NestedOpenCalls);
            Assert.Equal(restored.RootEntryIds[1], call.EntryId);
        }
    }

    [Fact]
    public async Task RuntimeNestedByteReservationsEnforceTheSharedCeilingBeforeClientUse()
    {
        NestedFakeArchiveClient fake = NestedFakeArchiveClient.CreateChain();
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        Assert.True(runtime.TryReserveNestedArchiveBytes(
            ProtocolConstants.MaximumNestedArchiveBytes, out var first));
        Assert.True(runtime.TryReserveNestedArchiveBytes(
            ProtocolConstants.MaximumNestedArchiveBytes, out var second));
        Assert.False(runtime.TryReserveNestedArchiveBytes(1, out _));
        first.Dispose();
        Assert.True(runtime.TryReserveNestedArchiveBytes(1, out var replacement));
        replacement.Dispose();
        second.Dispose();
    }

    [Fact]
    public async Task RealProtocol122WorkerOpensThreeExtensionIndependentLevelsRestoresAndLeavesNoResidue()
    {
        string workerPath = Environment.GetEnvironmentVariable(
            "RAINEXPLORER_TEST_WORKER_PATH")
            ?? Path.Combine(AppContext.BaseDirectory, "worker", "RainArchives.Worker.exe");
        Assert.True(File.Exists(workerPath), $"Worker payload missing: {workerPath}");
        HashSet<int> priorWorkerIds = Process.GetProcessesByName("RainArchives.Worker")
            .Select(process => process.Id).ToHashSet();
        string root = Path.Combine(Path.GetTempPath(), "RainExplorer-M6D-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "outer path with spaces 雨.data");
        byte[] deepest = CreateZipBytes("payload.txt", "nested payload"u8.ToArray());
        byte[] levelThree = CreateZipBytes(
            ("next.bin", deepest),
            ("folder/hello.txt", "child text"u8.ToArray()));
        byte[] levelTwo = CreateZipBytes("next.bin", levelThree);
        byte[] levelOne = CreateZipBytes("next.bin", levelTwo);
        await File.WriteAllBytesAsync(source, CreateZipBytes("next.bin", levelOne));
        string sourceHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source)));

        try
        {
            await using var runtime = new ArchiveClientRuntime(async cancellationToken =>
                await ArchiveClient.StartAsync(workerPath, cancellationToken));
            string target;
            await using (var adapter = new ArchiveLocationAdapter(runtime))
            {
                await adapter.OpenAsync(source);
                for (int depth = 1; depth <= ProtocolConstants.MaximumNestedArchiveDepth; depth++)
                {
                    FileItem child = Assert.Single(adapter.Items, item => item.Name == "next.bin");
                    Assert.Equal("next.bin", child.Name);
                    Assert.Equal(ArchiveNestedOpenOutcome.Opened,
                        await adapter.OpenNestedArchiveAsync(child).WaitAsync(TimeSpan.FromSeconds(20)));
                }
                Assert.Equal(3, adapter.NestedDepth);
                FileItem folder = Assert.Single(adapter.Items, item => item.IsDirectory && item.Name == "folder");
                Assert.True(adapter.Open(folder));
                FileItem text = Assert.Single(adapter.Items);
                ArchiveEntryPreviewResult preview = await adapter.LoadPreviewAsync(text);
                Assert.Equal(ArchiveEntryPreviewState.Text, preview.State);
                Assert.Equal("child text", preview.Text);
                Assert.Equal(5, adapter.Breadcrumbs.Count);
                Assert.True(adapter.GoUp());
                adapter.SetSearch("next", recursive: false);
                FileItem nestedPayload = Assert.Single(adapter.Items);
                Assert.Equal(ArchiveEntryPreviewState.Binary,
                    (await adapter.LoadPreviewAsync(nestedPayload)).State);
                adapter.SetSearch(string.Empty, recursive: false);

                ArchiveOperationResult integrity = await adapter.TestIntegrityAsync()
                    .WaitAsync(TimeSpan.FromSeconds(20));
                Assert.Equal(ArchiveOperationOutcome.Completed, integrity.Outcome);
                string extraction = Path.Combine(root, "nested extraction with spaces 雨");
                adapter.UpdateSelection([Assert.Single(adapter.Items, item => item.Name == "next.bin")]);
                ArchiveOperationResult extracted = await adapter.ExtractSelectionAsync(extraction)
                    .WaitAsync(TimeSpan.FromSeconds(20));
                Assert.Equal(ArchiveOperationOutcome.Completed, extracted.Outcome);
                Assert.Equal(
                    Convert.ToHexString(SHA256.HashData(deepest)),
                    Convert.ToHexString(SHA256.HashData(
                        await File.ReadAllBytesAsync(Path.Combine(extraction, "next.bin")))));
                Directory.Delete(extraction, recursive: true);

                Assert.Equal(ArchiveNestedOpenOutcome.Refused,
                    await adapter.OpenNestedArchiveAsync(
                        Assert.Single(adapter.Items, item => item.Name == "next.bin")));
                Assert.True(adapter.TryGetPersistentTarget(out target));
                Assert.Equal(sourceHash,
                    Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source))));
            }

            await using (var restored = new ArchiveLocationAdapter(runtime))
            {
                await restored.OpenAsync(target).WaitAsync(TimeSpan.FromSeconds(30));
                Assert.Equal(ArchiveLocationState.Ready, restored.State);
                Assert.Equal(3, restored.NestedDepth);
                Assert.Contains(restored.Items, item => item.Name == "next.bin" && !item.IsDirectory);
                Assert.Contains(restored.Items, item => item.Name == "folder" && item.IsDirectory);
            }
            await runtime.DisposeAsync();
            Assert.Equal([source], Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
            Assert.Equal(sourceHash,
                Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source))));
            await WaitForWorkerSetToReturnToBaselineAsync(priorWorkerIds, TimeSpan.FromSeconds(5));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] CreateZipBytes(string entryName, byte[] content)
        => CreateZipBytes((entryName, content));

    private static async Task WaitForWorkerSetToReturnToBaselineAsync(
        IReadOnlySet<int> priorWorkerIds,
        TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (!HasNewWorkerProcess(priorWorkerIds)) return;
            await Task.Delay(50);
        }
        Assert.False(HasNewWorkerProcess(priorWorkerIds),
            "The real Worker test left a new RainArchives.Worker process running.");
    }

    private static bool HasNewWorkerProcess(IReadOnlySet<int> priorWorkerIds)
    {
        Process[] processes = Process.GetProcessesByName("RainArchives.Worker");
        try
        {
            return processes.Any(process => !priorWorkerIds.Contains(process.Id));
        }
        finally
        {
            foreach (Process process in processes) process.Dispose();
        }
    }

    private static byte[] CreateZipBytes(params (string Name, byte[] Content)[] entries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, byte[] content) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
                entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using Stream stream = entry.Open();
                stream.Write(content);
            }
        }
        return output.ToArray();
    }

    private static ArchiveFingerprint Fingerprint(long length, char hashCharacter) => new(
        length,
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        null,
        new string(hashCharacter, 64));

    public sealed record NestedOpenCall(
        ArchiveSessionId ParentSessionId,
        EntryId EntryId,
        long MaximumBytes,
        Guid? ParentPasswordSlotId,
        Guid? NestedPasswordSlotId);

    public class NestedFakeArchiveClient : DispatchProxy
    {
        private sealed class SessionSpec
        {
            public required ArchiveDescriptor Descriptor { get; init; }
            public required IReadOnlyList<ArchiveEntryDescriptor> Entries { get; init; }
            public Dictionary<EntryId, SessionSpec> Children { get; } = [];
            public string? Password { get; init; }
        }

        private sealed record ActiveSession(SessionSpec Spec, int Depth, long ChainBytes);

        private SessionSpec _root = null!;
        private readonly Dictionary<ArchiveSessionId, ActiveSession> _active = [];
        private readonly Dictionary<Guid, string> _secrets = [];
        private bool _supportsNested = true;
        private bool _malformedNestedResponse;
        private bool _blockNestedOpen;
        private bool _blockChildListing;
        private bool _sourceChanged;

        public IArchiveClient Client { get; private set; } = null!;
        public List<ArchiveSessionId> SessionIds { get; } = [];
        public List<ArchiveSessionId> ClosedSessions { get; } = [];
        public List<NestedOpenCall> NestedOpenCalls { get; } = [];
        public List<Guid> CreatedSecretIds { get; } = [];
        public HashSet<Guid> SettledSecretIds { get; } = [];
        public TaskCompletionSource NestedOpenStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ChildListStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<EntryId> RootEntryIds =>
            _root.Entries.OrderBy(entry => entry.DuplicateOrdinal).Select(entry => entry.EntryId).ToArray();

        public static NestedFakeArchiveClient CreateChain(
            int levels = 2,
            bool supportsNested = true,
            bool rootSolid = false,
            bool rootMultipart = false,
            ArchiveCapability? rootCapabilities = null,
            bool rootEntryEncrypted = false,
            long? rootEntrySize = null,
            string? childPassword = null,
            bool malformedNestedResponse = false,
            bool blockNestedOpen = false,
            bool blockChildListing = false,
            bool sourceChanged = false,
            bool changedChildFingerprint = false,
            bool duplicateRootEntries = false,
            IReadOnlyList<long>? childMaterializedBytes = null)
        {
            IArchiveClient client = DispatchProxy.Create<IArchiveClient, NestedFakeArchiveClient>();
            var fake = (NestedFakeArchiveClient)(object)client;
            fake.Client = client;
            fake._supportsNested = supportsNested;
            fake._malformedNestedResponse = malformedNestedResponse;
            fake._blockNestedOpen = blockNestedOpen;
            fake._blockChildListing = blockChildListing;
            fake._sourceChanged = sourceChanged;

            var specs = new SessionSpec[levels + 1];
            for (int depth = levels; depth >= 0; depth--)
            {
                ArchiveSessionId sessionId = ArchiveSessionId.New();
                fake.SessionIds.Add(sessionId);
                long fingerprintLength = depth > 0
                    && childMaterializedBytes is not null
                    && childMaterializedBytes.Count >= depth
                        ? childMaterializedBytes[depth - 1]
                        : 4_096 - depth * 256;
                char hash = depth == 1 && changedChildFingerprint
                    ? 'F' : (char)('A' + Math.Min(depth, 4));
                ArchiveCapability capabilities = depth == 0 && rootCapabilities is ArchiveCapability configured
                    ? configured
                    : ArchiveCapability.List | ArchiveCapability.Extract | ArchiveCapability.OpenEntryStream
                        | ArchiveCapability.Test;
                bool multipart = depth == 0 && rootMultipart;
                var descriptor = new ArchiveDescriptor(
                    sessionId,
                    "ZIP",
                    "test-provider",
                    capabilities,
                    Fingerprint(fingerprintLength, hash),
                    EntryCount: 1,
                    IsSolid: depth == 0 && rootSolid,
                    HasEncryptedEntries: depth == 0 && rootEntryEncrypted,
                    IsMultipart: multipart,
                    IsReadOnly: true,
                    Warnings: Array.Empty<ArchiveWarning>(),
                    VolumeSetKind: multipart
                        ? ArchiveVolumeSetKind.ConfirmedMultipart
                        : ArchiveVolumeSetKind.Single);
                specs[depth] = new SessionSpec
                {
                    Descriptor = descriptor,
                    Entries = Array.Empty<ArchiveEntryDescriptor>(),
                    Password = depth == 1 ? childPassword : null,
                };
            }

            for (int depth = levels; depth >= 0; depth--)
            {
                EntryId entryId = EntryId.New();
                long size = depth == 0 && rootEntrySize is long configuredSize
                    ? configuredSize
                    : depth < levels
                        ? specs[depth + 1].Descriptor.Fingerprint.Length
                        : 128;
                var entry = new ArchiveEntryDescriptor(
                    entryId,
                    ParentEntryId: null,
                    ProviderOrdinal: 0,
                    RawPath: depth < levels ? $"child-{depth + 1}.data" : "next.bin",
                    DisplayName: depth < levels ? $"child-{depth + 1}.data" : "next.bin",
                    Kind: ArchiveEntryKind.File,
                    OriginalSize: size,
                    CompressedSize: size,
                    ModifiedTimeUtc: null,
                    WindowsAttributes: null,
                    CompressionMethod: null,
                    IntegrityValue: null,
                    IsEncrypted: depth == 0 && rootEntryEncrypted,
                    IsSolid: false,
                    LinkTarget: null,
                    SafetyStatus: EntrySafetyStatus.Safe,
                    SafetyReason: null,
                    NormalizedPath: depth < levels ? $"child-{depth + 1}.data" : "next.bin",
                    DuplicateOrdinal: 0);
                IReadOnlyList<ArchiveEntryDescriptor> entries = depth == 0 && duplicateRootEntries
                    ?
                    [
                        entry,
                        entry with
                        {
                            EntryId = EntryId.New(),
                            ProviderOrdinal = 1,
                            DuplicateOrdinal = 1,
                        },
                    ]
                    : [entry];
                specs[depth] = new SessionSpec
                {
                    Descriptor = specs[depth].Descriptor with { EntryCount = entries.Count },
                    Entries = entries,
                    Password = specs[depth].Password,
                };
                if (depth < levels)
                {
                    foreach (ArchiveEntryDescriptor source in entries)
                        specs[depth].Children.Add(source.EntryId, specs[depth + 1]);
                }
            }
            fake.SessionIds.Reverse();
            fake._root = specs[0];
            return fake;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            args ??= Array.Empty<object>();
            CancellationToken cancellationToken = args.OfType<CancellationToken>().LastOrDefault();
            cancellationToken.ThrowIfCancellationRequested();
            switch (targetMethod.Name)
            {
                case nameof(IArchiveClient.SupportsFeature):
                    string feature = (string)args[0]!;
                    return feature switch
                    {
                        ProtocolFeatures.NestedArchiveOpen => _supportsNested,
                        ProtocolFeatures.PersistentEntryKeys
                            or ProtocolFeatures.RequestCancellation
                            or ProtocolFeatures.SourceFingerprintValidation
                            or ProtocolFeatures.VolumeSetIdentity
                            or ProtocolFeatures.SecretSlots
                            or ProtocolFeatures.SecretSlotDiscard => true,
                        _ => false,
                    };
                case nameof(IArchiveClient.OpenArchiveAsync):
                    _active[_root.Descriptor.SessionId] = new ActiveSession(_root, 0, 0);
                    return Task.FromResult(new OpenArchiveResponse(_root.Descriptor));
                case nameof(IArchiveClient.ListEntriesAsync):
                    return ListEntries(
                        (ArchiveSessionId)args[0]!, (int)args[1]!, (int)args[2]!, cancellationToken);
                case nameof(IArchiveClient.OpenNestedArchiveAsync):
                    return OpenNestedAsync(
                        (ArchiveSessionId)args[0]!,
                        (EntryId)args[1]!,
                        (long)args[2]!,
                        (Guid?)args[3],
                        (Guid?)args[4],
                        cancellationToken);
                case nameof(IArchiveClient.CreateSecretAsync):
                    Guid secretId = Guid.NewGuid();
                    CreatedSecretIds.Add(secretId);
                    _secrets.Add(secretId, new string(((ReadOnlyMemory<char>)args[0]!).Span));
                    return Task.FromResult(secretId);
                case nameof(IArchiveClient.DiscardSecretAsync):
                    Guid discarded = (Guid)args[0]!;
                    _secrets.Remove(discarded);
                    SettledSecretIds.Add(discarded);
                    return Task.FromResult(true);
                case nameof(IArchiveClient.CloseArchiveAsync):
                    ArchiveSessionId closed = (ArchiveSessionId)args[0]!;
                    ClosedSessions.Add(closed);
                    _active.Remove(closed);
                    return Task.FromResult(new CloseArchiveResponse(true));
                case nameof(IAsyncDisposable.DisposeAsync):
                    _active.Clear();
                    _secrets.Clear();
                    return ValueTask.CompletedTask;
                case "add_OperationEventReceived":
                case "remove_OperationEventReceived":
                    return null;
                default:
                    throw new NotSupportedException($"Fake operation not configured: {targetMethod.Name}");
            }
        }

        private async Task<ListEntriesResponse> ListEntries(
            ArchiveSessionId sessionId,
            int offset,
            int limit,
            CancellationToken cancellationToken)
        {
            ActiveSession active = _active[sessionId];
            if (_blockChildListing && active.Depth > 0)
            {
                ChildListStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            IReadOnlyList<ArchiveEntryDescriptor> entries = active.Spec.Entries;
            ArchiveEntryDescriptor[] page = entries.Skip(offset).Take(limit).ToArray();
            int? next = offset + page.Length < entries.Count ? offset + page.Length : null;
            return new ListEntriesResponse(page, offset, entries.Count, next);
        }

        private async Task<OpenNestedArchiveResponse> OpenNestedAsync(
            ArchiveSessionId parentSessionId,
            EntryId entryId,
            long maximumBytes,
            Guid? parentPasswordSlotId,
            Guid? nestedPasswordSlotId,
            CancellationToken cancellationToken)
        {
            NestedOpenCalls.Add(new NestedOpenCall(
                parentSessionId, entryId, maximumBytes, parentPasswordSlotId, nestedPasswordSlotId));
            NestedOpenStarted.TrySetResult();
            if (_blockNestedOpen)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable.");
            }
            if (_sourceChanged)
            {
                throw new ArchiveClientException(new ArchiveError(
                    "source.changed", ArchiveErrorCategory.SourceChanged, "test source change"));
            }

            ActiveSession parent = _active[parentSessionId];
            if (parent.Depth >= ProtocolConstants.MaximumNestedArchiveDepth)
            {
                throw new ArchiveClientException(new ArchiveError(
                    "nested.depth_limit", ArchiveErrorCategory.ResourceLimit, "test depth limit"));
            }
            SessionSpec child = parent.Spec.Children[entryId];
            try
            {
                if (child.Password is not null)
                {
                    if (nestedPasswordSlotId is not Guid slot)
                    {
                        throw new ArchiveClientException(new ArchiveError(
                            "archive.password_required", ArchiveErrorCategory.PasswordRequired,
                            "test password required"));
                    }
                    if (!_secrets.TryGetValue(slot, out string? supplied)
                        || !string.Equals(supplied, child.Password, StringComparison.Ordinal))
                    {
                        throw new ArchiveClientException(new ArchiveError(
                            "archive.incorrect_password", ArchiveErrorCategory.IncorrectPassword,
                            "test incorrect password"));
                    }
                }

                long materialized = child.Descriptor.Fingerprint.Length;
                if (materialized > maximumBytes)
                {
                    throw new ArchiveClientException(new ArchiveError(
                        "nested.declared_size_limit", ArchiveErrorCategory.ResourceLimit,
                        "test byte limit"));
                }
                long chainBytes = parent.ChainBytes + materialized;
                _active[child.Descriptor.SessionId] = new ActiveSession(
                    child, parent.Depth + 1, chainBytes);
                return new OpenNestedArchiveResponse(
                    child.Descriptor,
                    _malformedNestedResponse ? ArchiveSessionId.New() : parentSessionId,
                    entryId,
                    parent.Depth + 1,
                    materialized,
                    chainBytes);
            }
            finally
            {
                Settle(parentPasswordSlotId);
                Settle(nestedPasswordSlotId);
            }
        }

        private void Settle(Guid? slotId)
        {
            if (slotId is not Guid slot) return;
            _secrets.Remove(slot);
            SettledSecretIds.Add(slot);
        }
    }
}
