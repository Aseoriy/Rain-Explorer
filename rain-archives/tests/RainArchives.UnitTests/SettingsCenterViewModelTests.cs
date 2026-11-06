using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.App.Services;
using RainArchives.App.ViewModels;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class SettingsCenterViewModelTests
{
    [TestMethod]
    public async Task InitializationPresentsAllManagedFormatsAndTruthfulCurrentConstraints()
    {
        using var root = new TemporaryDirectory();
        var settingsStore = new AppSettingsStore(root.Path);
        var presetStore = new ZipPresetStore(root.Path);
        var settings = AppUiSettings.Default with
        {
            ManagedCreation = AppUiSettings.Default.ManagedCreation with
            {
                DefaultFormat = ArchiveCreationFormat.GZip,
                GZipPreset = ArchiveCompressionPreset.Maximum,
            },
            ShowAdvancedRebuildOptions = true,
        };
        Assert.IsTrue(settingsStore.Save(settings).Succeeded);
        Assert.IsTrue(presetStore.Save([
            ZipPreset.CreateUser("Release", ArchiveCompressionPreset.Fast),
        ]).Succeeded);
        await using var viewModel = new SettingsCenterViewModel(
            settingsStore,
            presetStore,
            _ => Task.FromResult(AllCapabilities(
                gzipPresets: [ArchiveCompressionPreset.Fast])));

        await viewModel.InitializeAsync();

        Assert.AreEqual(SettingsCenterPersistenceState.Ready, viewModel.PersistenceState);
        Assert.AreEqual(SettingsCapabilityState.Ready, viewModel.CapabilityState);
        Assert.HasCount(6, viewModel.Formats);
        Assert.IsTrue(viewModel.Formats.All(format => format.IsSupported));
        Assert.AreEqual(ArchiveCreationFormat.GZip, viewModel.SelectedDefaultFormat?.Format);
        Assert.AreEqual(
            ArchiveCompressionPreset.Fast,
            viewModel.Formats.Single(format => format.Format == ArchiveCreationFormat.GZip).CurrentPreset);
        StringAssert.Contains(
            viewModel.Formats.Single(format => format.Format == ArchiveCreationFormat.GZip).ConstraintText,
            "Exactly one ordinary file");
        StringAssert.Contains(
            viewModel.Formats.Single(format => format.Format == ArchiveCreationFormat.Tar).ConstraintText,
            "folder input: available");
        Assert.HasCount(1, viewModel.UserPresets);
        Assert.AreEqual("Release", viewModel.UserPresets[0].Name);
        Assert.IsTrue(viewModel.ShowAdvancedRebuildOptions);
        Assert.IsFalse(viewModel.HasUnsavedChanges);
        AssertNoSensitiveStatus(viewModel);
    }

    [TestMethod]
    public async Task SavePersistsOnlyValidatedHintsAndBoundedUserPresets()
    {
        using var root = new TemporaryDirectory();
        var settingsStore = new AppSettingsStore(root.Path);
        var presetStore = new ZipPresetStore(root.Path);
        await using var viewModel = new SettingsCenterViewModel(
            settingsStore,
            presetStore,
            _ => Task.FromResult(AllCapabilities()));
        await viewModel.InitializeAsync();
        var tarGzip = viewModel.Formats.Single(format => format.Format == ArchiveCreationFormat.TarGZip);
        viewModel.SelectedDefaultFormat = tarGzip;
        tarGzip.SelectedPreset = tarGzip.Presets.Single(option =>
            option.Value == ArchiveCompressionPreset.Maximum);
        viewModel.ZipRebuildPreset = viewModel.ZipRebuildPresets.Single(option =>
            option.Value == ArchiveCompressionPreset.Fast);
        viewModel.ShowAdvancedRebuildOptions = true;
        viewModel.PresetName = "Mapped metadata";
        viewModel.PresetCompression = viewModel.CompressionPresets.Single(option =>
            option.Value == ArchiveCompressionPreset.Maximum);
        viewModel.PresetTime = "2026-08-30T22:14:56";
        viewModel.PresetAttributes = "ReadOnly, Archive";
        Assert.IsTrue(viewModel.AddOrUpdatePreset());

        await viewModel.SaveAsync();

        Assert.AreEqual(SettingsCenterPersistenceState.Saved, viewModel.PersistenceState);
        Assert.IsFalse(viewModel.HasUnsavedChanges);
        var settings = new AppSettingsStore(root.Path).Load().Settings;
        Assert.AreEqual(ArchiveCreationFormat.TarGZip, settings.ManagedCreation.DefaultFormat);
        Assert.AreEqual(ArchiveCompressionPreset.Maximum, settings.ManagedCreation.TarGZipPreset);
        Assert.AreEqual(ArchiveCompressionPreset.Fast, settings.ManagedCreation.ZipRebuildPreset);
        Assert.IsTrue(settings.ShowAdvancedRebuildOptions);
        var preset = new ZipPresetStore(root.Path).Load().Presets.Single(item => item.Name == "Mapped metadata");
        Assert.AreEqual(ArchiveCompressionPreset.Maximum, preset.CompressionPreset);
        Assert.AreEqual((ushort)0x0021, preset.MetadataDefaults.AttributesLowWord);
        Assert.IsFalse(File.ReadAllText(settingsStore.SettingsPath).Contains("Mapped metadata", StringComparison.Ordinal));
        AssertNoSensitiveStatus(viewModel);
    }

    [TestMethod]
    public async Task InvalidPresetInputIsRejectedWithoutEchoingControlledText()
    {
        using var root = new TemporaryDirectory();
        await using var viewModel = new SettingsCenterViewModel(
            new AppSettingsStore(root.Path),
            new ZipPresetStore(root.Path),
            _ => Task.FromResult(AllCapabilities()));
        await viewModel.InitializeAsync();
        viewModel.PresetName = "bad\r\n\u202Ename";
        viewModel.PresetTime = "2026-08-30T22:14:55";

        Assert.IsFalse(viewModel.AddOrUpdatePreset());
        Assert.IsFalse(viewModel.StatusAnnouncement.Contains("bad", StringComparison.Ordinal));
        Assert.IsFalse(viewModel.StatusAnnouncement.Contains('\r'));
        Assert.IsFalse(viewModel.StatusAnnouncement.Contains('\n'));
        Assert.IsFalse(viewModel.StatusAnnouncement.Contains('\u202E'));
        Assert.IsEmpty(viewModel.UserPresets);
    }

    [TestMethod]
    public async Task MalformedDocumentsRequireExplicitResetBeforeOrdinarySave()
    {
        using var root = new TemporaryDirectory();
        var settingsStore = new AppSettingsStore(root.Path);
        var presetStore = new ZipPresetStore(root.Path);
        Assert.IsTrue(settingsStore.Save(AppUiSettings.Default).Succeeded);
        Assert.IsTrue(presetStore.Save([
            ZipPreset.CreateUser("Before", ArchiveCompressionPreset.Balanced),
        ]).Succeeded);
        File.WriteAllText(settingsStore.SettingsPath, "{ malformed");
        File.WriteAllText(presetStore.SettingsPath, "{ malformed");
        await using var viewModel = new SettingsCenterViewModel(
            settingsStore,
            presetStore,
            _ => Task.FromResult(AllCapabilities()));

        await viewModel.InitializeAsync();

        Assert.AreEqual(SettingsCenterPersistenceState.Corrupt, viewModel.PersistenceState);
        Assert.IsFalse(viewModel.CanSave);
        Assert.IsFalse(viewModel.CanEditPresets);
        StringAssert.Contains(viewModel.PresetStatus, "explicit Reset");
        await viewModel.ResetAsync();
        Assert.AreEqual(SettingsCenterPersistenceState.Reset, viewModel.PersistenceState);
        Assert.AreEqual(AppUiSettings.Default, new AppSettingsStore(root.Path).Load().Settings);
        Assert.IsFalse(new ZipPresetStore(root.Path).Load().Presets.Any(item => !item.IsBuiltIn));
    }

    [TestMethod]
    public async Task RecoveredPrimaryRemainsReadOnlyUntilExplicitReset()
    {
        using var root = new TemporaryDirectory();
        var settingsStore = new AppSettingsStore(root.Path);
        var backupSettings = AppUiSettings.Default with
        {
            ManagedCreation = AppUiSettings.Default.ManagedCreation with
            {
                DefaultFormat = ArchiveCreationFormat.Tar,
                TarPreset = ArchiveCompressionPreset.Store,
            },
        };
        var currentSettings = AppUiSettings.Default with
        {
            ManagedCreation = AppUiSettings.Default.ManagedCreation with
            {
                DefaultFormat = ArchiveCreationFormat.GZip,
                GZipPreset = ArchiveCompressionPreset.Fast,
            },
        };
        Assert.IsTrue(settingsStore.Save(backupSettings).Succeeded);
        Assert.IsTrue(settingsStore.Save(currentSettings).Succeeded);
        File.WriteAllText(settingsStore.SettingsPath, "{ malformed");
        var malformedPrimary = File.ReadAllBytes(settingsStore.SettingsPath);
        var validBackup = File.ReadAllBytes(settingsStore.BackupPath);
        await using var viewModel = new SettingsCenterViewModel(
            settingsStore,
            new ZipPresetStore(root.Path),
            _ => Task.FromResult(AllCapabilities()));

        await viewModel.InitializeAsync();

        Assert.AreEqual(SettingsCenterPersistenceState.Recovered, viewModel.PersistenceState);
        Assert.AreEqual(ArchiveCreationFormat.Tar, viewModel.SelectedDefaultFormat?.Format);
        viewModel.ShowAdvancedRebuildOptions = !viewModel.ShowAdvancedRebuildOptions;
        Assert.IsTrue(viewModel.HasUnsavedChanges);
        Assert.IsFalse(viewModel.CanSave);
        await viewModel.SaveAsync();
        CollectionAssert.AreEqual(malformedPrimary, File.ReadAllBytes(settingsStore.SettingsPath));
        CollectionAssert.AreEqual(validBackup, File.ReadAllBytes(settingsStore.BackupPath));

        await viewModel.ResetAsync();
        Assert.AreEqual(SettingsCenterPersistenceState.Reset, viewModel.PersistenceState);
        var reset = new AppSettingsStore(root.Path).Load();
        Assert.AreEqual(AppSettingsLoadState.Ready, reset.State);
        Assert.AreEqual(AppUiSettings.Default, reset.Settings);
    }

    [TestMethod]
    public async Task OfflineAndDowngradedCapabilityStatesRemainVisibleWithoutChangingDefaults()
    {
        using var root = new TemporaryDirectory();
        var settingsStore = new AppSettingsStore(root.Path);
        var expected = AppUiSettings.Default with
        {
            ManagedCreation = AppUiSettings.Default.ManagedCreation with
            {
                DefaultFormat = ArchiveCreationFormat.BZip2,
                BZip2Preset = ArchiveCompressionPreset.Fast,
            },
        };
        Assert.IsTrue(settingsStore.Save(expected).Succeeded);
        var responses = new Queue<SettingsCapabilitySnapshot>([
            SettingsCapabilitySnapshot.Offline,
            new SettingsCapabilitySnapshot(
                SettingsCapabilityState.Downgraded,
                null,
                null,
                null,
                "The connected protocol minor does not advertise managed creation capability queries. Defaults are not applied."),
        ]);
        await using var viewModel = new SettingsCenterViewModel(
            settingsStore,
            new ZipPresetStore(root.Path),
            _ => Task.FromResult(responses.Dequeue()));

        await viewModel.InitializeAsync();
        Assert.AreEqual(SettingsCapabilityState.Offline, viewModel.CapabilityState);
        Assert.IsFalse(viewModel.CanEditDefaults);
        Assert.AreEqual(ArchiveCreationFormat.BZip2, viewModel.SelectedDefaultFormat?.Format);
        Assert.AreEqual(ArchiveCompressionPreset.Fast,
            viewModel.Formats.Single(item => item.Format == ArchiveCreationFormat.BZip2).CurrentPreset);

        await viewModel.RefreshAsync();
        Assert.AreEqual(SettingsCapabilityState.Downgraded, viewModel.CapabilityState);
        Assert.IsFalse(viewModel.CanEditDefaults);
        Assert.AreEqual(expected, settingsStore.Load().Settings);
    }

    [TestMethod]
    public async Task CapabilityWorkNeverOverlapsAndCloseRejectsLateResponse()
    {
        using var root = new TemporaryDirectory();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var concurrent = 0;
        var maximumConcurrent = 0;
        var calls = 0;
        async Task<SettingsCapabilitySnapshot> Load(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            var active = Interlocked.Increment(ref concurrent);
            maximumConcurrent = Math.Max(maximumConcurrent, active);
            started.TrySetResult();
            await release.Task;
            Interlocked.Decrement(ref concurrent);
            return AllCapabilities();
        }

        var viewModel = new SettingsCenterViewModel(
            new AppSettingsStore(root.Path),
            new ZipPresetStore(root.Path),
            Load);
        var initialize = viewModel.InitializeAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var refresh = viewModel.RefreshAsync();
        var dispose = viewModel.DisposeAsync().AsTask();
        release.TrySetResult();

        await Task.WhenAll(initialize, refresh, dispose).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.AreEqual(1, calls);
        Assert.AreEqual(1, maximumConcurrent);
        Assert.AreEqual(SettingsCapabilityState.Closed, viewModel.CapabilityState);
        Assert.AreEqual(SettingsCenterPersistenceState.Closed, viewModel.PersistenceState);
    }

    [TestMethod]
    public async Task ClientCapabilityLoaderUsesOnlyNegotiatedQueriesAndRedactsFailures()
    {
        var client = new CapabilityClientDouble();
        var ready = await SettingsCapabilitySnapshot.LoadAsync(client, CancellationToken.None);
        Assert.AreEqual(SettingsCapabilityState.Ready, ready.State);
        Assert.AreEqual(1, client.ZipQueries);
        Assert.AreEqual(1, client.TarQueries);
        Assert.AreEqual(1, client.ManagedQueries);

        client.Features.Clear();
        var downgraded = await SettingsCapabilitySnapshot.LoadAsync(client, CancellationToken.None);
        Assert.AreEqual(SettingsCapabilityState.Downgraded, downgraded.State);
        Assert.AreEqual(1, client.ZipQueries);

        client.Features.Add(ProtocolFeatures.ZipWriteCapabilities);
        client.Failure = new ArchiveClientException(new ArchiveError(
            "provider.failed",
            ArchiveErrorCategory.ProviderFailure,
            "C:\\private\\provider-controlled-text"));
        var failed = await SettingsCapabilitySnapshot.LoadAsync(client, CancellationToken.None);
        Assert.AreEqual(SettingsCapabilityState.Error, failed.State);
        Assert.IsFalse(failed.Status.Contains("private", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(failed.Status.Contains("provider-controlled", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void DefaultResolverClampsEveryFormatAndPresetDeterministically()
    {
        var settings = AppUiSettings.Default with
        {
            ManagedCreation = new ManagedCreationDefaults(
                ArchiveCreationFormat.TarBZip2,
                ArchiveCompressionPreset.Maximum,
                ArchiveCompressionPreset.Store,
                ArchiveCompressionPreset.Fast,
                ArchiveCompressionPreset.Maximum,
                ArchiveCompressionPreset.Fast,
                ArchiveCompressionPreset.Maximum,
                ArchiveCompressionPreset.Maximum),
        };
        Assert.AreEqual(
            ArchiveCreationFormat.TarBZip2,
            ManagedCreationDefaultResolver.ResolveFormat(
                settings,
                [ArchiveCreationFormat.Zip, ArchiveCreationFormat.TarBZip2]));
        Assert.AreEqual(
            ArchiveCreationFormat.Zip,
            ManagedCreationDefaultResolver.ResolveFormat(settings, [ArchiveCreationFormat.Zip]));
        foreach (var format in Enum.GetValues<ArchiveCreationFormat>())
        {
            Assert.AreEqual(
                ArchiveCompressionPreset.Balanced,
                ManagedCreationDefaultResolver.ResolvePreset(
                    settings,
                    format,
                    [ArchiveCompressionPreset.Balanced]));
        }

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            ManagedCreationDefaultResolver.ResolveFormat(settings, []));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            ManagedCreationDefaultResolver.ResolvePreset(settings, ArchiveCreationFormat.Zip, []));
    }

    private static SettingsCapabilitySnapshot AllCapabilities(
        IReadOnlyList<ArchiveCompressionPreset>? gzipPresets = null) =>
        new(
            SettingsCapabilityState.Ready,
            ZipCapabilities(),
            TarCapabilities(),
            new GetManagedWriteCapabilitiesResponse(
            [
                ManagedCapability(ManagedArchiveCreationFormat.TarGZip, true),
                ManagedCapability(ManagedArchiveCreationFormat.TarBZip2, true),
                ManagedCapability(
                    ManagedArchiveCreationFormat.GZip,
                    false,
                    gzipPresets ?? Enum.GetValues<ArchiveCompressionPreset>()),
                ManagedCapability(ManagedArchiveCreationFormat.BZip2, false),
            ]),
            "Current managed-format capabilities were authenticated. Defaults remain hints and are checked again before an operation starts.");

    private static GetZipWriteCapabilitiesResponse ZipCapabilities() => new(
        CanCreate: true,
        CanRebuild: false,
        SupportsAdd: true,
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

    private static GetTarWriteCapabilitiesResponse TarCapabilities() => new(
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

    private static ManagedWriteCapability ManagedCapability(
        ManagedArchiveCreationFormat format,
        bool supportsTree,
        IReadOnlyList<ArchiveCompressionPreset>? presets = null) => new(
            format,
            CanCreate: true,
            SupportsTreeCreation: supportsTree,
            CanRebuild: false,
            CompressionPresets: presets ?? Enum.GetValues<ArchiveCompressionPreset>(),
            MaximumEntries: 4096,
            MaximumSourceBytes: 64L * 1024 * 1024 * 1024,
            SupportsEncryption: false,
            SupportsFilenameEncryption: false,
            SupportsSplitOutput: false,
            SupportsExistingDestinationReplacement: false,
            SupportsInPlaceUpdate: false);

    private static void AssertNoSensitiveStatus(SettingsCenterViewModel viewModel)
    {
        var status = string.Join(
            " ",
            viewModel.PersistenceStatus,
            viewModel.PresetStatus,
            viewModel.CapabilityStatus,
            viewModel.StatusAnnouncement);
        Assert.IsFalse(status.Contains(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(status.Contains("provider-controlled", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(status.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(status.Contains("recoveryId", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class CapabilityClientDouble : ArchiveClientTestDouble
    {
        public HashSet<string> Features { get; } =
        [
            ProtocolFeatures.ZipWriteCapabilities,
            ProtocolFeatures.TarWriteCapabilities,
            ProtocolFeatures.ManagedWriteCapabilities,
        ];
        public Exception? Failure { get; set; }
        public int ZipQueries { get; private set; }
        public int TarQueries { get; private set; }
        public int ManagedQueries { get; private set; }

        public override bool SupportsFeature(string feature) => Features.Contains(feature);

        public override Task<GetZipWriteCapabilitiesResponse> GetZipWriteCapabilitiesAsync(
            ArchiveSessionId? sessionId = null,
            CancellationToken cancellationToken = default)
        {
            ZipQueries++;
            return Failure is null
                ? Task.FromResult(ZipCapabilities())
                : Task.FromException<GetZipWriteCapabilitiesResponse>(Failure);
        }

        public override Task<GetTarWriteCapabilitiesResponse> GetTarWriteCapabilitiesAsync(
            CancellationToken cancellationToken = default)
        {
            TarQueries++;
            return Task.FromResult(TarCapabilities());
        }

        public override Task<GetManagedWriteCapabilitiesResponse> GetManagedWriteCapabilitiesAsync(
            CancellationToken cancellationToken = default)
        {
            ManagedQueries++;
            return Task.FromResult(AllCapabilities().Managed!);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainArchives.SettingsCenterViewModelTests",
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
