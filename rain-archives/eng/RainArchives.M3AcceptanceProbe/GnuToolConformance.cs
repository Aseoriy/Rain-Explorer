using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.M3AcceptanceProbe;

internal static partial class Program
{
    private static readonly TimeSpan ExternalToolTimeout = TimeSpan.FromSeconds(45);

    private static async Task<int> RunGnuToolConformanceMainAsync(string[] args)
    {
        if (!TryParseGnuToolArguments(args, out var options))
        {
            Console.Error.WriteLine(
                "Usage: RainArchives.M3AcceptanceProbe --package <artifact-root> "
                + "--evidence <new-directory> --gnu-tool-conformance <tool-bin-root> "
                + "[--winrar <WinRAR.exe>] [--libarchive <bsdtar.exe>]");
            return 2;
        }

        var evidenceRoot = Path.GetFullPath(options.EvidenceRoot);
        if (Directory.Exists(evidenceRoot) || File.Exists(evidenceRoot))
        {
            Console.Error.WriteLine("The evidence directory must not already exist.");
            return 2;
        }

        Directory.CreateDirectory(evidenceRoot);
        try
        {
            var result = await RunGnuToolConformanceAsync(options, evidenceRoot).ConfigureAwait(false);
            await WriteEvidenceAsync(
                    Path.Combine(evidenceRoot, "gnu-tool-conformance.json"),
                    result)
                .ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception)
        {
            var failure = new FailureEvidence(
                DateTimeOffset.UtcNow,
                exception.GetType().FullName ?? exception.GetType().Name,
                Sanitize(exception.Message),
                exception.StackTrace is null ? null : Sanitize(exception.StackTrace));
            await WriteEvidenceAsync(
                    Path.Combine(evidenceRoot, "gnu-tool-conformance-failure.json"),
                    failure)
                .ConfigureAwait(false);
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task<GnuToolConformanceEvidence> RunGnuToolConformanceAsync(
        GnuToolProbeOptions options,
        string evidenceRoot)
    {
        var package = await ValidateAliasPackageAsync(options.PackageArtifactRoot).ConfigureAwait(false);
        var toolRoot = Path.GetFullPath(options.ToolBinRoot);
        RequireDirectory(toolRoot, "GNU tool bin root");
        var tarPath = Path.Combine(toolRoot, "tar.exe");
        var gzipPath = Path.Combine(toolRoot, "gzip.exe");
        var bzip2Path = Path.Combine(toolRoot, "bzip2.exe");
        RequireFile(tarPath, "GNU tar executable");
        RequireFile(gzipPath, "GNU gzip executable");
        RequireFile(bzip2Path, "GNU bzip2 executable");

        var tarIdentity = await ReadTextToolIdentityAsync(tarPath, "--version", "tar (GNU tar)")
            .ConfigureAwait(false);
        var gzipIdentity = await ReadTextToolIdentityAsync(gzipPath, "--version", "gzip ")
            .ConfigureAwait(false);
        var bzip2Identity = await ReadBZip2IdentityAsync(bzip2Path).ConfigureAwait(false);
        ExternalToolIdentity? winRarIdentity = null;
        IReadOnlyList<WinRarFormatEvidence> winRarChecks = [];
        if (!string.IsNullOrWhiteSpace(options.WinRarPath))
        {
            var winRarPath = Path.GetFullPath(options.WinRarPath);
            RequireFile(winRarPath, "WinRAR executable");
            var version = FileVersionInfo.GetVersionInfo(winRarPath).ProductVersion;
            Require(!string.IsNullOrWhiteSpace(version), "WinRAR has no product version.");
            winRarIdentity = new ExternalToolIdentity(
                winRarPath,
                new FileInfo(winRarPath).Length,
                Sha256File(winRarPath),
                $"WinRAR {version}");
        }

        ExternalToolIdentity? libArchiveIdentity = null;
        IReadOnlyList<LibArchiveFormatEvidence> libArchiveChecks = [];
        if (!string.IsNullOrWhiteSpace(options.LibArchivePath))
        {
            var libArchivePath = Path.GetFullPath(options.LibArchivePath);
            RequireFile(libArchivePath, "libarchive bsdtar executable");
            libArchiveIdentity = await ReadTextToolIdentityAsync(
                    libArchivePath,
                    "--version",
                    "bsdtar ")
                .ConfigureAwait(false);
        }

        var corpusRoot = Path.Combine(evidenceRoot, "corpus");
        Directory.CreateDirectory(corpusRoot);
        var formats = await ExerciseFormatsAsync(package.WorkerPath, corpusRoot).ConfigureAwait(false);
        var layoutFormats = await ExerciseTarLayoutCorpusAsync(package.WorkerPath, corpusRoot)
            .ConfigureAwait(false);
        var sourcePath = Path.Combine(corpusRoot, "manual-creation-雨.txt");
        var sourceHash = Sha256File(sourcePath);
        var checks = new List<GnuFormatEvidence>
        {
            await ValidateGnuTarAsync(
                    tarPath,
                    corpusRoot,
                    "TAR",
                    "clean-machine.tar",
                    "plain")
                .ConfigureAwait(false),
            await ValidateGnuTarAsync(
                    tarPath,
                    corpusRoot,
                    "TAR.GZ",
                    "clean-machine.tar.gz",
                    "gzip")
                .ConfigureAwait(false),
            await ValidateGnuTarAsync(
                    tarPath,
                    corpusRoot,
                    "TAR.BZ2",
                    "clean-machine.tar.bz2",
                    "bzip2")
                .ConfigureAwait(false),
            await ValidateGnuRawAsync(
                    gzipPath,
                    corpusRoot,
                    "GZIP",
                    "clean-machine-gzip.gz",
                    ["-t"],
                    ["-dc"])
                .ConfigureAwait(false),
            await ValidateGnuRawAsync(
                    bzip2Path,
                    corpusRoot,
                    "BZIP2",
                    "clean-machine-bzip2.bz2",
                    ["-t"],
                    ["-dc"])
                .ConfigureAwait(false),
        };

        foreach (var check in checks)
        {
            Require(check.ExtractedSha256 == sourceHash, $"GNU {check.Format} payload hash changed.");
            var rain = formats.Single(item => item.Format == check.Format);
            Require(rain.ArchiveSha256 == check.ArchiveSha256, $"GNU {check.Format} validation changed the archive.");
        }

        if (winRarIdentity is not null)
        {
            winRarChecks = await ValidateWinRarAsync(
                    winRarIdentity.Path,
                    corpusRoot,
                    sourceHash)
                .ConfigureAwait(false);
            Require(
                Sha256File(winRarIdentity.Path) == winRarIdentity.Sha256,
                "WinRAR changed during conformance.");
        }

        if (libArchiveIdentity is not null)
        {
            libArchiveChecks = await ValidateLibArchiveAsync(
                    libArchiveIdentity.Path,
                    corpusRoot,
                    sourceHash,
                    layoutFormats)
                .ConfigureAwait(false);
            Require(
                Sha256File(libArchiveIdentity.Path) == libArchiveIdentity.Sha256,
                "libarchive bsdtar changed during conformance.");
        }

        Require(Sha256File(tarPath) == tarIdentity.Sha256, "GNU tar changed during conformance.");
        Require(Sha256File(gzipPath) == gzipIdentity.Sha256, "GNU gzip changed during conformance.");
        Require(Sha256File(bzip2Path) == bzip2Identity.Sha256, "GNU bzip2 changed during conformance.");
        Require(Sha256File(sourcePath) == sourceHash, "The GNU conformance source changed.");
        Require(CountStaging(evidenceRoot) == 0, "GNU conformance left Rain staging residue.");
        var launchedExternalProcessIds = checks
            .SelectMany(check => new[] { check.IntegrityOrListProcessId, check.ExtractionProcessId })
            .Concat(winRarChecks.SelectMany(check => new[] { check.IntegrityProcessId, check.ExtractionProcessId }))
            .Concat(libArchiveChecks.SelectMany(check => new[] { check.ListProcessId, check.ExtractionProcessId }))
            .Distinct()
            .OrderBy(processId => processId)
            .ToArray();
        var remainingExternalProcessIds = launchedExternalProcessIds
            .Where(IsProcessRunning)
            .ToArray();
        Require(remainingExternalProcessIds.Length == 0, "An exact launched external-tool process remained.");

        int recoveries;
        await using (var client = await ArchiveClient.StartAsync(package.WorkerPath)
                         .WaitAsync(OperationTimeout)
                         .ConfigureAwait(false))
        {
            recoveries = (await client.ListArchiveCreationRecoveriesAsync().ConfigureAwait(false))
                .Recoveries.Count;
        }
        Require(recoveries == 0, "GNU conformance found a creation recovery record.");
        await WaitForNoRainProcessesAsync().ConfigureAwait(false);
        var remainingProcesses = GetRainProcesses();
        Require(remainingProcesses.Count == 0, "Rain processes remained after GNU conformance.");

        return new GnuToolConformanceEvidence(
            SchemaVersion: 1,
            RecordedUtc: DateTimeOffset.UtcNow,
            Protocol: ExpectedProtocol,
            SourceInputCount: ExpectedSourceInputs,
            SourceSha256: ExpectedSourceSha256,
            PackageArtifactRoot: package.ArtifactRoot,
            ManifestSha256: package.ManifestSha256,
            WorkerSha256: package.WorkerSha256,
            ToolBinRoot: toolRoot,
            Tar: tarIdentity,
            GZip: gzipIdentity,
            BZip2: bzip2Identity,
            WinRar: winRarIdentity,
            LibArchive: libArchiveIdentity,
            CorpusSourceSha256: sourceHash,
            RainFormats: formats,
            RainLayoutFormats: layoutFormats,
            GnuChecks: checks,
            WinRarChecks: winRarChecks,
            LibArchiveChecks: libArchiveChecks,
            LaunchedExternalProcessIds: launchedExternalProcessIds,
            RemainingLaunchedExternalProcessIds: remainingExternalProcessIds,
            RecoveryRecords: recoveries,
            RemainingStagingArtifacts: 0,
            RemainingRainProcesses: remainingProcesses);
    }

    private static async Task<IReadOnlyList<TarLayoutEvidence>> ExerciseTarLayoutCorpusAsync(
        string workerPath,
        string corpusRoot)
    {
        var sourceRoot = Path.Combine(corpusRoot, "pax-layout-source");
        var nestedRoot = Path.Combine(sourceRoot, "nested");
        var emptyRoot = Path.Combine(sourceRoot, "empty");
        Directory.CreateDirectory(emptyRoot);
        Directory.CreateDirectory(nestedRoot);
        var zeroPath = Path.Combine(sourceRoot, "zero.bin");
        await File.WriteAllBytesAsync(zeroPath, []).ConfigureAwait(false);
        var longLeaf = $"pax-{new string('x', 112)}-雨.txt";
        var longPath = Path.Combine(nestedRoot, longLeaf);
        var longBytes = Encoding.UTF8.GetBytes(
            "Rain Archives PAX layout fixture — fractional timestamp — pluie — 雨\n");
        await File.WriteAllBytesAsync(longPath, longBytes).ConfigureAwait(false);
        var requestedTime = new DateTimeOffset(2026, 8, 26, 12, 34, 56, TimeSpan.Zero).AddTicks(1_234);
        File.SetLastWriteTimeUtc(zeroPath, requestedTime.UtcDateTime);
        File.SetLastWriteTimeUtc(longPath, requestedTime.UtcDateTime);
        Directory.SetLastWriteTimeUtc(emptyRoot, requestedTime.UtcDateTime);
        Directory.SetLastWriteTimeUtc(nestedRoot, requestedTime.UtcDateTime);
        Directory.SetLastWriteTimeUtc(sourceRoot, requestedTime.UtcDateTime);
        var sourceTime = new DateTimeOffset(File.GetLastWriteTimeUtc(longPath));
        Require(sourceTime == requestedTime, "The owned NTFS source did not retain the PAX timestamp fixture.");
        Require(new DateTimeOffset(File.GetLastWriteTimeUtc(zeroPath)) == requestedTime,
            "The zero-byte source did not retain the PAX timestamp fixture.");
        Require(new DateTimeOffset(Directory.GetLastWriteTimeUtc(emptyRoot)) == requestedTime
            && new DateTimeOffset(Directory.GetLastWriteTimeUtc(nestedRoot)) == requestedTime
            && new DateTimeOffset(Directory.GetLastWriteTimeUtc(sourceRoot)) == requestedTime,
            "The owned source directories did not retain the PAX timestamp fixture.");

        var longEntryPath = $"Mapped/nested/{longLeaf}";
        var expectedDirectories = new[] { "Mapped/", "Mapped/empty/", "Mapped/nested/" };
        var expectedFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Mapped/zero.bin"] = Sha256File(zeroPath),
            [longEntryPath] = Sha256File(longPath),
        };

        await using var client = await ArchiveClient.StartAsync(workerPath)
            .WaitAsync(OperationTimeout)
            .ConfigureAwait(false);
        var directory = new ArchiveCreationDirectory(sourceRoot, "Mapped");
        var cases = new[]
        {
            (Format: "TAR-PAX", Archive: "pax-layout.tar"),
            (Format: "TAR.GZ-PAX", Archive: "pax-layout.tar.gz"),
            (Format: "TAR.BZ2-PAX", Archive: "pax-layout.tar.bz2"),
        };
        var results = new List<TarLayoutEvidence>();
        foreach (var item in cases)
        {
            var archivePath = Path.Combine(corpusRoot, item.Archive);
            var started = item.Format switch
            {
                "TAR-PAX" => await client.StartTarTreeCreationAsync(
                        archivePath,
                        [],
                        [directory])
                    .WaitAsync(OperationTimeout)
                    .ConfigureAwait(false),
                "TAR.GZ-PAX" => await client.StartTarGZipTreeCreationAsync(
                        archivePath,
                        [],
                        [directory],
                        ArchiveCompressionPreset.Maximum)
                    .WaitAsync(OperationTimeout)
                    .ConfigureAwait(false),
                "TAR.BZ2-PAX" => await client.StartTarBZip2TreeCreationAsync(
                        archivePath,
                        [],
                        [directory],
                        ArchiveCompressionPreset.Balanced)
                    .WaitAsync(OperationTimeout)
                    .ConfigureAwait(false),
                _ => throw new InvalidDataException("Unknown TAR layout format."),
            };
            results.Add(await CompleteAndVerifyTarLayoutAsync(
                    client,
                    started,
                    item.Format,
                    archivePath,
                    expectedDirectories,
                    expectedFiles,
                    longEntryPath,
                    sourceTime)
                .ConfigureAwait(false));
        }

        Require(Sha256File(zeroPath) == expectedFiles["Mapped/zero.bin"], "The zero-byte layout source changed.");
        Require(Sha256File(longPath) == expectedFiles[longEntryPath], "The long-name layout source changed.");
        Require(new DateTimeOffset(File.GetLastWriteTimeUtc(zeroPath)) == sourceTime
            && new DateTimeOffset(File.GetLastWriteTimeUtc(longPath)) == sourceTime
            && new DateTimeOffset(Directory.GetLastWriteTimeUtc(emptyRoot)) == sourceTime
            && new DateTimeOffset(Directory.GetLastWriteTimeUtc(nestedRoot)) == sourceTime
            && new DateTimeOffset(Directory.GetLastWriteTimeUtc(sourceRoot)) == sourceTime,
            "A PAX layout source timestamp changed.");
        Require(CountStaging(corpusRoot) == 0, "The TAR layout corpus left Rain staging residue.");
        return results;
    }

    private static async Task<TarLayoutEvidence> CompleteAndVerifyTarLayoutAsync(
        ArchiveClient client,
        StartOperationResponse started,
        string format,
        string archivePath,
        IReadOnlyList<string> expectedDirectories,
        IReadOnlyDictionary<string, string> expectedFiles,
        string longEntryPath,
        DateTimeOffset expectedLongEntryTime)
    {
        var terminal = await client.WaitForTerminalAsync(started.OperationId)
            .WaitAsync(OperationTimeout)
            .ConfigureAwait(false);
        Require(
            terminal.Phase is OperationPhase.Completed or OperationPhase.CompletedWithWarnings,
            $"{format} layout creation failed: {terminal.Error?.Code} {terminal.Error?.Message}");
        Require(terminal.Error is null, $"{format} layout creation returned an error.");
        RequireFile(archivePath, $"{format} layout destination");
        var archiveHash = Sha256File(archivePath);
        var expectedPaths = expectedDirectories
            .Concat(expectedFiles.Keys)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var opened = await client.OpenArchiveAsync(archivePath)
            .WaitAsync(OperationTimeout)
            .ConfigureAwait(false);
        IReadOnlyList<string> listedPaths;
        DateTimeOffset? rainLongEntryTime;
        try
        {
            var expectedRainFormat = format switch
            {
                "TAR-PAX" => "tar",
                "TAR.GZ-PAX" => "tar.gz",
                "TAR.BZ2-PAX" => "tar.bz2",
                _ => throw new InvalidDataException("Unknown TAR layout format."),
            };
            Require(opened.Archive.Format == expectedRainFormat, $"{format} reopened as the wrong format.");
            var listed = await client.ListEntriesAsync(opened.Archive.SessionId, 0, 128)
                .WaitAsync(OperationTimeout)
                .ConfigureAwait(false);
            listedPaths = listed.Entries
                .Select(entry => entry.RawPath)
                .Order(StringComparer.Ordinal)
                .ToArray();
            Require(
                listedPaths.SequenceEqual(expectedPaths, StringComparer.Ordinal),
                $"{format} Rain listing returned an unexpected PAX manifest.");
            rainLongEntryTime = listed.Entries.Single(entry => entry.RawPath == longEntryPath).ModifiedTimeUtc;
            var timestampDeltaTicks = rainLongEntryTime is { } observed
                ? Math.Abs(observed.Ticks - expectedLongEntryTime.Ticks)
                : long.MaxValue;
            Require(
                timestampDeltaTicks <= 1,
                $"{format} Rain readback changed the fractional PAX timestamp: "
                + $"expected {expectedLongEntryTime:O}, actual {rainLongEntryTime:O}.");

            var integrity = await client.StartIntegrityTestAsync(opened.Archive.SessionId, [])
                .WaitAsync(OperationTimeout)
                .ConfigureAwait(false);
            var integrityTerminal = await client.WaitForTerminalAsync(integrity.OperationId)
                .WaitAsync(OperationTimeout)
                .ConfigureAwait(false);
            Require(
                integrityTerminal.Phase == OperationPhase.Completed,
                $"{format} layout integrity failed: {integrityTerminal.Error?.Code} "
                + integrityTerminal.Error?.Message);

            var extractionRoot = Path.Combine(
                Path.GetDirectoryName(archivePath)!,
                $"rain-layout-extract-{format.Replace('.', '-')}-{Guid.NewGuid():N}");
            try
            {
                var extraction = await client.StartExtractionAsync(opened.Archive.SessionId, [], extractionRoot)
                    .WaitAsync(OperationTimeout)
                    .ConfigureAwait(false);
                var extractionTerminal = await client.WaitForTerminalAsync(extraction.OperationId)
                    .WaitAsync(OperationTimeout)
                    .ConfigureAwait(false);
                Require(
                    extractionTerminal.Phase == OperationPhase.Completed,
                    $"{format} layout extraction failed: {extractionTerminal.Error?.Code} "
                    + extractionTerminal.Error?.Message);
                foreach (var directory in expectedDirectories)
                {
                    RequireDirectory(
                        Path.Combine(extractionRoot, directory.Replace('/', Path.DirectorySeparatorChar)),
                        $"{format} Rain extracted directory");
                }

                foreach (var expected in expectedFiles)
                {
                    var extractedPath = Path.Combine(
                        extractionRoot,
                        expected.Key.Replace('/', Path.DirectorySeparatorChar));
                    RequireFile(extractedPath, $"{format} Rain extracted payload");
                    Require(Sha256File(extractedPath) == expected.Value,
                        $"{format} Rain extracted payload hash changed: {expected.Key}");
                }
            }
            finally
            {
                if (Directory.Exists(extractionRoot))
                {
                    var extractionParent = Path.GetDirectoryName(Path.GetFullPath(extractionRoot));
                    Require(
                        extractionParent?.Equals(
                            Path.GetFullPath(Path.GetDirectoryName(archivePath)!),
                            StringComparison.OrdinalIgnoreCase) == true,
                        "The Rain layout extraction root escaped its owned corpus root.");
                    Require(
                        (File.GetAttributes(extractionRoot) & FileAttributes.ReparsePoint) == 0,
                        "The Rain layout extraction root became a reparse point.");
                    Directory.Delete(extractionRoot, recursive: true);
                }
            }
        }
        finally
        {
            var closed = await client.CloseArchiveAsync(opened.Archive.SessionId)
                .WaitAsync(OperationTimeout)
                .ConfigureAwait(false);
            Require(closed.Closed, $"{format} layout session did not close.");
        }

        Require(Sha256File(archivePath) == archiveHash, $"{format} layout archive changed during Rain validation.");
        return new TarLayoutEvidence(
            format,
            Path.GetFileName(archivePath),
            new FileInfo(archivePath).Length,
            archiveHash,
            listedPaths,
            expectedDirectories,
            expectedFiles,
            longEntryPath,
            expectedLongEntryTime,
            rainLongEntryTime,
            Math.Abs(rainLongEntryTime!.Value.Ticks - expectedLongEntryTime.Ticks));
    }

    private static async Task<IReadOnlyList<LibArchiveFormatEvidence>> ValidateLibArchiveAsync(
        string toolPath,
        string corpusRoot,
        string simpleSourceHash,
        IReadOnlyList<TarLayoutEvidence> layoutFormats)
    {
        var expectations = new List<LibArchiveExpectation>
        {
            new(
                "ZIP",
                "clean-machine.zip",
                [],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["manual-creation-雨.txt"] = simpleSourceHash,
                }),
            new(
                "TAR",
                "clean-machine.tar",
                [],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["manual-creation-雨.txt"] = simpleSourceHash,
                }),
            new(
                "TAR.GZ",
                "clean-machine.tar.gz",
                [],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["manual-creation-雨.txt"] = simpleSourceHash,
                }),
            new(
                "TAR.BZ2",
                "clean-machine.tar.bz2",
                [],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["manual-creation-雨.txt"] = simpleSourceHash,
                }),
        };
        expectations.AddRange(layoutFormats.Select(item => new LibArchiveExpectation(
            item.Format,
            item.ArchiveFileName,
            item.ExpectedDirectories,
            item.ExpectedFileSha256)));

        var results = new List<LibArchiveFormatEvidence>();
        foreach (var item in expectations)
        {
            var archivePath = Path.Combine(corpusRoot, item.ArchiveFileName);
            var archiveHash = Sha256File(archivePath);
            var listing = await RunTextToolAsync(
                    toolPath,
                    corpusRoot,
                    ["-tf", item.ArchiveFileName])
                .ConfigureAwait(false);
            Require(
                listing.ExitCode == 0,
                $"libarchive {item.Format} listing failed: {Sanitize(listing.StandardError)}");
            var listedPaths = listing.StandardOutput
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(path => path.Replace('\\', '/'))
                .ToArray();
            var expectedPaths = item.ExpectedDirectories
                .Concat(item.ExpectedFileSha256.Keys)
                .Order(StringComparer.Ordinal)
                .ToArray();
            Require(
                listedPaths.Order(StringComparer.Ordinal).SequenceEqual(expectedPaths, StringComparer.Ordinal),
                $"libarchive {item.Format} listing returned an unexpected manifest.");

            var extractionRoot = Path.Combine(
                corpusRoot,
                $"libarchive-extract-{item.Format.Replace('.', '-').ToLowerInvariant()}");
            Require(!Directory.Exists(extractionRoot) && !File.Exists(extractionRoot),
                $"libarchive {item.Format} extraction root already exists.");
            Directory.CreateDirectory(extractionRoot);
            var extraction = await RunTextToolAsync(
                    toolPath,
                    corpusRoot,
                    ["-xf", item.ArchiveFileName, "-C", Path.GetFileName(extractionRoot)])
                .ConfigureAwait(false);
            Require(
                extraction.ExitCode == 0,
                $"libarchive {item.Format} extraction failed: {Sanitize(extraction.StandardError)}");
            foreach (var directory in item.ExpectedDirectories)
            {
                RequireDirectory(
                    Path.Combine(extractionRoot, directory.Replace('/', Path.DirectorySeparatorChar)),
                    $"libarchive {item.Format} extracted directory");
            }

            var extractedHashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var expected in item.ExpectedFileSha256)
            {
                var extractedPath = Path.Combine(
                    extractionRoot,
                    expected.Key.Replace('/', Path.DirectorySeparatorChar));
                RequireFile(extractedPath, $"libarchive {item.Format} extracted payload");
                var hash = Sha256File(extractedPath);
                Require(hash == expected.Value, $"libarchive {item.Format} payload hash changed: {expected.Key}");
                extractedHashes[expected.Key] = hash;
            }

            Require(Sha256File(archivePath) == archiveHash, $"libarchive {item.Format} mutated the archive.");
            results.Add(new LibArchiveFormatEvidence(
                item.Format,
                item.ArchiveFileName,
                new FileInfo(archivePath).Length,
                archiveHash,
                listedPaths,
                extractedHashes,
                listing.ExitCode,
                extraction.ExitCode,
                listing.ProcessId,
                extraction.ProcessId));
        }

        return results;
    }

    private static async Task<IReadOnlyList<WinRarFormatEvidence>> ValidateWinRarAsync(
        string winRarPath,
        string corpusRoot,
        string expectedSourceHash)
    {
        var cases = new[]
        {
            (Format: "ZIP", Archive: "clean-machine.zip", Member: "manual-creation-雨.txt"),
            (Format: "TAR", Archive: "clean-machine.tar", Member: "manual-creation-雨.txt"),
            (Format: "TAR.GZ", Archive: "clean-machine.tar.gz", Member: "manual-creation-雨.txt"),
            (Format: "TAR.BZ2", Archive: "clean-machine.tar.bz2", Member: "manual-creation-雨.txt"),
            (Format: "GZIP", Archive: "clean-machine-gzip.gz", Member: "clean-machine-gzip"),
            (Format: "BZIP2", Archive: "clean-machine-bzip2.bz2", Member: "clean-machine-bzip2"),
        };
        var results = new List<WinRarFormatEvidence>();
        foreach (var item in cases)
        {
            var archivePath = Path.Combine(corpusRoot, item.Archive);
            var archiveHash = Sha256File(archivePath);
            var integrity = await RunTextToolAsync(
                    winRarPath,
                    corpusRoot,
                    ["t", "-inul", item.Archive])
                .ConfigureAwait(false);
            Require(
                integrity.ExitCode == 0,
                $"WinRAR {item.Format} integrity failed: {Sanitize(integrity.StandardError)}");

            var extractionRoot = Path.Combine(
                corpusRoot,
                $"winrar-extract-{item.Format.Replace('.', '-').ToLowerInvariant()}");
            Directory.CreateDirectory(extractionRoot);
            var extractionDestination = Path.TrimEndingDirectorySeparator(extractionRoot)
                + Path.DirectorySeparatorChar;
            var extraction = await RunTextToolAsync(
                    winRarPath,
                    corpusRoot,
                    ["x", "-inul", "-o-", item.Archive, extractionDestination])
                .ConfigureAwait(false);
            Require(
                extraction.ExitCode == 0,
                $"WinRAR {item.Format} extraction failed: {Sanitize(extraction.StandardError)}");
            var extractedFiles = Directory
                .EnumerateFiles(extractionRoot, "*", SearchOption.AllDirectories)
                .ToArray();
            Require(extractedFiles.Length == 1, $"WinRAR {item.Format} extracted an unexpected file count.");
            var relative = Path.GetRelativePath(extractionRoot, extractedFiles[0]).Replace('\\', '/');
            Require(relative == item.Member, $"WinRAR {item.Format} extracted the wrong member: {relative}");
            var extractedHash = Sha256File(extractedFiles[0]);
            Require(extractedHash == expectedSourceHash, $"WinRAR {item.Format} payload hash changed.");
            Require(Sha256File(archivePath) == archiveHash, $"WinRAR {item.Format} mutated the archive.");
            results.Add(new WinRarFormatEvidence(
                item.Format,
                item.Archive,
                new FileInfo(archivePath).Length,
                archiveHash,
                relative,
                extractedHash,
                integrity.ExitCode,
                extraction.ExitCode,
                integrity.ProcessId,
                extraction.ProcessId));
        }

        return results;
    }

    private static async Task<GnuFormatEvidence> ValidateGnuTarAsync(
        string tarPath,
        string corpusRoot,
        string format,
        string archiveFileName,
        string compression)
    {
        var archivePath = Path.Combine(corpusRoot, archiveFileName);
        var archiveHash = Sha256File(archivePath);
        var listSwitch = compression switch
        {
            "plain" => "-tf",
            "gzip" => "-tzf",
            "bzip2" => "-tjf",
            _ => throw new InvalidDataException("Unsupported GNU TAR compression mode."),
        };
        var extractSwitch = compression switch
        {
            "plain" => "-xf",
            "gzip" => "-xzf",
            "bzip2" => "-xjf",
            _ => throw new InvalidDataException("Unsupported GNU TAR compression mode."),
        };
        var listing = await RunTextToolAsync(tarPath, corpusRoot, [listSwitch, archiveFileName])
            .ConfigureAwait(false);
        Require(listing.ExitCode == 0, $"GNU {format} listing failed: {Sanitize(listing.StandardError)}");
        var listedPaths = listing.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(path => path.Replace('\\', '/'))
            .ToArray();
        Require(
            listedPaths.SequenceEqual(["manual-creation-雨.txt"], StringComparer.Ordinal),
            $"GNU {format} listing returned an unexpected manifest.");

        var extractionRoot = Path.Combine(corpusRoot, $"gnu-extract-{format.Replace('.', '-').ToLowerInvariant()}");
        Directory.CreateDirectory(extractionRoot);
        var extractionLeaf = Path.GetFileName(extractionRoot);
        var extraction = await RunTextToolAsync(
                tarPath,
                corpusRoot,
                [extractSwitch, archiveFileName, "-C", extractionLeaf])
            .ConfigureAwait(false);
        Require(extraction.ExitCode == 0, $"GNU {format} extraction failed: {Sanitize(extraction.StandardError)}");
        var extractedPath = Path.Combine(extractionRoot, "manual-creation-雨.txt");
        RequireFile(extractedPath, $"GNU {format} extracted payload");
        Require(Sha256File(archivePath) == archiveHash, $"GNU {format} validation mutated the archive.");
        return new GnuFormatEvidence(
            format,
            archiveFileName,
            new FileInfo(archivePath).Length,
            archiveHash,
            listedPaths,
            Sha256File(extractedPath),
            listing.ExitCode,
            extraction.ExitCode,
            listing.ProcessId,
            extraction.ProcessId);
    }

    private static async Task<GnuFormatEvidence> ValidateGnuRawAsync(
        string toolPath,
        string corpusRoot,
        string format,
        string archiveFileName,
        IReadOnlyList<string> testArguments,
        IReadOnlyList<string> decompressArguments)
    {
        var archivePath = Path.Combine(corpusRoot, archiveFileName);
        var archiveHash = Sha256File(archivePath);
        var integrityArgs = testArguments.Concat([archiveFileName]).ToArray();
        var integrity = await RunTextToolAsync(toolPath, corpusRoot, integrityArgs).ConfigureAwait(false);
        Require(integrity.ExitCode == 0, $"GNU {format} integrity failed: {Sanitize(integrity.StandardError)}");

        var decompressedPath = Path.Combine(corpusRoot, $"gnu-decompressed-{format.ToLowerInvariant()}.bin");
        var decodeArgs = decompressArguments.Concat([archiveFileName]).ToArray();
        var extraction = await RunBinaryOutputToolAsync(
                toolPath,
                corpusRoot,
                decodeArgs,
                decompressedPath)
            .ConfigureAwait(false);
        Require(extraction.ExitCode == 0, $"GNU {format} decompression failed: {Sanitize(extraction.StandardError)}");
        Require(Sha256File(archivePath) == archiveHash, $"GNU {format} validation mutated the archive.");
        return new GnuFormatEvidence(
            format,
            archiveFileName,
            new FileInfo(archivePath).Length,
            archiveHash,
            [],
            Sha256File(decompressedPath),
            integrity.ExitCode,
            extraction.ExitCode,
            integrity.ProcessId,
            extraction.ProcessId);
    }

    private static async Task<ExternalToolIdentity> ReadTextToolIdentityAsync(
        string path,
        string versionArgument,
        string requiredVersionPrefix)
    {
        var result = await RunTextToolAsync(path, Path.GetDirectoryName(path)!, [versionArgument])
            .ConfigureAwait(false);
        Require(result.ExitCode == 0, $"{Path.GetFileName(path)} version query failed.");
        var versionLine = result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.Contains(requiredVersionPrefix, StringComparison.Ordinal))
            ?? throw new InvalidDataException($"{Path.GetFileName(path)} returned an unexpected version.");
        return new ExternalToolIdentity(
            path,
            new FileInfo(path).Length,
            Sha256File(path),
            versionLine);
    }

    private static async Task<ExternalToolIdentity> ReadBZip2IdentityAsync(string path)
    {
        var startInfo = CreateToolStartInfo(path, Path.GetDirectoryName(path)!, ["--version"]);
        startInfo.RedirectStandardInput = true;
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("GNU bzip2 did not start for its version query.");
        process.StandardInput.Close();
        var discardOutput = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        var standardError = process.StandardError.ReadToEndAsync();
        await WaitForToolAsync(process).ConfigureAwait(false);
        await discardOutput.ConfigureAwait(false);
        var error = await standardError.ConfigureAwait(false);
        var versionLine = error
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.Contains("Version 1.0.8", StringComparison.Ordinal))
            ?? throw new InvalidDataException("GNU bzip2 returned an unexpected version.");
        return new ExternalToolIdentity(
            path,
            new FileInfo(path).Length,
            Sha256File(path),
            versionLine);
    }

    private static async Task<ToolTextResult> RunTextToolAsync(
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments)
    {
        var startInfo = CreateToolStartInfo(executable, workingDirectory, arguments);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not launch '{Path.GetFileName(executable)}'.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await WaitForToolAsync(process).ConfigureAwait(false);
        return new ToolTextResult(
            process.Id,
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false));
    }

    private static async Task<ToolBinaryResult> RunBinaryOutputToolAsync(
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        string outputPath)
    {
        Require(!File.Exists(outputPath), "The GNU decoded output already exists.");
        var startInfo = CreateToolStartInfo(executable, workingDirectory, arguments);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not launch '{Path.GetFileName(executable)}'.");
        await using var output = new FileStream(
            outputPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            BufferBytes,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        var copyOutput = process.StandardOutput.BaseStream.CopyToAsync(output);
        var standardError = process.StandardError.ReadToEndAsync();
        await WaitForToolAsync(process).ConfigureAwait(false);
        await copyOutput.ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
        output.Flush(flushToDisk: true);
        return new ToolBinaryResult(
            process.Id,
            process.ExitCode,
            await standardError.ConfigureAwait(false));
    }

    private static ProcessStartInfo CreateToolStartInfo(
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment["LANG"] = "en_US.UTF-8";
        startInfo.Environment["LC_ALL"] = "en_US.UTF-8";
        startInfo.Environment["PATH"] = string.Join(
            Path.PathSeparator,
            Path.GetDirectoryName(executable)!,
            Environment.SystemDirectory);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static async Task WaitForToolAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().WaitAsync(ExternalToolTimeout).ConfigureAwait(false);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }

            throw;
        }
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryParseGnuToolArguments(string[] args, out GnuToolProbeOptions options)
    {
        string? package = null;
        string? evidence = null;
        string? toolBinRoot = null;
        string? winRar = null;
        string? libArchive = null;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--package" && index + 1 < args.Length)
            {
                package = args[++index];
            }
            else if (args[index] == "--evidence" && index + 1 < args.Length)
            {
                evidence = args[++index];
            }
            else if (args[index] == "--gnu-tool-conformance" && index + 1 < args.Length)
            {
                toolBinRoot = args[++index];
            }
            else if (args[index] == "--winrar" && index + 1 < args.Length)
            {
                winRar = args[++index];
            }
            else if (args[index] == "--libarchive" && index + 1 < args.Length)
            {
                libArchive = args[++index];
            }
            else
            {
                options = default;
                return false;
            }
        }

        options = new GnuToolProbeOptions(
            package ?? string.Empty,
            evidence ?? string.Empty,
            toolBinRoot ?? string.Empty,
            winRar,
            libArchive);
        return !string.IsNullOrWhiteSpace(options.PackageArtifactRoot)
            && !string.IsNullOrWhiteSpace(options.EvidenceRoot)
            && !string.IsNullOrWhiteSpace(options.ToolBinRoot);
    }

    private readonly record struct GnuToolProbeOptions(
        string PackageArtifactRoot,
        string EvidenceRoot,
        string ToolBinRoot,
        string? WinRarPath,
        string? LibArchivePath);
    private sealed record LibArchiveExpectation(
        string Format,
        string ArchiveFileName,
        IReadOnlyList<string> ExpectedDirectories,
        IReadOnlyDictionary<string, string> ExpectedFileSha256);
    private sealed record ToolTextResult(int ProcessId, int ExitCode, string StandardOutput, string StandardError);
    private sealed record ToolBinaryResult(int ProcessId, int ExitCode, string StandardError);
    private sealed record ExternalToolIdentity(string Path, long Bytes, string Sha256, string Version);
    private sealed record GnuFormatEvidence(
        string Format,
        string ArchiveFileName,
        long ArchiveBytes,
        string ArchiveSha256,
        IReadOnlyList<string> ListedPaths,
        string ExtractedSha256,
        int IntegrityOrListExitCode,
        int ExtractionExitCode,
        int IntegrityOrListProcessId,
        int ExtractionProcessId);
    private sealed record WinRarFormatEvidence(
        string Format,
        string ArchiveFileName,
        long ArchiveBytes,
        string ArchiveSha256,
        string MemberName,
        string ExtractedSha256,
        int IntegrityExitCode,
        int ExtractionExitCode,
        int IntegrityProcessId,
        int ExtractionProcessId);
    private sealed record LibArchiveFormatEvidence(
        string Format,
        string ArchiveFileName,
        long ArchiveBytes,
        string ArchiveSha256,
        IReadOnlyList<string> ListedPaths,
        IReadOnlyDictionary<string, string> ExtractedSha256,
        int ListExitCode,
        int ExtractionExitCode,
        int ListProcessId,
        int ExtractionProcessId);
    private sealed record TarLayoutEvidence(
        string Format,
        string ArchiveFileName,
        long ArchiveBytes,
        string ArchiveSha256,
        IReadOnlyList<string> ListedPaths,
        IReadOnlyList<string> ExpectedDirectories,
        IReadOnlyDictionary<string, string> ExpectedFileSha256,
        string LongEntryPath,
        DateTimeOffset ExpectedLongEntryModifiedTimeUtc,
        DateTimeOffset? RainLongEntryModifiedTimeUtc,
        long RainTimestampDeltaTicks);
    private sealed record GnuToolConformanceEvidence(
        int SchemaVersion,
        DateTimeOffset RecordedUtc,
        string Protocol,
        int SourceInputCount,
        string SourceSha256,
        string PackageArtifactRoot,
        string ManifestSha256,
        string WorkerSha256,
        string ToolBinRoot,
        ExternalToolIdentity Tar,
        ExternalToolIdentity GZip,
        ExternalToolIdentity BZip2,
        ExternalToolIdentity? WinRar,
        ExternalToolIdentity? LibArchive,
        string CorpusSourceSha256,
        IReadOnlyList<FormatEvidence> RainFormats,
        IReadOnlyList<TarLayoutEvidence> RainLayoutFormats,
        IReadOnlyList<GnuFormatEvidence> GnuChecks,
        IReadOnlyList<WinRarFormatEvidence> WinRarChecks,
        IReadOnlyList<LibArchiveFormatEvidence> LibArchiveChecks,
        IReadOnlyList<int> LaunchedExternalProcessIds,
        IReadOnlyList<int> RemainingLaunchedExternalProcessIds,
        int RecoveryRecords,
        int RemainingStagingArtifacts,
        IReadOnlyList<ProcessEvidence> RemainingRainProcesses);
}
