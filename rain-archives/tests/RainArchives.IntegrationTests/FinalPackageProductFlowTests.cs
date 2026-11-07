using System.IO.Compression;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.App.Services;
using RainArchives.App.ViewModels;
using RainArchives.Cli;
using RainArchives.Client;
using RainArchives.Contracts;
using RainArchives.Core;
using RainArchives.Security;

namespace RainArchives.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class FinalPackageProductFlowTests
{
    private const string PackageRootVariable = "RAIN_ARCHIVES_PACKAGE_ROOT";
    private const string ExpectedPackageDirectoryName =
        "RainArchives.M3AutomatedPackage.1.18.1C85F6D9";
    private const string ExpectedSourceSnapshotDirectoryName =
        "RainArchives.ProtocolCompatibility.1.18.1C85F6D9";
    private const int ExpectedSourceInputCount = 132;
    private const string ExpectedSourceSha256 =
        "1C85F6D929DE5F940E1357C72C210A8647DFD2E9F38A0A2B0B0832127F5CE844";
    private const int ExpectedAppFiles = 609;
    private const long ExpectedAppBytes = 232_216_977;
    private const string ExpectedAppSha256 =
        "C61749B2CC94A4E66606D3615DF88C44A2D95F599EE1F2B78C812BAC1BCFCFB0";
    private const int ExpectedWorkerFiles = 205;
    private const long ExpectedWorkerBytes = 84_699_121;
    private const string ExpectedWorkerSha256 =
        "36C64503CC9EA9FD6CF0F6C390F75F8871BEF9BFCA5461456CE645CBACB2966F";
    private const string Milestone4PackageDirectoryName =
        "RainArchives.M4AutomatedPackage.1.22.DCE2BC1E";
    private const string Milestone4SourceSha256 =
        "DCE2BC1E01DB8B563E161F1B58E87007ADF7FB6AFC5109CDF4C393B8AAD10C6C";
    private const string Milestone4ManifestSha256 =
        "8D2286A940E9468C0D919CE728399B61B2CA4C1F525BEFF3895839E42BD6002A";
    private const string Milestone5APackageDirectoryName =
        "RainArchives.M5AAutomatedPackage.1.22.08A4C999";
    private const string Milestone5ASourceSha256 =
        "08A4C999CB51DB9A2DCC7D32B8D845AC65932908CFB8EB750D9E1441A7A4EDC6";
    private const string Milestone5AManifestSha256 =
        "4DF99FF784ADBF39A2F019372A6AEE80981E6995F4F4D50739574E5D8F5D2478";
    private const string Milestone5BPackageDirectoryName =
        "RainArchives.M5BAutomatedPackage.1.22.41BB378A";
    private const string Milestone5BSourceSha256 =
        "41BB378A224046A505D54F955243BCD8570519FDF798018E09541425941D3C56";
    private const string Milestone5BManifestSha256 =
        "121CF5703435064DE4602DF3E5ACBCD19A1D760E32C045B59005447B018AB65F";
    private const string Milestone5CPackageDirectoryName =
        "RainArchives.M5CAutomatedPackage.1.22.6942C0B0";
    private const string Milestone5CSourceSha256 =
        "6942C0B02756DABBBB33DA7A664B8DDFEF6AFB12C003E35D59977E4C712DDA3E";
    private const string Milestone5CManifestSha256 =
        "1E457D65694D57C8B411D86B381FBD3634C55ABFB4EBB7F192799611C77FA196";
    private const string Milestone5DPackageDirectoryName =
        "RainArchives.M5DAutomatedPackage.1.22.AE156AB8";
    private const string Milestone5DSourceSha256 =
        "AE156AB8437974EF927A0049E231FEAA1F012F8EAF8DD17B439D48E608C3F3BD";
    private const string Milestone5DManifestSha256 =
        "A28ECFC93F37A1145CA2E265341ED6DD49DABFDC5B5594470B00252816F90F12";
    private const string Milestone5EPackageDirectoryName =
        "RainArchives.M5EAutomatedPackage.1.22.6E0AEC14";
    private const string Milestone5ESourceSha256 =
        "6E0AEC1419512030AD82FFF8241B94DD7A94116661113BCA9B937EEC1764B150";
    private const string Milestone5EManifestSha256 =
        "5B8711FDA66572D72DC26F6F6A88C875860861159F5F0B98B914514FD9AD431F";
    private const string Milestone5FPackageDirectoryName =
        "RainArchives.M5FAutomatedPackage.1.22.94811C7A";
    private const string Milestone5FSourceSha256 =
        "94811C7A626839524DA43826E6AD238BAF29E85EDECFC7E8C3D581B8C217C641";
    private const string Milestone5FManifestSha256 =
        "1066C02687DC900C785FB0921D9C3EFEE2359450ED05F991F64002728F1F7395";
    private const string Milestone5FInspectorRemediationPackageDirectoryName =
        "RainArchives.M5FInspectorRemediationPackage.1.22.3FD7C6F4";
    private const string Milestone5FInspectorRemediationSourceSha256 =
        "3FD7C6F42250A75ED8B62878D15CEB6FF4361D8DCDCC667B8B6134375C35DDAD";
    private const string Milestone5FInspectorRemediationManifestSha256 =
        "9A0F153DE2AF4203D385EE21BB1ABF8022E8B068893A68024C1693ED504CFD5B";
    private const string RejectedMilestone5GAPackageDirectoryName =
        "RainArchives.M5GAPolishPackage.1.22.BE985F08";
    private const string RejectedMilestone5GASourceSha256 =
        "BE985F08B8DCA840A339B74697E86280E2E90A3C170109449B88DAE6DE6FEE8E";
    private const string RejectedMilestone5GAManifestSha256 =
        "E4FA4D56AA7E3E9685170C7E78BE8CE61C38C90CBDC3AEB9122FA4E1048C6F46";
    private const string Milestone5GAAccessibilityRemediationPackageDirectoryName =
        "RainArchives.M5GAAccessibilityRemediationPackage.1.22.5AF06A6E";
    private const string Milestone5GAAccessibilityRemediationSourceSha256 =
        "5AF06A6EFF889132AFB2389312EE585E52D3721EDEA12D6151B3185278AC812C";
    private const string Milestone5GAAccessibilityRemediationManifestSha256 =
        "FADBAF43E14C03D34C8105D282B23EC1D1963FC893DF7FFC42AE94006EFF83D8";
    private const string Milestone5GBSecondaryWindowPackageDirectoryName =
        "RainArchives.M5GBSecondaryWindowPackage.1.22.20FF2288";
    private const string Milestone5GBSecondaryWindowSourceSha256 =
        "20FF2288E30812A5863018FE1FDAD0C46821997465F910203CCEC0A421B8462C";
    private const string Milestone5GBSecondaryWindowManifestSha256 =
        "C49011AD025FE9329F9D0AAC07C7A8EDF94A9287E52DA8D4EA1A9B581222317C";
    private const string Milestone5GCProductIdentityPackageDirectoryName =
        "RainArchives.M5GCProductIdentityPackage.1.22.56FFA877";
    private const string Milestone5GCProductIdentitySourceSha256 =
        "56FFA87711984983DD8AA52A9735A901ECCDE92A2EDD8A00ED2F4E17FB301491";
    private const string Milestone5GCProductIdentityManifestSha256 =
        "D8284B822C2475C61D40B155719A20522BD05CB67C12A5A2EEB8D74BB84CE74F";
    private const string Milestone5GDDesignSystemPackageDirectoryName =
        "RainArchives.M5GDDesignSystemPackage.1.22.651729F4";
    private const string Milestone5GDDesignSystemSourceSha256 =
        "651729F471468F590BB2F89CE663C14B5140356227509B3C247A93C74481D740";
    private const string Milestone5GDDesignSystemManifestSha256 =
        "01BF21F943C46B54C953E2A2BE3C5D037BE1FB26F738DBE67CA6C8C46AB30137";
    private const string Milestone7DRarPackageDirectoryName =
        "RainArchives.M7DRarPackage.1.23.0D84243C";
    private const string Milestone7DRarSourceSha256 =
        "0D84243CA1A09AE3BD77942EC66578E44E3EB5A7547411CCD40E224CD2460AB2";
    private const string Milestone7DRarManifestSha256 =
        "DF47FAD343B6DCBDE2B1FFB42489C7CF7D19C154CA9D2CB6398C07AE2F442ADB";
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private static readonly IReadOnlyDictionary<string, string> ExpectedFileHashes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["app/RainArchives.exe"] = "9EFD3BED1637298719FA33D50BF01C82D34040D2095D717E0A1BAFAE63D9CA26",
            ["app/RainArchives.dll"] = "AC856DFD5CC82F04B45B34D4708791391DACC0F77284FC3B331D943FC20CCE3C",
            ["app/RainArchives.Client.dll"] = "5403CA7B8238A134A87D717EBE4A698E119EC5726254B524B7E3CC9E7CB8A671",
            ["app/RainArchives.Contracts.dll"] = "BFDC6FB719D71BF4B85CC7471F6B95116F5B66E5A26C5EFCFADB7D9B848E2E91",
            ["app/worker/RainArchives.Worker.exe"] = "C5A6D421ED4838A868704B472C6690AFA905F3EE0E5FB3E0F883D4BDC6EDEEDF",
            ["app/worker/RainArchives.Worker.dll"] = "FE8CC4D51F8C873574A79BEB89B27F18C1D1FB503D2B2C1D70210AD2E7CDB178",
            ["app/worker/RainArchives.Contracts.dll"] = "BFDC6FB719D71BF4B85CC7471F6B95116F5B66E5A26C5EFCFADB7D9B848E2E91",
        };

    [TestMethod]
    [Timeout(120_000)]
    public async Task Milestone4PackageBindsDiagnosticsAndIndependentRecoveryReopen()
    {
        var repository = GetRepositoryRoot();
        var package = ResolvePackage(Path.Combine(
            repository,
            "artifacts",
            Milestone4PackageDirectoryName));
        var manifestPath = Path.Combine(package.ArtifactRoot, "package-manifest.json");
        Assert.AreEqual(Milestone4ManifestSha256, Sha256File(manifestPath));
        var manifest = JsonSerializer.Deserialize<PackageManifest>(
            File.ReadAllText(manifestPath),
            ManifestJsonOptions);
        Assert.IsNotNull(manifest);
        Assert.AreEqual(1, manifest.SchemaVersion);
        Assert.AreEqual("1.22", manifest.Protocol);
        Assert.AreEqual(135, manifest.SourceInputCount);
        Assert.AreEqual(Milestone4SourceSha256, manifest.SourceSha256);

        Assert.AreEqual(
            new TreeIdentity(
                609,
                232_823_153,
                "C378FB9DC2C1DA893825E6CCE2877A298E9155B07DFAFA3E5F2CCAE392F0FB22"),
            GetTreeIdentity(package.AppRoot));
        Assert.AreEqual(
            new TreeIdentity(
                205,
                85_041_541,
                "1487672F34AB9C054F67FF96C8B74D21DEA353AAAEAFD16416A34AE7D286491F"),
            GetTreeIdentity(Path.GetDirectoryName(package.WorkerPath)!));
        Assert.AreEqual(GetTreeIdentity(package.AppRoot), manifest.AppTree);
        Assert.AreEqual(
            GetTreeIdentity(Path.GetDirectoryName(package.WorkerPath)!),
            manifest.WorkerTree);
        AssertPackageComposition(package);

        var expectedFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["app/RainArchives.exe"] = "9EFD3BED1637298719FA33D50BF01C82D34040D2095D717E0A1BAFAE63D9CA26",
            ["app/RainArchives.dll"] = "7C2B363A1FC1BAC142A40341BA11CAE83C2DE370B7B1B96E60F429BA9B9A5510",
            ["app/RainArchives.Client.dll"] = "ABC6B96B4692040BDB928F9A670BC6225F524950CB51D37F0CA92541C9A93603",
            ["app/RainArchives.Contracts.dll"] = "E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8",
            ["app/worker/RainArchives.Worker.exe"] = "C5A6D421ED4838A868704B472C6690AFA905F3EE0E5FB3E0F883D4BDC6EDEEDF",
            ["app/worker/RainArchives.Worker.dll"] = "3B994B6657E97D7E4769526710306B26C38EF2367FFD838F91D945086EB33BD8",
            ["app/worker/RainArchives.Contracts.dll"] = "E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8",
        };
        CollectionAssert.AreEquivalent(expectedFiles.Keys.ToArray(), manifest.Files.Keys.ToArray());
        foreach (var expected in expectedFiles)
        {
            Assert.AreEqual(expected.Value, manifest.Files[expected.Key]);
            Assert.AreEqual(
                expected.Value,
                Sha256File(Path.Combine(
                    package.ArtifactRoot,
                    expected.Key.Replace('/', Path.DirectorySeparatorChar))));
        }

        using var temporary = new TemporaryDirectory();
        var cliExitCode = await RunCliAsync(
            temporary.Path,
            ["support-diagnostics", "--worker", package.WorkerPath]);
        Assert.AreEqual(0, cliExitCode);

        var workerTemporaryRoot = Path.Combine(temporary.Path, "m4-worker-temp");
        Directory.CreateDirectory(workerTemporaryRoot);
        var recoveryId = new string('c', 64);
        var sourceId = new string('d', 64);
        var workspaceName = OwnedNestedTemporaryWorkspace.WorkspacePrefix + recoveryId;
        var recoveryWorkspace = Path.Combine(workerTemporaryRoot, workspaceName);
        Directory.CreateDirectory(recoveryWorkspace);
        await File.WriteAllBytesAsync(
            Path.Combine(recoveryWorkspace, OwnedNestedTemporaryWorkspace.OwnerFileName),
            OwnedNestedTemporaryWorkspace.CreateOwnerRecord(workspaceName));
        var sourceDirectory = Path.Combine(
            recoveryWorkspace,
            OwnedTemporaryArchiveSource.SourceDirectoryPrefix + sourceId);
        Directory.CreateDirectory(sourceDirectory);
        var recoveredSource = Path.Combine(sourceDirectory, OwnedTemporaryArchiveSource.SourceFileName);
        CreateSourceArchive(recoveredSource);
        var recoveredSourceHash = await Sha256FileAsync(recoveredSource);

        await using (var client = await ArchiveClient.StartWithTemporaryRootAsync(
                         package.WorkerPath,
                         workerTemporaryRoot))
        {
            var diagnostics = await client.GetSupportDiagnosticsAsync();
            Assert.AreEqual(ProtocolConstants.MajorVersion, diagnostics.ProtocolMajor);
            Assert.AreEqual(22, diagnostics.ProtocolMinor);
            Assert.AreEqual(WorkerContainmentModel.SameUserWorkerProcess, diagnostics.ContainmentModel);
            Assert.IsFalse(diagnostics.IsSameUserSecuritySandbox);
            Assert.IsFalse(diagnostics.SupportsQueueReordering);
            Assert.IsFalse(diagnostics.SupportsPause);
            Assert.IsFalse(diagnostics.SupportsRetry);
            Assert.IsFalse(diagnostics.SupportsAutomaticRecoveryDisposition);
            Assert.IsFalse(diagnostics.PersistsOperationActivityAcrossWorkerRestart);

            var inspection = await client.InspectNestedWorkspaceReopenAsync(recoveryId);
            var source = inspection.Sources!.Single();
            var reopened = await client.ReopenNestedWorkspaceRecoveryAsync(
                recoveryId,
                inspection.Revision!,
                inspection.SourceCount!.Value,
                inspection.TotalSourceBytes!.Value,
                source.SourceId,
                source.Length,
                source.ContentSha256);
            Assert.AreEqual(NestedWorkspaceRecoveryReopenMode.IndependentRoot, reopened.Mode);
            Assert.AreEqual(0, reopened.Depth);
            Assert.IsTrue((await client.ListEntriesAsync(reopened.Archive.SessionId, 0, 32))
                .Entries
                .Any(entry => entry.RawPath == "keep.txt"));
            Assert.AreEqual(recoveredSourceHash, await Sha256FileAsync(recoveredSource));

            var activeWorkspace = Directory
                .EnumerateDirectories(
                    workerTemporaryRoot,
                    OwnedNestedTemporaryWorkspace.WorkspacePrefix + "*",
                    SearchOption.TopDirectoryOnly)
                .Single(path => !string.Equals(
                    path,
                    recoveryWorkspace,
                    StringComparison.OrdinalIgnoreCase));
            var reopenedCopy = Directory
                .EnumerateFiles(
                    activeWorkspace,
                    OwnedTemporaryArchiveSource.SourceFileName,
                    SearchOption.AllDirectories)
                .Single();
            Assert.AreEqual(recoveredSourceHash, await Sha256FileAsync(reopenedCopy));

            Assert.IsTrue((await client.CloseArchiveAsync(reopened.Archive.SessionId)).Closed);
            Assert.HasCount(
                0,
                Directory.EnumerateDirectories(
                    activeWorkspace,
                    OwnedTemporaryArchiveSource.SourceDirectoryPrefix + "*",
                    SearchOption.TopDirectoryOnly).ToArray());
        }

        Assert.AreEqual(recoveredSourceHash, await Sha256FileAsync(recoveredSource));
        CollectionAssert.AreEquivalent(
            new[] { recoveryWorkspace },
            Directory.GetDirectories(
                workerTemporaryRoot,
                OwnedNestedTemporaryWorkspace.WorkspacePrefix + "*",
                SearchOption.TopDirectoryOnly));
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task Milestone5APackageRunsAllActivityCenterOperationKinds()
    {
        var repository = GetRepositoryRoot();
        var package = ResolvePackage(Path.Combine(
            repository,
            "artifacts",
            Milestone5APackageDirectoryName));
        var manifestPath = Path.Combine(package.ArtifactRoot, "package-manifest.json");
        Assert.AreEqual(Milestone5AManifestSha256, Sha256File(manifestPath));
        var manifest = JsonSerializer.Deserialize<PackageManifest>(
            File.ReadAllText(manifestPath),
            ManifestJsonOptions);
        Assert.IsNotNull(manifest);
        Assert.AreEqual(1, manifest.SchemaVersion);
        Assert.AreEqual("1.22", manifest.Protocol);
        Assert.AreEqual(141, manifest.SourceInputCount);
        Assert.AreEqual(Milestone5ASourceSha256, manifest.SourceSha256);

        Assert.AreEqual(
            new TreeIdentity(
                609,
                232_895_609,
                "0FD865E36384CF1B955858A681AE74C29854D41E8DAD71F3D6539E1F937DC119"),
            GetTreeIdentity(package.AppRoot));
        Assert.AreEqual(
            new TreeIdentity(
                205,
                85_041_541,
                "1487672F34AB9C054F67FF96C8B74D21DEA353AAAEAFD16416A34AE7D286491F"),
            GetTreeIdentity(Path.GetDirectoryName(package.WorkerPath)!));
        Assert.AreEqual(GetTreeIdentity(package.AppRoot), manifest.AppTree);
        Assert.AreEqual(
            GetTreeIdentity(Path.GetDirectoryName(package.WorkerPath)!),
            manifest.WorkerTree);
        AssertPackageComposition(package);

        var expectedFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["app/RainArchives.exe"] = "9EFD3BED1637298719FA33D50BF01C82D34040D2095D717E0A1BAFAE63D9CA26",
            ["app/RainArchives.dll"] = "260CA3F8DE7D6811B72EF029FAE8E042E809120DC85736295EEB5762D3A27614",
            ["app/RainArchives.Client.dll"] = "ABC6B96B4692040BDB928F9A670BC6225F524950CB51D37F0CA92541C9A93603",
            ["app/RainArchives.Contracts.dll"] = "E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8",
            ["app/worker/RainArchives.Worker.exe"] = "C5A6D421ED4838A868704B472C6690AFA905F3EE0E5FB3E0F883D4BDC6EDEEDF",
            ["app/worker/RainArchives.Worker.dll"] = "3B994B6657E97D7E4769526710306B26C38EF2367FFD838F91D945086EB33BD8",
            ["app/worker/RainArchives.Contracts.dll"] = "E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8",
        };
        CollectionAssert.AreEquivalent(expectedFiles.Keys.ToArray(), manifest.Files.Keys.ToArray());
        foreach (var expected in expectedFiles)
        {
            Assert.AreEqual(expected.Value, manifest.Files[expected.Key]);
            Assert.AreEqual(
                expected.Value,
                Sha256File(Path.Combine(
                    package.ArtifactRoot,
                    expected.Key.Replace('/', Path.DirectorySeparatorChar))));
        }

        using var temporary = new TemporaryDirectory();
        var workerTemporaryRoot = Path.Combine(temporary.Path, "m5a-worker-temp");
        Directory.CreateDirectory(workerTemporaryRoot);
        var source = Path.Combine(temporary.Path, "source.txt");
        var created = Path.Combine(temporary.Path, "created.zip");
        var rebuilt = Path.Combine(temporary.Path, "rebuilt.zip");
        var extracted = Path.Combine(temporary.Path, "extracted");
        await File.WriteAllTextAsync(source, "package Activity Center payload 雨");
        var sourceHash = await Sha256FileAsync(source);

        await using var client = await ArchiveClient.StartWithTemporaryRootAsync(
            package.WorkerPath,
            workerTemporaryRoot);
        var uiQueue = new ConcurrentQueue<Action>();
        await using var activityCenter = new ActivityCenterViewModel(
            client,
            uiQueue.Enqueue,
            TimeSpan.FromMilliseconds(250));
        activityCenter.Start();
        await WaitForActivityCenterAsync(uiQueue, () => activityCenter.IsReady);

        var creation = await client.StartZipCreationAsync(
            created,
            [new ArchiveCreationFile(source, "payload.txt")],
            ArchiveCompressionPreset.Store);
        activityCenter.TrackStarted(
            creation.OperationId,
            OperationKind.CreateArchive,
            "ZIP archive creation");
        Assert.IsTrue((await client.WaitForTerminalAsync(creation.OperationId)).Phase is
            OperationPhase.Completed or OperationPhase.CompletedWithWarnings);
        var originalArchiveHash = await Sha256FileAsync(created);

        var opened = await client.OpenArchiveAsync(created);
        var integrity = await client.StartIntegrityTestAsync(opened.Archive.SessionId, []);
        activityCenter.TrackStarted(
            integrity.OperationId,
            OperationKind.IntegrityTest,
            "Integrity test");
        Assert.IsTrue((await client.WaitForTerminalAsync(integrity.OperationId)).Phase is
            OperationPhase.Completed or OperationPhase.CompletedWithWarnings);

        var extraction = await client.StartExtractionAsync(
            opened.Archive.SessionId,
            [],
            extracted);
        activityCenter.TrackStarted(
            extraction.OperationId,
            OperationKind.Extract,
            "Extraction");
        Assert.IsTrue((await client.WaitForTerminalAsync(extraction.OperationId)).Phase is
            OperationPhase.Completed or OperationPhase.CompletedWithWarnings);

        var rebuild = await client.StartZipRebuildCopyAsync(
            opened.Archive.SessionId,
            rebuilt,
            [],
            [],
            ArchiveCompressionPreset.Store);
        activityCenter.TrackStarted(
            rebuild.OperationId,
            OperationKind.RebuildArchive,
            "ZIP rebuild copy");
        Assert.IsTrue((await client.WaitForTerminalAsync(rebuild.OperationId)).Phase is
            OperationPhase.Completed or OperationPhase.CompletedWithWarnings);
        Assert.IsTrue((await client.CloseArchiveAsync(opened.Archive.SessionId)).Closed);

        activityCenter.RequestRefresh();
        await WaitForActivityCenterAsync(
            uiQueue,
            () => new[]
                {
                    OperationKind.CreateArchive,
                    OperationKind.IntegrityTest,
                    OperationKind.Extract,
                    OperationKind.RebuildArchive,
                }
                .All(kind => activityCenter.Operations.Any(operation =>
                    operation.Kind == kind && operation.IsTerminal)));

        Assert.IsTrue(activityCenter.SupportDiagnostics.IsAvailable);
        Assert.IsTrue(activityCenter.Operations.All(operation => !operation.CanCancel));
        Assert.AreEqual(sourceHash, await Sha256FileAsync(source));
        Assert.AreEqual(originalArchiveHash, await Sha256FileAsync(created));
        Assert.AreEqual(
            await File.ReadAllTextAsync(source),
            await File.ReadAllTextAsync(Path.Combine(extracted, "payload.txt")));
        Assert.IsTrue(File.Exists(rebuilt));
        Assert.HasCount(
            0,
            Directory.GetFiles(
                temporary.Path,
                ".rainarchives-*.staging",
                SearchOption.AllDirectories));
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task Milestone5BPackageRunsProductionArchiveBrowserFlow()
    {
        var repository = GetRepositoryRoot();
        var package = ResolvePackage(Path.Combine(
            repository,
            "artifacts",
            Milestone5BPackageDirectoryName));
        var manifestPath = Path.Combine(package.ArtifactRoot, "package-manifest.json");
        Assert.AreEqual(Milestone5BManifestSha256, Sha256File(manifestPath));
        var manifest = JsonSerializer.Deserialize<PackageManifest>(
            File.ReadAllText(manifestPath),
            ManifestJsonOptions);
        Assert.IsNotNull(manifest);
        Assert.AreEqual(1, manifest.SchemaVersion);
        Assert.AreEqual("1.22", manifest.Protocol);
        Assert.AreEqual(142, manifest.SourceInputCount);
        Assert.AreEqual(Milestone5BSourceSha256, manifest.SourceSha256);
        // M5B remains immutable predecessor evidence. The current live source is
        // bound by the newest package test and is expected to advance in M5C.
        Assert.AreEqual(
            new TreeIdentity(
                609,
                232_942_737,
                "BE3BC9D2B19C02906797B4DDFB4E66F305258CA9F211BF7A5D3915191476BAD9"),
            GetTreeIdentity(package.AppRoot));
        Assert.AreEqual(
            new TreeIdentity(
                205,
                85_041_541,
                "1487672F34AB9C054F67FF96C8B74D21DEA353AAAEAFD16416A34AE7D286491F"),
            GetTreeIdentity(Path.GetDirectoryName(package.WorkerPath)!));
        Assert.AreEqual(GetTreeIdentity(package.AppRoot), manifest.AppTree);
        Assert.AreEqual(
            GetTreeIdentity(Path.GetDirectoryName(package.WorkerPath)!),
            manifest.WorkerTree);
        AssertPackageComposition(package);

        var expectedFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["app/RainArchives.exe"] = "9EFD3BED1637298719FA33D50BF01C82D34040D2095D717E0A1BAFAE63D9CA26",
            ["app/RainArchives.dll"] = "8AD76AC756D485084468D93BCEC9AE7EDB0F3570D5B09229515E928D6EEA32E1",
            ["app/RainArchives.Client.dll"] = "ABC6B96B4692040BDB928F9A670BC6225F524950CB51D37F0CA92541C9A93603",
            ["app/RainArchives.Contracts.dll"] = "E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8",
            ["app/worker/RainArchives.Worker.exe"] = "C5A6D421ED4838A868704B472C6690AFA905F3EE0E5FB3E0F883D4BDC6EDEEDF",
            ["app/worker/RainArchives.Worker.dll"] = "3B994B6657E97D7E4769526710306B26C38EF2367FFD838F91D945086EB33BD8",
            ["app/worker/RainArchives.Contracts.dll"] = "E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8",
        };
        CollectionAssert.AreEquivalent(expectedFiles.Keys.ToArray(), manifest.Files.Keys.ToArray());
        foreach (var expected in expectedFiles)
        {
            Assert.AreEqual(expected.Value, manifest.Files[expected.Key]);
            Assert.AreEqual(
                expected.Value,
                Sha256File(Path.Combine(
                    package.ArtifactRoot,
                    expected.Key.Replace('/', Path.DirectorySeparatorChar))));
        }

        using var temporary = new TemporaryDirectory();
        var source = Path.Combine(temporary.Path, "browser.zip");
        var workerTemporaryRoot = Path.Combine(temporary.Path, "worker-temp");
        var previewCache = Path.Combine(temporary.Path, "preview-cache");
        Directory.CreateDirectory(workerTemporaryRoot);
        Directory.CreateDirectory(previewCache);
        using (var stream = new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            _ = archive.CreateEntry("folder/", CompressionLevel.NoCompression);
            CreateEntry(archive, "folder/雨.txt", "first package duplicate");
            CreateEntry(archive, "folder/雨.txt", "second package duplicate");
            CreateEntry(archive, "root.txt", "package root");
        }

        var sourceHash = await Sha256FileAsync(source);
        var dialogs = new RecordingDialogService();
        await using (var viewModel = new MainWindowViewModel(
                         dialogs,
                         async cancellationToken => await ArchiveClient.StartWithTemporaryRootAsync(
                                 package.WorkerPath,
                                 workerTemporaryRoot,
                                 cancellationToken)
                             .ConfigureAwait(false),
                         client => new TextEntryPreviewService(client, previewCache)))
        {
            await viewModel.OpenArchivePathAsync(source);
            Assert.AreEqual(ArchiveBrowserState.Ready, viewModel.Browser.State);
            Assert.IsTrue(viewModel.Browser.PersistentKeysEnabled);
            await viewModel.ActivateEntryAsync(viewModel.Entries.Single(entry => entry.Name == "folder"));
            viewModel.SearchText = "雨";
            var duplicate = viewModel.Entries.Single(entry => entry.Descriptor.DuplicateOrdinal == 1);
            viewModel.Browser.SelectedEntry = duplicate;
            var persistentKey = viewModel.Browser.SelectedPersistentKey;
            await viewModel.ActivateEntryAsync(duplicate);
            Assert.IsTrue(viewModel.IsPreviewVisible, dialogs.ErrorMessage);
            Assert.AreEqual("second package duplicate", viewModel.PreviewText);
            await viewModel.ClosePreviewAsync();

            await viewModel.OpenArchivePathAsync(source);
            Assert.AreEqual("folder", viewModel.Browser.CurrentLocationName);
            Assert.AreEqual("雨", viewModel.SearchText);
            Assert.AreEqual(persistentKey, viewModel.Browser.SelectedPersistentKey);
            Assert.AreEqual(1, viewModel.Browser.SelectedEntry?.Descriptor.DuplicateOrdinal);
            Assert.IsFalse(viewModel.ArchiveDetails.Contains("RainArchives.Providers", StringComparison.Ordinal));
        }

        Assert.AreEqual(sourceHash, await Sha256FileAsync(source));
        Assert.HasCount(0, Directory.GetFiles(workerTemporaryRoot, "*", SearchOption.AllDirectories));
        Assert.HasCount(0, Directory.GetFiles(previewCache, "*", SearchOption.AllDirectories));
        Assert.HasCount(
            0,
            Directory.GetFiles(
                temporary.Path,
                ".rainarchives-*.staging",
                SearchOption.AllDirectories));
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task Milestone5CPackageRunsSettingsBackedCreateAndRebuildFlow()
    {
        var repository = GetRepositoryRoot();
        var package = ResolvePackage(Path.Combine(
            repository,
            "artifacts",
            Milestone5CPackageDirectoryName));
        var manifestPath = Path.Combine(package.ArtifactRoot, "package-manifest.json");
        Assert.AreEqual(Milestone5CManifestSha256, Sha256File(manifestPath));
        var manifest = JsonSerializer.Deserialize<PackageManifest>(
            File.ReadAllText(manifestPath),
            ManifestJsonOptions);
        Assert.IsNotNull(manifest);
        Assert.AreEqual(1, manifest.SchemaVersion);
        Assert.AreEqual("1.22", manifest.Protocol);
        Assert.AreEqual(148, manifest.SourceInputCount);
        Assert.AreEqual(Milestone5CSourceSha256, manifest.SourceSha256);
        // M5C remains immutable predecessor evidence. The current live source is
        // bound by the M5D package test below.
        Assert.AreEqual(
            new TreeIdentity(
                609,
                233_062_821,
                "C7DA0DB768CC32C38B375A5C67F9F5F3ED7D92A5606C015DDE595075E67AA4BA"),
            GetTreeIdentity(package.AppRoot));
        Assert.AreEqual(
            new TreeIdentity(
                205,
                85_041_541,
                "1487672F34AB9C054F67FF96C8B74D21DEA353AAAEAFD16416A34AE7D286491F"),
            GetTreeIdentity(Path.GetDirectoryName(package.WorkerPath)!));
        Assert.AreEqual(GetTreeIdentity(package.AppRoot), manifest.AppTree);
        Assert.AreEqual(
            GetTreeIdentity(Path.GetDirectoryName(package.WorkerPath)!),
            manifest.WorkerTree);
        AssertPackageComposition(package);

        var expectedFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["app/RainArchives.exe"] = "9EFD3BED1637298719FA33D50BF01C82D34040D2095D717E0A1BAFAE63D9CA26",
            ["app/RainArchives.dll"] = "B209428553E8066FD20C474FC3FB616F6A81239A3505451A1738D9991C801E39",
            ["app/RainArchives.Client.dll"] = "ABC6B96B4692040BDB928F9A670BC6225F524950CB51D37F0CA92541C9A93603",
            ["app/RainArchives.Contracts.dll"] = "E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8",
            ["app/worker/RainArchives.Worker.exe"] = "C5A6D421ED4838A868704B472C6690AFA905F3EE0E5FB3E0F883D4BDC6EDEEDF",
            ["app/worker/RainArchives.Worker.dll"] = "3B994B6657E97D7E4769526710306B26C38EF2367FFD838F91D945086EB33BD8",
            ["app/worker/RainArchives.Contracts.dll"] = "E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8",
        };
        CollectionAssert.AreEquivalent(expectedFiles.Keys.ToArray(), manifest.Files.Keys.ToArray());
        foreach (var expected in expectedFiles)
        {
            Assert.AreEqual(expected.Value, manifest.Files[expected.Key]);
            Assert.AreEqual(
                expected.Value,
                Sha256File(Path.Combine(
                    package.ArtifactRoot,
                    expected.Key.Replace('/', Path.DirectorySeparatorChar))));
        }

        using var temporary = new TemporaryDirectory();
        var settingsRoot = Path.Combine(temporary.Path, "local-app-data");
        var workerTemporaryRoot = Path.Combine(temporary.Path, "worker-temp");
        Directory.CreateDirectory(settingsRoot);
        Directory.CreateDirectory(workerTemporaryRoot);
        var settingsStore = new AppSettingsStore(settingsRoot);
        var savedSettings = AppUiSettings.Default with
        {
            ManagedCreation = AppUiSettings.Default.ManagedCreation with
            {
                DefaultFormat = ArchiveCreationFormat.Zip,
                ZipPreset = ArchiveCompressionPreset.Fast,
                ZipRebuildPreset = ArchiveCompressionPreset.Maximum,
            },
            ShowAdvancedRebuildOptions = true,
        };
        Assert.IsTrue(settingsStore.Save(savedSettings).Succeeded);
        var presetStore = new ZipPresetStore(settingsRoot);
        Assert.IsTrue(presetStore.Save([
            ZipPreset.CreateUser("Package fast", ArchiveCompressionPreset.Fast),
        ]).Succeeded);
        var loadedSettings = settingsStore.Load();
        Assert.AreEqual(AppSettingsLoadState.Ready, loadedSettings.State);
        Assert.AreEqual(savedSettings, loadedSettings.Settings);
        Assert.IsTrue(presetStore.Load().Presets.Any(preset =>
            !preset.IsBuiltIn
            && preset.Name == "Package fast"
            && preset.CompressionPreset == ArchiveCompressionPreset.Fast));

        const string payload = "M5C package settings payload 雨";
        var source = Path.Combine(temporary.Path, "sensitive-source-name.txt");
        var created = Path.Combine(temporary.Path, "settings-created.zip");
        var rebuilt = Path.Combine(temporary.Path, "settings-rebuilt.zip");
        await File.WriteAllTextAsync(source, payload);
        var sourceHash = await Sha256FileAsync(source);
        string createdHash;

        await using (var client = await ArchiveClient.StartWithTemporaryRootAsync(
                         package.WorkerPath,
                         workerTemporaryRoot))
        {
            var capabilities = await client.GetZipWriteCapabilitiesAsync();
            Assert.IsTrue(capabilities.CanCreate);
            var format = ManagedCreationDefaultResolver.ResolveFormat(
                loadedSettings.Settings,
                [ArchiveCreationFormat.Zip]);
            Assert.AreEqual(ArchiveCreationFormat.Zip, format);
            var createPreset = ManagedCreationDefaultResolver.ResolvePreset(
                loadedSettings.Settings,
                format,
                capabilities.CompressionPresets);
            Assert.AreEqual(ArchiveCompressionPreset.Fast, createPreset);

            var creation = await client.StartZipCreationAsync(
                created,
                [new ArchiveCreationFile(source, "payload.txt")],
                createPreset);
            await AssertCompletedAsync(client, creation.OperationId, "M5C package ZIP creation");
            createdHash = await Sha256FileAsync(created);

            var opened = await client.OpenArchiveAsync(created);
            var listed = await client.ListEntriesAsync(opened.Archive.SessionId, 0, 32);
            Assert.AreEqual("payload.txt", listed.Entries.Single().RawPath);
            var rebuildCapabilities = await client.GetZipWriteCapabilitiesAsync(
                opened.Archive.SessionId);
            Assert.IsTrue(rebuildCapabilities.CanRebuild);
            var rebuildPreset = ManagedCreationDefaultResolver.ResolveZipRebuildPreset(
                loadedSettings.Settings,
                rebuildCapabilities.CompressionPresets);
            Assert.AreEqual(ArchiveCompressionPreset.Maximum, rebuildPreset);
            var rebuild = await client.StartZipRebuildCopyAsync(
                opened.Archive.SessionId,
                rebuilt,
                [],
                [],
                rebuildPreset);
            await AssertCompletedAsync(client, rebuild.OperationId, "M5C package ZIP rebuild copy");
            Assert.IsTrue((await client.CloseArchiveAsync(opened.Archive.SessionId)).Closed);

            var reopened = await client.OpenArchiveAsync(rebuilt);
            var rebuiltEntry = (await client.ListEntriesAsync(
                reopened.Archive.SessionId,
                0,
                32)).Entries.Single();
            await using (var stream = await client.OpenEntryStreamAsync(
                             reopened.Archive.SessionId,
                             rebuiltEntry.EntryId))
            using (var reader = new StreamReader(stream.Content))
            {
                Assert.AreEqual(payload, await reader.ReadToEndAsync());
            }
            Assert.IsTrue((await client.CloseArchiveAsync(reopened.Archive.SessionId)).Closed);
        }

        Assert.AreEqual(sourceHash, await Sha256FileAsync(source));
        Assert.AreEqual(createdHash, await Sha256FileAsync(created));
        using (var external = ZipFile.OpenRead(rebuilt))
        using (var reader = new StreamReader(external.GetEntry("payload.txt")!.Open()))
        {
            Assert.AreEqual(payload, await reader.ReadToEndAsync());
        }

        var settingsDocuments = Directory.GetFiles(
            settingsRoot,
            "*",
            SearchOption.AllDirectories);
        Assert.IsNotEmpty(settingsDocuments);
        foreach (var document in settingsDocuments)
        {
            var text = await File.ReadAllTextAsync(document);
            Assert.IsFalse(text.Contains(source, StringComparison.Ordinal));
            Assert.IsFalse(text.Contains(created, StringComparison.Ordinal));
            Assert.IsFalse(text.Contains(rebuilt, StringComparison.Ordinal));
            Assert.IsFalse(text.Contains(payload, StringComparison.Ordinal));
        }

        Assert.HasCount(0, Directory.GetFiles(workerTemporaryRoot, "*", SearchOption.AllDirectories));
        Assert.HasCount(
            0,
            Directory.GetFiles(
                temporary.Path,
                ".rainarchives-*.staging",
                SearchOption.AllDirectories));
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task Milestone5DPackageRunsPropertiesAndSafePreviewFlow()
    {
        var repository = GetRepositoryRoot();
        var package = ResolvePackage(Path.Combine(
            repository,
            "artifacts",
            Milestone5DPackageDirectoryName));
        var manifestPath = Path.Combine(package.ArtifactRoot, "package-manifest.json");
        Assert.AreEqual(Milestone5DManifestSha256, Sha256File(manifestPath));
        var manifest = JsonSerializer.Deserialize<PackageManifest>(
            File.ReadAllText(manifestPath),
            ManifestJsonOptions);
        Assert.IsNotNull(manifest);
        Assert.AreEqual(1, manifest.SchemaVersion);
        Assert.AreEqual("1.22", manifest.Protocol);
        Assert.AreEqual(150, manifest.SourceInputCount);
        Assert.AreEqual(Milestone5DSourceSha256, manifest.SourceSha256);
        // M5D is immutable predecessor evidence. The live source advances independently in M5E.
        Assert.AreEqual(
            new TreeIdentity(
                609,
                233_098_501,
                "AC61D11EFBEF2CA4B83D5DE972F4A5B9746CBC28860189E4232E643E3ABE2EB0"),
            GetTreeIdentity(package.AppRoot));
        Assert.AreEqual(
            new TreeIdentity(
                205,
                85_041_541,
                "1487672F34AB9C054F67FF96C8B74D21DEA353AAAEAFD16416A34AE7D286491F"),
            GetTreeIdentity(Path.GetDirectoryName(package.WorkerPath)!));
        Assert.AreEqual(GetTreeIdentity(package.AppRoot), manifest.AppTree);
        Assert.AreEqual(
            GetTreeIdentity(Path.GetDirectoryName(package.WorkerPath)!),
            manifest.WorkerTree);
        AssertPackageComposition(package);

        var expectedFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["app/RainArchives.exe"] = "9EFD3BED1637298719FA33D50BF01C82D34040D2095D717E0A1BAFAE63D9CA26",
            ["app/RainArchives.dll"] = "B6CEE254CC8116B9B73F6708EB111DACE82EE668DCE9C51F54C370412EC52761",
            ["app/RainArchives.Client.dll"] = "ABC6B96B4692040BDB928F9A670BC6225F524950CB51D37F0CA92541C9A93603",
            ["app/RainArchives.Contracts.dll"] = "E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8",
            ["app/worker/RainArchives.Worker.exe"] = "C5A6D421ED4838A868704B472C6690AFA905F3EE0E5FB3E0F883D4BDC6EDEEDF",
            ["app/worker/RainArchives.Worker.dll"] = "3B994B6657E97D7E4769526710306B26C38EF2367FFD838F91D945086EB33BD8",
            ["app/worker/RainArchives.Contracts.dll"] = "E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8",
        };
        CollectionAssert.AreEquivalent(expectedFiles.Keys.ToArray(), manifest.Files.Keys.ToArray());
        foreach (var expected in expectedFiles)
        {
            Assert.AreEqual(expected.Value, manifest.Files[expected.Key]);
            Assert.AreEqual(
                expected.Value,
                Sha256File(Path.Combine(
                    package.ArtifactRoot,
                    expected.Key.Replace('/', Path.DirectorySeparatorChar))));
        }

        using var temporary = new TemporaryDirectory();
        var source = Path.Combine(temporary.Path, "m5d-preview.zip");
        var workerTemporaryRoot = Path.Combine(temporary.Path, "worker-temp");
        var previewCache = Path.Combine(temporary.Path, "preview-cache");
        Directory.CreateDirectory(workerTemporaryRoot);
        Directory.CreateDirectory(previewCache);
        using (var stream = new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            CreateEntry(archive, "雨.txt", "M5D package preview");
            var binary = archive.CreateEntry("binary.bin", CompressionLevel.NoCompression);
            await using (var output = binary.Open())
            {
                await output.WriteAsync(new byte[] { 0x41, 0x00, 0x42 });
            }

            var large = archive.CreateEntry("large.txt", CompressionLevel.NoCompression);
            await using (var output = large.Open())
            {
                await output.WriteAsync(Enumerable.Repeat(
                        (byte)'x',
                        TextEntryPreviewService.MaximumTextPreviewBytes + 1)
                    .ToArray());
            }
        }

        var sourceHash = await Sha256FileAsync(source);
        var dialogs = new RecordingDialogService();
        await using (var viewModel = new MainWindowViewModel(
                         dialogs,
                         async cancellationToken => await ArchiveClient.StartWithTemporaryRootAsync(
                                 package.WorkerPath,
                                 workerTemporaryRoot,
                                 cancellationToken)
                             .ConfigureAwait(false),
                         client => new TextEntryPreviewService(client, previewCache)))
        {
            await viewModel.OpenArchivePathAsync(source);
            Assert.AreEqual(ArchiveBrowserState.Ready, viewModel.Browser.State);
            Assert.AreEqual(ArchivePropertiesState.Ready, viewModel.Properties.State);
            Assert.AreEqual(
                "ZIP",
                viewModel.Properties.ArchiveItems.Single(item => item.Name == "Format").Value);
            Assert.IsFalse(viewModel.Properties.ArchiveItems.Any(item =>
                item.Value.Contains("RainArchives.Providers", StringComparison.Ordinal)));

            var text = viewModel.Entries.Single(entry => entry.Name == "雨.txt");
            viewModel.Browser.SelectedEntry = text;
            Assert.AreEqual(
                "#1",
                viewModel.Properties.EntryItems
                    .Single(item => item.Name == "Duplicate occurrence").Value);
            await viewModel.ActivateEntryAsync(text);
            Assert.AreEqual(SafePreviewState.Text, viewModel.Preview.State);
            Assert.AreEqual("M5D package preview", viewModel.Preview.Text);
            StringAssert.Contains(viewModel.Preview.Details, "UTF-8");
            await viewModel.ClosePreviewAsync();

            var binary = viewModel.Entries.Single(entry => entry.Name == "binary.bin");
            viewModel.Browser.SelectedEntry = binary;
            await viewModel.ActivateEntryAsync(binary);
            Assert.AreEqual(SafePreviewState.Binary, viewModel.Preview.State);
            Assert.AreEqual(string.Empty, viewModel.Preview.Text);
            await viewModel.ClosePreviewAsync();

            var large = viewModel.Entries.Single(entry => entry.Name == "large.txt");
            viewModel.Browser.SelectedEntry = large;
            await viewModel.ActivateEntryAsync(large);
            Assert.AreEqual(SafePreviewState.Oversize, viewModel.Preview.State);
            await viewModel.ClosePreviewAsync();
            Assert.IsNull(dialogs.ErrorMessage);
        }

        Assert.AreEqual(sourceHash, await Sha256FileAsync(source));
        Assert.HasCount(0, Directory.GetFiles(workerTemporaryRoot, "*", SearchOption.AllDirectories));
        Assert.HasCount(0, Directory.GetFiles(previewCache, "*", SearchOption.AllDirectories));
        Assert.HasCount(
            0,
            Directory.GetFiles(
                temporary.Path,
                ".rainarchives-*.staging",
                SearchOption.AllDirectories));
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task Milestone5EPackageRunsUnifiedRecoveryCenterModels()
    {
        var repository = GetRepositoryRoot();
        var package = ResolvePackage(Path.Combine(
            repository,
            "artifacts",
            Milestone5EPackageDirectoryName));
        var manifestPath = Path.Combine(package.ArtifactRoot, "package-manifest.json");
        Assert.AreEqual(Milestone5EManifestSha256, Sha256File(manifestPath));
        var manifest = JsonSerializer.Deserialize<PackageManifest>(
            File.ReadAllText(manifestPath),
            ManifestJsonOptions);
        Assert.IsNotNull(manifest);
        Assert.AreEqual(1, manifest.SchemaVersion);
        Assert.AreEqual("1.22", manifest.Protocol);
        Assert.AreEqual(152, manifest.SourceInputCount);
        Assert.AreEqual(Milestone5ESourceSha256, manifest.SourceSha256);
        // M5E is immutable predecessor evidence. The live source advances independently in M5F.
        Assert.AreEqual(
            new TreeIdentity(
                609,
                233_165_429,
                "F6D83EFEC18C7D60F09A5D7FD9E79CDF29FD912464F16347582B51FC2157F88D"),
            GetTreeIdentity(package.AppRoot));
        Assert.AreEqual(
            new TreeIdentity(
                205,
                85_041_541,
                "1487672F34AB9C054F67FF96C8B74D21DEA353AAAEAFD16416A34AE7D286491F"),
            GetTreeIdentity(Path.GetDirectoryName(package.WorkerPath)!));
        Assert.AreEqual(GetTreeIdentity(package.AppRoot), manifest.AppTree);
        Assert.AreEqual(
            GetTreeIdentity(Path.GetDirectoryName(package.WorkerPath)!),
            manifest.WorkerTree);
        AssertPackageComposition(package);

        var expectedFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["app/RainArchives.exe"] = "9EFD3BED1637298719FA33D50BF01C82D34040D2095D717E0A1BAFAE63D9CA26",
            ["app/RainArchives.dll"] = "6D0992C0BC62746B56C3EB1629C53B02A95963DBAEE3E963F4DAE161BCB9BA91",
            ["app/RainArchives.Client.dll"] = "53B933055CC5043FE8C5B15DB0B61C783AFCBBE429DE1E6EF443CD317498292D",
            ["app/RainArchives.Contracts.dll"] = "E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8",
            ["app/worker/RainArchives.Worker.exe"] = "C5A6D421ED4838A868704B472C6690AFA905F3EE0E5FB3E0F883D4BDC6EDEEDF",
            ["app/worker/RainArchives.Worker.dll"] = "3B994B6657E97D7E4769526710306B26C38EF2367FFD838F91D945086EB33BD8",
            ["app/worker/RainArchives.Contracts.dll"] = "E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8",
        };
        CollectionAssert.AreEquivalent(expectedFiles.Keys.ToArray(), manifest.Files.Keys.ToArray());
        foreach (var expected in expectedFiles)
        {
            Assert.AreEqual(expected.Value, manifest.Files[expected.Key]);
            Assert.AreEqual(
                expected.Value,
                Sha256File(Path.Combine(
                    package.ArtifactRoot,
                    expected.Key.Replace('/', Path.DirectorySeparatorChar))));
        }

        string fixtureRoot;
        using (var temporary = new TemporaryDirectory())
        {
            fixtureRoot = temporary.Path;
            var workerTemporaryRoot = Path.Combine(temporary.Path, "m5e-worker-temp");
            Directory.CreateDirectory(workerTemporaryRoot);
            var creationRecoveryRoot = Path.Combine(
                workerTemporaryRoot,
                "rainarchives-creation-recovery");
            var extractionRecoveryRoot = Path.Combine(
                workerTemporaryRoot,
                "rainarchives-extraction-recovery");

            var creationDestination = Path.Combine(temporary.Path, "creation-output.zip");
            var creationStore = new ArchiveCreationRecoveryStore(creationRecoveryRoot);
            var creationTransaction = await creationStore.BeginAsync(
                OperationId.New(),
                creationDestination,
                FileSystemIdentityService.CaptureExistingDirectoryIdentity(temporary.Path),
                CancellationToken.None);
            await creationTransaction.DisposeAsync();

            var extractionDestination = Path.Combine(temporary.Path, "extraction-output");
            var extractionStore = new ExtractionRecoveryStore(extractionRecoveryRoot);
            var extractionTransaction = await extractionStore.BeginAsync(
                OperationId.New(),
                extractionDestination,
                FileSystemIdentityService.CaptureExistingDirectoryIdentity(temporary.Path),
                CancellationToken.None);
            await extractionTransaction.DisposeAsync();

            var recoveryId = new string('c', 64);
            var sourceId = new string('d', 64);
            var workspaceName = OwnedNestedTemporaryWorkspace.WorkspacePrefix + recoveryId;
            var recoveryWorkspace = Path.Combine(workerTemporaryRoot, workspaceName);
            Directory.CreateDirectory(recoveryWorkspace);
            await File.WriteAllBytesAsync(
                Path.Combine(recoveryWorkspace, OwnedNestedTemporaryWorkspace.OwnerFileName),
                OwnedNestedTemporaryWorkspace.CreateOwnerRecord(workspaceName));
            var sourceDirectory = Path.Combine(
                recoveryWorkspace,
                OwnedTemporaryArchiveSource.SourceDirectoryPrefix + sourceId);
            Directory.CreateDirectory(sourceDirectory);
            var recoveredSource = Path.Combine(
                sourceDirectory,
                OwnedTemporaryArchiveSource.SourceFileName);
            CreateSourceArchive(recoveredSource);
            var recoveredHash = await Sha256FileAsync(recoveredSource);

            await using (var client = await ArchiveClient.StartWithIsolatedRecoveryRootsAsync(
                             package.WorkerPath,
                             workerTemporaryRoot))
            {
                await using var creation = new ArchiveRecoveryCenterViewModel(client, _ => true);
                await creation.InitializeAsync();
                Assert.HasCount(1, creation.Recoveries);
                Assert.IsFalse(creation.InventorySummaryText.Contains(
                    creationDestination,
                    StringComparison.OrdinalIgnoreCase));
                creation.SelectedRecovery = creation.Recoveries.Single();
                await creation.InspectSelectedAsync();
                Assert.IsTrue(creation.CanAcknowledgeNoOwnedArtifact);
                await creation.ApplyDispositionAsync(
                    ArchiveCreationRecoveryDispositionAction.AcknowledgeNoOwnedArtifact);
                Assert.AreEqual(0, creationStore.Inspect().PotentialRecoveryCount);

                await using var extraction = new ExtractionRecoveryCenterViewModel(client);
                await extraction.InitializeAsync();
                Assert.HasCount(1, extraction.Recoveries);
                Assert.IsFalse(extraction.InventorySummaryText.Contains(
                    extractionDestination,
                    StringComparison.OrdinalIgnoreCase));
                extraction.SelectedRecovery = extraction.Recoveries.Single();
                await extraction.InspectSelectedAsync();
                StringAssert.Contains(extraction.ActionGuidance, "read-only");
                Assert.AreEqual(1, extractionStore.Inspect().PotentialRecoveryCount);
                Assert.IsFalse(Directory.Exists(extractionDestination));

                ReopenNestedWorkspaceRecoveryResponse? reopened = null;
                await using var nested = new NestedWorkspaceRecoveryCenterViewModel(
                    client,
                    _ => true,
                    _ => true,
                    requestPasswordSlot: null,
                    result => reopened = result);
                await nested.InitializeAsync();
                nested.SelectedRecovery = nested.Recoveries.Single();
                await nested.InspectSelectedAsync();
                Assert.IsTrue(nested.CanReopen);
                await nested.ReopenInspectedAsync();
                Assert.IsNotNull(reopened);
                Assert.AreEqual(0, reopened.Depth);
                Assert.IsTrue((await client.ListEntriesAsync(reopened.Archive.SessionId, 0, 32))
                    .Entries.Any(entry => entry.RawPath == "keep.txt"));
                Assert.IsTrue((await client.CloseArchiveAsync(reopened.Archive.SessionId)).Closed);
                Assert.AreEqual(recoveredHash, await Sha256FileAsync(recoveredSource));

                await nested.RefreshAsync();
                nested.SelectedRecovery = nested.Recoveries.Single();
                await nested.InspectSelectedAsync();
                await nested.DiscardInspectedAsync();
                Assert.IsFalse(Directory.Exists(recoveryWorkspace));

                await using var support = new RecoverySupportViewModel(client);
                await support.InitializeAsync();
                Assert.IsTrue(support.Diagnostics.IsAvailable);
                StringAssert.Contains(support.Diagnostics.ProtocolText, "1.22");
                StringAssert.Contains(
                    support.Diagnostics.SandboxText,
                    "Not a same-user filesystem sandbox");
                StringAssert.Contains(support.Diagnostics.RecoveryText, "unavailable");
            }

            Assert.IsFalse(File.Exists(creationDestination));
            Assert.IsFalse(Directory.Exists(extractionDestination));
            Assert.HasCount(
                0,
                Directory.GetFiles(
                    workerTemporaryRoot,
                    ".rainarchives-*.staging",
                    SearchOption.AllDirectories));
        }

        Assert.IsFalse(Directory.Exists(fixtureRoot));
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task Milestone5FPackageRunsContextAuthorityProductFlow()
    {
        var repository = GetRepositoryRoot();
        var package = ResolvePackage(Path.Combine(
            repository,
            "artifacts",
            Milestone5FPackageDirectoryName));
        var manifestPath = Path.Combine(package.ArtifactRoot, "package-manifest.json");
        Assert.AreEqual(Milestone5FManifestSha256, Sha256File(manifestPath));
        var manifest = JsonSerializer.Deserialize<PackageManifest>(
            File.ReadAllText(manifestPath),
            ManifestJsonOptions);
        Assert.IsNotNull(manifest);
        Assert.AreEqual(1, manifest.SchemaVersion);
        Assert.AreEqual("1.22", manifest.Protocol);
        Assert.AreEqual(153, manifest.SourceInputCount);
        Assert.AreEqual(Milestone5FSourceSha256, manifest.SourceSha256);
        Assert.AreEqual(
            new TreeIdentity(
                609,
                233_181_141,
                "2A3FD53E4C8DF40F1F6E3B6A48E6BEF01DC9885F4EAD7B8F817357F1717A472A"),
            GetTreeIdentity(package.AppRoot));
        Assert.AreEqual(
            new TreeIdentity(
                205,
                85_041_541,
                "1487672F34AB9C054F67FF96C8B74D21DEA353AAAEAFD16416A34AE7D286491F"),
            GetTreeIdentity(Path.GetDirectoryName(package.WorkerPath)!));
        Assert.AreEqual(GetTreeIdentity(package.AppRoot), manifest.AppTree);
        Assert.AreEqual(
            GetTreeIdentity(Path.GetDirectoryName(package.WorkerPath)!),
            manifest.WorkerTree);
        AssertPackageComposition(package);

        var expectedFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["app/RainArchives.exe"] = "9EFD3BED1637298719FA33D50BF01C82D34040D2095D717E0A1BAFAE63D9CA26",
            ["app/RainArchives.dll"] = "3C9031FD54569C99D736E0BD9DA2C6589F50C590AA8D494D1E043F1D10A99FC1",
            ["app/RainArchives.Client.dll"] = "53B933055CC5043FE8C5B15DB0B61C783AFCBBE429DE1E6EF443CD317498292D",
            ["app/RainArchives.Contracts.dll"] = "E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8",
            ["app/worker/RainArchives.Worker.exe"] = "C5A6D421ED4838A868704B472C6690AFA905F3EE0E5FB3E0F883D4BDC6EDEEDF",
            ["app/worker/RainArchives.Worker.dll"] = "3B994B6657E97D7E4769526710306B26C38EF2367FFD838F91D945086EB33BD8",
            ["app/worker/RainArchives.Contracts.dll"] = "E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8",
        };
        CollectionAssert.AreEquivalent(expectedFiles.Keys.ToArray(), manifest.Files.Keys.ToArray());
        foreach (var expected in expectedFiles)
        {
            Assert.AreEqual(expected.Value, manifest.Files[expected.Key]);
            Assert.AreEqual(
                expected.Value,
                Sha256File(Path.Combine(
                    package.ArtifactRoot,
                    expected.Key.Replace('/', Path.DirectorySeparatorChar))));
        }

        using var temporary = new TemporaryDirectory();
        var source = Path.Combine(temporary.Path, "m5f-context-source.zip");
        var extraction = Path.Combine(temporary.Path, "m5f-selected-extraction");
        var workerTemporaryRoot = Path.Combine(temporary.Path, "worker-temp");
        var previewCache = Path.Combine(temporary.Path, "preview-cache");
        Directory.CreateDirectory(workerTemporaryRoot);
        Directory.CreateDirectory(previewCache);
        using (var stream = new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            _ = archive.CreateEntry("folder/", CompressionLevel.NoCompression);
            CreateEntry(archive, "folder/雨.txt", "M5F package preview");
            CreateEntry(archive, "root.txt", "M5F package extraction");
        }

        var sourceHash = await Sha256FileAsync(source);
        var dialogs = new RecordingDialogService
        {
            ExtractionDestination = extraction,
        };
        await using (var viewModel = new MainWindowViewModel(
                         dialogs,
                         async cancellationToken => await ArchiveClient.StartWithTemporaryRootAsync(
                                 package.WorkerPath,
                                 workerTemporaryRoot,
                                 cancellationToken)
                             .ConfigureAwait(false),
                         client => new TextEntryPreviewService(client, previewCache)))
        {
            await viewModel.OpenArchivePathAsync(source);
            var folder = viewModel.Entries.Single(entry => entry.IsDirectory);
            viewModel.Browser.SelectedEntry = folder;
            Assert.IsTrue(viewModel.CommandContext.CanNavigate);
            Assert.IsFalse(viewModel.CommandContext.CanPreview);
            viewModel.NavigateSelectionCommand.Execute(null);
            await WaitForViewModelAsync(() => viewModel.CurrentFolderId == folder.EntryId);

            var previewEntry = viewModel.Entries.Single();
            viewModel.Browser.SelectedEntry = previewEntry;
            Assert.IsTrue(viewModel.CommandContext.CanPreview);
            Assert.IsTrue(viewModel.CommandContext.CanShowProperties);
            Assert.IsTrue(viewModel.CommandContext.CanTestIntegrity);
            Assert.IsTrue(viewModel.CommandContext.CanExtractSelected);
            viewModel.PreviewSelectionCommand.Execute(null);
            await WaitForViewModelAsync(() => viewModel.Preview.State == SafePreviewState.Text);
            Assert.AreEqual("M5F package preview", viewModel.Preview.Text);
            viewModel.ShowPropertiesCommand.Execute(null);
            Assert.AreEqual(0, viewModel.InspectorSelectedTabIndex);
            Assert.IsTrue(viewModel.Properties.HasSelectedEntry);

            var previousOperationId = viewModel.ActiveActivity.OperationId;
            viewModel.TestCommand.Execute(null);
            await WaitForViewModelOperationAsync(viewModel, previousOperationId);
            Assert.AreEqual(OperationPhase.Completed, viewModel.ActiveActivity.Phase);

            viewModel.RootCommand.Execute(null);
            await WaitForViewModelAsync(() => viewModel.CurrentFolderId is null);
            var extractEntry = viewModel.Entries.Single(entry => entry.Name == "root.txt");
            viewModel.Browser.SelectedEntry = extractEntry;
            previousOperationId = viewModel.ActiveActivity.OperationId;
            viewModel.ExtractSelectionCommand.Execute(null);
            await WaitForViewModelOperationAsync(viewModel, previousOperationId);
            Assert.AreEqual(OperationPhase.Completed, viewModel.ActiveActivity.Phase, dialogs.ErrorMessage);
            Assert.AreEqual(
                "M5F package extraction",
                await File.ReadAllTextAsync(Path.Combine(extraction, "root.txt")));
        }

        Assert.AreEqual(sourceHash, await Sha256FileAsync(source));
        Assert.HasCount(0, Directory.GetFiles(workerTemporaryRoot, "*", SearchOption.AllDirectories));
        Assert.HasCount(0, Directory.GetFiles(previewCache, "*", SearchOption.AllDirectories));
        Assert.HasCount(
            0,
            Directory.GetFiles(
                temporary.Path,
                ".rainarchives-*.staging",
                SearchOption.AllDirectories));
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task Milestone5FInspectorRemediationPackageRunsInspectorLifecycleProductFlow()
    {
        var repository = GetRepositoryRoot();
        var package = ResolvePackage(Path.Combine(
            repository,
            "artifacts",
            Milestone5FInspectorRemediationPackageDirectoryName));
        var manifestPath = Path.Combine(package.ArtifactRoot, "package-manifest.json");
        Assert.AreEqual(Milestone5FInspectorRemediationManifestSha256, Sha256File(manifestPath));
        var manifest = JsonSerializer.Deserialize<PackageManifest>(
            File.ReadAllText(manifestPath),
            ManifestJsonOptions);
        Assert.IsNotNull(manifest);
        Assert.AreEqual(1, manifest.SchemaVersion);
        Assert.AreEqual("1.22", manifest.Protocol);
        Assert.AreEqual(153, manifest.SourceInputCount);
        Assert.AreEqual(Milestone5FInspectorRemediationSourceSha256, manifest.SourceSha256);
        Assert.AreEqual(
            new TreeIdentity(
                609,
                233_182_721,
                "78945421A56A188ED64C6908574613DEA3BEE0C3DBCB43F1A89EFE24BAA336C6"),
            GetTreeIdentity(package.AppRoot));
        Assert.AreEqual(
            new TreeIdentity(
                205,
                85_041_541,
                "1487672F34AB9C054F67FF96C8B74D21DEA353AAAEAFD16416A34AE7D286491F"),
            GetTreeIdentity(Path.GetDirectoryName(package.WorkerPath)!));
        Assert.AreEqual(GetTreeIdentity(package.AppRoot), manifest.AppTree);
        Assert.AreEqual(
            GetTreeIdentity(Path.GetDirectoryName(package.WorkerPath)!),
            manifest.WorkerTree);
        AssertPackageComposition(package);

        var expectedFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["app/RainArchives.exe"] = "9EFD3BED1637298719FA33D50BF01C82D34040D2095D717E0A1BAFAE63D9CA26",
            ["app/RainArchives.dll"] = "54AF1FBFCD72390B31023DDCFF4FF7FA164AEF985271471B7097AD642DD7CA52",
            ["app/RainArchives.Client.dll"] = "53B933055CC5043FE8C5B15DB0B61C783AFCBBE429DE1E6EF443CD317498292D",
            ["app/RainArchives.Contracts.dll"] = "E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8",
            ["app/worker/RainArchives.Worker.exe"] = "C5A6D421ED4838A868704B472C6690AFA905F3EE0E5FB3E0F883D4BDC6EDEEDF",
            ["app/worker/RainArchives.Worker.dll"] = "3B994B6657E97D7E4769526710306B26C38EF2367FFD838F91D945086EB33BD8",
            ["app/worker/RainArchives.Contracts.dll"] = "E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8",
        };
        CollectionAssert.AreEquivalent(expectedFiles.Keys.ToArray(), manifest.Files.Keys.ToArray());
        foreach (var expected in expectedFiles)
        {
            Assert.AreEqual(expected.Value, manifest.Files[expected.Key]);
            Assert.AreEqual(
                expected.Value,
                Sha256File(Path.Combine(
                    package.ArtifactRoot,
                    expected.Key.Replace('/', Path.DirectorySeparatorChar))));
        }

        using var temporary = new TemporaryDirectory();
        var source = Path.Combine(temporary.Path, "m5f-inspector-source.zip");
        var workerTemporaryRoot = Path.Combine(temporary.Path, "worker-temp");
        var previewCache = Path.Combine(temporary.Path, "preview-cache");
        Directory.CreateDirectory(workerTemporaryRoot);
        Directory.CreateDirectory(previewCache);
        using (var stream = new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            CreateEntry(archive, "雨.txt", "M5F inspector remediation preview");
        }

        var sourceHash = await Sha256FileAsync(source);
        await using (var viewModel = new MainWindowViewModel(
                         new RecordingDialogService(),
                         async cancellationToken => await ArchiveClient.StartWithTemporaryRootAsync(
                                 package.WorkerPath,
                                 workerTemporaryRoot,
                                 cancellationToken)
                             .ConfigureAwait(false),
                         client => new TextEntryPreviewService(client, previewCache)))
        {
            ArchiveBrowserFocusTarget? restoredFocus = null;
            viewModel.Browser.FocusRequested += (_, args) => restoredFocus = args.Target;

            await viewModel.OpenArchivePathAsync(source);
            viewModel.Browser.SelectedEntry = viewModel.Entries.Single();
            Assert.IsFalse(viewModel.IsInspectorVisible);
            Assert.IsFalse(viewModel.CloseInspectorCommand.CanExecute(null));

            viewModel.ShowPropertiesCommand.Execute(null);
            await WaitForViewModelAsync(() =>
                viewModel.IsInspectorVisible && viewModel.InspectorSelectedTabIndex == 0);
            Assert.IsTrue(viewModel.Properties.HasSelectedEntry);
            await viewModel.CloseInspectorAsync();
            Assert.IsFalse(viewModel.IsInspectorVisible);
            Assert.AreEqual(ArchiveBrowserFocusTarget.EntryList, restoredFocus);

            viewModel.PreviewSelectionCommand.Execute(null);
            await WaitForViewModelAsync(() => viewModel.Preview.State == SafePreviewState.Text);
            Assert.IsTrue(viewModel.IsInspectorVisible);
            Assert.AreEqual(1, viewModel.InspectorSelectedTabIndex);
            Assert.AreEqual("M5F inspector remediation preview", viewModel.Preview.Text);
            await viewModel.CloseInspectorAsync();
            Assert.IsFalse(viewModel.IsInspectorVisible);
            Assert.AreEqual(SafePreviewState.Idle, viewModel.Preview.State);

            viewModel.ShowPropertiesCommand.Execute(null);
            await WaitForViewModelAsync(() =>
                viewModel.IsInspectorVisible && viewModel.InspectorSelectedTabIndex == 0);
            await viewModel.CloseInspectorAsync();
            Assert.IsFalse(viewModel.IsInspectorVisible);
        }

        Assert.AreEqual(sourceHash, await Sha256FileAsync(source));
        Assert.HasCount(0, Directory.GetFiles(workerTemporaryRoot, "*", SearchOption.AllDirectories));
        Assert.HasCount(0, Directory.GetFiles(previewCache, "*", SearchOption.AllDirectories));
        Assert.HasCount(
            0,
            Directory.GetFiles(
                temporary.Path,
                ".rainarchives-*.staging",
                SearchOption.AllDirectories));
    }

    [TestMethod]
    public void RejectedMilestone5GAPreRemediationPackageRemainsImmutable()
    {
        var repository = GetRepositoryRoot();
        var package = ResolvePackage(Path.Combine(
            repository,
            "artifacts",
            RejectedMilestone5GAPackageDirectoryName));
        var manifestPath = Path.Combine(package.ArtifactRoot, "package-manifest.json");
        Assert.AreEqual(RejectedMilestone5GAManifestSha256, Sha256File(manifestPath));
        var manifest = JsonSerializer.Deserialize<PackageManifest>(
            File.ReadAllText(manifestPath),
            ManifestJsonOptions);
        Assert.IsNotNull(manifest);
        Assert.AreEqual(1, manifest.SchemaVersion);
        Assert.AreEqual("1.22", manifest.Protocol);
        Assert.AreEqual(158, manifest.SourceInputCount);
        Assert.AreEqual(RejectedMilestone5GASourceSha256, manifest.SourceSha256);
        Assert.AreEqual(
            new TreeIdentity(
                609,
                233_233_429,
                "0D016721475FB431CDE636711855D6D267D1F0641A9219221A649E318674258E"),
            GetTreeIdentity(package.AppRoot));
        Assert.AreEqual(
            new TreeIdentity(
                205,
                85_041_541,
                "1487672F34AB9C054F67FF96C8B74D21DEA353AAAEAFD16416A34AE7D286491F"),
            GetTreeIdentity(Path.GetDirectoryName(package.WorkerPath)!));
        Assert.AreEqual(GetTreeIdentity(package.AppRoot), manifest.AppTree);
        Assert.AreEqual(
            GetTreeIdentity(Path.GetDirectoryName(package.WorkerPath)!),
            manifest.WorkerTree);
        AssertPackageComposition(package);

        var expectedFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["app/RainArchives.exe"] = "9EFD3BED1637298719FA33D50BF01C82D34040D2095D717E0A1BAFAE63D9CA26",
            ["app/RainArchives.dll"] = "49CD53ABDA0DD99BBFD1751B866B28CE441FCEC5234F50ABA548A71FFC95F468",
            ["app/RainArchives.Client.dll"] = "53B933055CC5043FE8C5B15DB0B61C783AFCBBE429DE1E6EF443CD317498292D",
            ["app/RainArchives.Contracts.dll"] = "E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8",
            ["app/worker/RainArchives.Worker.exe"] = "C5A6D421ED4838A868704B472C6690AFA905F3EE0E5FB3E0F883D4BDC6EDEEDF",
            ["app/worker/RainArchives.Worker.dll"] = "3B994B6657E97D7E4769526710306B26C38EF2367FFD838F91D945086EB33BD8",
            ["app/worker/RainArchives.Contracts.dll"] = "E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8",
        };
        CollectionAssert.AreEquivalent(expectedFiles.Keys.ToArray(), manifest.Files.Keys.ToArray());
        foreach (var expected in expectedFiles)
        {
            Assert.AreEqual(expected.Value, manifest.Files[expected.Key]);
            Assert.AreEqual(
                expected.Value,
                Sha256File(Path.Combine(
                    package.ArtifactRoot,
                    expected.Key.Replace('/', Path.DirectorySeparatorChar))));
        }
    }

    [TestMethod]
    [Timeout(120_000)]
    public void Milestone5GAAccessibilityRemediationPackageRemainsImmutableHistoricalEvidence()
    {
        var repository = GetRepositoryRoot();
        var package = ResolvePackage(Path.Combine(
            repository,
            "artifacts",
            Milestone5GAAccessibilityRemediationPackageDirectoryName));
        var manifestPath = Path.Combine(package.ArtifactRoot, "package-manifest.json");
        Assert.AreEqual(Milestone5GAAccessibilityRemediationManifestSha256, Sha256File(manifestPath));
        var manifest = JsonSerializer.Deserialize<PackageManifest>(
            File.ReadAllText(manifestPath),
            ManifestJsonOptions);
        Assert.IsNotNull(manifest);
        Assert.AreEqual(1, manifest.SchemaVersion);
        Assert.AreEqual("1.22", manifest.Protocol);
        Assert.AreEqual(158, manifest.SourceInputCount);
        Assert.AreEqual(Milestone5GAAccessibilityRemediationSourceSha256, manifest.SourceSha256);
        Assert.AreEqual(
            new TreeIdentity(
                609,
                233_238_233,
                "BB266C5E105FB731220D56BD31A75316FBA8BC68CC9B62277B8A38F6F5FA828B"),
            GetTreeIdentity(package.AppRoot));
        Assert.AreEqual(
            new TreeIdentity(
                205,
                85_041_541,
                "1487672F34AB9C054F67FF96C8B74D21DEA353AAAEAFD16416A34AE7D286491F"),
            GetTreeIdentity(Path.GetDirectoryName(package.WorkerPath)!));
        Assert.AreEqual(GetTreeIdentity(package.AppRoot), manifest.AppTree);
        Assert.AreEqual(
            GetTreeIdentity(Path.GetDirectoryName(package.WorkerPath)!),
            manifest.WorkerTree);
        AssertPackageComposition(package);

        var expectedFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["app/RainArchives.exe"] = "9EFD3BED1637298719FA33D50BF01C82D34040D2095D717E0A1BAFAE63D9CA26",
            ["app/RainArchives.dll"] = "D0E7A321DBA964C5F66DFE41CE2B235E37523CAB6F733CF5F4FF7EE0BEA8E9BD",
            ["app/RainArchives.Client.dll"] = "53B933055CC5043FE8C5B15DB0B61C783AFCBBE429DE1E6EF443CD317498292D",
            ["app/RainArchives.Contracts.dll"] = "E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8",
            ["app/worker/RainArchives.Worker.exe"] = "C5A6D421ED4838A868704B472C6690AFA905F3EE0E5FB3E0F883D4BDC6EDEEDF",
            ["app/worker/RainArchives.Worker.dll"] = "3B994B6657E97D7E4769526710306B26C38EF2367FFD838F91D945086EB33BD8",
            ["app/worker/RainArchives.Contracts.dll"] = "E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8",
        };
        CollectionAssert.AreEquivalent(expectedFiles.Keys.ToArray(), manifest.Files.Keys.ToArray());
        foreach (var expected in expectedFiles)
        {
            Assert.AreEqual(expected.Value, manifest.Files[expected.Key]);
            Assert.AreEqual(
                expected.Value,
                Sha256File(Path.Combine(
                    package.ArtifactRoot,
                    expected.Key.Replace('/', Path.DirectorySeparatorChar))));
        }
    }

    [TestMethod]
    [Timeout(120_000)]
    public void Milestone5GBSecondaryWindowPackageRemainsImmutableHistoricalEvidence()
    {
        var repository = GetRepositoryRoot();
        var package = ResolvePackage(Path.Combine(
            repository,
            "artifacts",
            Milestone5GBSecondaryWindowPackageDirectoryName));
        var manifestPath = Path.Combine(package.ArtifactRoot, "package-manifest.json");
        Assert.AreEqual(Milestone5GBSecondaryWindowManifestSha256, Sha256File(manifestPath));
        var manifest = JsonSerializer.Deserialize<PackageManifest>(
            File.ReadAllText(manifestPath),
            ManifestJsonOptions);
        Assert.IsNotNull(manifest);
        Assert.AreEqual(1, manifest.SchemaVersion);
        Assert.AreEqual("1.22", manifest.Protocol);
        Assert.AreEqual(158, manifest.SourceInputCount);
        Assert.AreEqual(Milestone5GBSecondaryWindowSourceSha256, manifest.SourceSha256);
        Assert.AreEqual(
            new TreeIdentity(
                609,
                233_249_953,
                "8D551EC0E0F7F513EB21DBBB65DCCDFDCC1FA8349A15A0AC82864878B7DCC342"),
            GetTreeIdentity(package.AppRoot));
        Assert.AreEqual(
            new TreeIdentity(
                205,
                85_041_541,
                "1487672F34AB9C054F67FF96C8B74D21DEA353AAAEAFD16416A34AE7D286491F"),
            GetTreeIdentity(Path.GetDirectoryName(package.WorkerPath)!));
        Assert.AreEqual(GetTreeIdentity(package.AppRoot), manifest.AppTree);
        Assert.AreEqual(
            GetTreeIdentity(Path.GetDirectoryName(package.WorkerPath)!),
            manifest.WorkerTree);
        AssertPackageComposition(package);

        var expectedFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["app/RainArchives.exe"] = "9EFD3BED1637298719FA33D50BF01C82D34040D2095D717E0A1BAFAE63D9CA26",
            ["app/RainArchives.dll"] = "4BDE3284E4CF14FFAC46F0DC9C3361C61072B1BE05C3C7A55C6965F4040090C7",
            ["app/RainArchives.Client.dll"] = "53B933055CC5043FE8C5B15DB0B61C783AFCBBE429DE1E6EF443CD317498292D",
            ["app/RainArchives.Contracts.dll"] = "E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8",
            ["app/worker/RainArchives.Worker.exe"] = "C5A6D421ED4838A868704B472C6690AFA905F3EE0E5FB3E0F883D4BDC6EDEEDF",
            ["app/worker/RainArchives.Worker.dll"] = "3B994B6657E97D7E4769526710306B26C38EF2367FFD838F91D945086EB33BD8",
            ["app/worker/RainArchives.Contracts.dll"] = "E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8",
        };
        CollectionAssert.AreEquivalent(expectedFiles.Keys.ToArray(), manifest.Files.Keys.ToArray());
        foreach (var expected in expectedFiles)
        {
            Assert.AreEqual(expected.Value, manifest.Files[expected.Key]);
            Assert.AreEqual(
                expected.Value,
                Sha256File(Path.Combine(
                    package.ArtifactRoot,
                    expected.Key.Replace('/', Path.DirectorySeparatorChar))));
        }
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task Milestone5GCProductIdentityPackageRemainsImmutableHistoricalEvidence()
    {
        var repository = GetRepositoryRoot();
        var package = ResolvePackage(Path.Combine(
            repository,
            "artifacts",
            Milestone5GCProductIdentityPackageDirectoryName));
        var manifestPath = Path.Combine(package.ArtifactRoot, "package-manifest.json");
        Assert.AreEqual(Milestone5GCProductIdentityManifestSha256, Sha256File(manifestPath));
        var manifest = JsonSerializer.Deserialize<PackageManifest>(
            File.ReadAllText(manifestPath),
            ManifestJsonOptions);
        Assert.IsNotNull(manifest);
        Assert.AreEqual(1, manifest.SchemaVersion);
        Assert.AreEqual("1.22", manifest.Protocol);
        Assert.AreEqual(159, manifest.SourceInputCount);
        Assert.AreEqual(Milestone5GCProductIdentitySourceSha256, manifest.SourceSha256);
        Assert.AreEqual(
            new TreeIdentity(
                609,
                234_012_833,
                "D490AB5B36ECDD4D3C67384C86178A7A23467E99E1E4CE6F8459B4676A99F8B5"),
            GetTreeIdentity(package.AppRoot));
        Assert.AreEqual(
            new TreeIdentity(
                205,
                85_041_541,
                "1487672F34AB9C054F67FF96C8B74D21DEA353AAAEAFD16416A34AE7D286491F"),
            GetTreeIdentity(Path.GetDirectoryName(package.WorkerPath)!));
        Assert.AreEqual(GetTreeIdentity(package.AppRoot), manifest.AppTree);
        Assert.AreEqual(
            GetTreeIdentity(Path.GetDirectoryName(package.WorkerPath)!),
            manifest.WorkerTree);
        AssertPackageComposition(package);
        AssertWindowsExecutableHasIcon(Path.Combine(package.AppRoot, "RainArchives.exe"));

        var expectedFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["app/RainArchives.exe"] = "A565C435B523F656416ADB7736A0D005BB75B3D733379D2D443A7F7794F2E5C9",
            ["app/RainArchives.dll"] = "B5827F9F439EE9D672BD3040AB797A286B0C6067104704936B31A740879CE25C",
            ["app/RainArchives.Client.dll"] = "53B933055CC5043FE8C5B15DB0B61C783AFCBBE429DE1E6EF443CD317498292D",
            ["app/RainArchives.Contracts.dll"] = "E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8",
            ["app/worker/RainArchives.Worker.exe"] = "C5A6D421ED4838A868704B472C6690AFA905F3EE0E5FB3E0F883D4BDC6EDEEDF",
            ["app/worker/RainArchives.Worker.dll"] = "3B994B6657E97D7E4769526710306B26C38EF2367FFD838F91D945086EB33BD8",
            ["app/worker/RainArchives.Contracts.dll"] = "E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8",
        };
        CollectionAssert.AreEquivalent(expectedFiles.Keys.ToArray(), manifest.Files.Keys.ToArray());
        foreach (var expected in expectedFiles)
        {
            Assert.AreEqual(expected.Value, manifest.Files[expected.Key]);
            Assert.AreEqual(
                expected.Value,
                Sha256File(Path.Combine(
                    package.ArtifactRoot,
                    expected.Key.Replace('/', Path.DirectorySeparatorChar))));
        }

        await AssertMilestone5GPackageBrowserFlowAsync(package, "M5G-C product-identity");
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task Milestone5GDDesignSystemPackageMatchesCurrentSourceAndRunsBrowserFlow()
    {
        var repository = GetRepositoryRoot();
        var package = ResolvePackage(Path.Combine(
            repository,
            "artifacts",
            Milestone5GDDesignSystemPackageDirectoryName));
        var manifestPath = Path.Combine(package.ArtifactRoot, "package-manifest.json");
        Assert.AreEqual(Milestone5GDDesignSystemManifestSha256, Sha256File(manifestPath));
        var manifest = JsonSerializer.Deserialize<PackageManifest>(
            File.ReadAllText(manifestPath),
            ManifestJsonOptions);
        Assert.IsNotNull(manifest);
        Assert.AreEqual(1, manifest.SchemaVersion);
        Assert.AreEqual("1.22", manifest.Protocol);

        Assert.AreEqual(160, manifest.SourceInputCount);
        Assert.AreEqual(Milestone5GDDesignSystemSourceSha256, manifest.SourceSha256);

        Assert.AreEqual(
            new TreeIdentity(
                609,
                234_037_929,
                "AD4D7D620C75771788083B29B7DEB92DE403D665336BBF5D96810FF921BC212A"),
            GetTreeIdentity(package.AppRoot));
        Assert.AreEqual(
            new TreeIdentity(
                205,
                85_041_541,
                "1487672F34AB9C054F67FF96C8B74D21DEA353AAAEAFD16416A34AE7D286491F"),
            GetTreeIdentity(Path.GetDirectoryName(package.WorkerPath)!));
        Assert.AreEqual(GetTreeIdentity(package.AppRoot), manifest.AppTree);
        Assert.AreEqual(
            GetTreeIdentity(Path.GetDirectoryName(package.WorkerPath)!),
            manifest.WorkerTree);
        AssertPackageComposition(package);
        AssertWindowsExecutableHasIcon(Path.Combine(package.AppRoot, "RainArchives.exe"));

        var expectedFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["app/RainArchives.exe"] = "A565C435B523F656416ADB7736A0D005BB75B3D733379D2D443A7F7794F2E5C9",
            ["app/RainArchives.dll"] = "9C60B64D4A678AA0D15B2DF3E5B73A77275331E6E479CE28259E770FEB251AF5",
            ["app/RainArchives.Client.dll"] = "53B933055CC5043FE8C5B15DB0B61C783AFCBBE429DE1E6EF443CD317498292D",
            ["app/RainArchives.Contracts.dll"] = "E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8",
            ["app/worker/RainArchives.Worker.exe"] = "C5A6D421ED4838A868704B472C6690AFA905F3EE0E5FB3E0F883D4BDC6EDEEDF",
            ["app/worker/RainArchives.Worker.dll"] = "3B994B6657E97D7E4769526710306B26C38EF2367FFD838F91D945086EB33BD8",
            ["app/worker/RainArchives.Contracts.dll"] = "E2D749217EDD839A17C413494E79C2CFA0D84AEBD63B410A792A70C6597A5DF8",
        };
        CollectionAssert.AreEquivalent(expectedFiles.Keys.ToArray(), manifest.Files.Keys.ToArray());
        foreach (var expected in expectedFiles)
        {
            Assert.AreEqual(expected.Value, manifest.Files[expected.Key]);
            Assert.AreEqual(
                expected.Value,
                Sha256File(Path.Combine(
                    package.ArtifactRoot,
                    expected.Key.Replace('/', Path.DirectorySeparatorChar))));
        }

        await AssertMilestone5GPackageBrowserFlowAsync(package, "M5G-D design-system");
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task Milestone7DRarPackageMatchesCurrentSourceAndOpensDirectoryWithoutCrc()
    {
        var repository = GetRepositoryRoot();
        var package = ResolvePackage(Path.Combine(
            repository,
            "artifacts",
            Milestone7DRarPackageDirectoryName));
        var manifestPath = Path.Combine(package.ArtifactRoot, "package-manifest.json");
        Assert.AreEqual(Milestone7DRarManifestSha256, Sha256File(manifestPath));
        var manifest = JsonSerializer.Deserialize<PackageManifest>(
            File.ReadAllText(manifestPath),
            ManifestJsonOptions);
        Assert.IsNotNull(manifest);
        Assert.AreEqual(1, manifest.SchemaVersion);
        Assert.AreEqual("1.23", manifest.Protocol);
        Assert.AreEqual(164, manifest.SourceInputCount);
        Assert.AreEqual(Milestone7DRarSourceSha256, manifest.SourceSha256);
        Assert.AreEqual(
            new TreeIdentity(
                611,
                234_227_669,
                "4995B32081A66E95A77A0B914876256146D094F9630ED22A4E3B64F4BEA34BCD"),
            GetTreeIdentity(package.AppRoot));
        Assert.AreEqual(
            new TreeIdentity(
                207,
                85_185_437,
                "A4A83D8B51E704B95BB588FF6F21432E6E7A0B37F882A6E1193F7FCC12D2DACC"),
            GetTreeIdentity(Path.GetDirectoryName(package.WorkerPath)!));
        Assert.AreEqual(GetTreeIdentity(package.AppRoot), manifest.AppTree);
        Assert.AreEqual(
            GetTreeIdentity(Path.GetDirectoryName(package.WorkerPath)!),
            manifest.WorkerTree);
        AssertPackageComposition(package, expectWinRarProvider: true);
        AssertWindowsExecutableHasIcon(Path.Combine(package.AppRoot, "RainArchives.exe"));

        var expectedFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["app/RainArchives.exe"] = "A565C435B523F656416ADB7736A0D005BB75B3D733379D2D443A7F7794F2E5C9",
            ["app/RainArchives.dll"] = "D46C8C2B03A7CE9062A771B06B232F261DF640C658D5BF9A436569E37E3A8988",
            ["app/RainArchives.Client.dll"] = "38FF68D7EB7043F8200F4821C43B0289D41AA8009632129EE598AAD5B35A9AFB",
            ["app/RainArchives.Contracts.dll"] = "E71B945555031A9933EB0631BE260CFCA5CF9AA0257BEEF658BAD9319CB398EF",
            ["app/worker/RainArchives.Worker.exe"] = "C5A6D421ED4838A868704B472C6690AFA905F3EE0E5FB3E0F883D4BDC6EDEEDF",
            ["app/worker/RainArchives.Worker.dll"] = "42406932E8EF96F56DDAA1BBD0E82D595442E2D4815E6274547FFC9BEFB53C2B",
            ["app/worker/RainArchives.Contracts.dll"] = "E71B945555031A9933EB0631BE260CFCA5CF9AA0257BEEF658BAD9319CB398EF",
        };
        CollectionAssert.AreEquivalent(expectedFiles.Keys.ToArray(), manifest.Files.Keys.ToArray());
        foreach (var expected in expectedFiles)
        {
            Assert.AreEqual(expected.Value, manifest.Files[expected.Key]);
            Assert.AreEqual(
                expected.Value,
                Sha256File(Path.Combine(
                    package.ArtifactRoot,
                    expected.Key.Replace('/', Path.DirectorySeparatorChar))));
        }

        using var temporary = new TemporaryDirectory();
        var workerTemporaryRoot = Path.Combine(temporary.Path, "worker-temp");
        Directory.CreateDirectory(workerTemporaryRoot);
        await using var client = await ArchiveClient.StartWithTemporaryRootAsync(
            package.WorkerPath,
            workerTemporaryRoot);
        var rarCapability = await client.GetRarWriteCapabilitiesAsync();
        Assert.AreEqual(
            rarCapability.Availability == RarEncoderAvailability.Available,
            rarCapability.CanCreate);
        Assert.AreEqual(rarCapability.CanCreate, rarCapability.SupportsTreeCreation);
        Assert.IsFalse(rarCapability.CanRebuild);
        Assert.IsFalse(rarCapability.SupportsEncryption);
        Assert.IsFalse(rarCapability.SupportsFilenameEncryption);
        Assert.IsFalse(rarCapability.SupportsSplitOutput);
        Assert.IsFalse(rarCapability.SupportsExistingDestinationReplacement);
        Assert.IsFalse(rarCapability.SupportsInPlaceUpdate);

        var source = Path.Combine(
            repository,
            "tests",
            "fixtures",
            "corpus",
            "archives",
            "rar",
            "directory-no-crc.rar");
        var opened = await client.OpenArchiveAsync(source);
        try
        {
            Assert.AreEqual("rar", opened.Archive.Format);
            var directory = (await client.ListEntriesAsync(opened.Archive.SessionId, 0, 32))
                .Entries
                .Single(entry => entry.RawPath.TrimEnd('/', '\\') == "empty-directory");
            Assert.AreEqual(ArchiveEntryKind.Directory, directory.Kind);

            var destination = Path.Combine(temporary.Path, "extracted");
            var extraction = await client.StartExtractionAsync(
                opened.Archive.SessionId,
                [],
                destination);
            var terminal = await client.WaitForTerminalAsync(extraction.OperationId);
            Assert.IsTrue(terminal.Phase is OperationPhase.Completed
                or OperationPhase.CompletedWithWarnings);
            Assert.IsTrue(Directory.Exists(Path.Combine(destination, "empty-directory")));
        }
        finally
        {
            await client.CloseArchiveAsync(opened.Archive.SessionId);
        }
    }

    private static async Task AssertMilestone5GPackageBrowserFlowAsync(
        PackageLayout package,
        string scenarioLabel)
    {
        using var temporary = new TemporaryDirectory();
        var source = Path.Combine(temporary.Path, "m5g-package-source.zip");
        var extraction = Path.Combine(temporary.Path, "m5g-package-selected-extraction");
        var workerTemporaryRoot = Path.Combine(temporary.Path, "worker-temp");
        var previewCache = Path.Combine(temporary.Path, "preview-cache");
        Directory.CreateDirectory(workerTemporaryRoot);
        Directory.CreateDirectory(previewCache);
        using (var stream = new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            _ = archive.CreateEntry("folder/", CompressionLevel.NoCompression);
            CreateEntry(archive, "folder/雨.txt", $"{scenarioLabel} preview");
            CreateEntry(archive, "root.txt", $"{scenarioLabel} extraction");
        }

        var sourceHash = await Sha256FileAsync(source);
        var dialogs = new RecordingDialogService
        {
            ExtractionDestination = extraction,
        };
        await using (var viewModel = new MainWindowViewModel(
                         dialogs,
                         async cancellationToken => await ArchiveClient.StartWithTemporaryRootAsync(
                                 package.WorkerPath,
                                 workerTemporaryRoot,
                                 cancellationToken)
                             .ConfigureAwait(false),
                         client => new TextEntryPreviewService(client, previewCache)))
        {
            ArchiveBrowserFocusTarget? restoredFocus = null;
            viewModel.Browser.FocusRequested += (_, args) => restoredFocus = args.Target;

            await viewModel.OpenArchivePathAsync(source);
            var folder = viewModel.Entries.Single(entry => entry.IsDirectory);
            viewModel.Browser.SelectedEntry = folder;
            viewModel.NavigateSelectionCommand.Execute(null);
            await WaitForViewModelAsync(() => viewModel.CurrentFolderId == folder.EntryId);

            var previewEntry = viewModel.Entries.Single();
            Assert.AreEqual(
                "雨.txt, File, archive occurrence 1, security status Safe, not encrypted",
                previewEntry.AutomationName);
            viewModel.Browser.SelectedEntry = previewEntry;
            viewModel.PreviewSelectionCommand.Execute(null);
            await WaitForViewModelAsync(() => viewModel.Preview.State == SafePreviewState.Text);
            Assert.AreEqual($"{scenarioLabel} preview", viewModel.Preview.Text);
            Assert.IsTrue(viewModel.IsInspectorVisible);
            Assert.AreEqual(1, viewModel.InspectorSelectedTabIndex);

            viewModel.ShowPropertiesCommand.Execute(null);
            Assert.AreEqual(0, viewModel.InspectorSelectedTabIndex);
            Assert.IsTrue(viewModel.Properties.HasSelectedEntry);
            await viewModel.CloseInspectorAsync();
            Assert.IsFalse(viewModel.IsInspectorVisible);
            Assert.AreEqual(SafePreviewState.Idle, viewModel.Preview.State);
            Assert.AreEqual(ArchiveBrowserFocusTarget.EntryList, restoredFocus);

            var previousOperationId = viewModel.ActiveActivity.OperationId;
            viewModel.TestCommand.Execute(null);
            await WaitForViewModelOperationAsync(viewModel, previousOperationId);
            Assert.AreEqual(OperationPhase.Completed, viewModel.ActiveActivity.Phase);

            viewModel.RootCommand.Execute(null);
            await WaitForViewModelAsync(() => viewModel.CurrentFolderId is null);
            viewModel.Browser.SelectedEntry = viewModel.Entries.Single(entry => entry.Name == "root.txt");
            previousOperationId = viewModel.ActiveActivity.OperationId;
            viewModel.ExtractSelectionCommand.Execute(null);
            await WaitForViewModelOperationAsync(viewModel, previousOperationId);
            Assert.AreEqual(OperationPhase.Completed, viewModel.ActiveActivity.Phase, dialogs.ErrorMessage);
            Assert.AreEqual(
                $"{scenarioLabel} extraction",
                await File.ReadAllTextAsync(Path.Combine(extraction, "root.txt")));
        }

        Assert.AreEqual(sourceHash, await Sha256FileAsync(source));
        Assert.HasCount(0, Directory.GetFiles(workerTemporaryRoot, "*", SearchOption.AllDirectories));
        Assert.HasCount(0, Directory.GetFiles(previewCache, "*", SearchOption.AllDirectories));
        Assert.HasCount(
            0,
            Directory.GetFiles(
                temporary.Path,
                ".rainarchives-*.staging",
                SearchOption.AllDirectories));
    }

    [TestMethod]
    [Timeout(240_000)]
    public async Task FinalPackageExercisesCliClientAndWpfProductFlow()
    {
        var configuredRoot = Environment.GetEnvironmentVariable(PackageRootVariable);
        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            configuredRoot = Path.Combine(
                GetRepositoryRoot(),
                "artifacts",
                ExpectedPackageDirectoryName);
        }

        var package = ResolvePackage(configuredRoot);
        AssertPackageProvenance(package);
        AssertPackageComposition(package);

        using var temporary = new TemporaryDirectory();
        var source = Path.Combine(temporary.Path, "source.zip");
        var replacement = Path.Combine(temporary.Path, "replacement.txt");
        var addition = Path.Combine(temporary.Path, "addition.txt");
        var importedRoot = Path.Combine(temporary.Path, "imported-source");
        Directory.CreateDirectory(Path.Combine(importedRoot, "empty"));
        await File.WriteAllTextAsync(replacement, "replacement payload");
        await File.WriteAllTextAsync(addition, "new payload 雨");
        await File.WriteAllTextAsync(Path.Combine(importedRoot, "imported.txt"), "mapped folder");
        CreateSourceArchive(source);
        var originalHash = await Sha256FileAsync(source);

        var cliExitCode = await RunCliAsync(
            temporary.Path,
            ["capabilities", "--worker", package.WorkerPath]);
        Assert.AreEqual(0, cliExitCode, "The packaged Worker did not answer the CLI capability query.");

        var cliCreated = Path.Combine(temporary.Path, "cli-created.zip");
        cliExitCode = await RunCliAsync(
            temporary.Path,
            [
                "create-zip", cliCreated, "--file", replacement, "created.txt",
                "--worker", package.WorkerPath,
            ]);
        Assert.AreEqual(0, cliExitCode, "The packaged Worker did not complete CLI ZIP creation.");

        var cliCreatedTarGZip = Path.Combine(temporary.Path, "cli-created.tar.gz");
        cliExitCode = await RunCliAsync(
            temporary.Path,
            [
                "create-tar-gz", cliCreatedTarGZip, "--file", replacement, "created.txt",
                "--preset", "fast", "--worker", package.WorkerPath,
            ]);
        Assert.AreEqual(0, cliExitCode, "The packaged Worker did not complete CLI TAR.GZ creation.");

        var cliCreatedBZip2 = Path.Combine(temporary.Path, "cli-created.bz2");
        cliExitCode = await RunCliAsync(
            temporary.Path,
            ["create-bzip2", cliCreatedBZip2, replacement, "--worker", package.WorkerPath]);
        Assert.AreEqual(0, cliExitCode, "The packaged Worker did not complete CLI BZIP2 creation.");

        var cliRebuilt = Path.Combine(temporary.Path, "cli-rebuilt.zip");
        cliExitCode = await RunCliAsync(
            temporary.Path,
            [
                "rebuild-zip", source, cliRebuilt,
                "--delete", "delete.txt", "0",
                "--replace", replacement, "replace.txt",
                "--add", addition, "added/addition.txt",
                "--confirm-rebuild-copy", "--worker", package.WorkerPath,
            ]);
        Assert.AreEqual(0, cliExitCode, "The packaged Worker did not complete CLI mixed rebuild.");

        var workerTemporaryRoot = Path.Combine(temporary.Path, "worker-temp");
        Directory.CreateDirectory(workerTemporaryRoot);
        await using var client = await ArchiveClient.StartWithTemporaryRootAsync(
            package.WorkerPath,
            workerTemporaryRoot);

        var globalCapabilities = await client.GetZipWriteCapabilitiesAsync();
        AssertCapabilities(globalCapabilities, expectRebuild: false);
        AssertTarCapabilities(await client.GetTarWriteCapabilitiesAsync());
        AssertManagedCapabilities(await client.GetManagedWriteCapabilitiesAsync());

        var opened = await client.OpenArchiveAsync(source);
        var sessionCapabilities = await client.GetZipWriteCapabilitiesAsync(opened.Archive.SessionId);
        AssertCapabilities(sessionCapabilities, expectRebuild: true);
        var listed = await client.ListEntriesAsync(opened.Archive.SessionId, 0, 128);
        var keep = listed.Entries.Single(entry => entry.RawPath == "keep.txt");
        var delete = listed.Entries.Single(entry => entry.RawPath == "delete.txt");
        Assert.IsNotNull(keep.NormalizedPath);
        Assert.IsNotNull(delete.NormalizedPath);

        await VerifyArchiveAsync(client, cliCreated, ["created.txt"], ("created.txt", "replacement payload"));
        await VerifyArchiveAsync(
            client,
            cliCreatedTarGZip,
            ["created.txt"],
            ("created.txt", "replacement payload"));
        await VerifyArchiveAsync(
            client,
            cliCreatedBZip2,
            ["cli-created"],
            ("cli-created", "replacement payload"));
        await VerifyArchiveAsync(
            client,
            cliRebuilt,
            ["added", "added/addition.txt", "delete-tree/", "delete-tree/remove.txt", "keep.txt", "replace.txt", "tree/", "tree/moved.txt"],
            ("replace.txt", "replacement payload"),
            ("added/addition.txt", "new payload 雨"));
        Assert.AreEqual(originalHash, await Sha256FileAsync(source));

        var clientTar = Path.Combine(temporary.Path, "client-created.tar");
        var managedStarted = await client.StartTarCreationAsync(
            clientTar,
            [new ArchiveCreationFile(replacement, "created.txt")]);
        await AssertCompletedAsync(client, managedStarted.OperationId, "TAR creation");

        var clientTarGZip = Path.Combine(temporary.Path, "client-created.tar.gz");
        managedStarted = await client.StartTarGZipCreationAsync(
            clientTarGZip,
            [new ArchiveCreationFile(replacement, "created.txt")],
            ArchiveCompressionPreset.Maximum);
        await AssertCompletedAsync(client, managedStarted.OperationId, "TAR.GZ creation");

        var clientTarBZip2 = Path.Combine(temporary.Path, "client-created.tar.bz2");
        managedStarted = await client.StartTarBZip2CreationAsync(
            clientTarBZip2,
            [new ArchiveCreationFile(replacement, "created.txt")],
            ArchiveCompressionPreset.Balanced);
        await AssertCompletedAsync(client, managedStarted.OperationId, "TAR.BZ2 creation");

        var clientGZip = Path.Combine(temporary.Path, "client-raw-gzip.gz");
        managedStarted = await client.StartGZipCreationAsync(
            clientGZip,
            replacement,
            ArchiveCompressionPreset.Fast);
        await AssertCompletedAsync(client, managedStarted.OperationId, "GZIP creation");

        var clientBZip2 = Path.Combine(temporary.Path, "client-raw-bzip2.bz2");
        managedStarted = await client.StartBZip2CreationAsync(
            clientBZip2,
            replacement,
            ArchiveCompressionPreset.Balanced);
        await AssertCompletedAsync(client, managedStarted.OperationId, "BZIP2 creation");

        foreach (var archive in new[] { clientTar, clientTarGZip, clientTarBZip2 })
        {
            await VerifyArchiveAsync(
                client,
                archive,
                ["created.txt"],
                ("created.txt", "replacement payload"));
        }
        await VerifyArchiveAsync(
            client,
            clientGZip,
            ["client-raw-gzip"],
            ("client-raw-gzip", "replacement payload"));
        await VerifyArchiveAsync(
            client,
            clientBZip2,
            ["client-raw-bzip2"],
            ("client-raw-bzip2", "replacement payload"));

        var treePlan = await client.GetZipRebuildTreePlanAsync(opened.Archive.SessionId);
        var tree = treePlan.Directories.Single(node => node.NormalizedPath == "tree");
        var deleteTree = treePlan.Directories.Single(node => node.NormalizedPath == "delete-tree");
        var folderReview = await client.GetZipRebuildSourceFolderReviewAsync(
            opened.Archive.SessionId,
            [new ArchiveCreationDirectory(importedRoot, "Imported")]);
        Assert.AreEqual("Imported", folderReview.Directories.Single().ArchiveRoot);
        Assert.AreEqual(2, folderReview.Directories.Single().DescendantCount);

        var copyOutput = Path.Combine(temporary.Path, "copy.zip");
        var copyStarted = await client.StartZipRebuildCopyAsync(
            opened.Archive.SessionId,
            copyOutput,
            [new ArchiveEntryKey(delete.NormalizedPath!, delete.DuplicateOrdinal)],
            [
                new ArchiveRebuildCopyFile(replacement, "replace.txt"),
                new ArchiveRebuildCopyFile(addition, "added/addition.txt"),
            ],
            ArchiveCompressionPreset.Balanced);
        await AssertCompletedAsync(client, copyStarted.OperationId, "copy/add/replace/delete");
        await VerifyArchiveAsync(
            client,
            copyOutput,
            ["added", "added/addition.txt", "delete-tree/", "delete-tree/remove.txt", "keep.txt", "replace.txt", "tree/", "tree/moved.txt"],
            ("replace.txt", "replacement payload"),
            ("added/addition.txt", "new payload 雨"));
        Assert.AreEqual(originalHash, await Sha256FileAsync(source));

        var renameOutput = Path.Combine(temporary.Path, "rename.zip");
        var renameStarted = await client.StartZipRebuildRenameAsync(
            opened.Archive.SessionId,
            renameOutput,
            [],
            [],
            [new ArchiveRebuildRename(
                new ArchiveEntryKey(keep.NormalizedPath!, keep.DuplicateOrdinal),
                "renamed/keep.txt")],
            ArchiveCompressionPreset.Fast);
        await AssertCompletedAsync(client, renameStarted.OperationId, "file rename");
        await VerifyArchiveAsync(
            client,
            renameOutput,
            ["delete-tree/", "delete-tree/remove.txt", "delete.txt", "replace.txt", "renamed", "renamed/keep.txt", "tree/", "tree/moved.txt"],
            ("renamed/keep.txt", "keep payload"));
        Assert.AreEqual(originalHash, await Sha256FileAsync(source));

        var treeOutput = Path.Combine(temporary.Path, "tree.zip");
        var treeStarted = await client.StartZipRebuildTreeEditAsync(
            opened.Archive.SessionId,
            treeOutput,
            [],
            [],
            [],
            [new ArchiveCreationDirectory(importedRoot, "Imported")],
            [
                new ArchiveRebuildTreeAction(
                    tree.NormalizedPath,
                    tree.SubtreeRevision,
                    ArchiveRebuildTreeActionKind.Move,
                    "Moved"),
                new ArchiveRebuildTreeAction(
                    deleteTree.NormalizedPath,
                    deleteTree.SubtreeRevision,
                    ArchiveRebuildTreeActionKind.Delete),
            ],
            ArchiveCompressionPreset.Maximum);
        await AssertCompletedAsync(client, treeStarted.OperationId, "tree edit/source folder");
        await VerifyArchiveAsync(
            client,
            treeOutput,
            ["delete.txt", "Imported/", "Imported/empty/", "Imported/imported.txt", "keep.txt", "Moved/", "Moved/moved.txt", "replace.txt"],
            ("Moved/moved.txt", "tree payload"),
            ("Imported/imported.txt", "mapped folder"));
        Assert.AreEqual(originalHash, await Sha256FileAsync(source));

        var metadataOutput = Path.Combine(temporary.Path, "metadata.zip");
        var metadataStarted = await client.StartZipRebuildMetadataAsync(
            opened.Archive.SessionId,
            metadataOutput,
            [],
            [],
            [],
            [],
            [],
            [new ArchiveRebuildMetadataDirective(
                new ArchiveRebuildMetadataTarget(
                    ArchiveRebuildMetadataTargetKind.RetainedEntryKey,
                    RetainedEntryKey: new ArchiveEntryKey(keep.NormalizedPath!, keep.DuplicateOrdinal)),
                new ArchiveDosLocalTimestamp(2107, 12, 31, 23, 59, 58),
                0x0021)],
            ArchiveCompressionPreset.Store);
        await AssertCompletedAsync(client, metadataStarted.OperationId, "metadata edit");
        var metadataEntries = await VerifyArchiveAsync(
            client,
            metadataOutput,
            ["delete-tree/", "delete-tree/remove.txt", "delete.txt", "keep.txt", "replace.txt", "tree/", "tree/moved.txt"],
            ("keep.txt", "keep payload"));
        var metadataEntry = metadataEntries.Single(entry => entry.RawPath == "keep.txt");
        Assert.AreEqual(0x0021, metadataEntry.WindowsAttributes);
        Assert.AreEqual(originalHash, await Sha256FileAsync(source));
        Assert.IsTrue((await client.CloseArchiveAsync(opened.Archive.SessionId)).Closed);

        var wpfOutput = Path.Combine(temporary.Path, "created-by-wpf.zip");
        var wpfTarGZipOutput = Path.Combine(temporary.Path, "created-by-wpf.tar.gz");
        var wpfGZipOutput = Path.Combine(temporary.Path, "created-by-wpf-raw.gz");
        var dialogs = new RecordingDialogService();
        await using (var viewModel = new MainWindowViewModel(
                         dialogs,
                         async cancellationToken => await ArchiveClient.StartWithTemporaryRootAsync(
                                 package.WorkerPath,
                                 Path.Combine(temporary.Path, "wpf-worker-temp"),
                                 cancellationToken)
                             .ConfigureAwait(false)))
        {
            Directory.CreateDirectory(Path.Combine(temporary.Path, "wpf-worker-temp"));
            await viewModel.CreateArchiveAsync(new ArchiveCreationSelection(
                wpfOutput,
                [new ArchiveCreationFile(replacement, "created.txt")],
                [],
                ArchiveCompressionPreset.Balanced));
            Assert.IsTrue(viewModel.HasArchive, dialogs.ErrorMessage);
            Assert.AreEqual("ZIP archive created and opened.", viewModel.StatusText);
            Assert.AreEqual(OperationPhase.Completed, viewModel.ActiveActivity.Phase);

            await viewModel.CreateArchiveAsync(new ArchiveCreationSelection(
                wpfTarGZipOutput,
                [new ArchiveCreationFile(replacement, "created.txt")],
                [],
                ArchiveCompressionPreset.Balanced,
                ArchiveCreationFormat.TarGZip));
            Assert.IsTrue(viewModel.HasArchive, dialogs.ErrorMessage);
            Assert.AreEqual("TAR.GZ archive created and opened.", viewModel.StatusText);
            Assert.AreEqual(OperationPhase.Completed, viewModel.ActiveActivity.Phase);

            await viewModel.CreateArchiveAsync(new ArchiveCreationSelection(
                wpfGZipOutput,
                [new ArchiveCreationFile(replacement, "ignored.txt")],
                [],
                ArchiveCompressionPreset.Maximum,
                ArchiveCreationFormat.GZip));
            Assert.IsTrue(viewModel.HasArchive, dialogs.ErrorMessage);
            Assert.AreEqual("GZIP archive created and opened.", viewModel.StatusText);
            Assert.AreEqual(OperationPhase.Completed, viewModel.ActiveActivity.Phase);
        }

        await VerifyArchiveAsync(
            client,
            wpfOutput,
            ["created.txt"],
            ("created.txt", "replacement payload"));
        await VerifyArchiveAsync(
            client,
            wpfTarGZipOutput,
            ["created.txt"],
            ("created.txt", "replacement payload"));
        await VerifyArchiveAsync(
            client,
            wpfGZipOutput,
            ["created-by-wpf-raw"],
            ("created-by-wpf-raw", "replacement payload"));
        Assert.AreEqual(originalHash, await Sha256FileAsync(source));
        Assert.HasCount(0, Directory.GetFiles(temporary.Path, ".rainarchives-*.staging", SearchOption.AllDirectories));
    }

    private static async Task WaitForViewModelAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail("The package-backed production model did not reach the expected state.");
            }

            await Task.Delay(10);
        }
    }

    private static async Task WaitForViewModelOperationAsync(
        MainWindowViewModel viewModel,
        OperationId? previousOperationId)
    {
        await WaitForViewModelAsync(() =>
            viewModel.ActiveActivity.OperationId is { } operationId
            && operationId != previousOperationId
            && viewModel.ActiveActivity.Phase is (
                OperationPhase.Completed
                or OperationPhase.CompletedWithWarnings
                or OperationPhase.Cancelled
                or OperationPhase.Failed)
            && !viewModel.IsBusy);
    }

    private static PackageLayout ResolvePackage(string configuredRoot)
    {
        var configured = Path.GetFullPath(configuredRoot);
        var appRoot = Directory.Exists(configured)
            ? (File.Exists(Path.Combine(configured, "RainArchives.exe"))
                ? configured
                : Path.Combine(configured, "app"))
            : Path.GetDirectoryName(configured)!;
        var worker = File.Exists(configured) &&
                     string.Equals(Path.GetFileName(configured), "RainArchives.Worker.exe", StringComparison.OrdinalIgnoreCase)
            ? configured
            : Path.Combine(appRoot, "worker", "RainArchives.Worker.exe");
        if (!File.Exists(worker))
        {
            worker = Path.Combine(appRoot, "RainArchives.Worker.exe");
        }

        Assert.IsTrue(File.Exists(worker), $"Package gate could not find RainArchives.Worker.exe under '{configuredRoot}'.");
        if (string.Equals(Path.GetFileName(configured), "RainArchives.Worker.exe", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(Path.GetDirectoryName(worker)), "worker", StringComparison.OrdinalIgnoreCase))
        {
            appRoot = Directory.GetParent(Path.GetDirectoryName(worker)!)!.FullName;
        }

        var artifactRoot = Directory.GetParent(appRoot)?.FullName
            ?? throw new DirectoryNotFoundException("The App package has no artifact parent.");
        return new PackageLayout(
            Path.GetFullPath(artifactRoot),
            Path.GetFullPath(appRoot),
            Path.GetFullPath(worker));
    }

    private static async Task WaitForActivityCenterAsync(
        ConcurrentQueue<Action> uiQueue,
        Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            while (uiQueue.TryDequeue(out var action))
            {
                action();
            }

            if (condition())
            {
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail("The package-backed Activity Center did not reach the expected bounded state.");
            }

            await Task.Delay(10);
        }
    }

    private static void AssertPackageProvenance(PackageLayout package)
    {
        var manifestPath = Path.Combine(package.ArtifactRoot, "package-manifest.json");
        Assert.IsTrue(File.Exists(manifestPath), "The final package provenance manifest is missing.");
        var manifest = JsonSerializer.Deserialize<PackageManifest>(
            File.ReadAllText(manifestPath),
            ManifestJsonOptions);
        Assert.IsNotNull(manifest);
        Assert.AreEqual(1, manifest.SchemaVersion);
        Assert.AreEqual("1.18", manifest.Protocol);

        var repository = GetRepositoryRoot();
        var sourceSnapshot = Path.Combine(
            repository,
            "artifacts",
            ExpectedSourceSnapshotDirectoryName,
            "source-snapshot");
        Assert.IsTrue(
            Directory.Exists(sourceSnapshot),
            "The immutable protocol-1.18 package source snapshot is missing.");
        var source = GetCanonicalSourceIdentity(sourceSnapshot);
        Assert.AreEqual(ExpectedSourceInputCount, source.Files);
        Assert.AreEqual(ExpectedSourceSha256, source.Sha256);
        Assert.AreEqual(source.Files, manifest.SourceInputCount);
        Assert.AreEqual(source.Sha256, manifest.SourceSha256);

        var app = GetTreeIdentity(package.AppRoot);
        var worker = GetTreeIdentity(Path.GetDirectoryName(package.WorkerPath)!);
        AssertTreeIdentity(
            app,
            manifest.AppTree,
            ExpectedAppFiles,
            ExpectedAppBytes,
            ExpectedAppSha256,
            "App package");
        AssertTreeIdentity(
            worker,
            manifest.WorkerTree,
            ExpectedWorkerFiles,
            ExpectedWorkerBytes,
            ExpectedWorkerSha256,
            "private Worker");

        CollectionAssert.AreEquivalent(
            ExpectedFileHashes.Keys.ToArray(),
            manifest.Files.Keys.ToArray(),
            "The package manifest principal-file set changed.");
        foreach (var expected in ExpectedFileHashes)
        {
            Assert.AreEqual(expected.Value, manifest.Files[expected.Key], expected.Key);
            var path = Path.Combine(
                package.ArtifactRoot,
                expected.Key.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(path), $"The pinned package file is missing: {expected.Key}");
            Assert.AreEqual(expected.Value, Sha256File(path), expected.Key);
        }
    }

    private static void AssertTreeIdentity(
        TreeIdentity actual,
        TreeIdentity manifest,
        int expectedFiles,
        long expectedBytes,
        string expectedSha256,
        string label)
    {
        Assert.AreEqual(expectedFiles, actual.Files, $"{label} file count changed.");
        Assert.AreEqual(expectedBytes, actual.Bytes, $"{label} byte count changed.");
        Assert.AreEqual(expectedSha256, actual.Sha256, $"{label} tree hash changed.");
        Assert.AreEqual(manifest, actual, $"{label} does not match its provenance manifest.");
    }

    private static TreeIdentity GetCanonicalSourceIdentity(string repository)
    {
        var paths = new List<string>();
        foreach (var name in new[]
                 {
                     "Directory.Build.props",
                     "Directory.Packages.props",
                     "global.json",
                     "NuGet.config",
                     "RainArchives.slnx",
                 })
        {
            paths.Add(Path.Combine(repository, name));
        }

        var extensions = new HashSet<string>(
            [".cs", ".csproj", ".xaml", ".manifest", ".ico"],
            StringComparer.OrdinalIgnoreCase);
        foreach (var scope in new[] { "src" })
        {
            paths.AddRange(Directory.EnumerateFiles(
                    Path.Combine(repository, scope),
                    "*",
                    SearchOption.AllDirectories)
                .Where(path => extensions.Contains(Path.GetExtension(path)))
                .Where(path => !HasBuildOutputComponent(repository, path)));
        }

        return GetIdentity(repository, paths);
    }

    private static TreeIdentity GetTreeIdentity(string root) =>
        GetIdentity(root, Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));

    private static TreeIdentity GetIdentity(string root, IEnumerable<string> paths)
    {
        var items = paths
            .Select(path => new
            {
                FullPath = path,
                RelativePath = Path.GetRelativePath(root, path).Replace('\\', '/'),
            })
            .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
            .ToArray();
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long bytes = 0;
        foreach (var item in items)
        {
            aggregate.AppendData(Encoding.UTF8.GetBytes(item.RelativePath));
            aggregate.AppendData([0]);
            using var stream = File.OpenRead(item.FullPath);
            var fileHash = SHA256.HashData(stream);
            aggregate.AppendData(fileHash);
            bytes = checked(bytes + stream.Length);
        }

        return new TreeIdentity(
            items.Length,
            bytes,
            Convert.ToHexString(aggregate.GetHashAndReset()));
    }

    private static bool HasBuildOutputComponent(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(component => component is "bin" or "obj");
    }

    private static string GetRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "RainArchives.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Rain Archives workspace root.");
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void AssertPackageComposition(
        PackageLayout package,
        bool expectWinRarProvider = false)
    {
        foreach (var file in new[] { "RainArchives.exe", "RainArchives.dll", "RainArchives.Client.dll", "RainArchives.Contracts.dll" })
        {
            Assert.IsTrue(File.Exists(Path.Combine(package.AppRoot, file)), $"Missing App package file: {file}");
        }

        var appContracts = Path.Combine(package.AppRoot, "RainArchives.Contracts.dll");
        var workerDirectory = Path.GetDirectoryName(package.WorkerPath)!;
        var workerContracts = Path.Combine(workerDirectory, "RainArchives.Contracts.dll");
        CollectionAssert.AreEqual(
            File.ReadAllBytes(appContracts),
            File.ReadAllBytes(workerContracts),
            "App-root and private-Worker Contracts assemblies are not byte-coherent.");

        var expectedWorkerFiles = new List<string>
        {
            "RainArchives.Worker.exe", "RainArchives.Worker.dll", "RainArchives.Contracts.dll",
            "RainArchives.Core.dll", "RainArchives.Security.dll", "RainArchives.Providers.Zip.dll",
            "RainArchives.Providers.SharpCompress.dll", "RainArchives.Providers.SevenZip.dll",
            "SharpCompress.dll"
        };
        if (expectWinRarProvider)
        {
            expectedWorkerFiles.Add("RainArchives.Providers.WinRar.dll");
        }

        foreach (var file in expectedWorkerFiles)
        {
            Assert.IsTrue(File.Exists(Path.Combine(Path.GetDirectoryName(package.WorkerPath)!, file)),
                $"Missing private Worker package file: {file}");
        }

        Assert.IsTrue(
            Path.GetFullPath(package.WorkerPath).StartsWith(
                Path.GetFullPath(package.AppRoot) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase),
            "The Worker must be private to the App package root.");
        Assert.AreEqual(
            1,
            Directory.EnumerateFiles(package.AppRoot, "RainArchives.Worker.exe", SearchOption.AllDirectories).Count(),
            "The App package must contain exactly one private Worker executable.");
        foreach (var forbiddenAtAppRoot in new[]
                 {
                     "RainArchives.Worker.exe", "RainArchives.Worker.dll", "RainArchives.Core.dll",
                     "RainArchives.Security.dll", "RainArchives.Providers.Zip.dll",
                     "RainArchives.Providers.SharpCompress.dll", "RainArchives.Providers.SevenZip.dll",
                     "RainArchives.Providers.WinRar.dll",
                     "SharpCompress.dll",
                 })
        {
            Assert.IsFalse(
                File.Exists(Path.Combine(package.AppRoot, forbiddenAtAppRoot)),
                $"Worker-only assembly escaped the private Worker subtree: {forbiddenAtAppRoot}");
        }
        Assert.AreEqual(
            expectWinRarProvider,
            File.Exists(Path.Combine(workerDirectory, "RainArchives.Providers.WinRar.dll")),
            "The private Worker WinRAR provider composition did not match this package generation.");
        var forbidden = Directory.EnumerateFiles(package.AppRoot, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .Where(name => name is not null &&
                (name.Equals("7z.dll", StringComparison.OrdinalIgnoreCase)
                 || name.Equals("7za.exe", StringComparison.OrdinalIgnoreCase)
                 || name.Equals("SharpSevenZip.dll", StringComparison.OrdinalIgnoreCase)
                 || name.Equals("Rar.exe", StringComparison.OrdinalIgnoreCase)
                 || name.Equals("WinRAR.exe", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        Assert.HasCount(0, forbidden, $"Forbidden native assets were found: {string.Join(", ", forbidden)}");
    }

    private static void AssertCapabilities(GetZipWriteCapabilitiesResponse capabilities, bool expectRebuild)
    {
        Assert.IsTrue(capabilities.CanCreate);
        Assert.AreEqual(expectRebuild, capabilities.CanRebuild);
        Assert.IsTrue(capabilities.SupportsAdd);
        Assert.IsTrue(capabilities.SupportsReplace);
        Assert.IsTrue(capabilities.SupportsDelete);
        Assert.IsTrue(capabilities.SupportsFileRename);
        Assert.IsTrue(capabilities.SupportsTreeEdit);
        Assert.IsTrue(capabilities.SupportsMetadata);
        CollectionAssert.AreEquivalent(
            Enum.GetValues<ArchiveCompressionPreset>(),
            capabilities.CompressionPresets.ToArray());
        Assert.IsFalse(capabilities.SupportsEncryption);
        Assert.IsFalse(capabilities.SupportsFilenameEncryption);
        Assert.IsFalse(capabilities.SupportsSplitOutput);
        Assert.IsFalse(capabilities.SupportsExistingDestinationReplacement);
        Assert.IsFalse(capabilities.SupportsInPlaceUpdate);
    }

    private static void AssertTarCapabilities(GetTarWriteCapabilitiesResponse capabilities)
    {
        Assert.IsTrue(capabilities.CanCreate);
        Assert.IsFalse(capabilities.CanRebuild);
        Assert.IsTrue(capabilities.SupportsTreeCreation);
        CollectionAssert.AreEqual(
            new[] { ArchiveCompressionPreset.Store },
            capabilities.CompressionPresets.ToArray());
        Assert.AreEqual(ProtocolConstants.MaximumTarCreationEntries, capabilities.MaximumEntries);
        Assert.AreEqual(ProtocolConstants.MaximumTarCreationSourceBytes, capabilities.MaximumSourceBytes);
        Assert.IsFalse(capabilities.SupportsEncryption);
        Assert.IsFalse(capabilities.SupportsFilenameEncryption);
        Assert.IsFalse(capabilities.SupportsSplitOutput);
        Assert.IsFalse(capabilities.SupportsExistingDestinationReplacement);
        Assert.IsFalse(capabilities.SupportsInPlaceUpdate);
    }

    private static void AssertManagedCapabilities(GetManagedWriteCapabilitiesResponse response)
    {
        CollectionAssert.AreEqual(
            Enum.GetValues<ManagedArchiveCreationFormat>(),
            response.Capabilities.Select(capability => capability.Format).ToArray());
        foreach (var capability in response.Capabilities)
        {
            Assert.IsTrue(capability.CanCreate, capability.Format.ToString());
            Assert.IsFalse(capability.CanRebuild, capability.Format.ToString());
            Assert.AreEqual(
                capability.Format is ManagedArchiveCreationFormat.TarGZip
                    or ManagedArchiveCreationFormat.TarBZip2,
                capability.SupportsTreeCreation,
                capability.Format.ToString());
            CollectionAssert.AreEqual(
                capability.Format is ManagedArchiveCreationFormat.TarBZip2
                    or ManagedArchiveCreationFormat.BZip2
                    ? new[] { ArchiveCompressionPreset.Balanced }
                    : Enum.GetValues<ArchiveCompressionPreset>(),
                capability.CompressionPresets.ToArray(),
                capability.Format.ToString());
            Assert.AreEqual(
                capability.SupportsTreeCreation
                    ? ProtocolConstants.MaximumTarCreationEntries
                    : ProtocolConstants.MaximumRawCreationEntries,
                capability.MaximumEntries,
                capability.Format.ToString());
            Assert.AreEqual(
                capability.SupportsTreeCreation
                    ? ProtocolConstants.MaximumTarCreationSourceBytes
                    : ProtocolConstants.MaximumRawCreationSourceBytes,
                capability.MaximumSourceBytes,
                capability.Format.ToString());
            Assert.IsFalse(capability.SupportsEncryption, capability.Format.ToString());
            Assert.IsFalse(capability.SupportsFilenameEncryption, capability.Format.ToString());
            Assert.IsFalse(capability.SupportsSplitOutput, capability.Format.ToString());
            Assert.IsFalse(capability.SupportsExistingDestinationReplacement, capability.Format.ToString());
            Assert.IsFalse(capability.SupportsInPlaceUpdate, capability.Format.ToString());
        }
    }

    private static async Task AssertCompletedAsync(ArchiveClient client, OperationId operationId, string operation)
    {
        var terminal = await client.WaitForTerminalAsync(operationId).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.IsTrue(
            terminal.Phase is OperationPhase.Completed or OperationPhase.CompletedWithWarnings,
            $"Packaged {operation} failed: {terminal.Error?.Code} {terminal.Error?.Message}");
        Assert.IsNull(terminal.Error);
    }

    private static async Task<IReadOnlyList<ArchiveEntryDescriptor>> VerifyArchiveAsync(
        ArchiveClient client,
        string archivePath,
        IReadOnlyList<string> expectedEntries,
        params (string Path, string Text)[] expectedText)
    {
        var archiveHash = await Sha256FileAsync(archivePath);
        var opened = await client.OpenArchiveAsync(archivePath);
        IReadOnlyList<ArchiveEntryDescriptor> result;
        try
        {
            var listed = await client.ListEntriesAsync(opened.Archive.SessionId, 0, 128);
            var actualEntries = listed.Entries.Select(entry => entry.RawPath).ToArray();
            CollectionAssert.AreEquivalent(
                expectedEntries.ToArray(),
                actualEntries,
                $"Expected: {string.Join(", ", expectedEntries)}; actual: {string.Join(", ", actualEntries)}");
            foreach (var expected in expectedText)
            {
                var entry = listed.Entries.Single(item => item.RawPath == expected.Path);
                var stream = await client.OpenEntryStreamAsync(opened.Archive.SessionId, entry.EntryId);
                await using (stream)
                {
                    using var reader = new StreamReader(stream.Content, Encoding.UTF8);
                    Assert.AreEqual(expected.Text, await reader.ReadToEndAsync());
                }
            }

            var integrity = await client.StartIntegrityTestAsync(opened.Archive.SessionId, []);
            var terminal = await client.WaitForTerminalAsync(integrity.OperationId);
            Assert.AreEqual(OperationPhase.Completed, terminal.Phase, terminal.Error?.Message);

            var extractionRoot = Path.Combine(
                Path.GetDirectoryName(archivePath)!,
                $"rain-extract-{Guid.NewGuid():N}");
            try
            {
                var extraction = await client.StartExtractionAsync(
                    opened.Archive.SessionId,
                    [],
                    extractionRoot);
                var extractionTerminal = await client.WaitForTerminalAsync(extraction.OperationId);
                Assert.AreEqual(OperationPhase.Completed, extractionTerminal.Phase, extractionTerminal.Error?.Message);
                foreach (var expected in expectedText)
                {
                    var extractedPath = Path.Combine(
                        extractionRoot,
                        expected.Path.Replace('/', Path.DirectorySeparatorChar));
                    Assert.AreEqual(expected.Text, await File.ReadAllTextAsync(extractedPath));
                }
            }
            finally
            {
                if (Directory.Exists(extractionRoot))
                {
                    Directory.Delete(extractionRoot, recursive: true);
                }
            }

            result = listed.Entries;
        }
        finally
        {
            Assert.IsTrue((await client.CloseArchiveAsync(opened.Archive.SessionId)).Closed);
        }

        Assert.AreEqual(archiveHash, await Sha256FileAsync(archivePath));
        return result;
    }

    private static void CreateSourceArchive(string path)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        CreateEntry(archive, "keep.txt", "keep payload");
        CreateEntry(archive, "replace.txt", "old replacement");
        CreateEntry(archive, "delete.txt", "delete payload");
        _ = archive.CreateEntry("tree/");
        CreateEntry(archive, "tree/moved.txt", "tree payload");
        _ = archive.CreateEntry("delete-tree/");
        CreateEntry(archive, "delete-tree/remove.txt", "remove payload");
    }

    private static void CreateEntry(ZipArchive archive, string path, string text)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open(), Encoding.UTF8);
        writer.Write(text);
    }

    private static async Task<string> Sha256FileAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static void AssertWindowsExecutableHasIcon(string executable)
    {
        Assert.IsGreaterThan(0u, ExtractIconEx(executable, -1, null, null, 0),
            "The packaged App executable has no icon group.");
        var large = new IntPtr[1];
        var small = new IntPtr[1];
        Assert.IsGreaterThan(0u, ExtractIconEx(executable, 0, large, small, 1),
            "Windows could not extract the packaged App icon group.");
        try
        {
            Assert.AreNotEqual(IntPtr.Zero, large[0], "The packaged App has no large shell icon.");
            Assert.AreNotEqual(IntPtr.Zero, small[0], "The packaged App has no small shell icon.");
        }
        finally
        {
            if (large[0] != IntPtr.Zero)
            {
                _ = DestroyIcon(large[0]);
            }

            if (small[0] != IntPtr.Zero)
            {
                _ = DestroyIcon(small[0]);
            }
        }
    }

    private static async Task<int> RunCliAsync(string temporaryRoot, string[] arguments)
    {
        var localAppData = Path.Combine(temporaryRoot, "cli-local-app-data");
        Directory.CreateDirectory(localAppData);
        var previousLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        var previousTemp = Environment.GetEnvironmentVariable("TEMP");
        var previousTmp = Environment.GetEnvironmentVariable("TMP");
        Environment.SetEnvironmentVariable("LOCALAPPDATA", localAppData);
        Environment.SetEnvironmentVariable("TEMP", temporaryRoot);
        Environment.SetEnvironmentVariable("TMP", temporaryRoot);
        try
        {
            return await CliProgram.RunAsync(arguments);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", previousLocalAppData);
            Environment.SetEnvironmentVariable("TEMP", previousTemp);
            Environment.SetEnvironmentVariable("TMP", previousTmp);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(
        string file,
        int iconIndex,
        IntPtr[]? largeIcons,
        IntPtr[]? smallIcons,
        uint iconCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    private sealed record PackageLayout(
        string ArtifactRoot,
        string AppRoot,
        string WorkerPath);

    private sealed record PackageManifest(
        int SchemaVersion,
        string Protocol,
        int SourceInputCount,
        string SourceSha256,
        TreeIdentity AppTree,
        TreeIdentity WorkerTree,
        IReadOnlyDictionary<string, string> Files);

    private sealed record TreeIdentity(
        int Files,
        long Bytes,
        string Sha256);

    private sealed class RecordingDialogService : IAppDialogService
    {
        public string? ErrorMessage { get; private set; }

        public string? ExtractionDestination { get; init; }

        public string? ChooseArchive() => null;

        public ArchiveCreationSelection? ChooseArchiveCreation(bool supportsDirectoryCreation) => null;

        public string? ChooseNewExtractionDestination(string archivePath, bool selectedEntriesOnly) =>
            ExtractionDestination;

        public Task<char[]?> RequestPasswordAsync(string archiveName, CancellationToken cancellationToken) =>
            Task.FromResult<char[]?>(null);

        public Task ShowArchiveRecoveryAsync(IArchiveClient client, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public void ShowError(string title, string message) => ErrorMessage = message;

        public void ShowInformation(string title, string message)
        {
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainArchives.FinalPackageProductFlowTests",
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
