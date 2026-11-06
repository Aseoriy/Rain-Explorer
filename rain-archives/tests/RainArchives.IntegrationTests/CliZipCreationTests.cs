using System.IO.Compression;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Cli;
using RainArchives.Contracts;

namespace RainArchives.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class CliZipCreationTests
{
    [TestMethod]
    public void ParserBuildsExplicitZipCreationRequestWithoutTreatingArchivePathsAsFilesystemPaths()
    {
        using var root = new TemporaryDirectory();
        var destination = Path.Combine(root.Path, "created.zip");
        var source = Path.Combine(root.Path, "source.txt");
        var worker = Path.Combine(root.Path, "worker.exe");

        var parsed = CliArguments.TryParse(
            [
                "create-zip",
                destination,
                "--file",
                source,
                "Folder/../literal.txt",
                "--preset",
                "maximum",
                "--worker",
                worker,
            ],
            out var options,
            out var error);

        Assert.IsTrue(parsed, error);
        Assert.AreEqual(CliCommand.CreateZip, options.Command);
        Assert.AreEqual(Path.GetFullPath(destination), options.ArchivePath);
        Assert.AreEqual(Path.GetFullPath(worker), options.WorkerPath);
        Assert.AreEqual(ArchiveCompressionPreset.Maximum, options.CompressionPreset);
        Assert.HasCount(1, options.CreationFiles);
        Assert.HasCount(0, options.CreationDirectories);
        Assert.AreEqual(Path.GetFullPath(source), options.CreationFiles[0].SourcePath);
        Assert.AreEqual("Folder/../literal.txt", options.CreationFiles[0].ArchivePath);
    }

    [TestMethod]
    public void ParserRejectsCreationPasswordAndCreationOptionsOnReadCommands()
    {
        Assert.IsFalse(CliArguments.TryParse(
            ["create-zip", @"C:\created.zip", "--password-stdin"],
            out _,
            out var passwordError));
        StringAssert.Contains(passwordError, "does not support encryption");

        Assert.IsFalse(CliArguments.TryParse(
            ["list", @"C:\archive.zip", "--file", @"C:\source.txt", "source.txt"],
            out _,
            out var fileError));
        StringAssert.Contains(fileError, "valid only for ZIP or TAR-family creation");

        Assert.IsFalse(CliArguments.TryParse(
            ["list", @"C:\archive.zip", "--directory", @"C:\source", "source"],
            out _,
            out var directoryError));
        StringAssert.Contains(directoryError, "valid only for ZIP or TAR-family creation");
    }

    [TestMethod]
    public void ParserRejectsAmbiguousCreationFilesystemPathsAndNumericPresets()
    {
        using var root = new TemporaryDirectory();
        var safeDestination = Path.Combine(root.Path, "created.zip");
        var unsafeDestination = Path.Combine(root.Path, "folder", "..", "created.zip");
        var unsafeSource = Path.Combine(root.Path, "source.txt. ");

        Assert.IsFalse(CliArguments.TryParse(
            ["create-zip", unsafeDestination],
            out _,
            out var dotSegmentError));
        StringAssert.Contains(dotSegmentError, "invalid");

        Assert.IsFalse(CliArguments.TryParse(
            ["create-zip", safeDestination, "--file", unsafeSource, "source.txt"],
            out _,
            out var trailingAliasError));
        StringAssert.Contains(trailingAliasError, "invalid");

        foreach (var numericPreset in new[] { "1", "999" })
        {
            Assert.IsFalse(CliArguments.TryParse(
                ["create-zip", safeDestination, "--preset", numericPreset],
                out _,
                out var presetError));
            StringAssert.Contains(presetError, "store, fast, balanced, or maximum");
        }
    }

    [TestMethod]
    public void ParserBuildsPathFreeRecoveryDiagnosticCommands()
    {
        var worker = Path.Combine(Path.GetTempPath(), "worker.exe");
        var recoveryId = new string('a', 64);

        Assert.IsTrue(CliArguments.TryParse(
            ["recovery-list", "--worker", worker],
            out var list,
            out var listError), listError);
        Assert.AreEqual(CliCommand.RecoveryList, list.Command);
        Assert.AreEqual(string.Empty, list.ArchivePath);
        Assert.IsNull(list.RecoveryId);

        Assert.IsTrue(CliArguments.TryParse(
            ["recovery-inspect", recoveryId, "--worker", worker],
            out var inspect,
            out var inspectError), inspectError);
        Assert.AreEqual(CliCommand.RecoveryInspect, inspect.Command);
        Assert.AreEqual(recoveryId, inspect.RecoveryId);
        Assert.AreEqual(string.Empty, inspect.ArchivePath);
    }

