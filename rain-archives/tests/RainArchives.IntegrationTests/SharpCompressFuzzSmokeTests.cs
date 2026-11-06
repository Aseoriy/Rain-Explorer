using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.IntegrationTests;

[TestClass]
public sealed class SharpCompressFuzzSmokeTests
{
    private const string FuzzRoundsEnvironmentVariable = "RAIN_ARCHIVES_FUZZ_ROUNDS";
    private const int MaximumFuzzRounds = 1_000;
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(4);

    private static readonly MutationCase[] MutationCases =
    [
        new("zip-truncate", ["archives", "zip", "plain.zip"], MutationKind.TruncateTail, 0x1101),
        new("zip-flip", ["archives", "zip", "plain.zip"], MutationKind.FlipPayloadByte, 0x1102),
        new("sevenzip-truncate", ["archives", "7z", "solid.7z"], MutationKind.TruncateTail, 0x2201),
        new("sevenzip-flip", ["archives", "7z", "solid.7z"], MutationKind.FlipPayloadByte, 0x2202),
        new("rar-truncate", ["archives", "rar", "plain.rar"], MutationKind.TruncateTail, 0x3301),
        new("rar-flip", ["archives", "rar", "plain.rar"], MutationKind.FlipPayloadByte, 0x3302),
        new("gzip-truncate", ["archives", "streams", "alpha.txt.gz"], MutationKind.TruncateTail, 0x4401),
        new("gzip-flip", ["archives", "streams", "alpha.txt.gz"], MutationKind.FlipPayloadByte, 0x4402),
        new("tar-gzip-truncate", ["archives", "streams", "sample.tar.gz"], MutationKind.TruncateTail, 0x5501),
        new("tar-gzip-flip", ["archives", "streams", "sample.tar.gz"], MutationKind.FlipPayloadByte, 0x5502),
    ];

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(900_000)]
    public async Task SeededMalformedCorpusMutationsStayBoundedAndWorkerRecovers()
    {
        var rounds = GetConfiguredFuzzRounds();
        var startedAt = DateTimeOffset.UtcNow;
        using var root = new TemporaryDirectory();
        var knownGoodPath = Path.Combine(root.Path, "known-good.zip");
        File.Copy(FixturePath("archives", "zip", "plain.zip"), knownGoodPath);

        await using var client = await WithinAsync(
            ArchiveClient.StartAsync(FindPackagedWorker()),
            "packaged worker startup");

        var caseIndex = 0;
        try
        {
            for (var round = 0; round < rounds; round++)
            {
                foreach (var template in MutationCases)
                {
                    var mutation = template with
                    {
                        Name = $"{template.Name}-round-{round:D4}",
                        Seed = unchecked(template.Seed + (round * 0x1F123BB5)),
                    };
                    var caseRoot = Path.Combine(root.Path, $"case-{caseIndex:D6}-{mutation.Name}");
                    Directory.CreateDirectory(caseRoot);

                    var mutatedPath = Path.Combine(caseRoot, "mutated-input.data");
                    var destinationPath = Path.Combine(caseRoot, "extracted-output");
                    var fixturePath = FixturePath(mutation.FixtureParts);
                    var fixtureBytes = await File.ReadAllBytesAsync(fixturePath);
                    var mutatedBytes = ApplyMutation(fixtureBytes, mutation);
                    await File.WriteAllBytesAsync(mutatedPath, mutatedBytes);

                    await ExerciseMutationAsync(
                        client,
                        mutation,
                        mutatedPath,
                        destinationPath,
                        caseRoot);

                    await ProveWorkerHealthAsync(client, knownGoodPath, caseIndex);
                    caseIndex++;
                }
            }

            TestContext.WriteLine(
                $"Completed {caseIndex} deterministic malformed-archive cases "
                + $"across {rounds} round(s) in {DateTimeOffset.UtcNow - startedAt}.");
        }
        catch
        {
            root.Retain();
            TestContext.WriteLine($"Failure artifacts retained at {root.Path}.");
            throw;
        }
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task SingleVolumeZipEntryReferencingAnotherDiskIsRejectedAndWorkerRecovers()
    {
        using var root = new TemporaryDirectory();
        var knownGoodPath = Path.Combine(root.Path, "known-good.zip");
        File.Copy(FixturePath("archives", "zip", "plain.zip"), knownGoodPath);

        var malformedPath = Path.Combine(root.Path, "invalid-disk-reference.data");
        var bytes = await File.ReadAllBytesAsync(knownGoodPath);
        var lastCentralDirectoryEntry = bytes.AsSpan().LastIndexOf(
            new byte[] { 0x50, 0x4B, 0x01, 0x02 });
        Assert.IsGreaterThanOrEqualTo(0, lastCentralDirectoryEntry);
        bytes[lastCentralDirectoryEntry + 35] = 0x5F;
        await File.WriteAllBytesAsync(malformedPath, bytes);

        await using var client = await WithinAsync(
            ArchiveClient.StartAsync(FindPackagedWorker()),
            "packaged worker startup");
        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await WithinAsync(
                client.OpenArchiveAsync(malformedPath),
                "invalid ZIP disk-reference open"));

        Assert.AreEqual(ArchiveErrorCategory.InvalidArchive, failure.Error.Category);
        Assert.AreEqual("archive.invalid_zip_volume_reference", failure.Error.Code);
        await ProveWorkerHealthAsync(client, knownGoodPath, 0);
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task RarVariableIntegerOverflowIsRejectedAndWorkerRecovers()
    {
        using var root = new TemporaryDirectory();
        var knownGoodPath = Path.Combine(root.Path, "known-good.zip");
        File.Copy(FixturePath("archives", "zip", "plain.zip"), knownGoodPath);

        var template = MutationCases.Single(mutation => mutation.Name == "rar-flip");
        var mutation = template with
        {
            Name = "rar-flip-round-0196",
            Seed = unchecked(template.Seed + (196 * 0x1F123BB5)),
        };
        var malformedPath = Path.Combine(root.Path, "invalid-variable-integer.data");
        var bytes = await File.ReadAllBytesAsync(FixturePath(mutation.FixtureParts));
        await File.WriteAllBytesAsync(malformedPath, ApplyMutation(bytes, mutation));

        await using var client = await WithinAsync(
            ArchiveClient.StartAsync(FindPackagedWorker()),
            "packaged worker startup");
        var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await WithinAsync(
                client.OpenArchiveAsync(malformedPath),
                "invalid RAR variable-integer open"));

        Assert.AreEqual(ArchiveErrorCategory.InvalidArchive, failure.Error.Category);
        Assert.AreEqual("archive.invalid", failure.Error.Code);
        await ProveWorkerHealthAsync(client, knownGoodPath, 0);
    }

    private static int GetConfiguredFuzzRounds()
    {
        var configured = Environment.GetEnvironmentVariable(FuzzRoundsEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return 1;
        }

        if (!int.TryParse(configured, out var rounds)
            || rounds < 1
            || rounds > MaximumFuzzRounds)
        {
            throw new InvalidOperationException(
                $"{FuzzRoundsEnvironmentVariable} must be an integer from 1 through {MaximumFuzzRounds}.");
        }

        return rounds;
    }

    private static async Task ExerciseMutationAsync(
        ArchiveClient client,
        MutationCase mutation,
        string mutatedPath,
        string destinationPath,
        string caseRoot)
    {
        OpenArchiveResponse? opened = null;
        try
        {
            try
            {
                opened = await WithinAsync(
                    client.OpenArchiveAsync(mutatedPath),
                    $"{mutation.Name} open");
            }
            catch (ArchiveClientException exception)
            {
                AssertStructuredFailure(exception, mutation.Name, "open");
                AssertNoStagingTrees(caseRoot);
                return;
            }

            try
            {
                try
                {
                    var listed = await WithinAsync(
                        client.ListEntriesAsync(opened.Archive.SessionId, 0, 128),
                        $"{mutation.Name} list");
                    Assert.AreEqual(0, listed.Offset, mutation.Name);
                    Assert.IsTrue(listed.TotalCount >= listed.Entries.Count, mutation.Name);
                }
                catch (ArchiveClientException exception)
                {
                    AssertStructuredFailure(exception, mutation.Name, "list");
                }

                try
                {
                    var started = await WithinAsync(
                        client.StartIntegrityTestAsync(opened.Archive.SessionId, []),
                        $"{mutation.Name} integrity start");
                    var terminal = await WithinAsync(
                        client.WaitForTerminalAsync(started.OperationId),
                        $"{mutation.Name} integrity");
                    AssertSafeTerminal(terminal, mutation.Name, "integrity");
                }
                catch (ArchiveClientException exception)
                {
                    AssertStructuredFailure(exception, mutation.Name, "integrity");
                }

                AssertNoStagingTrees(caseRoot);

                if (opened.Archive.Capabilities.HasFlag(ArchiveCapability.Extract))
                {
                    try
                    {
                        var started = await WithinAsync(
                            client.StartExtractionAsync(
                                opened.Archive.SessionId,
                                [],
                                destinationPath),
                            $"{mutation.Name} extraction start");
                        var terminal = await WithinAsync(
                            client.WaitForTerminalAsync(started.OperationId),
                            $"{mutation.Name} extraction");
                        AssertSafeTerminal(terminal, mutation.Name, "extraction");

                        if (terminal.Phase is OperationPhase.Completed or OperationPhase.CompletedWithWarnings)
                        {
                            Assert.IsTrue(Directory.Exists(destinationPath),
                                $"{mutation.Name} completed without publishing its destination.");
                        }
                        else
                        {
                            Assert.IsFalse(Directory.Exists(destinationPath),
                                $"{mutation.Name} published a destination after {terminal.Phase}.");
                        }
                    }
                    catch (ArchiveClientException exception)
                    {
                        AssertStructuredFailure(exception, mutation.Name, "extraction");
                        Assert.IsFalse(Directory.Exists(destinationPath),
                            $"{mutation.Name} published a destination after a structured extraction failure.");
                    }

                    AssertNoStagingTrees(caseRoot);
                }
            }
            finally
            {
                await WithinAsync(
                    client.CloseArchiveAsync(opened.Archive.SessionId),
                    $"{mutation.Name} close");
            }
        }
        finally
        {
            if (opened is null)
            {
                AssertNoStagingTrees(caseRoot);
            }
        }
    }

    private static async Task ProveWorkerHealthAsync(
        ArchiveClient client,
        string knownGoodPath,
        int caseIndex)
    {
        var opened = await WithinAsync(
            client.OpenArchiveAsync(knownGoodPath),
            $"health {caseIndex:D2} open");
        try
        {
            var listed = await WithinAsync(
                client.ListEntriesAsync(opened.Archive.SessionId, 0, 128),
                $"health {caseIndex:D2} list");
            Assert.IsTrue(listed.Entries.Any(entry => entry.RawPath == "alpha.txt"),
                $"The packaged worker could not list the known-good fixture after case {caseIndex}.");
        }
        finally
        {
            await WithinAsync(
                client.CloseArchiveAsync(opened.Archive.SessionId),
                $"health {caseIndex:D2} close");
        }
    }

    private static void AssertSafeTerminal(
        OperationEvent terminal,
        string mutationName,
        string operationName)
    {
        Assert.IsTrue(
            terminal.Phase is OperationPhase.Completed
                or OperationPhase.CompletedWithWarnings
                or OperationPhase.Failed
                or OperationPhase.Cancelled,
            $"{mutationName} {operationName} returned non-terminal phase {terminal.Phase}.");
        if (terminal.Phase is OperationPhase.Failed or OperationPhase.Cancelled)
        {
            Assert.IsNotNull(terminal.Error, $"{mutationName} {operationName} had no structured error.");
        }
    }

    private static void AssertStructuredFailure(
        ArchiveClientException exception,
        string mutationName,
        string operationName)
    {
        Assert.AreNotEqual(
            ArchiveErrorCategory.WorkerFailure,
            exception.Error.Category,
            $"{mutationName} {operationName} escaped malformed-input handling as a worker failure.");
        Assert.AreNotEqual(
            ArchiveErrorCategory.ProtocolMismatch,
            exception.Error.Category,
            $"{mutationName} {operationName} broke the worker protocol.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(exception.Error.Code),
            $"{mutationName} {operationName} returned an unstructured error code.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(exception.Error.Message),
            $"{mutationName} {operationName} returned an unstructured error message.");
    }

    private static void AssertNoStagingTrees(string parent)
    {
        var leftovers = Directory
            .EnumerateFileSystemEntries(parent, "*.rainarchives-*.staging", SearchOption.AllDirectories)
            .ToArray();
        Assert.AreEqual(0, leftovers.Length, string.Join(Environment.NewLine, leftovers));
    }

    private static byte[] ApplyMutation(byte[] original, MutationCase mutation)
    {
        return mutation.Kind switch
        {
            MutationKind.TruncateTail => TruncateTail(original, mutation.Seed),
            MutationKind.FlipPayloadByte => FlipPayloadByte(original, mutation.Seed),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation.Kind)),
        };
    }

    private static byte[] TruncateTail(byte[] original, int seed)
    {
        var maximumRemoval = Math.Max(1, original.Length / 3);
        var removal = 1 + (int)((uint)seed % (uint)maximumRemoval);
        var keep = Math.Max(1, original.Length - removal);
        return original[..keep];
    }

    private static byte[] FlipPayloadByte(byte[] original, int seed)
    {
        var mutated = (byte[])original.Clone();
        var protectedPrefixLength = Math.Min(8, Math.Max(1, mutated.Length - 1));
        var payloadLength = mutated.Length - protectedPrefixLength;
        var index = protectedPrefixLength + (int)((uint)seed % (uint)payloadLength);
        var bit = (byte)(1 + ((uint)seed >> 8) % 0x7F);
        mutated[index] ^= bit;
        return mutated;
    }

    private static async Task<T> WithinAsync<T>(Task<T> operation, string description) =>
        await operation.WaitAsync(StepTimeout).ConfigureAwait(false);

    private static string FixturePath(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, "fixtures", "corpus", .. parts]);

    private static string FindPackagedWorker()
    {
        var worker = Path.Combine(AppContext.BaseDirectory, "RainArchives.Worker.exe");
        if (!File.Exists(worker))
        {
            throw new FileNotFoundException(
                "The integration test output does not contain the packaged RainArchives.Worker.exe.",
                worker);
        }

        return worker;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private bool _retain;

        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainArchives.SharpCompressFuzzSmokeTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Retain() => _retain = true;

        public void Dispose()
        {
            if (!_retain && Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed record MutationCase(
        string Name,
        string[] FixtureParts,
        MutationKind Kind,
        int Seed);

    private enum MutationKind
    {
        TruncateTail,
        FlipPayloadByte,
    }
}
