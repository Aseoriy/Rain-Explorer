using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.App.Services;
using RainArchives.Contracts;
using System.Runtime.InteropServices;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class ZipPresetStoreTests
{
    [TestMethod]
    public void BuiltInsAreImmutableAndTheDefaultPathIsExact()
    {
        using var root = new TemporaryDirectory();
        var store = new ZipPresetStore(root.Path);

        CollectionAssert.AreEqual(
            new[] { "Store", "Fast", "Balanced", "Maximum" },
            ZipPresetStore.BuiltIns.Select(preset => preset.Name).ToArray());
        Assert.IsTrue(ZipPresetStore.BuiltIns.All(preset => preset.IsBuiltIn));
        Assert.AreEqual(
            Path.Combine(root.Path, "SailHub", "Rain Archives", "settings", "zip-presets.v1.json"),
            store.SettingsPath);
        var relativeStore = new ZipPresetStore(Path.GetRelativePath(Environment.CurrentDirectory, root.Path));
        Assert.AreEqual(store.SettingsPath, relativeStore.SettingsPath);

        Assert.ThrowsExactly<ArgumentException>(() => store.Save(ZipPresetStore.BuiltIns));
        Assert.IsFalse(ZipPresetStore.BuiltIns[0].MetadataDefaults.AttributesLowWord.HasValue);
    }

    [TestMethod]
    public void SaveAndLoadRoundTripsOnlySupportedPresetFieldsAndKeepsOneBackup()
    {
        using var root = new TemporaryDirectory();
        var store = new ZipPresetStore(root.Path);
        var first = ZipPreset.CreateUser(
            "Rain preset",
            ArchiveCompressionPreset.Fast,
            new ZipPresetMetadataDefaults(
                new ArchiveDosLocalTimestamp(2026, 8, 25, 12, 34, 56),
                0x0021));

        Assert.IsTrue(store.Save([first]).Succeeded);
        var firstPayload = File.ReadAllText(store.SettingsPath);
        StringAssert.Contains(firstPayload, "metadataDefaults");
        Assert.IsFalse(firstPayload.Contains("sourcePath", StringComparison.Ordinal));
        Assert.IsFalse(firstPayload.Contains("destinationPath", StringComparison.Ordinal));
        Assert.IsFalse(firstPayload.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(firstPayload.Contains("recoveryId", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(firstPayload.Contains("isBuiltIn", StringComparison.Ordinal));

        var second = ZipPreset.CreateUser("Second", ArchiveCompressionPreset.Maximum);
        Assert.IsTrue(store.Save([first, second]).Succeeded);
        var secondPayload = File.ReadAllText(store.SettingsPath);
        var third = ZipPreset.CreateUser("Third", ArchiveCompressionPreset.Balanced);
        Assert.IsTrue(store.Save([first, second, third]).Succeeded);
        var thirdPayload = File.ReadAllText(store.SettingsPath);
        StringAssert.Contains(thirdPayload, "Third");
        Assert.AreEqual(secondPayload, File.ReadAllText(store.BackupPath));
        Assert.IsTrue(File.Exists(store.BackupPath));
        Assert.AreEqual(
            1,
            Directory.GetFiles(store.SettingsDirectoryPath, "zip-presets.v1.json.bak", SearchOption.TopDirectoryOnly).Length);

        var loaded = store.Load();
        Assert.IsNull(loaded.Warning);
        Assert.IsFalse(loaded.UsedBuiltIns);
        Assert.HasCount(7, loaded.Presets);
        var loadedFirst = loaded.Presets.Single(preset => preset.Name == "Rain preset");
        Assert.AreEqual(ArchiveCompressionPreset.Fast, loadedFirst.CompressionPreset);
        Assert.AreEqual(
            new ArchiveDosLocalTimestamp(2026, 8, 25, 12, 34, 56),
            loadedFirst.MetadataDefaults.LastWriteTime);
        Assert.AreEqual((ushort)0x0021, loadedFirst.MetadataDefaults.AttributesLowWord);
    }

    [TestMethod]
    public void NameAndMetadataValidationRejectsUnsafeOrUnsupportedValues()
    {
        var emojiName = string.Concat(Enumerable.Repeat("😀", 64));
        var accepted = ZipPreset.CreateUser(emojiName, ArchiveCompressionPreset.Store);
        Assert.AreEqual(emojiName, accepted.Name);

        Assert.ThrowsExactly<ArgumentException>(() =>
            ZipPreset.CreateUser(string.Concat(Enumerable.Repeat("😀", 65)), ArchiveCompressionPreset.Store));
        Assert.ThrowsExactly<ArgumentException>(() =>
            ZipPreset.CreateUser(new string('\u0344', 64), ArchiveCompressionPreset.Store));
        Assert.ThrowsExactly<ArgumentException>(() => ZipPreset.CreateUser("bad\u0001name", ArchiveCompressionPreset.Store));
        Assert.ThrowsExactly<ArgumentException>(() => ZipPreset.CreateUser("bad\u202Ename", ArchiveCompressionPreset.Store));
        Assert.ThrowsExactly<ArgumentException>(() => ZipPreset.CreateUser("bad\uD800name", ArchiveCompressionPreset.Store));
        Assert.ThrowsExactly<ArgumentException>(() => ZipPreset.CreateUser(" ", ArchiveCompressionPreset.Store));
        Assert.ThrowsExactly<ArgumentException>(() =>
            ZipPreset.CreateUser("invalid preset", (ArchiveCompressionPreset)99));

        Assert.ThrowsExactly<ArgumentException>(() =>
            new ZipPresetMetadataDefaults(new ArchiveDosLocalTimestamp(2024, 2, 30, 0, 0, 0)));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new ZipPresetMetadataDefaults(new ArchiveDosLocalTimestamp(2024, 1, 1, 0, 0, 1)));
        Assert.ThrowsExactly<ArgumentException>(() => new ZipPresetMetadataDefaults(attributesLowWord: 0x0010));
        Assert.ThrowsExactly<ArgumentException>(() => new ZipPresetMetadataDefaults(attributesLowWord: 0x0080));
    }

    [TestMethod]
    public void SaveRejectsNfcCaseDuplicatesAndBuiltInShadowing()
    {
        using var root = new TemporaryDirectory();
        var store = new ZipPresetStore(root.Path);

        var first = ZipPreset.CreateUser("Café", ArchiveCompressionPreset.Store);
        var decomposed = ZipPreset.CreateUser("cafe\u0301", ArchiveCompressionPreset.Fast);
        Assert.ThrowsExactly<ArgumentException>(() => store.Save([first, decomposed]));

        var builtinShadow = ZipPreset.CreateUser("balanced", ArchiveCompressionPreset.Fast);
        Assert.ThrowsExactly<ArgumentException>(() => store.Save([builtinShadow]));
    }

    [TestMethod]
    public void FileSizeContractAcceptsExactly128KiBAndRejectsTheNextByte()
    {
        const int contractBytes = 128 * 1024;
        const string emptyDocument = "{\"version\":1,\"userPresets\":[]}";
        var limitField = typeof(ZipPresetStore).GetField(nameof(ZipPresetStore.MaximumFileBytes));
        Assert.IsNotNull(limitField);
        var configuredLimit = limitField.GetRawConstantValue();
        Assert.AreEqual(contractBytes, configuredLimit);

        using var root = new TemporaryDirectory();
        var store = new ZipPresetStore(root.Path);
        Directory.CreateDirectory(store.SettingsDirectoryPath);

        File.WriteAllText(
            store.SettingsPath,
            emptyDocument.PadRight(contractBytes, ' '),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
        Assert.AreEqual(contractBytes, new FileInfo(store.SettingsPath).Length);
        var exactBoundary = store.Load();
        Assert.AreEqual(ZipPresetLoadState.Ready, exactBoundary.State);
        Assert.IsNull(exactBoundary.Warning);
        Assert.HasCount(4, exactBoundary.Presets);

        File.WriteAllText(
            store.SettingsPath,
            emptyDocument.PadRight(contractBytes + 1, ' '),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
        Assert.AreEqual(contractBytes + 1, new FileInfo(store.SettingsPath).Length);
        AssertFallback(store.Load());
    }

    [TestMethod]
    public void InvalidExistingDocumentRequiresExplicitResetBeforeReplacement()
    {
        using var root = new TemporaryDirectory();
        var store = new ZipPresetStore(root.Path);
        Assert.IsTrue(store.Save([
            ZipPreset.CreateUser("Before", ArchiveCompressionPreset.Balanced),
        ]).Succeeded);
        File.WriteAllText(store.SettingsPath, "{ malformed");
        var malformed = File.ReadAllBytes(store.SettingsPath);

        var refused = store.Save([
            ZipPreset.CreateUser("Replacement", ArchiveCompressionPreset.Maximum),
        ]);

        Assert.IsFalse(refused.Succeeded);
        CollectionAssert.AreEqual(malformed, File.ReadAllBytes(store.SettingsPath));
        Assert.IsTrue(store.Reset().Succeeded);
        var reset = store.Load();
        Assert.AreEqual(ZipPresetLoadState.Ready, reset.State);
        Assert.IsFalse(reset.Presets.Any(preset => !preset.IsBuiltIn));
    }

    [TestMethod]
    public void MissingMalformedOversizedFutureAndInterruptedStateFallsBackToBuiltIns()
    {
        using var root = new TemporaryDirectory();
        var store = new ZipPresetStore(root.Path);

        var missing = store.Load();
        AssertFallback(missing);

        Directory.CreateDirectory(store.SettingsDirectoryPath);
        File.WriteAllText(store.SettingsPath, "{ not json }");
        AssertFallback(store.Load());

        File.WriteAllText(store.SettingsPath, "{\"version\":99,\"userPresets\":[]}");
        AssertFallback(store.Load());

        File.WriteAllText(store.SettingsPath, "{\"version\":1,\"userPresets\":[],\"unexpected\":true}");
        AssertFallback(store.Load());

        File.WriteAllText(store.SettingsPath, "{\"version\":1,\"version\":1,\"userPresets\":[]}");
        AssertFallback(store.Load());

        File.WriteAllText(store.SettingsPath,
            "{\"version\":1,\"userPresets\":[{\"name\":\"Missing compression\",\"metadataDefaults\":{}}]}");
        AssertFallback(store.Load());

        File.WriteAllText(store.SettingsPath, new string('x', (128 * 1024) + 1));
        AssertFallback(store.Load());

        File.WriteAllText(store.SettingsPath, "{\"version\":1,\"userPresets\":[");
        AssertFallback(store.Load());
    }

    [TestMethod]
    public void FailedSavePreservesPreviousStateAndCleansOnlyItsTemporarySibling()
    {
        using var root = new TemporaryDirectory();
        var store = new ZipPresetStore(root.Path);
        var previous = ZipPreset.CreateUser("Previous", ArchiveCompressionPreset.Balanced);
        Assert.IsTrue(store.Save([previous]).Succeeded);

        using var held = new FileStream(
            store.SettingsPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1,
            options: FileOptions.SequentialScan);
        var failed = store.Save([ZipPreset.CreateUser("Replacement", ArchiveCompressionPreset.Maximum)]);
        Assert.IsFalse(failed.Succeeded);
        StringAssert.Contains(failed.Warning!, "built-in presets");

        held.Dispose();
        var loaded = store.Load();
        Assert.IsTrue(loaded.Presets.Any(preset => preset.Name == "Previous"));
        Assert.IsFalse(loaded.Presets.Any(preset => preset.Name == "Replacement"));
        Assert.IsEmpty(
            Directory.GetFiles(
                store.SettingsDirectoryPath,
                "zip-presets.v1.json.*.tmp",
                SearchOption.TopDirectoryOnly));
    }

    [TestMethod]
    public void ReparseSettingsRootAndFileAreRefused()
    {
        using var root = new TemporaryDirectory();
        var store = new ZipPresetStore(root.Path);
        var rootTarget = Path.Combine(root.Path, "settings-target");
        Directory.CreateDirectory(rootTarget);
        Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsDirectoryPath)!);
        Directory.CreateSymbolicLink(store.SettingsDirectoryPath, rootTarget);
        try
        {
            AssertFallback(store.Load());
            Assert.IsFalse(store.Save([ZipPreset.CreateUser("blocked-root", ArchiveCompressionPreset.Store)]).Succeeded);
            Assert.IsEmpty(Directory.GetFiles(rootTarget, "*", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            Directory.Delete(store.SettingsDirectoryPath);
        }

        Directory.CreateDirectory(store.SettingsDirectoryPath);
        var fileTarget = Path.Combine(root.Path, "settings-file-target.json");
        File.WriteAllText(fileTarget, "previous");
        File.CreateSymbolicLink(store.SettingsPath, fileTarget);
        try
        {
            AssertFallback(store.Load());
            Assert.IsFalse(store.Save([ZipPreset.CreateUser("blocked-file", ArchiveCompressionPreset.Store)]).Succeeded);
            Assert.AreEqual("previous", File.ReadAllText(fileTarget));
        }
        finally
        {
            File.Delete(store.SettingsPath);
        }
    }

    [TestMethod]
    public async Task ConcurrentWritersProduceOneValidWholeDocument()
    {
        using var root = new TemporaryDirectory();
        var stores = Enumerable.Range(0, 8)
            .Select(_ => new ZipPresetStore(root.Path))
            .ToArray();
        var tasks = stores.Select((store, index) => Task.Run(() =>
        {
            var result = store.Save([
                ZipPreset.CreateUser($"Writer {index}", ArchiveCompressionPreset.Balanced),
            ]);
            Assert.IsTrue(result.Succeeded, result.Warning);
        }));

        await Task.WhenAll(tasks);
        var loaded = stores[0].Load();
        Assert.IsNull(loaded.Warning);
        Assert.HasCount(5, loaded.Presets);
        Assert.IsTrue(loaded.Presets.Skip(4).Single().Name.StartsWith("Writer ", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AlternateStreamsHardLinksAndDirectoryStreamsAreRefused()
    {
        using var root = new TemporaryDirectory();
        var store = new ZipPresetStore(root.Path);
        Assert.IsTrue(store.Save([ZipPreset.CreateUser("safe", ArchiveCompressionPreset.Balanced)]).Succeeded);

        File.WriteAllText(store.SettingsPath + ":Zone.Identifier", "ZoneId=3");
        AssertFallback(new ZipPresetStore(root.Path).Load());
        Assert.IsFalse(store.Save([ZipPreset.CreateUser("blocked", ArchiveCompressionPreset.Store)]).Succeeded);
        File.Delete(store.SettingsPath);

        Assert.IsTrue(store.Save([ZipPreset.CreateUser("safe", ArchiveCompressionPreset.Balanced)]).Succeeded);
        var secondLink = Path.Combine(store.SettingsDirectoryPath, "zip-preset-hard-link.json");
        Assert.IsTrue(
            NativeMethods.CreateHardLink(secondLink, store.SettingsPath, IntPtr.Zero),
            $"CreateHardLink failed with Win32 error {Marshal.GetLastWin32Error()}.");
        AssertFallback(new ZipPresetStore(root.Path).Load());
        Assert.IsFalse(store.Save([ZipPreset.CreateUser("blocked", ArchiveCompressionPreset.Store)]).Succeeded);
        File.Delete(secondLink);

        var directoryStream = store.SettingsDirectoryPath + ":Zone.Identifier";
        File.WriteAllText(directoryStream, "ZoneId=3");
        try
        {
            AssertFallback(new ZipPresetStore(root.Path).Load());
            Assert.IsFalse(store.Save([ZipPreset.CreateUser("blocked", ArchiveCompressionPreset.Store)]).Succeeded);
        }
        finally
        {
            File.Delete(directoryStream);
        }
    }

    [TestMethod]
    public async Task WaitingForThePerUserLockIsCancellableAndDoesNotMutateState()
    {
        using var root = new TemporaryDirectory();
        var store = new ZipPresetStore(root.Path);
        Assert.IsTrue(store.Save([ZipPreset.CreateUser("safe", ArchiveCompressionPreset.Balanced)]).Succeeded);
        using var acquired = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            using var mutex = new Mutex(false, store.LockName);
            _ = mutex.WaitOne();
            acquired.Set();
            release.Wait();
            mutex.ReleaseMutex();
        });
        Assert.IsTrue(acquired.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            Assert.ThrowsExactly<OperationCanceledException>(() =>
                store.Save([ZipPreset.CreateUser("blocked", ArchiveCompressionPreset.Store)], cancellation.Token));
        }
        finally
        {
            release.Set();
            await holder;
        }

        var loaded = store.Load();
        Assert.IsTrue(loaded.Presets.Any(preset => preset.Name == "safe"));
        Assert.IsFalse(loaded.Presets.Any(preset => preset.Name == "blocked"));
    }

    private static void AssertFallback(ZipPresetLoadResult result)
    {
        Assert.IsTrue(result.UsedBuiltIns);
        Assert.IsNotNull(result.Warning);
        Assert.HasCount(4, result.Presets);
        Assert.IsTrue(result.Presets.All(preset => preset.IsBuiltIn));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainArchives.ZipPresetStoreTests",
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
    }
}
