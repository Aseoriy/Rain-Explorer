using System.Runtime.InteropServices;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.App.Services;
using RainArchives.Contracts;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class AppSettingsStoreTests
{
    [TestMethod]
    public void MissingStateUsesBoundedDefaultsAndExactPrivatePath()
    {
        using var root = new TemporaryDirectory();
        var store = new AppSettingsStore(root.Path);

        var result = store.Load();

        Assert.AreEqual(AppSettingsLoadState.Defaults, result.State);
        Assert.AreEqual(AppUiSettings.Default, result.Settings);
        Assert.IsFalse(result.IsLastKnownGood);
        Assert.AreEqual(
            Path.Combine(root.Path, "SailHub", "Rain Archives", "settings", "ui-settings.v1.json"),
            store.SettingsPath);
        Assert.IsFalse(Directory.Exists(store.SettingsDirectoryPath));
        AssertNoSensitiveText(result.Status);
    }

    [TestMethod]
    public void SaveRoundTripsOnlyFixedFieldsAndMaintainsOneValidatedBackup()
    {
        using var root = new TemporaryDirectory();
        var store = new AppSettingsStore(root.Path);
        var first = Settings(
            ArchiveCreationFormat.TarBZip2,
            ArchiveCompressionPreset.Maximum,
            showAdvanced: true);
        var second = Settings(
            ArchiveCreationFormat.GZip,
            ArchiveCompressionPreset.Fast,
            showAdvanced: false);

        Assert.IsTrue(store.Save(first).Succeeded);
        var firstPayload = File.ReadAllText(store.SettingsPath);
        Assert.IsTrue(store.Save(second).Succeeded);
        var secondPayload = File.ReadAllText(store.SettingsPath);
        Assert.AreNotEqual(firstPayload, secondPayload);
        Assert.AreEqual(firstPayload, File.ReadAllText(store.BackupPath));
        Assert.AreEqual(
            1,
            Directory.GetFiles(
                store.SettingsDirectoryPath,
                "ui-settings.v1.json.bak",
                SearchOption.TopDirectoryOnly).Length);

        var loaded = new AppSettingsStore(root.Path).Load();
        Assert.AreEqual(AppSettingsLoadState.Ready, loaded.State);
        Assert.AreEqual(second, loaded.Settings);
        Assert.IsFalse(loaded.IsLastKnownGood);
        AssertNoForbiddenPersistence(firstPayload);
        AssertNoForbiddenPersistence(secondPayload);
    }

    [TestMethod]
    public void MalformedPrimaryUsesValidatedBackupWithoutRewritingEitherDocument()
    {
        using var root = new TemporaryDirectory();
        var store = new AppSettingsStore(root.Path);
        var backupSettings = Settings(
            ArchiveCreationFormat.Tar,
            ArchiveCompressionPreset.Store,
            showAdvanced: true);
        var currentSettings = Settings(
            ArchiveCreationFormat.Zip,
            ArchiveCompressionPreset.Maximum,
            showAdvanced: false);
        Assert.IsTrue(store.Save(backupSettings).Succeeded);
        Assert.IsTrue(store.Save(currentSettings).Succeeded);
        var backupPayload = File.ReadAllBytes(store.BackupPath);
        File.WriteAllText(store.SettingsPath, "{ truncated");
        var malformedPayload = File.ReadAllBytes(store.SettingsPath);

        var loaded = new AppSettingsStore(root.Path).Load();

        Assert.AreEqual(AppSettingsLoadState.RecoveredFromBackup, loaded.State);
        Assert.IsTrue(loaded.IsLastKnownGood);
        Assert.AreEqual(backupSettings, loaded.Settings);
        CollectionAssert.AreEqual(malformedPayload, File.ReadAllBytes(store.SettingsPath));
        CollectionAssert.AreEqual(backupPayload, File.ReadAllBytes(store.BackupPath));
        AssertNoSensitiveText(loaded.Status);
    }

    [TestMethod]
    public void UnsupportedVersionDoesNotFallBackAcrossTheVersionBoundary()
    {
        using var root = new TemporaryDirectory();
        var writer = new AppSettingsStore(root.Path);
        var oldSettings = Settings(
            ArchiveCreationFormat.Tar,
            ArchiveCompressionPreset.Store,
            showAdvanced: false);
        var currentSettings = Settings(
            ArchiveCreationFormat.BZip2,
            ArchiveCompressionPreset.Fast,
            showAdvanced: true);
        Assert.IsTrue(writer.Save(oldSettings).Succeeded);
        Assert.IsTrue(writer.Save(currentSettings).Succeeded);
        File.WriteAllText(
            writer.SettingsPath,
            File.ReadAllText(writer.SettingsPath).Replace(
                "\"version\":1",
                "\"version\":99",
                StringComparison.Ordinal));

        var loaded = new AppSettingsStore(root.Path).Load();

        Assert.AreEqual(AppSettingsLoadState.UnsupportedVersion, loaded.State);
        Assert.AreEqual(AppUiSettings.Default, loaded.Settings);
        Assert.IsTrue(loaded.IsLastKnownGood);
        Assert.AreNotEqual(oldSettings, loaded.Settings);
    }

    [TestMethod]
    public void CorruptUnknownOversizeTruncatedAndInvalidUtf8StatesFailClosed()
    {
        using var root = new TemporaryDirectory();
        var store = new AppSettingsStore(root.Path);
        Assert.IsTrue(store.Save(AppUiSettings.Default).Succeeded);

        var valid = File.ReadAllText(store.SettingsPath);
        foreach (var payload in new[]
                 {
                     valid.Replace("}", ",\"unexpected\":true}", StringComparison.Ordinal),
                     valid.Replace("\"Balanced\"", "\"balanced\"", StringComparison.Ordinal),
                     valid.Replace("\"Zip\"", "\"SevenZip\"", StringComparison.Ordinal),
                     valid.Replace(
                         "\"version\":1",
                         "\"version\":1,\"version\":1",
                         StringComparison.Ordinal),
                     valid.Replace(",\"showAdvancedRebuildOptions\":false", string.Empty, StringComparison.Ordinal),
                     "{",
                 })
        {
            File.Delete(store.BackupPath);
            File.WriteAllText(store.SettingsPath, payload);
            var result = new AppSettingsStore(root.Path).Load();
            Assert.AreEqual(AppSettingsLoadState.Corrupt, result.State);
            Assert.AreEqual(AppUiSettings.Default, result.Settings);
        }

        File.WriteAllBytes(store.SettingsPath, Enumerable.Repeat((byte)'x', AppSettingsStore.MaximumFileBytes + 1).ToArray());
        var oversized = new AppSettingsStore(root.Path).Load();
        Assert.AreEqual(AppSettingsLoadState.Corrupt, oversized.State);

        var invalidUtf8 = Encoding.ASCII.GetBytes(valid).ToList();
        var tokenStart = Encoding.ASCII.GetBytes("\"defaultCreationFormat\":\"");
        var start = IndexOf(invalidUtf8, tokenStart) + tokenStart.Length;
        invalidUtf8.Insert(start, 0xC3);
        invalidUtf8.Insert(start + 1, 0x28);
        File.WriteAllBytes(store.SettingsPath, invalidUtf8.ToArray());
        var invalid = new AppSettingsStore(root.Path).Load();
        Assert.AreEqual(AppSettingsLoadState.Corrupt, invalid.State);
    }

    [TestMethod]
    public void InMemoryLastKnownGoodSurvivesLaterCorruption()
    {
        using var root = new TemporaryDirectory();
        var store = new AppSettingsStore(root.Path);
        var expected = Settings(
            ArchiveCreationFormat.TarGZip,
            ArchiveCompressionPreset.Maximum,
            showAdvanced: true);
        Assert.IsTrue(store.Save(expected).Succeeded);
        Assert.AreEqual(expected, store.Load().Settings);
        File.WriteAllText(store.SettingsPath, "not-json");

        var result = store.Load();

        Assert.AreEqual(AppSettingsLoadState.Corrupt, result.State);
        Assert.IsTrue(result.IsLastKnownGood);
        Assert.AreEqual(expected, result.Settings);
    }

    [TestMethod]
    public void ResetPersistsDeterministicDefaultsAndPreservesPriorDocumentAsBackup()
    {
        using var root = new TemporaryDirectory();
        var store = new AppSettingsStore(root.Path);
        var custom = Settings(
            ArchiveCreationFormat.BZip2,
            ArchiveCompressionPreset.Maximum,
            showAdvanced: true);
        Assert.IsTrue(store.Save(custom).Succeeded);
        var customPayload = File.ReadAllText(store.SettingsPath);

        var reset = store.Reset();

        Assert.IsTrue(reset.Succeeded);
        Assert.AreEqual(AppSettingsSaveState.Saved, reset.State);
        Assert.AreEqual(AppUiSettings.Default, new AppSettingsStore(root.Path).Load().Settings);
        Assert.AreEqual(customPayload, File.ReadAllText(store.BackupPath));
        AssertNoSensitiveText(reset.Status);
    }

    [TestMethod]
    public void InvalidModelValuesAreRejectedBeforeStorageIsCreated()
    {
        using var root = new TemporaryDirectory();
        var store = new AppSettingsStore(root.Path);
        var invalidFormat = AppUiSettings.Default with
        {
            ManagedCreation = AppUiSettings.Default.ManagedCreation with
            {
                DefaultFormat = (ArchiveCreationFormat)99,
            },
        };
        var invalidPreset = AppUiSettings.Default with
        {
            ManagedCreation = AppUiSettings.Default.ManagedCreation with
            {
                GZipPreset = (ArchiveCompressionPreset)99,
            },
        };

        Assert.ThrowsExactly<ArgumentException>(() => store.Save(invalidFormat));
        Assert.ThrowsExactly<ArgumentException>(() => store.Save(invalidPreset));
        Assert.IsFalse(Directory.Exists(store.SettingsDirectoryPath));
    }

    [TestMethod]
    public void ReparseHardLinkAndAlternateStreamStatesAreRefused()
    {
        using var root = new TemporaryDirectory();
        var store = new AppSettingsStore(root.Path);
        var target = Path.Combine(root.Path, "outside-settings");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsDirectoryPath)!);
        Directory.CreateSymbolicLink(store.SettingsDirectoryPath, target);
        try
        {
            Assert.AreEqual(AppSettingsLoadState.UnsafeStorage, store.Load().State);
            Assert.AreEqual(AppSettingsSaveState.UnsafeStorage, store.Save(AppUiSettings.Default).State);
            Assert.IsEmpty(Directory.GetFiles(target));
        }
        finally
        {
            Directory.Delete(store.SettingsDirectoryPath);
        }

        Assert.IsTrue(store.Save(AppUiSettings.Default).Succeeded);
        File.WriteAllText(store.SettingsPath + ":Zone.Identifier", "ZoneId=3");
        Assert.AreEqual(AppSettingsLoadState.UnsafeStorage, new AppSettingsStore(root.Path).Load().State);
        Assert.AreEqual(AppSettingsSaveState.UnsafeStorage, store.Save(AppUiSettings.Default).State);
        File.Delete(store.SettingsPath);

        Assert.IsTrue(store.Save(AppUiSettings.Default).Succeeded);
        var secondLink = Path.Combine(store.SettingsDirectoryPath, "settings-hard-link.json");
        Assert.IsTrue(
            NativeMethods.CreateHardLink(secondLink, store.SettingsPath, IntPtr.Zero),
            $"CreateHardLink failed with Win32 error {Marshal.GetLastWin32Error()}.");
        Assert.AreEqual(AppSettingsLoadState.UnsafeStorage, new AppSettingsStore(root.Path).Load().State);
        Assert.AreEqual(AppSettingsSaveState.UnsafeStorage, store.Save(AppUiSettings.Default).State);
        File.Delete(secondLink);
    }

    [TestMethod]
    public void FailedReplacementPreservesPrimaryAndCleansOnlyOwnedSibling()
    {
        using var root = new TemporaryDirectory();
        var store = new AppSettingsStore(root.Path);
        var previous = Settings(
            ArchiveCreationFormat.Tar,
            ArchiveCompressionPreset.Store,
            showAdvanced: true);
        Assert.IsTrue(store.Save(previous).Succeeded);
        var previousPayload = File.ReadAllText(store.SettingsPath);
        using var held = new FileStream(
            store.SettingsPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.SequentialScan);

        var result = store.Save(AppUiSettings.Default);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(AppSettingsSaveState.Unavailable, result.State);
        Assert.AreEqual(previousPayload, File.ReadAllText(store.SettingsPath));
        Assert.IsEmpty(Directory.GetFiles(
            store.SettingsDirectoryPath,
            "ui-settings.v1.json.*.tmp",
            SearchOption.TopDirectoryOnly));
    }

    [TestMethod]
    public async Task ConcurrentWritersProduceOneWholeValidatedDocument()
    {
        using var root = new TemporaryDirectory();
        var stores = Enumerable.Range(0, 8)
            .Select(_ => new AppSettingsStore(root.Path))
            .ToArray();
        var expected = Enumerable.Range(0, stores.Length)
            .Select(index => Settings(
                (ArchiveCreationFormat)(index % 6),
                (ArchiveCompressionPreset)(index % 4),
                showAdvanced: (index & 1) == 0))
            .ToArray();

        await Task.WhenAll(stores.Select((store, index) => Task.Run(() =>
        {
            var result = store.Save(expected[index]);
            Assert.IsTrue(result.Succeeded, result.Status);
        })));

        var loaded = stores[0].Load();
        Assert.AreEqual(AppSettingsLoadState.Ready, loaded.State);
        Assert.IsTrue(expected.Contains(loaded.Settings));
        Assert.IsEmpty(Directory.GetFiles(
            stores[0].SettingsDirectoryPath,
            "ui-settings.v1.json.*.tmp",
            SearchOption.TopDirectoryOnly));
    }

    [TestMethod]
    public async Task HeldWriterLockRefusesOrCancelsWithoutChangingState()
    {
        using var root = new TemporaryDirectory();
        var store = new AppSettingsStore(root.Path, TimeSpan.FromMilliseconds(150));
        Assert.IsTrue(store.Save(AppUiSettings.Default).Succeeded);
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
            var refused = store.Save(Settings(
                ArchiveCreationFormat.Tar,
                ArchiveCompressionPreset.Maximum,
                showAdvanced: true));
            Assert.IsFalse(refused.Succeeded);
            Assert.AreEqual(AppSettingsSaveState.Unavailable, refused.State);

            using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            Assert.ThrowsExactly<OperationCanceledException>(() => store.Load(cancelled.Token));
        }
        finally
        {
            release.Set();
            await holder;
        }

        Assert.AreEqual(AppUiSettings.Default, store.Load().Settings);
    }

    private static AppUiSettings Settings(
        ArchiveCreationFormat format,
        ArchiveCompressionPreset preset,
        bool showAdvanced) =>
        new(
            new ManagedCreationDefaults(
                format,
                preset,
                ArchiveCompressionPreset.Store,
                ArchiveCompressionPreset.Fast,
                ArchiveCompressionPreset.Maximum,
                ArchiveCompressionPreset.Balanced,
                ArchiveCompressionPreset.Fast,
                preset),
            showAdvanced);

    private static int IndexOf(IReadOnlyList<byte> value, IReadOnlyList<byte> sought)
    {
        for (var index = 0; index <= value.Count - sought.Count; index++)
        {
            var matches = true;
            for (var offset = 0; offset < sought.Count; offset++)
            {
                if (value[index + offset] == sought[offset])
                {
                    continue;
                }

                matches = false;
                break;
            }

            if (matches)
            {
                return index;
            }
        }

        Assert.Fail("Expected schema token was not found.");
        return -1;
    }

    private static void AssertNoForbiddenPersistence(string payload)
    {
        foreach (var forbidden in new[]
                 {
                     "sourcePath",
                     "destinationPath",
                     "archivePath",
                     "entryName",
                     "password",
                     "secret",
                     "recoveryId",
                     "revision",
                     "operation",
                     "provider",
                     "exception",
                     "temporary",
                     "handle",
                     "identity",
                 })
        {
            Assert.IsFalse(
                payload.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                $"Unexpected sensitive field {forbidden} was persisted.");
        }
    }

    private static void AssertNoSensitiveText(string value)
    {
        Assert.IsFalse(value.Contains(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(value.Contains("exception", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(value.Contains("provider", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(value.Contains("password", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainArchives.AppSettingsStoreTests",
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
