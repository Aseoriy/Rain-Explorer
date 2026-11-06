using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.App.Services;
using RainArchives.App.Views;
using RainArchives.Contracts;

namespace RainArchives.UnitTests;

internal static class PresetMutationPolicyWpfAssertions
{
    public static void Run(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        AssertCreateRefusesInvalidState(owner);
        AssertRebuildRefusesInvalidState(owner);
        AssertReadyAndMissingMutationRemainAvailable(owner);
    }

    private static void AssertCreateRefusesInvalidState(Window owner)
    {
        foreach (var kind in Enum.GetValues<InvalidPresetState>())
        {
            using var fixture = InvalidPresetFixture.Create(kind);
            var dialog = new CreateArchiveDialog(
                supportsDirectoryCreation: true,
                presetStore: fixture.Store,
                capabilities: null)
            {
                Owner = owner,
            };
            try
            {
                AssertInvalidControlsAndBuiltIns(dialog);
                Assert.IsFalse(dialog.TrySaveUserPreset("Blocked"));
                fixture.AssertUnchanged();
                StringAssert.Contains(
                    Assert.IsInstanceOfType<TextBlock>(dialog.FindName("PresetWarningText")).Text,
                    "explicit Reset");
            }
            finally
            {
                dialog.Close();
            }
        }

        using var postOpenSave = ReadyPresetFixture.Create();
        var postOpenSaveDialog = new CreateArchiveDialog(
            supportsDirectoryCreation: true,
            presetStore: postOpenSave.Store,
            capabilities: null)
        {
            Owner = owner,
        };
        try
        {
            postOpenSave.MakePrimaryMalformed();
            Assert.IsFalse(postOpenSaveDialog.TrySaveUserPreset("Blocked after open"));
            postOpenSave.AssertMalformedUnchanged();
            AssertInvalidControlsAndBuiltIns(postOpenSaveDialog);
        }
        finally
        {
            postOpenSaveDialog.Close();
        }

        using var postOpenRemove = ReadyPresetFixture.Create();
        var postOpenRemoveDialog = new CreateArchiveDialog(
            supportsDirectoryCreation: true,
            presetStore: postOpenRemove.Store,
            capabilities: null)
        {
            Owner = owner,
        };
        try
        {
            SelectUserPreset(postOpenRemoveDialog, "Existing");
            postOpenRemove.MakePrimaryMalformed();
            Assert.IsFalse(postOpenRemoveDialog.TryRemoveSelectedUserPreset());
            postOpenRemove.AssertMalformedUnchanged();
            AssertInvalidControlsAndBuiltIns(postOpenRemoveDialog);
        }
        finally
        {
            postOpenRemoveDialog.Close();
        }
    }