    [TestMethod]
    public void ParserRejectsInvalidRecoveryIdentifiersAndUnrelatedOptions()
    {
        Assert.IsFalse(CliArguments.TryParse(
            ["recovery-inspect", "../not-an-id"],
            out _,
            out var invalidId));
        StringAssert.Contains(invalidId, "lowercase hexadecimal");

        Assert.IsFalse(CliArguments.TryParse(
            ["recovery-inspect", new string('A', 64)],
            out _,
            out var uppercaseId));
        StringAssert.Contains(uppercaseId, "lowercase hexadecimal");

        Assert.IsFalse(CliArguments.TryParse(
            ["recovery-list", @"C:\not-accepted"],
            out _,
            out var positionalPath));
        StringAssert.Contains(positionalPath, "does not accept");

        Assert.IsFalse(CliArguments.TryParse(
            ["recovery-list", "--password-stdin"],
            out _,
            out var password));
        StringAssert.Contains(password, "do not accept");
    }

    [TestMethod]
    public void ParserBuildsExplicitRevisionBoundRecoveryDisposition()
    {
        var recoveryId = new string('b', 64);
        var recordedAtUtc = new DateTimeOffset(2026, 8, 23, 12, 34, 56, TimeSpan.Zero);

        var parsed = CliArguments.TryParse(
            [
                "recovery-apply",
                recoveryId,
                "discard-owned-staging",
                "--expected-phase",
                "Writing",
                "--expected-sequence",
                "2",
                "--expected-recorded-at",
                recordedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                "--confirm",
                recoveryId,
            ],
            out var options,
            out var error);

        Assert.IsTrue(parsed, error);
        Assert.AreEqual(CliCommand.RecoveryApply, options.Command);
        Assert.AreEqual(string.Empty, options.ArchivePath);
        Assert.AreEqual(recoveryId, options.RecoveryId);
        Assert.IsNotNull(options.RecoveryDisposition);
        Assert.AreEqual(
            ArchiveCreationRecoveryDispositionAction.DiscardOwnedStaging,
            options.RecoveryDisposition.Action);
        Assert.AreEqual(
            ArchiveCreationRecoveryPhase.Writing,
            options.RecoveryDisposition.ExpectedPhase);
        Assert.AreEqual(2, options.RecoveryDisposition.ExpectedSequence);
        Assert.IsTrue(recordedAtUtc.Equals(options.RecoveryDisposition.ExpectedRecordedAtUtc));
    }

    [TestMethod]
    public void ParserRefusesImplicitOrStaleRecoveryDispositionRequests()
    {
        var recoveryId = new string('c', 64);
        const string recordedAtUtc = "2026-08-23T12:34:56.0000000+00:00";
        var validPrefix = new[]
        {
            "recovery-apply",
            recoveryId,
            "commit-owned-staging",
            "--expected-phase",
            "CommitIntent",
            "--expected-sequence",
            "4",
            "--expected-recorded-at",
            recordedAtUtc,
        };

        Assert.IsFalse(CliArguments.TryParse(
            validPrefix,
            out _,
            out var missingConfirmation));
        StringAssert.Contains(missingConfirmation, "--confirm");

        Assert.IsFalse(CliArguments.TryParse(
            [.. validPrefix, "--confirm", new string('d', 64)],
            out _,
            out var wrongConfirmation));
        StringAssert.Contains(wrongConfirmation, "exactly repeat");

        var staleSequence = validPrefix.ToArray();
        staleSequence[6] = "3";
        Assert.IsFalse(CliArguments.TryParse(
            [.. staleSequence, "--confirm", recoveryId],
            out _,
            out var sequenceError));
        StringAssert.Contains(sequenceError, "must match");

        var unknownAction = validPrefix.ToArray();
        unknownAction[2] = "delete-everything";
        Assert.IsFalse(CliArguments.TryParse(
            [.. unknownAction, "--confirm", recoveryId],
            out _,
            out var actionError));
        StringAssert.Contains(actionError, "action must be");

        Assert.IsFalse(CliArguments.TryParse(
            ["recovery-inspect", recoveryId, "--confirm", recoveryId],
            out _,
            out var unrelatedOption));
        StringAssert.Contains(unrelatedOption, "valid only for recovery-apply or nested-recovery-discard");
    }

