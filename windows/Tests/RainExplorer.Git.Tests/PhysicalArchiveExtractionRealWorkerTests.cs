using System.Diagnostics;
using System.Security.Cryptography;
using RainArchives.Client;
using RainArchives.Contracts;
using RainExplorer.Models;
using RainExplorer.Services;
using Xunit;

namespace RainExplorer.Git.Tests;

[Collection("Archive managed-write real Worker")]
public sealed class PhysicalArchiveExtractionRealWorkerTests
{
    private const string AlphaSha256 =
        "42C82805FB9599A724D099683BB0AD73BA6931A5A859E5B5DD2F2BD43FB00868";
    private const string BetaSha256 =
        "CF6E10CD1871B45A498F644ED8D37A6C935D89153745F5E4F3EEEC0354949A63";
    private const string UnicodeSha256 =
        "B550ABF52B3500720734589CE534AE1E6F5CA0575D7A8E78BA1659972C3CCE3E";
    private const string FixturePassword = "RainArchives-Fixture-2026!";
    private const string WrongFixturePassword = "wrong-fixture-password";
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task SupportedFilesAreContentDetectedAndFullyExtractedInSelectionOrder()
    {
        using var root = new TemporaryDirectory("formats path with spaces 雨");
        var cases = new[]
        {
            new FixtureCase("zip", "plain.zip", "opaque input.data", true),
            new FixtureCase("7z", "solid.7z", "solid-not-a-zip.zip", false),
            new FixtureCase("rar", "plain.rar", "plain-rar-without-extension", false),
            new FixtureCase("streams", "sample.tar.gz", "tar payload.bin", false),
            new FixtureCase("streams", "alpha.txt.gz", "gzip payload.weird", false),
            new FixtureCase("streams", "alpha.txt.bz2", "bzip payload", false),
        };
        string[] sources = cases.Select(item => CopyFixture(root.Path, item)).ToArray();
        var sourceHashes = sources.ToDictionary(path => path, HashFile, StringComparer.Ordinal);
        Assert.True(PhysicalArchiveExtractionService.TryCreatePlan(
            sources,
            out IReadOnlyList<PhysicalArchiveExtractionPlan> plan));

        HashSet<int> priorWorkerIds = SnapshotWorkerProcessIds();
        await using (var runtime = CreateRuntime())
        {
            string recoveryBaseline = await CaptureRecoveryInventoryAsync(runtime);
            await using (var service = new PhysicalArchiveExtractionService(
                             runtime,
                             NoOpArchiveActivitySink.Instance))
            {
                PhysicalArchiveExtractionBatchResult result = await service.ExtractAsync(
                        sources,
                        _ => throw new InvalidOperationException(
                            "Unencrypted fixtures must not request a password."))
                    .WaitAsync(OperationTimeout);

                Assert.Equal(cases.Length, result.RequestedCount);
                Assert.Equal(cases.Length, result.CompletedCount);
                Assert.All(result.Results, item => Assert.True(item.Succeeded));
                for (int index = 0; index < plan.Count; index++)
                {
                    string destination = plan[index].DestinationPath;
                    Assert.Contains(
                        Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories),
                        path => string.Equals(HashFile(path), AlphaSha256, StringComparison.Ordinal));
                    if (!cases[index].HasMultipleEntries) continue;
                    Assert.Equal(BetaSha256, HashFile(Path.Combine(destination, "beta.txt")));
                    Assert.Equal(
                        UnicodeSha256,
                        HashFile(Path.Combine(destination, "unicode-雪.txt")));
                }

                AssertSourceHashes(sourceHashes);
                AssertNoStagingArtifacts(root.Path);
            }

            Assert.Equal(recoveryBaseline, await CaptureRecoveryInventoryAsync(runtime));
        }