    private static void AssertRebuildRefusesInvalidState(Window owner)
    {
        foreach (var kind in Enum.GetValues<InvalidPresetState>())
        {
            using var fixture = InvalidPresetFixture.Create(kind);
            var source = fixture.CreateSourceArchivePlaceholder();
            var dialog = new RebuildArchiveDialog(
                source,
                [],
                [],
                client: null,
                sessionId: null,
                presetStore: fixture.Store)
            {
                Owner = owner,
            };
            try
            {
                AssertInvalidControlsAndBuiltIns(dialog);
                Assert.IsFalse(dialog.TrySaveUserPreset("Blocked"));
                fixture.AssertUnchanged();
                StringAssert.Contains(
                    Assert.IsInstanceOfType<TextBlock>(dialog.FindName("PresetWarningText")).Text,
                    "explicit Reset");
            }
            finally
            {
                dialog.Close();
            }
        }

        using var postOpenSave = ReadyPresetFixture.Create();
        var postOpenSaveSource = postOpenSave.CreateSourceArchivePlaceholder();
        var postOpenSaveDialog = new RebuildArchiveDialog(
            postOpenSaveSource,
            [],
            [],
            client: null,
            sessionId: null,
            presetStore: postOpenSave.Store)
        {
            Owner = owner,
        };
        try
        {
            postOpenSave.MakePrimaryMalformed();
            Assert.IsFalse(postOpenSaveDialog.TrySaveUserPreset("Blocked after open"));
            postOpenSave.AssertMalformedUnchanged();
            AssertInvalidControlsAndBuiltIns(postOpenSaveDialog);
        }
        finally
        {
            postOpenSaveDialog.Close();
        }

        using var postOpenRemove = ReadyPresetFixture.Create();
        var postOpenRemoveSource = postOpenRemove.CreateSourceArchivePlaceholder();
        var postOpenRemoveDialog = new RebuildArchiveDialog(
            postOpenRemoveSource,
            [],
            [],
            client: null,
            sessionId: null,
            presetStore: postOpenRemove.Store)
        {
            Owner = owner,
        };
        try
        {
            SelectUserPreset(postOpenRemoveDialog, "Existing");
            postOpenRemove.MakePrimaryMalformed();
            Assert.IsFalse(postOpenRemoveDialog.TryRemoveSelectedUserPreset());
            postOpenRemove.AssertMalformedUnchanged();
            AssertInvalidControlsAndBuiltIns(postOpenRemoveDialog);
        }
        finally
        {
            postOpenRemoveDialog.Close();
        }
    }

    private static void AssertReadyAndMissingMutationRemainAvailable(Window owner)
    {
        using var createRoot = new TemporaryDirectory("RainArchives.CreatePresetMutationPolicyTests");
        var createStore = new ZipPresetStore(createRoot.Path);
        var createDialog = new CreateArchiveDialog(true, createStore, capabilities: null)
        {
            Owner = owner,
        };
        try
        {
            Assert.IsTrue(Assert.IsInstanceOfType<Button>(createDialog.FindName("SavePresetButton")).IsEnabled);
            Assert.IsTrue(createDialog.TrySaveUserPreset("Create valid"));
            Assert.AreEqual(ZipPresetLoadState.Ready, createStore.Load().State);
            SelectUserPreset(createDialog, "Create valid");
            Assert.IsTrue(createDialog.TryRemoveSelectedUserPreset());
            Assert.IsFalse(createStore.Load().Presets.Any(preset => !preset.IsBuiltIn));
        }
        finally
        {
            createDialog.Close();
        }

        using var rebuildRoot = new TemporaryDirectory("RainArchives.RebuildPresetMutationPolicyTests");
        var rebuildStore = new ZipPresetStore(rebuildRoot.Path);
        var rebuildSource = Path.Combine(rebuildRoot.Path, "source.zip");
        File.WriteAllText(rebuildSource, "source");
        var rebuildDialog = new RebuildArchiveDialog(
            rebuildSource,
            [],
            [],
            client: null,
            sessionId: null,
            presetStore: rebuildStore)
        {
            Owner = owner,
        };
        try
        {
            Assert.IsTrue(Assert.IsInstanceOfType<Button>(rebuildDialog.FindName("SavePresetButton")).IsEnabled);
            Assert.IsTrue(rebuildDialog.TrySaveUserPreset("Rebuild valid"));
            Assert.AreEqual(ZipPresetLoadState.Ready, rebuildStore.Load().State);
            SelectUserPreset(rebuildDialog, "Rebuild valid");
            Assert.IsTrue(rebuildDialog.TryRemoveSelectedUserPreset());
            Assert.IsFalse(rebuildStore.Load().Presets.Any(preset => !preset.IsBuiltIn));
        }
        finally
        {
            rebuildDialog.Close();
        }
    }