    [TestMethod]
    public void ParserBuildsExactNestedRecoveryReviewAndDiscardCommands()
    {
        var recoveryId = new string('d', 64);
        var revision = new string('e', 64);

        Assert.IsTrue(CliArguments.TryParse(
            ["nested-recovery-list"],
            out var list,
            out var listError), listError);
        Assert.AreEqual(CliCommand.NestedRecoveryList, list.Command);
        Assert.AreEqual(string.Empty, list.ArchivePath);

        Assert.IsTrue(CliArguments.TryParse(
            ["nested-recovery-inspect", recoveryId],
            out var inspect,
            out var inspectError), inspectError);
        Assert.AreEqual(CliCommand.NestedRecoveryInspect, inspect.Command);
        Assert.AreEqual(recoveryId, inspect.RecoveryId);

        Assert.IsTrue(CliArguments.TryParse(
            [
                "nested-recovery-discard",
                recoveryId,
                "--expected-revision",
                revision,
                "--expected-source-count",
                "2",
                "--expected-total-bytes",
                "17",
                "--confirm",
                recoveryId,
            ],
            out var discard,
            out var discardError), discardError);
        Assert.AreEqual(CliCommand.NestedRecoveryDiscard, discard.Command);
        Assert.IsNotNull(discard.NestedRecoveryDiscard);
        Assert.IsTrue(string.Equals(
            revision,
            discard.NestedRecoveryDiscard.ExpectedRevision,
            StringComparison.Ordinal));
        Assert.AreEqual(2, discard.NestedRecoveryDiscard.ExpectedSourceCount);
        Assert.AreEqual(17L, discard.NestedRecoveryDiscard.ExpectedTotalSourceBytes);
    }

    [TestMethod]
    public void ParserRejectsUnconfirmedOrOutOfBoundsNestedRecoveryDiscard()
    {
        var recoveryId = new string('f', 64);
        var revision = new string('1', 64);
        var valid = new[]
        {
            "nested-recovery-discard",
            recoveryId,
            "--expected-revision",
            revision,
            "--expected-source-count",
            "1",
            "--expected-total-bytes",
            "10",
        };

        Assert.IsFalse(CliArguments.TryParse(valid, out _, out var missingConfirmation));
        StringAssert.Contains(missingConfirmation, "--confirm");

        Assert.IsFalse(CliArguments.TryParse(
            [.. valid, "--confirm", new string('2', 64)],
            out _,
            out var wrongConfirmation));
        StringAssert.Contains(wrongConfirmation, "exactly repeat");

        var excessiveCount = valid.ToArray();
        excessiveCount[5] = (ProtocolConstants.MaximumOpenSessions + 1).ToString();
        Assert.IsFalse(CliArguments.TryParse(
            [.. excessiveCount, "--confirm", recoveryId],
            out _,
            out var countError));
        StringAssert.Contains(countError, "outside");
    }

    [TestMethod]
    public void ParserBuildsExactNestedRecoveryReopenEvidenceAndAllowsPasswordStdin()
    {
        var recoveryId = new string('a', 64);
        var revision = new string('b', 64);
        var sourceId = new string('c', 64);
        var hash = new string('d', 64);

        Assert.IsTrue(CliArguments.TryParse(
            [
                "nested-recovery-reopen",
                recoveryId,
                sourceId,
                "--expected-revision",
                revision,
                "--expected-source-count",
                "2",
                "--expected-total-bytes",
                "17",
                "--expected-source-length",
                "10",
                "--expected-content-sha256",
                hash,
                "--password-stdin",
            ],
            out var options,
            out var error), error);

        Assert.AreEqual(CliCommand.NestedRecoveryReopen, options.Command);
        Assert.AreEqual(string.Empty, options.ArchivePath);
        Assert.IsTrue(options.PasswordFromStandardInput);
        Assert.IsNotNull(options.NestedRecoveryReopen);
        var reopen = options.NestedRecoveryReopen!;
        var actualRevision = reopen.ExpectedRevision;
        var actualHash = reopen.ExpectedContentSha256;
        Assert.AreEqual(revision, actualRevision);
        Assert.AreEqual(2, reopen.ExpectedSourceCount);
        Assert.AreEqual(17L, reopen.ExpectedTotalSourceBytes);
        Assert.AreEqual(sourceId, reopen.SourceId);
        Assert.AreEqual(10L, reopen.ExpectedSourceLength);
        Assert.AreEqual(hash, actualHash);
    }