        await WaitForWorkerSetToReturnToBaselineAsync(priorWorkerIds);
    }

    [Fact]
    public async Task EncryptedFileUsesOnePasswordAttemptPerExplicitExtraction()
    {
        using var root = new TemporaryDirectory("password path with spaces 雨");
        string source = CopyFixture(
            root.Path,
            new FixtureCase("zip", "aes256.zip", "encrypted payload.zip", true));
        string sourceHash = HashFile(source);
        Assert.True(PhysicalArchiveExtractionService.TryCreatePlan(
            [source],
            out IReadOnlyList<PhysicalArchiveExtractionPlan> firstPlan));
        char[] wrongPassword = WrongFixturePassword.ToCharArray();
        int wrongPromptCount = 0;
        HashSet<int> priorWorkerIds = SnapshotWorkerProcessIds();

        await using (var runtime = CreateRuntime())
        {
            string recoveryBaseline = await CaptureRecoveryInventoryAsync(runtime);
            await using (var service = new PhysicalArchiveExtractionService(
                             runtime,
                             NoOpArchiveActivitySink.Instance))
            {
                PhysicalArchiveExtractionBatchResult failed = await service.ExtractAsync(
                        [source],
                        _ =>
                        {
                            wrongPromptCount++;
                            return wrongPassword;
                        })
                    .WaitAsync(OperationTimeout);

                Assert.Equal(ArchiveOperationOutcome.Failed, Assert.Single(failed.Results).Outcome);
                Assert.Equal(1, wrongPromptCount);
                Assert.All(wrongPassword, character => Assert.Equal('\0', character));
                Assert.False(Directory.Exists(firstPlan[0].DestinationPath));
            }

            char[] correctPassword = FixturePassword.ToCharArray();
            int correctPromptCount = 0;
            await using (var service = new PhysicalArchiveExtractionService(
                             runtime,
                             NoOpArchiveActivitySink.Instance))
            {
                PhysicalArchiveExtractionBatchResult completed = await service.ExtractAsync(
                        [source],
                        _ =>
                        {
                            correctPromptCount++;
                            return correctPassword;
                        })
                    .WaitAsync(OperationTimeout);

                Assert.Equal(
                    ArchiveOperationOutcome.Completed,
                    Assert.Single(completed.Results).Outcome);
                Assert.Equal(1, correctPromptCount);
                Assert.All(correctPassword, character => Assert.Equal('\0', character));
                Assert.Equal(
                    AlphaSha256,
                    HashFile(Path.Combine(firstPlan[0].DestinationPath, "alpha.txt")));
            }

            Assert.Equal(sourceHash, HashFile(source));
            AssertNoStagingArtifacts(root.Path);
            Assert.Equal(recoveryBaseline, await CaptureRecoveryInventoryAsync(runtime));
        }

        await WaitForWorkerSetToReturnToBaselineAsync(priorWorkerIds);
    }

    [Theory]
    [InlineData("7z", "header-encrypted.7z", "header archive.data")]
    [InlineData("rar", "header-encrypted.rar", "header archive.bin")]
    public async Task HeaderEncryptedFileUsesOnePasswordBearingOpenAttempt(
        string group,
        string fixtureName,
        string destinationName)
    {
        using var root = new TemporaryDirectory("header password path with spaces 雨");
        string source = CopyFixture(
            root.Path,
            new FixtureCase(group, fixtureName, destinationName, false));
        string sourceHash = HashFile(source);
        Assert.True(PhysicalArchiveExtractionService.TryCreatePlan(
            [source],
            out IReadOnlyList<PhysicalArchiveExtractionPlan> plan));
        char[] password = FixturePassword.ToCharArray();
        int promptCount = 0;
        HashSet<int> priorWorkerIds = SnapshotWorkerProcessIds();

        await using (var runtime = CreateRuntime())
        {
            string recoveryBaseline = await CaptureRecoveryInventoryAsync(runtime);
            await using (var service = new PhysicalArchiveExtractionService(
                             runtime,
                             NoOpArchiveActivitySink.Instance))
            {
                PhysicalArchiveExtractionBatchResult result = await service.ExtractAsync(
                        [source],
                        _ =>
                        {
                            promptCount++;
                            return password;
                        })
                    .WaitAsync(OperationTimeout);

                Assert.Equal(ArchiveOperationOutcome.Completed, Assert.Single(result.Results).Outcome);
                Assert.Equal(1, promptCount);
                Assert.All(password, character => Assert.Equal('\0', character));
                Assert.Contains(
                    Directory.EnumerateFiles(
                        plan[0].DestinationPath,
                        "*",
                        SearchOption.AllDirectories),
                    path => string.Equals(HashFile(path), AlphaSha256, StringComparison.Ordinal));
                Assert.Equal(sourceHash, HashFile(source));
                AssertNoStagingArtifacts(root.Path);
            }

            Assert.Equal(recoveryBaseline, await CaptureRecoveryInventoryAsync(runtime));
        }

        await WaitForWorkerSetToReturnToBaselineAsync(priorWorkerIds);
    }

    [Fact]
    public async Task HeaderEncryptedWrongPasswordFailsAfterOnePromptWithoutOutput()
    {
        using var root = new TemporaryDirectory("header wrong password path 雨");
        string source = CopyFixture(
            root.Path,
            new FixtureCase("7z", "header-encrypted.7z", "header wrong.data", false));
        string sourceHash = HashFile(source);
        Assert.True(PhysicalArchiveExtractionService.TryCreatePlan(
            [source],
            out IReadOnlyList<PhysicalArchiveExtractionPlan> plan));
        char[] password = WrongFixturePassword.ToCharArray();
        int promptCount = 0;
        HashSet<int> priorWorkerIds = SnapshotWorkerProcessIds();

        await using (var runtime = CreateRuntime())
        {
            string recoveryBaseline = await CaptureRecoveryInventoryAsync(runtime);
            await using (var service = new PhysicalArchiveExtractionService(
                             runtime,
                             NoOpArchiveActivitySink.Instance))
            {
                PhysicalArchiveExtractionBatchResult result = await service.ExtractAsync(
                        [source],
                        _ =>
                        {
                            promptCount++;
                            return password;
                        })
                    .WaitAsync(OperationTimeout);

                Assert.Equal(ArchiveOperationOutcome.Failed, Assert.Single(result.Results).Outcome);
                Assert.Equal(1, promptCount);
                Assert.All(password, character => Assert.Equal('\0', character));
                Assert.False(Directory.Exists(plan[0].DestinationPath));
                Assert.Equal(sourceHash, HashFile(source));
                AssertNoStagingArtifacts(root.Path);
            }

            Assert.Equal(recoveryBaseline, await CaptureRecoveryInventoryAsync(runtime));
        }

        await WaitForWorkerSetToReturnToBaselineAsync(priorWorkerIds);
    }

    [Fact]
    public async Task WorkerRecoveryInventoriesRemainReadOnlyAndProtocolBound()
    {
        HashSet<int> priorWorkerIds = SnapshotWorkerProcessIds();
        await using (var runtime = CreateRuntime())
        {
            IArchiveClient client = await runtime.GetClientAsync(default)
                .WaitAsync(OperationTimeout);
            Assert.True(client.SupportsFeature(
                ProtocolFeatures.ExtractionRecoveryInspection));
            Assert.True(client.SupportsFeature(
                ProtocolFeatures.ArchiveCreationRecoveryInspection));
            Assert.True(client.SupportsFeature(ProtocolFeatures.NestedWorkspaceRecovery));

            string first = await CaptureRecoveryInventoryAsync(runtime);
            string second = await CaptureRecoveryInventoryAsync(runtime);
            Assert.Equal(first, second);
        }

        await WaitForWorkerSetToReturnToBaselineAsync(priorWorkerIds);
    }

    [Fact]
    public async Task NonArchiveCorruptAndMultipartInputsFailClosedWithoutOutput()
    {
        using var root = new TemporaryDirectory("refusal path with spaces 雨");
        string nonArchive = Path.Combine(root.Path, "not really an archive.zip");
        await File.WriteAllTextAsync(nonArchive, "ordinary text, not an archive");
        string corrupt = CopyFixture(
            root.Path,
            new FixtureCase("zip", "stored-corrupt-crc.zip", "corrupt.data", false));
        string split = CopySplitSevenZip(root.Path);
        string[] sources = [nonArchive, corrupt, split];
        var sourceHashes = sources.ToDictionary(path => path, HashFile, StringComparer.Ordinal);
        Assert.True(PhysicalArchiveExtractionService.TryCreatePlan(
            sources,
            out IReadOnlyList<PhysicalArchiveExtractionPlan> plan));
        HashSet<int> priorWorkerIds = SnapshotWorkerProcessIds();

        await using (var runtime = CreateRuntime())
        {
            string recoveryBaseline = await CaptureRecoveryInventoryAsync(runtime);
            await using (var service = new PhysicalArchiveExtractionService(
                             runtime,
                             NoOpArchiveActivitySink.Instance))
            {
                PhysicalArchiveExtractionBatchResult result = await service.ExtractAsync(
                        sources,
                        _ => throw new InvalidOperationException(
                            "Refused fixtures must not request a password."))
                    .WaitAsync(OperationTimeout);

                Assert.Equal(3, result.Results.Count);
                Assert.All(result.Results, item => Assert.False(item.Succeeded));
                Assert.Contains(result.Results, item => item.Outcome == ArchiveOperationOutcome.Unsupported);
                Assert.All(plan, item => Assert.False(Directory.Exists(item.DestinationPath)));
                AssertSourceHashes(sourceHashes);
                AssertNoStagingArtifacts(root.Path);
            }

            Assert.Equal(recoveryBaseline, await CaptureRecoveryInventoryAsync(runtime));
        }

        await WaitForWorkerSetToReturnToBaselineAsync(priorWorkerIds);
    }

    private static ArchiveClientRuntime CreateRuntime()
    {
        string workerPath = Environment.GetEnvironmentVariable(
            "RAINEXPLORER_TEST_WORKER_PATH")
            ?? Path.Combine(AppContext.BaseDirectory, "worker", "RainArchives.Worker.exe");
        Assert.True(File.Exists(workerPath), $"Worker payload missing: {workerPath}");
        return new ArchiveClientRuntime(async cancellationToken =>
            await ArchiveClient.StartAsync(workerPath, cancellationToken));
    }

    private static async Task<string> CaptureRecoveryInventoryAsync(
        ArchiveClientRuntime runtime)
    {
        IArchiveClient client = await runtime.GetClientAsync(default)
            .WaitAsync(OperationTimeout);
        ListExtractionRecoveriesResponse extraction =
            await client.ListExtractionRecoveriesAsync().WaitAsync(OperationTimeout);
        ListArchiveCreationRecoveriesResponse creation =
            await client.ListArchiveCreationRecoveriesAsync().WaitAsync(OperationTimeout);
        ListNestedWorkspaceRecoveriesResponse nested =
            await client.ListNestedWorkspaceRecoveriesAsync().WaitAsync(OperationTimeout);

        return string.Join(
            "|",
            string.Join(",", extraction.Recoveries.Select(item => item.RecoveryId)
                .Order(StringComparer.Ordinal)),
            extraction.ActiveOperationCount,
            extraction.SuspiciousOperationCount,
            extraction.IncompleteJournalCount,
            extraction.IsTruncated,
            extraction.InspectionIncomplete,
            string.Join(",", creation.Recoveries.Select(item => item.RecoveryId)
                .Order(StringComparer.Ordinal)),
            creation.ActiveOperationCount,
            creation.SuspiciousOperationCount,
            creation.IncompleteJournalCount,
            creation.IsTruncated,
            creation.InspectionIncomplete,
            string.Join(",", nested.Recoveries.Select(item => item.RecoveryId)
                .Order(StringComparer.Ordinal)),
            nested.ActiveWorkspaceCount,
            nested.SuspiciousWorkspaceCount,
            nested.IsTruncated,
            nested.InspectionIncomplete);
    }

    private static string CopyFixture(string destinationRoot, FixtureCase item)
    {
        string source = Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            "corpus",
            "archives",
            item.Group,
            item.FileName);
        Assert.True(File.Exists(source), $"Fixture missing: {source}");
        string destination = Path.Combine(destinationRoot, item.DestinationName);
        File.Copy(source, destination);
        return destination;
    }

    private static string CopySplitSevenZip(string destinationRoot)
    {
        string fixtureRoot = Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            "corpus",
            "archives",
            "7z");
        foreach (string source in Directory.EnumerateFiles(fixtureRoot, "split.7z.*"))
            File.Copy(source, Path.Combine(destinationRoot, Path.GetFileName(source)));
        return Path.Combine(destinationRoot, "split.7z.001");
    }

    private static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void AssertSourceHashes(IReadOnlyDictionary<string, string> sourceHashes)
    {
        foreach ((string path, string hash) in sourceHashes)
            Assert.Equal(hash, HashFile(path));
    }

    private static void AssertNoStagingArtifacts(string parent)
    {
        string[] leftovers = Directory
            .EnumerateFileSystemEntries(
                parent,
                "*.rainarchives-*.staging",
                SearchOption.TopDirectoryOnly)
            .ToArray();
        Assert.Empty(leftovers);
    }

    private static HashSet<int> SnapshotWorkerProcessIds() =>
        Process.GetProcessesByName("RainArchives.Worker")
            .Select(process =>
            {
                using (process) return process.Id;
            })
            .ToHashSet();

    private static async Task WaitForWorkerSetToReturnToBaselineAsync(
        IReadOnlySet<int> priorWorkerIds)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(8))
        {
            if (!HasNewWorkerProcess(priorWorkerIds)) return;
            await Task.Delay(50);
        }

        Assert.False(
            HasNewWorkerProcess(priorWorkerIds),
            "The physical extraction test left a new RainArchives.Worker process running.");
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

    private sealed class NoOpArchiveActivitySink : IArchiveActivitySink
    {
        public static NoOpArchiveActivitySink Instance { get; } = new();

        public IArchiveActivityHandle Begin(ActivityOperationKind kind) => new Handle();

        private sealed class Handle : IArchiveActivityHandle
        {
            public void AttachCancel(Action cancel)
            {
            }

            public void MarkCancellationPending()
            {
            }

            public void Report(ActivityOperationUpdate update)
            {
            }
        }
    }

    private sealed record FixtureCase(
        string Group,
        string FileName,
        string DestinationName,
        bool HasMultipleEntries);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory(string label)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"RainExplorer-M6Exit-{label}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