    private static void AssertInvalidControlsAndBuiltIns(Window dialog)
    {
        var presets = Assert.IsInstanceOfType<ComboBox>(dialog.FindName("PresetInput"));
        Assert.HasCount(4, presets.Items);
        Assert.IsTrue(presets.Items.OfType<CompressionPresetOption>().All(option => option.Preset?.IsBuiltIn == true));
        Assert.IsFalse(Assert.IsInstanceOfType<Button>(dialog.FindName("SavePresetButton")).IsEnabled);
        Assert.IsFalse(Assert.IsInstanceOfType<Button>(dialog.FindName("RemovePresetButton")).IsEnabled);
        Assert.IsFalse(Assert.IsInstanceOfType<TextBox>(dialog.FindName("PresetNameInput")).IsEnabled);
        if (dialog.FindName("PresetDefaultTimeInput") is TextBox defaultTime)
        {
            Assert.IsFalse(defaultTime.IsEnabled);
        }

        if (dialog.FindName("PresetDefaultAttributesInput") is TextBox defaultAttributes)
        {
            Assert.IsFalse(defaultAttributes.IsEnabled);
        }

        Assert.AreEqual(
            ZipPresetOptionFactory.ResetRequiredWarning,
            Assert.IsInstanceOfType<TextBlock>(dialog.FindName("PresetWarningText")).Text);
    }

    private static void SelectUserPreset(Window dialog, string name)
    {
        var input = Assert.IsInstanceOfType<ComboBox>(dialog.FindName("PresetInput"));
        input.SelectedItem = input.Items.OfType<CompressionPresetOption>()
            .Single(option => string.Equals(option.Name, name, StringComparison.Ordinal));
    }

    private enum InvalidPresetState
    {
        Malformed,
        FutureVersion,
        HardLinked,
    }

    private sealed class InvalidPresetFixture : IDisposable
    {
        private readonly TemporaryDirectory _root;
        private readonly string? _hardLinkPath;
        private readonly byte[] _expectedBytes;
        private readonly FileObjectIdentity? _expectedIdentity;

        private InvalidPresetFixture(
            TemporaryDirectory root,
            ZipPresetStore store,
            byte[] expectedBytes,
            string? hardLinkPath,
            FileObjectIdentity? expectedIdentity)
        {
            _root = root;
            Store = store;
            _expectedBytes = expectedBytes;
            _hardLinkPath = hardLinkPath;
            _expectedIdentity = expectedIdentity;
        }

        public ZipPresetStore Store { get; }

        public static InvalidPresetFixture Create(InvalidPresetState kind)
        {
            var root = new TemporaryDirectory($"RainArchives.InvalidPresetPolicy.{kind}");
            var store = new ZipPresetStore(root.Path);
            string? hardLinkPath = null;
            switch (kind)
            {
                case InvalidPresetState.Malformed:
                    Directory.CreateDirectory(store.SettingsDirectoryPath);
                    File.WriteAllText(store.SettingsPath, "{ malformed");
                    break;
                case InvalidPresetState.FutureVersion:
                    Directory.CreateDirectory(store.SettingsDirectoryPath);
                    File.WriteAllText(store.SettingsPath, "{\"version\":99,\"userPresets\":[]}");
                    break;
                case InvalidPresetState.HardLinked:
                    Assert.IsTrue(store.Save([
                        ZipPreset.CreateUser("Existing", ArchiveCompressionPreset.Balanced),
                    ]).Succeeded);
                    hardLinkPath = Path.Combine(store.SettingsDirectoryPath, "hard-link.json");
                    Assert.IsTrue(
                        NativeMethods.CreateHardLink(hardLinkPath, store.SettingsPath, IntPtr.Zero),
                        $"CreateHardLink failed with Win32 error {Marshal.GetLastWin32Error()}.");
                    break;
                default:
                    throw new InvalidOperationException("Unknown preset fixture state.");
            }

            var expectedBytes = File.ReadAllBytes(store.SettingsPath);
            FileObjectIdentity? expectedIdentity = hardLinkPath is null
                ? null
                : GetFileObjectIdentity(store.SettingsPath);
            if (hardLinkPath is not null)
            {
                Assert.AreEqual(expectedIdentity, GetFileObjectIdentity(hardLinkPath));
                Assert.IsTrue(expectedIdentity.HasValue);
                Assert.AreEqual((uint)2, expectedIdentity.Value.NumberOfLinks);
            }

            Assert.AreEqual(ZipPresetLoadState.UnavailableOrInvalid, store.Load().State);
            return new InvalidPresetFixture(root, store, expectedBytes, hardLinkPath, expectedIdentity);
        }

