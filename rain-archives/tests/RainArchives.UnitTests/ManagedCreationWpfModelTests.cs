using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.App.Services;
using RainArchives.App.ViewModels;
using RainArchives.App.Views;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class ManagedCreationWpfModelTests
{
    [TestMethod]
    public void FormatFactoryOnlyExposesAdvertisedManagedFormatsAndPresets()
    {
        var managed = new GetManagedWriteCapabilitiesResponse(
        [
            Capability(ManagedArchiveCreationFormat.TarGZip, supportsTree: true,
                ArchiveCompressionPreset.Fast, ArchiveCompressionPreset.Balanced),
            Capability(ManagedArchiveCreationFormat.TarBZip2, supportsTree: true,
                ArchiveCompressionPreset.Balanced),
            Capability(ManagedArchiveCreationFormat.GZip, supportsTree: false,
                ArchiveCompressionPreset.Balanced),
            Capability(ManagedArchiveCreationFormat.BZip2, supportsTree: false,
                ArchiveCompressionPreset.Balanced),
        ]);
        var tar = new GetTarWriteCapabilitiesResponse(
            CanCreate: true,
            CanRebuild: false,
            SupportsTreeCreation: true,
            CompressionPresets: [ArchiveCompressionPreset.Store],
            MaximumEntries: ProtocolConstants.MaximumTarCreationEntries,
            MaximumSourceBytes: ProtocolConstants.MaximumTarCreationSourceBytes,
            SupportsEncryption: false,
            SupportsFilenameEncryption: false,
            SupportsSplitOutput: false,
            SupportsExistingDestinationReplacement: false,
            SupportsInPlaceUpdate: false);

        var options = CreationFormatOptionFactory.Load(
            supportsZipDirectories: true,
            zipCapabilities: ZipCapabilities(canCreate: true),
            tarCapabilities: tar,
            managedCapabilities: managed);

        Assert.HasCount(6, options);
        Assert.IsTrue(options.Single(option => option.Value == ArchiveCreationFormat.Tar).SupportsDirectories);
        Assert.IsTrue(options.Single(option => option.Value == ArchiveCreationFormat.TarGZip).SupportsDirectories);
        Assert.IsFalse(options.Single(option => option.Value == ArchiveCreationFormat.GZip).SupportsDirectories);
        Assert.IsFalse(options.Any(option => option.Name is "XZ" or "7z" or "RAR"));

        var bzip = options.Single(option => option.Value == ArchiveCreationFormat.BZip2);
        var presets = CreationFormatOptionFactory.LoadPresets(bzip, null, null);
        Assert.HasCount(1, presets);
        Assert.AreEqual(ArchiveCompressionPreset.Balanced, presets[0].Value);
        Assert.IsNull(presets[0].Preset);
    }

    [TestMethod]
    public void UnadvertisedManagedFormatsAreNotAddedAndUnavailableZipRemainsDisabled()
    {
        var zip = new GetZipWriteCapabilitiesResponse(
            CanCreate: false,
            CanRebuild: false,
            SupportsAdd: false,
            SupportsReplace: false,
            SupportsDelete: false,
            SupportsFileRename: false,
            SupportsTreeEdit: false,
            SupportsMetadata: false,
            CompressionPresets: [],
            MaximumEntries: 0,
            MaximumSourceBytes: 0,
            SupportsEncryption: false,
            SupportsFilenameEncryption: false,
            SupportsSplitOutput: false,
            SupportsExistingDestinationReplacement: false,
            SupportsInPlaceUpdate: false);

        var options = CreationFormatOptionFactory.Load(true, zip, null, null);

        Assert.HasCount(1, options);
        Assert.AreEqual(ArchiveCreationFormat.Zip, options[0].Value);
        Assert.IsFalse(options[0].IsAvailable);
    }

    [TestMethod]
    public void SelectionDefaultsToZipAndCanRepresentManagedFormatWithoutChangingLegacyCallers()
    {
        var legacy = new ArchiveCreationSelection("C:\\out.zip", [], [], ArchiveCompressionPreset.Balanced);
        var managed = legacy with { Format = ArchiveCreationFormat.TarGZip };

        Assert.AreEqual(ArchiveCreationFormat.Zip, legacy.Format);
        Assert.AreEqual(ArchiveCreationFormat.TarGZip, managed.Format);
    }

    [TestMethod]
    public async Task ViewModelDispatchesEveryTarAndManagedCreationRoute()
    {
        var root = Path.GetTempPath();
        var file = new ArchiveCreationFile(Path.Combine(root, "source.bin"), "source.bin");
        var directory = new ArchiveCreationDirectory(Path.Combine(root, "source-tree"), "source-tree");
        var cases = new[]
        {
            new RouteCase(ArchiveCreationFormat.Tar, ".tar", false, ArchiveCompressionPreset.Store, "tar"),
            new RouteCase(ArchiveCreationFormat.Tar, ".tar", true, ArchiveCompressionPreset.Store, "tar-tree"),
            new RouteCase(ArchiveCreationFormat.TarGZip, ".tar.gz", false, ArchiveCompressionPreset.Maximum, "tar-gzip"),
            new RouteCase(ArchiveCreationFormat.TarGZip, ".tar.gz", true, ArchiveCompressionPreset.Fast, "tar-gzip-tree"),
            new RouteCase(ArchiveCreationFormat.TarBZip2, ".tar.bz2", false, ArchiveCompressionPreset.Balanced, "tar-bzip2"),
            new RouteCase(ArchiveCreationFormat.TarBZip2, ".tar.bz2", true, ArchiveCompressionPreset.Balanced, "tar-bzip2-tree"),
            new RouteCase(ArchiveCreationFormat.GZip, ".gz", false, ArchiveCompressionPreset.Store, "gzip"),
            new RouteCase(ArchiveCreationFormat.BZip2, ".bz2", false, ArchiveCompressionPreset.Balanced, "bzip2"),
        };

        foreach (var item in cases)
        {
            var client = new ManagedCreationClient();
            await using var viewModel = new MainWindowViewModel(
                new PassiveDialogs(),
                _ => Task.FromResult<IArchiveClient>(client));
            await viewModel.CreateArchiveAsync(new ArchiveCreationSelection(
                Path.Combine(root, $"{Guid.NewGuid():N}{item.Extension}"),
                item.Tree ? [] : [file],
                item.Tree ? [directory] : [],
                item.Preset,
                item.Format));

            Assert.AreEqual(item.ExpectedRoute, client.Route, item.Format.ToString());
            Assert.AreEqual(item.Preset, client.Preset, item.Format.ToString());
        }
    }

    private static ManagedWriteCapability Capability(
        ManagedArchiveCreationFormat format,
        bool supportsTree,
        params ArchiveCompressionPreset[] presets) =>
        new(
            format,
            CanCreate: true,
            SupportsTreeCreation: supportsTree,
            CanRebuild: false,
            CompressionPresets: presets,
            MaximumEntries: 4096,
            MaximumSourceBytes: 64L * 1024 * 1024 * 1024,
            SupportsEncryption: false,
            SupportsFilenameEncryption: false,
            SupportsSplitOutput: false,
            SupportsExistingDestinationReplacement: false,
            SupportsInPlaceUpdate: false);

    private static GetZipWriteCapabilitiesResponse ZipCapabilities(bool canCreate) =>
        new(
            CanCreate: canCreate,
            CanRebuild: false,
            SupportsAdd: canCreate,
            SupportsReplace: false,
            SupportsDelete: false,
            SupportsFileRename: false,
            SupportsTreeEdit: false,
            SupportsMetadata: false,
            CompressionPresets: Enum.GetValues<ArchiveCompressionPreset>(),
            MaximumEntries: ProtocolConstants.MaximumZipCreationEntries,
            MaximumSourceBytes: ProtocolConstants.MaximumZipCreationSourceBytes,
            SupportsEncryption: false,
            SupportsFilenameEncryption: false,
            SupportsSplitOutput: false,
            SupportsExistingDestinationReplacement: false,
            SupportsInPlaceUpdate: false);

    private sealed record RouteCase(
        ArchiveCreationFormat Format,
        string Extension,
        bool Tree,
        ArchiveCompressionPreset Preset,
        string ExpectedRoute);

    private sealed class ManagedCreationClient : ArchiveClientTestDouble
    {
        private readonly OperationId _operationId = OperationId.New();

        public string? Route { get; private set; }
        public ArchiveCompressionPreset Preset { get; private set; }

        public override Task<GetTarWriteCapabilitiesResponse> GetTarWriteCapabilitiesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GetTarWriteCapabilitiesResponse(
                CanCreate: true,
                CanRebuild: false,
                SupportsTreeCreation: true,
                CompressionPresets: [ArchiveCompressionPreset.Store],
                MaximumEntries: ProtocolConstants.MaximumTarCreationEntries,
                MaximumSourceBytes: ProtocolConstants.MaximumTarCreationSourceBytes,
                SupportsEncryption: false,
                SupportsFilenameEncryption: false,
                SupportsSplitOutput: false,
                SupportsExistingDestinationReplacement: false,
                SupportsInPlaceUpdate: false));

        public override Task<GetManagedWriteCapabilitiesResponse> GetManagedWriteCapabilitiesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GetManagedWriteCapabilitiesResponse(
            [
                Capability(ManagedArchiveCreationFormat.TarGZip, true,
                    ArchiveCompressionPreset.Store,
                    ArchiveCompressionPreset.Fast,
                    ArchiveCompressionPreset.Balanced,
                    ArchiveCompressionPreset.Maximum),
                Capability(ManagedArchiveCreationFormat.TarBZip2, true,
                    ArchiveCompressionPreset.Balanced),
                Capability(ManagedArchiveCreationFormat.GZip, false,
                    ArchiveCompressionPreset.Store,
                    ArchiveCompressionPreset.Fast,
                    ArchiveCompressionPreset.Balanced,
                    ArchiveCompressionPreset.Maximum),
                Capability(ManagedArchiveCreationFormat.BZip2, false,
                    ArchiveCompressionPreset.Balanced),
            ]));

        public override Task<StartOperationResponse> StartTarCreationAsync(
            string destinationPath,
            IReadOnlyList<ArchiveCreationFile> files,
            CancellationToken cancellationToken = default) => Record("tar", ArchiveCompressionPreset.Store);

        public override Task<StartOperationResponse> StartTarTreeCreationAsync(
            string destinationPath,
            IReadOnlyList<ArchiveCreationFile> files,
            IReadOnlyList<ArchiveCreationDirectory> directories,
            CancellationToken cancellationToken = default) => Record("tar-tree", ArchiveCompressionPreset.Store);

        public override Task<StartOperationResponse> StartTarGZipCreationAsync(
            string destinationPath,
            IReadOnlyList<ArchiveCreationFile> files,
            ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
            CancellationToken cancellationToken = default) => Record("tar-gzip", compressionPreset);

        public override Task<StartOperationResponse> StartTarGZipTreeCreationAsync(
            string destinationPath,
            IReadOnlyList<ArchiveCreationFile> files,
            IReadOnlyList<ArchiveCreationDirectory> directories,
            ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
            CancellationToken cancellationToken = default) => Record("tar-gzip-tree", compressionPreset);

        public override Task<StartOperationResponse> StartTarBZip2CreationAsync(
            string destinationPath,
            IReadOnlyList<ArchiveCreationFile> files,
            ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
            CancellationToken cancellationToken = default) => Record("tar-bzip2", compressionPreset);

        public override Task<StartOperationResponse> StartTarBZip2TreeCreationAsync(
            string destinationPath,
            IReadOnlyList<ArchiveCreationFile> files,
            IReadOnlyList<ArchiveCreationDirectory> directories,
            ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
            CancellationToken cancellationToken = default) => Record("tar-bzip2-tree", compressionPreset);

        public override Task<StartOperationResponse> StartGZipCreationAsync(
            string destinationPath,
            string sourcePath,
            ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
            CancellationToken cancellationToken = default) => Record("gzip", compressionPreset);

        public override Task<StartOperationResponse> StartBZip2CreationAsync(
            string destinationPath,
            string sourcePath,
            ArchiveCompressionPreset compressionPreset = ArchiveCompressionPreset.Balanced,
            CancellationToken cancellationToken = default) => Record("bzip2", compressionPreset);

        public override Task<OperationEvent> WaitForTerminalAsync(
            OperationId operationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new OperationEvent(
                _operationId,
                1,
                OperationEventKind.Terminal,
                OperationPhase.Failed,
                Error: new ArchiveError(
                    "test.expected_failure",
                    ArchiveErrorCategory.ProviderFailure,
                    "The route was recorded.")));

        private Task<StartOperationResponse> Record(
            string route,
            ArchiveCompressionPreset preset)
        {
            Route = route;
            Preset = preset;
            return Task.FromResult(new StartOperationResponse(_operationId));
        }
    }

    private sealed class PassiveDialogs : IAppDialogService
    {
        public string? ChooseArchive() => null;
        public ArchiveCreationSelection? ChooseArchiveCreation(bool supportsDirectoryCreation) => null;
        public string? ChooseNewExtractionDestination(string archivePath, bool selectedEntriesOnly) => null;
        public Task<char[]?> RequestPasswordAsync(string archiveName, CancellationToken cancellationToken) =>
            Task.FromResult<char[]?>(null);
        public Task ShowArchiveRecoveryAsync(IArchiveClient client, CancellationToken cancellationToken) =>
            Task.CompletedTask;
        public void ShowError(string title, string message) { }
        public void ShowInformation(string title, string message) { }
    }
}