    [TestMethod]
    public void ParserRejectsIncompleteOrInvalidNestedRecoveryReopenEvidence()
    {
        var recoveryId = new string('e', 64);
        var sourceId = new string('f', 64);
        var revision = new string('1', 64);
        var hash = new string('2', 64);
        var common = new[]
        {
            "nested-recovery-reopen",
            recoveryId,
            sourceId,
            "--expected-revision",
            revision,
            "--expected-source-count",
            "1",
            "--expected-total-bytes",
            "10",
            "--expected-source-length",
            "10",
            "--expected-content-sha256",
            hash,
        };

        Assert.IsTrue(CliArguments.TryParse(common, out _, out var validError), validError);

        var missingHash = common[..^2];
        Assert.IsFalse(CliArguments.TryParse(missingHash, out _, out var hashError));
        StringAssert.Contains(hashError, "content-sha256");

        var excessiveLength = common.ToArray();
        excessiveLength[10] = "11";
        Assert.IsFalse(CliArguments.TryParse(excessiveLength, out _, out var lengthError));
        StringAssert.Contains(lengthError, "source-length");

        var discardWithReopenOption = new[]
        {
            "nested-recovery-discard",
            recoveryId,
            "--expected-revision",
            revision,
            "--expected-source-count",
            "1",
            "--expected-total-bytes",
            "10",
            "--expected-source-length",
            "10",
            "--confirm",
            recoveryId,
        };
        Assert.IsFalse(CliArguments.TryParse(
            discardWithReopenOption,
            out _,
            out var optionError));
        StringAssert.Contains(optionError, "reopen");
    }

    [TestMethod]
    public void ParserBuildsFeatureGatedSupportDiagnosticsCommandWithoutPositionalData()
    {
        Assert.IsTrue(CliArguments.TryParse(
            ["support-diagnostics", "--worker", "worker.exe"],
            out var options,
            out var error), error);
        Assert.AreEqual(CliCommand.SupportDiagnostics, options.Command);
        Assert.AreEqual(string.Empty, options.ArchivePath);
        Assert.IsFalse(options.PasswordFromStandardInput);

        Assert.IsFalse(CliArguments.TryParse(
            ["support-diagnostics", "archive.zip"],
            out _,
            out var positionalError));
        StringAssert.Contains(positionalError, "does not accept");

        Assert.IsFalse(CliArguments.TryParse(
            ["support-diagnostics", "--password-stdin"],
            out _,
            out var passwordError));
        StringAssert.Contains(passwordError, "do not accept");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task DiagnosticCliCreatesZipThroughPackagedWorker()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source.txt");
        var destination = Path.Combine(root.Path, "created.zip");
        await File.WriteAllTextAsync(source, "CLI ZIP creation");

        var exitCode = await CliProgram.RunAsync(
            [
                "create-zip",
                destination,
                "--file",
                source,
                "Folder/source.txt",
                "--preset",
                "fast",
                "--worker",
                GetWorkerPath(),
            ]);

        Assert.AreEqual(0, exitCode);
        using var stream = File.OpenRead(destination);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = archive.GetEntry("Folder/source.txt");
        Assert.IsNotNull(entry);
        using var reader = new StreamReader(entry.Open());
        Assert.AreEqual("CLI ZIP creation", await reader.ReadToEndAsync());
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task DiagnosticCliCreatesBoundedDirectoryTreeThroughPackagedWorker()
    {
        using var root = new TemporaryDirectory();
        var source = Path.Combine(root.Path, "source");
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        await File.WriteAllTextAsync(Path.Combine(source, "rain.txt"), "CLI tree creation");
        var destination = Path.Combine(root.Path, "tree.zip");

        var exitCode = await CliProgram.RunAsync(
            [
                "create-zip",
                destination,
                "--directory",
                source,
                "ArchiveRoot",
                "--worker",
                GetWorkerPath(),
            ]);

        Assert.AreEqual(0, exitCode);
        using var stream = File.OpenRead(destination);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.IsNotNull(archive.GetEntry("ArchiveRoot/"));
        Assert.IsNotNull(archive.GetEntry("ArchiveRoot/empty/"));
        using var reader = new StreamReader(archive.GetEntry("ArchiveRoot/rain.txt")!.Open());
        Assert.AreEqual("CLI tree creation", await reader.ReadToEndAsync());
    }

    private static string GetWorkerPath()
    {
        var worker = Path.Combine(AppContext.BaseDirectory, "RainArchives.Worker.exe");
        if (!File.Exists(worker))
        {
            throw new FileNotFoundException(
                "The integration test output does not contain RainArchives.Worker.exe.",
                worker);
        }

        return worker;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainArchives.CliZipCreationTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