        public string CreateSourceArchivePlaceholder()
        {
            var path = Path.Combine(_root.Path, $"source-{Guid.NewGuid():N}.zip");
            File.WriteAllText(path, "source");
            return path;
        }

        public void AssertUnchanged()
        {
            CollectionAssert.AreEqual(_expectedBytes, File.ReadAllBytes(Store.SettingsPath));
            if (_hardLinkPath is not null)
            {
                Assert.IsTrue(File.Exists(_hardLinkPath));
                CollectionAssert.AreEqual(_expectedBytes, File.ReadAllBytes(_hardLinkPath));
                Assert.AreEqual(_expectedIdentity, GetFileObjectIdentity(Store.SettingsPath));
                Assert.AreEqual(_expectedIdentity, GetFileObjectIdentity(_hardLinkPath));
            }
        }

        public void Dispose() => _root.Dispose();
    }

    private sealed class ReadyPresetFixture : IDisposable
    {
        private readonly TemporaryDirectory _root;
        private byte[]? _malformed;

        private ReadyPresetFixture(TemporaryDirectory root, ZipPresetStore store)
        {
            _root = root;
            Store = store;
        }

        public ZipPresetStore Store { get; }

        public static ReadyPresetFixture Create()
        {
            var root = new TemporaryDirectory("RainArchives.ReadyPresetMutationPolicyTests");
            var store = new ZipPresetStore(root.Path);
            Assert.IsTrue(store.Save([
                ZipPreset.CreateUser("Existing", ArchiveCompressionPreset.Balanced),
            ]).Succeeded);
            return new ReadyPresetFixture(root, store);
        }

        public string CreateSourceArchivePlaceholder()
        {
            var path = Path.Combine(_root.Path, $"source-{Guid.NewGuid():N}.zip");
            File.WriteAllText(path, "source");
            return path;
        }

        public void MakePrimaryMalformed()
        {
            File.WriteAllText(Store.SettingsPath, "{ post-open corruption");
            _malformed = File.ReadAllBytes(Store.SettingsPath);
        }

        public void AssertMalformedUnchanged()
        {
            Assert.IsNotNull(_malformed);
            CollectionAssert.AreEqual(_malformed, File.ReadAllBytes(Store.SettingsPath));
        }

        public void Dispose() => _root.Dispose();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory(string scope)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                scope,
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

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateHardLink(
            string fileName,
            string existingFileName,
            IntPtr securityAttributes);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(
            Microsoft.Win32.SafeHandles.SafeFileHandle fileHandle,
            out ByHandleFileInformation information);

        [StructLayout(LayoutKind.Sequential)]
        internal struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public uint CreationTimeLow;
            public uint CreationTimeHigh;
            public uint LastAccessTimeLow;
            public uint LastAccessTimeHigh;
            public uint LastWriteTimeLow;
            public uint LastWriteTimeHigh;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }
    }

    private static FileObjectIdentity GetFileObjectIdentity(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        Assert.IsTrue(
            NativeMethods.GetFileInformationByHandle(stream.SafeFileHandle, out var information),
            $"GetFileInformationByHandle failed with Win32 error {Marshal.GetLastWin32Error()}.");
        return new FileObjectIdentity(
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow,
            information.NumberOfLinks);
    }

    private readonly record struct FileObjectIdentity(
        uint VolumeSerialNumber,
        ulong FileIndex,
        uint NumberOfLinks);
}
