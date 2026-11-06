using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.App.Services;
using RainArchives.App.ViewModels;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class WpfCreationEndToEndTests
{
    private const string FixturePassword = "RainArchives-Fixture-2026!";

    [TestMethod]
    [Timeout(120_000)]
    public async Task PersistedDefaultsAreRevalidatedForRealWorkerCreateAndRebuild()
    {
        using var temporary = new TemporaryDirectory();
        var settingsRoot = Path.Combine(temporary.Path, "local-app-data");
        var workerTemporaryRoot = Path.Combine(temporary.Path, "worker-temp");
        Directory.CreateDirectory(settingsRoot);
        Directory.CreateDirectory(workerTemporaryRoot);
        var settingsStore = new AppSettingsStore(settingsRoot);
        var settings = AppUiSettings.Default with
        {
            ManagedCreation = AppUiSettings.Default.ManagedCreation with
            {
                DefaultFormat = ArchiveCreationFormat.Zip,
                ZipPreset = ArchiveCompressionPreset.Fast,
                ZipRebuildPreset = ArchiveCompressionPreset.Maximum,
            },
            ShowAdvancedRebuildOptions = true,
        };
        Assert.IsTrue(settingsStore.Save(settings).Succeeded);
        var loaded = settingsStore.Load();
        Assert.AreEqual(AppSettingsLoadState.Ready, loaded.State);
        Assert.AreEqual(settings, loaded.Settings);

        var source = Path.Combine(temporary.Path, "settings-source.txt");
        var created = Path.Combine(temporary.Path, "settings-created.zip");
        var rebuilt = Path.Combine(temporary.Path, "settings-rebuilt.zip");
        await File.WriteAllTextAsync(source, "settings-backed production payload 雨");
        var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var workerPath = Path.Combine(AppContext.BaseDirectory, "RainArchives.Worker.exe");
        Assert.IsTrue(File.Exists(workerPath));

        string createdHash;
        await using (var client = await ArchiveClient.StartWithTemporaryRootAsync(
                         workerPath,
                         workerTemporaryRoot))
        {
            var capabilities = await client.GetZipWriteCapabilitiesAsync();
            var availableFormats = capabilities.CanCreate
                ? new[] { ArchiveCreationFormat.Zip }
                : Array.Empty<ArchiveCreationFormat>();
            Assert.AreEqual(
                ArchiveCreationFormat.Zip,
                ManagedCreationDefaultResolver.ResolveFormat(loaded.Settings, availableFormats));
            var createPreset = ManagedCreationDefaultResolver.ResolvePreset(
                loaded.Settings,
                ArchiveCreationFormat.Zip,
                capabilities.CompressionPresets);
            Assert.AreEqual(ArchiveCompressionPreset.Fast, createPreset);

            var creation = await client.StartZipCreationAsync(
                created,
                [new ArchiveCreationFile(source, "payload.txt")],
                createPreset);
            var creationTerminal = await client.WaitForTerminalAsync(creation.OperationId);
            Assert.IsTrue(
                creationTerminal.Phase is OperationPhase.Completed or OperationPhase.CompletedWithWarnings,
                creationTerminal.Error?.Message);
            createdHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(created)));

            var opened = await client.OpenArchiveAsync(created);
            var listed = await client.ListEntriesAsync(opened.Archive.SessionId, 0, 32);
            Assert.AreEqual("payload.txt", listed.Entries.Single().RawPath);
            var rebuildPreset = ManagedCreationDefaultResolver.ResolveZipRebuildPreset(
                loaded.Settings,
                capabilities.CompressionPresets);
            Assert.AreEqual(ArchiveCompressionPreset.Maximum, rebuildPreset);
            var rebuild = await client.StartZipRebuildCopyAsync(
                opened.Archive.SessionId,
                rebuilt,
                [],
                [],
                rebuildPreset);
            var rebuildTerminal = await client.WaitForTerminalAsync(rebuild.OperationId);
            Assert.IsTrue(
                rebuildTerminal.Phase is OperationPhase.Completed or OperationPhase.CompletedWithWarnings,
                rebuildTerminal.Error?.Message);
            Assert.IsTrue((await client.CloseArchiveAsync(opened.Archive.SessionId)).Closed);

            var reopened = await client.OpenArchiveAsync(rebuilt);
            var rebuiltEntries = await client.ListEntriesAsync(reopened.Archive.SessionId, 0, 32);
            Assert.AreEqual("payload.txt", rebuiltEntries.Entries.Single().RawPath);
            var stream = await client.OpenEntryStreamAsync(
                reopened.Archive.SessionId,
                rebuiltEntries.Entries.Single().EntryId);
            await using (stream)
            using (var reader = new StreamReader(stream.Content))
            {
                Assert.AreEqual("settings-backed production payload 雨", await reader.ReadToEndAsync());
            }
            Assert.IsTrue((await client.CloseArchiveAsync(reopened.Archive.SessionId)).Closed);
        }

        CollectionAssert.AreEqual(sourceHash, SHA256.HashData(await File.ReadAllBytesAsync(source)));
        Assert.AreEqual(createdHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(created))));
        Assert.IsTrue(File.Exists(rebuilt));
        Assert.HasCount(0, Directory.GetFiles(
            temporary.Path,
            ".rainarchives-*.staging",
            SearchOption.AllDirectories));
        Assert.HasCount(0, Directory.GetFiles(workerTemporaryRoot, "*", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task ViewModelCreatesMixedZipThroughPackagedWorkerAndOpensTheResult()
    {
        using var temporary = new TemporaryDirectory();
        var explicitSource = Path.Combine(temporary.Path, "README.txt");
        var directorySource = Path.Combine(temporary.Path, "assets-source");
        var nestedSource = Path.Combine(directorySource, "nested");
        var emptySource = Path.Combine(directorySource, "empty");
        Directory.CreateDirectory(nestedSource);
        Directory.CreateDirectory(emptySource);
        await File.WriteAllTextAsync(explicitSource, "Rain Archives WPF creation route");
        await File.WriteAllTextAsync(Path.Combine(nestedSource, "雨.txt"), "unicode payload");
        var destination = Path.Combine(temporary.Path, "created-from-wpf.zip");
        var workerPath = Path.Combine(AppContext.BaseDirectory, "RainArchives.Worker.exe");
        Assert.IsTrue(File.Exists(workerPath), "The integration output does not contain the packaged worker.");
        var dialogs = new RecordingDialogService();

        await using (var viewModel = new MainWindowViewModel(
                         dialogs,
                         async cancellationToken => await ArchiveClient.StartAsync(
                                 workerPath,
                                 cancellationToken: cancellationToken)
                             .ConfigureAwait(false)))
        {
            await viewModel.CreateArchiveAsync(new ArchiveCreationSelection(
                destination,
                [new ArchiveCreationFile(explicitSource, "README.txt")],
                [new ArchiveCreationDirectory(directorySource, "assets")],
                ArchiveCompressionPreset.Balanced));

            Assert.IsTrue(
                File.Exists(destination),
                $"Creation did not publish the destination. Status='{viewModel.StatusText}', "
                + $"phase='{viewModel.ActiveActivity.Phase}', dialog='{dialogs.ErrorTitle}: {dialogs.ErrorMessage}'.");
            Assert.IsTrue(viewModel.HasArchive);
            Assert.AreEqual("ZIP archive created and opened.", viewModel.StatusText);
            Assert.AreEqual(OperationPhase.Completed, viewModel.ActiveActivity.Phase);
            Assert.AreEqual(ArchivePropertiesState.Ready, viewModel.Properties.State);
            Assert.AreEqual(
                "ZIP",
                viewModel.Properties.ArchiveItems.Single(item => item.Name == "Format").Value);
            CollectionAssert.AreEquivalent(
                new[] { "assets", "README.txt" },
                viewModel.Entries.Select(entry => entry.Name).ToArray());
            await viewModel.ActivateEntryAsync(
                viewModel.Entries.Single(entry => entry.Name == "README.txt"));
            Assert.IsTrue(viewModel.IsPreviewVisible);
            Assert.IsFalse(viewModel.IsPreviewLoading);
            Assert.AreEqual("README.txt", viewModel.PreviewTitle);
            Assert.AreEqual("Rain Archives WPF creation route", viewModel.PreviewText);
            Assert.AreEqual(SafePreviewState.Text, viewModel.Preview.State);
            StringAssert.Contains(viewModel.PreviewDetails, "Read-only text");
            StringAssert.Contains(viewModel.PreviewDetails, "UTF-8");
            await viewModel.ClosePreviewAsync();
            Assert.IsFalse(viewModel.IsPreviewVisible);
            Assert.IsNull(dialogs.ErrorTitle, dialogs.ErrorMessage);
            CollectionAssert.AreEquivalent(
                new[] { "README.txt", "assets-source", "created-from-wpf.zip" },
                Directory.GetFileSystemEntries(temporary.Path)
                    .Select(Path.GetFileName)
                    .ToArray());
        }

        using var archive = ZipFile.OpenRead(destination);
        CollectionAssert.AreEquivalent(
            new[]
            {
                "README.txt",
                "assets/",
                "assets/empty/",
                "assets/nested/",
                "assets/nested/雨.txt",
            },
            archive.Entries.Select(entry => entry.FullName).ToArray());
        var unicodeEntry = archive.GetEntry("assets/nested/雨.txt");
        Assert.IsNotNull(unicodeEntry);
        using var reader = new StreamReader(unicodeEntry.Open());
        Assert.AreEqual("unicode payload", await reader.ReadToEndAsync());
    }

    [TestMethod]
    public async Task ViewModelPreservesExistingDestinationAndSurfacesWorkerRefusal()
    {
        using var temporary = new TemporaryDirectory();
        var source = Path.Combine(temporary.Path, "source.txt");
        var destination = Path.Combine(temporary.Path, "existing.zip");
        await File.WriteAllTextAsync(source, "new archive payload");
        await File.WriteAllTextAsync(destination, "existing destination sentinel");
        var workerPath = Path.Combine(AppContext.BaseDirectory, "RainArchives.Worker.exe");
        Assert.IsTrue(File.Exists(workerPath));
        var dialogs = new RecordingDialogService();

        await using (var viewModel = new MainWindowViewModel(
                         dialogs,
                         async cancellationToken => await ArchiveClient.StartAsync(
                                 workerPath,
                                 cancellationToken: cancellationToken)
                             .ConfigureAwait(false)))
        {
            await viewModel.CreateArchiveAsync(new ArchiveCreationSelection(
                destination,
                [new ArchiveCreationFile(source, "source.txt")],
                [],
                ArchiveCompressionPreset.Balanced));

            Assert.AreEqual(OperationPhase.Failed, viewModel.ActiveActivity.Phase);
            Assert.IsFalse(viewModel.HasArchive);
            Assert.AreEqual("Archive operation failed", dialogs.ErrorTitle);
            Assert.AreEqual(dialogs.ErrorMessage, viewModel.StatusText);
            Assert.IsFalse(string.IsNullOrWhiteSpace(dialogs.ErrorMessage));
            Assert.AreEqual("existing destination sentinel", await File.ReadAllTextAsync(destination));
            CollectionAssert.AreEquivalent(
                new[] { "source.txt", "existing.zip" },
                Directory.GetFileSystemEntries(temporary.Path)
                    .Select(Path.GetFileName)
                    .ToArray());
        }
    }

    [TestMethod]
    public async Task ViewModelSurfacesWorkerCollisionRefusalWithoutPublishingOutput()
    {
        using var temporary = new TemporaryDirectory();
        var firstDirectory = Path.Combine(temporary.Path, "first");
        var secondDirectory = Path.Combine(temporary.Path, "second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var firstSource = Path.Combine(firstDirectory, "duplicate.txt");
        var secondSource = Path.Combine(secondDirectory, "DUPLICATE.txt");
        await File.WriteAllTextAsync(firstSource, "first payload");
        await File.WriteAllTextAsync(secondSource, "second payload");
        var destination = Path.Combine(temporary.Path, "collision.zip");
        var workerPath = Path.Combine(AppContext.BaseDirectory, "RainArchives.Worker.exe");
        Assert.IsTrue(File.Exists(workerPath));
        var dialogs = new RecordingDialogService();

        await using (var viewModel = new MainWindowViewModel(
                         dialogs,
                         async cancellationToken => await ArchiveClient.StartAsync(
                                 workerPath,
                                 cancellationToken: cancellationToken)
                             .ConfigureAwait(false)))
        {
            await viewModel.CreateArchiveAsync(new ArchiveCreationSelection(
                destination,
                [
                    new ArchiveCreationFile(firstSource, "duplicate.txt"),
                    new ArchiveCreationFile(secondSource, "DUPLICATE.txt"),
                ],
                [],
                ArchiveCompressionPreset.Balanced));

            Assert.AreEqual(OperationPhase.Failed, viewModel.ActiveActivity.Phase);
            Assert.IsFalse(File.Exists(destination));
            Assert.IsFalse(viewModel.HasArchive);
            Assert.AreEqual("Archive operation failed", dialogs.ErrorTitle);
            Assert.IsFalse(string.IsNullOrWhiteSpace(dialogs.ErrorMessage));
            Assert.HasCount(
                0,
                Directory.GetFiles(
                    temporary.Path,
                    ".rainarchives-*.zip.staging",
                    SearchOption.TopDirectoryOnly));
        }
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task ViewModelRebuildsMixedCopyThroughPackagedWorkerAndReopensOnlyTheResult()
    {
        using var temporary = new TemporaryDirectory();
        var source = Path.Combine(temporary.Path, "source.zip");
        var replacement = Path.Combine(temporary.Path, "replacement.txt");
        var addition = Path.Combine(temporary.Path, "addition.txt");
        var destination = Path.Combine(temporary.Path, "rebuilt-from-wpf.zip");
        using (var stream = new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            _ = archive.CreateEntry("empty/", CompressionLevel.NoCompression);
            WriteZipEntry(archive, "keep.txt", "retained payload");
            WriteZipEntry(archive, "replace.txt", "old replacement");
            WriteZipEntry(archive, "delete.txt", "delete me");
        }

        await File.WriteAllTextAsync(replacement, "new replacement");
        await File.WriteAllTextAsync(addition, "雨 addition");
        var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var workerPath = Path.Combine(AppContext.BaseDirectory, "RainArchives.Worker.exe");
        Assert.IsTrue(File.Exists(workerPath));
        var dialogs = new RecordingDialogService();

        await using (var viewModel = new MainWindowViewModel(
                         dialogs,
                         async cancellationToken => await ArchiveClient.StartAsync(
                                 workerPath,
                                 cancellationToken: cancellationToken)
                             .ConfigureAwait(false)))
        {
            await viewModel.OpenArchivePathAsync(source);
            Assert.IsTrue(viewModel.CanRebuildCopy, viewModel.RebuildCopyHelpText);
            var deletion = viewModel.Entries.Single(entry => entry.RawPath == "delete.txt");
            Assert.IsNotNull(deletion.Descriptor.NormalizedPath);

            await viewModel.RebuildArchiveAsync(new ArchiveRebuildCopySelection(
                destination,
                [
                    new ArchiveEntryKey(
                        deletion.Descriptor.NormalizedPath,
                        deletion.Descriptor.DuplicateOrdinal),
                ],
                [
                    new ArchiveRebuildCopyFile(replacement, "REPLACE.TXT"),
                    new ArchiveRebuildCopyFile(addition, "Unicode/雨.txt"),
                ],
                ArchiveCompressionPreset.Maximum));

            Assert.IsTrue(
                File.Exists(destination),
                $"Rebuild did not publish the destination. Status='{viewModel.StatusText}', "
                + $"phase='{viewModel.ActiveActivity.Phase}', dialog='{dialogs.ErrorTitle}: {dialogs.ErrorMessage}'.");
            Assert.AreEqual(OperationPhase.CompletedWithWarnings, viewModel.ActiveActivity.Phase);
            Assert.AreEqual(
                "ZIP rebuild copy completed and opened. Warnings were reported.",
                viewModel.StatusText);
            Assert.AreEqual(Path.GetFileName(destination), viewModel.ArchiveTitle);
            Assert.IsTrue(viewModel.CanRebuildCopy);
            CollectionAssert.AreEquivalent(
                new[] { "empty", "keep.txt", "replace.txt", "Unicode" },
                viewModel.Entries.Select(entry => entry.Name).ToArray());
            Assert.IsNull(dialogs.ErrorTitle, dialogs.ErrorMessage);
        }

        CollectionAssert.AreEqual(
            sourceHash,
            SHA256.HashData(await File.ReadAllBytesAsync(source)));
        using (var archive = ZipFile.OpenRead(destination))
        {
            CollectionAssert.AreEquivalent(
                new[] { "empty/", "keep.txt", "replace.txt", "Unicode/雨.txt" },
                archive.Entries.Select(entry => entry.FullName).ToArray());
            Assert.IsNull(archive.GetEntry("delete.txt"));
            Assert.AreEqual("retained payload", await ReadZipEntryAsync(archive, "keep.txt"));
            Assert.AreEqual("new replacement", await ReadZipEntryAsync(archive, "replace.txt"));
            Assert.AreEqual("雨 addition", await ReadZipEntryAsync(archive, "Unicode/雨.txt"));
        }

        Assert.HasCount(
            0,
            Directory.GetFiles(
                temporary.Path,
                ".rainarchives-*.zip.staging",
                SearchOption.TopDirectoryOnly));
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task ViewModelAppliesWorkerReviewedTreePlanThroughPackagedWorker()
    {
        using var temporary = new TemporaryDirectory();
        var source = Path.Combine(temporary.Path, "tree-source.zip");
        var destination = Path.Combine(temporary.Path, "tree-rebuilt-from-wpf.zip");
        var folderSource = Path.Combine(temporary.Path, "folder-source");
        Directory.CreateDirectory(Path.Combine(folderSource, "empty"));
        await File.WriteAllTextAsync(Path.Combine(folderSource, "雨.txt"), "mapped folder");
        using (var stream = new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            _ = archive.CreateEntry("tree/", CompressionLevel.NoCompression);
            WriteZipEntry(archive, "tree/keep.txt", "kept");
            _ = archive.CreateEntry("delete-tree/", CompressionLevel.NoCompression);
            WriteZipEntry(archive, "delete-tree/remove.txt", "removed");
        }

        var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var workerPath = Path.Combine(AppContext.BaseDirectory, "RainArchives.Worker.exe");
        string moveRevision;
        string deleteRevision;
        await using (var planner = await ArchiveClient.StartAsync(workerPath))
        {
            var opened = await planner.OpenArchiveAsync(source);
            var plan = await planner.GetZipRebuildTreePlanAsync(opened.Archive.SessionId);
            moveRevision = plan.Directories.Single(node => node.NormalizedPath == "tree").SubtreeRevision;
            deleteRevision = plan.Directories.Single(node => node.NormalizedPath == "delete-tree").SubtreeRevision;
            var folderReview = await planner.GetZipRebuildSourceFolderReviewAsync(
                opened.Archive.SessionId,
                [new ArchiveCreationDirectory(folderSource, "Imported/")]);
            Assert.AreEqual("Imported", folderReview.Directories.Single().ArchiveRoot);
            Assert.AreEqual(2, folderReview.Directories.Single().DescendantCount);
            Assert.AreEqual(1, folderReview.Directories.Single().FileCount);
            Assert.IsTrue((await planner.CloseArchiveAsync(opened.Archive.SessionId)).Closed);
        }

        var dialogs = new RecordingDialogService();
        await using (var viewModel = new MainWindowViewModel(
                         dialogs,
                         async cancellationToken => await ArchiveClient.StartAsync(
                                 workerPath,
                                 cancellationToken: cancellationToken)
                             .ConfigureAwait(false)))
        {
            await viewModel.OpenArchivePathAsync(source);
            await viewModel.RebuildArchiveAsync(new ArchiveRebuildCopySelection(
                destination,
                [],
                [],
                ArchiveCompressionPreset.Balanced,
                [],
                [new ArchiveCreationDirectory(folderSource, "Imported")],
                [
                    new ArchiveRebuildTreeAction(
                        "tree",
                        moveRevision,
                        ArchiveRebuildTreeActionKind.Move,
                        "Moved"),
                    new ArchiveRebuildTreeAction(
                        "delete-tree",
                        deleteRevision,
                        ArchiveRebuildTreeActionKind.Delete),
                ]));

            Assert.IsTrue(File.Exists(destination), dialogs.ErrorMessage);
            Assert.AreEqual(OperationPhase.CompletedWithWarnings, viewModel.ActiveActivity.Phase);
            Assert.AreEqual(
                "ZIP tree edit completed and opened. Warnings were reported.",
                viewModel.StatusText);
            CollectionAssert.AreEquivalent(
                new[] { "Imported", "Moved" },
                viewModel.Entries.Select(entry => entry.Name).ToArray());
        }

        CollectionAssert.AreEqual(sourceHash, SHA256.HashData(await File.ReadAllBytesAsync(source)));
        using var output = ZipFile.OpenRead(destination);
        CollectionAssert.AreEqual(
            new[] { "Imported/", "Imported/empty/", "Imported/雨.txt", "Moved/", "Moved/keep.txt" },
            output.Entries.Select(entry => entry.FullName).ToArray());
        Assert.IsNull(output.GetEntry("delete-tree/remove.txt"));
    }

    [TestMethod]
    public async Task ViewModelRetriesEncryptedTextPreviewThroughThePackagedWorker()
    {
        var workerPath = Path.Combine(AppContext.BaseDirectory, "RainArchives.Worker.exe");
        var archivePath = Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            "corpus",
            "archives",
            "zip",
            "aes256.zip");
        var expectedText = await File.ReadAllTextAsync(Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            "corpus",
            "source",
            "alpha.txt"));
        Assert.IsTrue(File.Exists(workerPath));
        Assert.IsTrue(File.Exists(archivePath));
        var wrongPassword = "wrong-fixture-password".ToCharArray();
        var correctPassword = FixturePassword.ToCharArray();
        var dialogs = new RecordingDialogService();
        dialogs.PasswordsToReturn.Enqueue(wrongPassword);
        dialogs.PasswordsToReturn.Enqueue(correctPassword);

        await using var viewModel = new MainWindowViewModel(
            dialogs,
            async cancellationToken => await ArchiveClient.StartAsync(
                    workerPath,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false));
        await viewModel.OpenArchivePathAsync(archivePath);
        Assert.IsTrue(viewModel.HasArchive, dialogs.ErrorMessage);
        var entry = viewModel.Entries.Single(item => item.Name == "alpha.txt");

        await viewModel.ActivateEntryAsync(entry);
        Assert.IsTrue(viewModel.IsPreviewVisible);
        Assert.AreEqual(SafePreviewState.Error, viewModel.Preview.State);
        StringAssert.Contains(viewModel.StatusText.ToLowerInvariant(), "password");
        Assert.IsFalse(viewModel.StatusText.Contains("wrong-fixture-password", StringComparison.Ordinal));

        await viewModel.ActivateEntryAsync(entry);
        Assert.IsTrue(viewModel.IsPreviewVisible, dialogs.ErrorMessage);
        Assert.AreEqual(expectedText, viewModel.PreviewText);
        Assert.AreEqual(2, dialogs.PasswordRequestCalls);
        Assert.IsTrue(wrongPassword.All(character => character == '\0'));
        Assert.IsTrue(correctPassword.All(character => character == '\0'));

        await viewModel.ClosePreviewAsync();
        await viewModel.ActivateEntryAsync(entry);
        Assert.IsTrue(viewModel.IsPreviewVisible);
        Assert.AreEqual(expectedText, viewModel.PreviewText);
        Assert.AreEqual(2, dialogs.PasswordRequestCalls);
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task SafePreviewClassifiesTextBinaryAndOversizeThroughRealWorkerAndCleansCache()
    {
        using var temporary = new TemporaryDirectory();
        var source = Path.Combine(temporary.Path, "preview-states.zip");
        var workerTemporaryRoot = Path.Combine(temporary.Path, "worker-temp");
        var previewCache = Path.Combine(temporary.Path, "preview-cache");
        Directory.CreateDirectory(workerTemporaryRoot);
        Directory.CreateDirectory(previewCache);
        using (var stream = new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteZipEntry(archive, "text.txt", "Unicode 雨 text");
            var binary = archive.CreateEntry("binary.bin", CompressionLevel.NoCompression);
            await using (var output = binary.Open())
            {
                await output.WriteAsync(new byte[] { 0x41, 0x00, 0x42 });
            }

            var large = archive.CreateEntry("large.txt", CompressionLevel.NoCompression);
            await using (var output = large.Open())
            {
                var bytes = Enumerable.Repeat(
                        (byte)'x',
                        TextEntryPreviewService.MaximumTextPreviewBytes + 1)
                    .ToArray();
                await output.WriteAsync(bytes);
            }
        }

        string sourceHash;
        await using (var sourceStream = new FileStream(
                         source,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read))
        {
            sourceHash = Convert.ToHexString(await SHA256.HashDataAsync(sourceStream));
        }
        var workerPath = Path.Combine(AppContext.BaseDirectory, "RainArchives.Worker.exe");
        var dialogs = new RecordingDialogService();
        await using (var viewModel = new MainWindowViewModel(
                         dialogs,
                         async cancellationToken => await ArchiveClient.StartWithTemporaryRootAsync(
                                 workerPath,
                                 workerTemporaryRoot,
                                 cancellationToken)
                             .ConfigureAwait(false),
                         client => new TextEntryPreviewService(client, previewCache)))
        {
            await viewModel.OpenArchivePathAsync(source);
            Assert.AreEqual(ArchivePropertiesState.Ready, viewModel.Properties.State);

            var text = viewModel.Entries.Single(entry => entry.Name == "text.txt");
            viewModel.Browser.SelectedEntry = text;
            await viewModel.ActivateEntryAsync(text);
            Assert.AreEqual(SafePreviewState.Text, viewModel.Preview.State);
            Assert.AreEqual("Unicode 雨 text", viewModel.Preview.Text);
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
            Assert.AreEqual(string.Empty, viewModel.Preview.Text);
            await viewModel.ClosePreviewAsync();
            Assert.IsNull(dialogs.ErrorTitle, dialogs.ErrorMessage);
        }

        await using (var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.AreEqual(sourceHash, Convert.ToHexString(await SHA256.HashDataAsync(sourceStream)));
        }

        Assert.HasCount(0, Directory.GetFiles(workerTemporaryRoot, "*", SearchOption.AllDirectories));
        Assert.HasCount(0, Directory.GetFiles(previewCache, "*", SearchOption.AllDirectories));
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task ArchiveBrowserNavigatesUnicodeDuplicatesAndRestoresExactIdentityThroughRealWorker()
    {
        using var temporary = new TemporaryDirectory();
        var source = Path.Combine(temporary.Path, "browser-source.zip");
        var operationSource = Path.Combine(temporary.Path, "operation-source.zip");
        var extraction = Path.Combine(temporary.Path, "extracted");
        var workerTemporaryRoot = Path.Combine(temporary.Path, "worker-temp");
        var previewCache = Path.Combine(temporary.Path, "preview-cache");
        Directory.CreateDirectory(workerTemporaryRoot);
        Directory.CreateDirectory(previewCache);
        using (var stream = new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            _ = archive.CreateEntry("folder/", CompressionLevel.NoCompression);
            WriteZipEntry(archive, "folder/雨.txt", "first duplicate");
            WriteZipEntry(archive, "folder/雨.txt", "second duplicate");
            _ = archive.CreateEntry("folder/sub/", CompressionLevel.NoCompression);
            WriteZipEntry(archive, "folder/sub/deep.txt", "deep payload");
            WriteZipEntry(archive, "root.txt", "root payload");
        }

        using (var stream = new FileStream(operationSource, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteZipEntry(archive, "root.txt", "root payload");
        }

        var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var operationSourceHash = SHA256.HashData(await File.ReadAllBytesAsync(operationSource));
        var workerPath = Path.Combine(AppContext.BaseDirectory, "RainArchives.Worker.exe");
        Assert.IsTrue(File.Exists(workerPath));
        var dialogs = new RecordingDialogService
        {
            ExtractionDestination = extraction,
        };

        await using (var viewModel = new MainWindowViewModel(
                         dialogs,
                         async cancellationToken => await ArchiveClient.StartWithTemporaryRootAsync(
                                 workerPath,
                                 workerTemporaryRoot,
                                 cancellationToken)
                             .ConfigureAwait(false),
                         client => new TextEntryPreviewService(client, previewCache)))
        {
            await viewModel.OpenArchivePathAsync(source);
            Assert.AreEqual(ArchiveBrowserState.Ready, viewModel.Browser.State);
            Assert.IsTrue(viewModel.Browser.PersistentKeysEnabled);
            CollectionAssert.AreEqual(
                new[] { "folder", "root.txt" },
                viewModel.Entries.Select(entry => entry.Name).ToArray());

            var folder = viewModel.Entries.Single(entry => entry.Name == "folder");
            await viewModel.ActivateEntryAsync(folder);
            Assert.AreEqual("folder", viewModel.Browser.CurrentLocationName);
            CollectionAssert.AreEqual(
                new[] { viewModel.ArchiveTitle, "folder" },
                viewModel.Breadcrumbs.Select(item => item.Name).ToArray());

            viewModel.SearchText = "雨";
            Assert.AreEqual(2, viewModel.Browser.TotalResults);
            var duplicates = viewModel.Entries.OrderBy(entry => entry.Descriptor.DuplicateOrdinal).ToArray();
            CollectionAssert.AreEqual(new[] { 0, 1 }, duplicates.Select(entry => entry.Descriptor.DuplicateOrdinal).ToArray());
            viewModel.Browser.SelectedEntry = duplicates[1];
            var selectedKey = viewModel.Browser.SelectedPersistentKey;
            Assert.IsNotNull(selectedKey);
            Assert.AreEqual(
                "#2",
                viewModel.Properties.EntryItems
                    .Single(item => item.Name == "Duplicate occurrence").Value);

            await viewModel.ActivateEntryAsync(duplicates[1]);
            Assert.IsTrue(viewModel.IsPreviewVisible, dialogs.ErrorMessage);
            Assert.AreEqual("second duplicate", viewModel.PreviewText);
            await viewModel.ClosePreviewAsync();

            await viewModel.OpenArchivePathAsync(source);
            Assert.AreEqual("folder", viewModel.Browser.CurrentLocationName);
            Assert.AreEqual("雨", viewModel.SearchText);
            Assert.AreEqual(selectedKey, viewModel.Browser.SelectedPersistentKey);
            Assert.AreEqual(1, viewModel.Browser.SelectedEntry?.Descriptor.DuplicateOrdinal);
            Assert.AreEqual(
                "#2",
                viewModel.Properties.EntryItems
                    .Single(item => item.Name == "Duplicate occurrence").Value);

            viewModel.BackCommand.Execute(null);
            Assert.IsNull(viewModel.CurrentFolderId);
            viewModel.ForwardCommand.Execute(null);
            Assert.AreEqual("folder", viewModel.Browser.CurrentLocationName);

            await viewModel.OpenArchivePathAsync(operationSource);
            viewModel.SearchText = "root";
            var selected = viewModel.Entries.Single(entry => entry.Name == "root.txt");
            await viewModel.ExtractAsync([selected]);
            Assert.AreEqual(OperationPhase.Completed, viewModel.ActiveActivity.Phase);
            Assert.AreEqual(
                "root payload",
                await File.ReadAllTextAsync(Path.Combine(extraction, "root.txt")));

            viewModel.TestCommand.Execute(null);
            await WaitForTerminalActivityAsync(viewModel);
            Assert.AreEqual(OperationPhase.Completed, viewModel.ActiveActivity.Phase);
            Assert.AreEqual(ArchiveBrowserState.Ready, viewModel.Browser.State);
            CollectionAssert.AreEqual(
                sourceHash,
                SHA256.HashData(await File.ReadAllBytesAsync(source)));
            CollectionAssert.AreEqual(
                operationSourceHash,
                SHA256.HashData(await File.ReadAllBytesAsync(operationSource)));
            Assert.IsFalse(viewModel.ArchiveDetails.Contains("RainArchives.Providers", StringComparison.Ordinal));
        }

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
    public async Task ContextAuthorityRunsExistingCommandsThroughRealWorkerAndPreservesSource()
    {
        using var temporary = new TemporaryDirectory();
        var source = Path.Combine(temporary.Path, "context-source.zip");
        var extraction = Path.Combine(temporary.Path, "context-extracted");
        var workerTemporaryRoot = Path.Combine(temporary.Path, "worker-temp");
        var previewCache = Path.Combine(temporary.Path, "preview-cache");
        Directory.CreateDirectory(workerTemporaryRoot);
        Directory.CreateDirectory(previewCache);
        using (var stream = new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            _ = archive.CreateEntry("folder/", CompressionLevel.NoCompression);
            WriteZipEntry(archive, "folder/雨.txt", "context payload");
            WriteZipEntry(archive, "root.txt", "root extraction payload");
        }

        var sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var dialogs = new RecordingDialogService
        {
            ExtractionDestination = extraction,
        };
        var workerPath = Path.Combine(AppContext.BaseDirectory, "RainArchives.Worker.exe");
        await using (var viewModel = new MainWindowViewModel(
                         dialogs,
                         async cancellationToken => await ArchiveClient.StartWithTemporaryRootAsync(
                                 workerPath,
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
            await WaitUntilAsync(() => viewModel.CurrentFolderId == folder.EntryId);

            var file = viewModel.Entries.Single();
            viewModel.Browser.SelectedEntry = file;
            Assert.IsFalse(viewModel.CommandContext.CanNavigate);
            Assert.IsTrue(viewModel.CommandContext.CanPreview);
            Assert.IsTrue(viewModel.CommandContext.CanShowProperties);
            Assert.IsTrue(viewModel.CommandContext.CanTestIntegrity);
            Assert.IsTrue(viewModel.CommandContext.CanExtractSelected);

            viewModel.PreviewSelectionCommand.Execute(null);
            await WaitUntilAsync(() => viewModel.Preview.State == SafePreviewState.Text);
            Assert.AreEqual("context payload", viewModel.Preview.Text);

            viewModel.ShowPropertiesCommand.Execute(null);
            await Task.Yield();
            Assert.AreEqual(0, viewModel.InspectorSelectedTabIndex);
            Assert.IsTrue(viewModel.Properties.HasSelectedEntry);

            viewModel.TestCommand.Execute(null);
            await WaitForTerminalActivityAsync(viewModel);
            Assert.AreEqual(OperationPhase.Completed, viewModel.ActiveActivity.Phase);

            viewModel.RootCommand.Execute(null);
            await WaitUntilAsync(() => viewModel.CurrentFolderId is null);
            var rootFile = viewModel.Entries.Single(entry => entry.Name == "root.txt");
            viewModel.Browser.SelectedEntry = rootFile;
            viewModel.ExtractSelectionCommand.Execute(null);
            await WaitForTerminalActivityAsync(viewModel);
            Assert.AreEqual(OperationPhase.Completed, viewModel.ActiveActivity.Phase, dialogs.ErrorMessage);
            Assert.AreEqual(
                "root extraction payload",
                await File.ReadAllTextAsync(Path.Combine(extraction, "root.txt")));
        }

        CollectionAssert.AreEqual(sourceHash, SHA256.HashData(await File.ReadAllBytesAsync(source)));
        Assert.HasCount(0, Directory.GetFiles(workerTemporaryRoot, "*", SearchOption.AllDirectories));
        Assert.HasCount(0, Directory.GetFiles(previewCache, "*", SearchOption.AllDirectories));
        Assert.HasCount(
            0,
            Directory.GetFiles(
                temporary.Path,
                ".rainarchives-*.staging",
                SearchOption.AllDirectories));
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail("The WPF production model did not reach the expected state.");
            }

            await Task.Delay(10);
        }
    }

    private static async Task WaitForTerminalActivityAsync(MainWindowViewModel viewModel)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (viewModel.ActiveActivity.OperationId is null
               || viewModel.ActiveActivity.Phase is not (
                   OperationPhase.Completed
                   or OperationPhase.CompletedWithWarnings
                   or OperationPhase.Cancelled
                   or OperationPhase.Failed)
               || viewModel.IsBusy)
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail("The real Worker operation did not settle before the bounded deadline.");
            }

            await Task.Delay(20);
        }
    }

    private static void WriteZipEntry(ZipArchive archive, string path, string payload)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(payload);
    }

    private static async Task<string> ReadZipEntryAsync(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path);
        Assert.IsNotNull(entry, path);
        using var reader = new StreamReader(entry.Open());
        return await reader.ReadToEndAsync();
    }

    private sealed class RecordingDialogService : IAppDialogService
    {
        public string? ErrorTitle { get; private set; }
        public string? ErrorMessage { get; private set; }
        public Queue<char[]> PasswordsToReturn { get; } = new();
        public int PasswordRequestCalls { get; private set; }
        public string? ExtractionDestination { get; init; }

        public string? ChooseArchive() => null;

        public ArchiveCreationSelection? ChooseArchiveCreation(bool supportsDirectoryCreation) => null;

        public string? ChooseNewExtractionDestination(string archivePath, bool selectedEntriesOnly) =>
            ExtractionDestination;

        public Task<char[]?> RequestPasswordAsync(
            string archiveName,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PasswordRequestCalls++;
            return Task.FromResult<char[]?>(
                PasswordsToReturn.Count > 0 ? PasswordsToReturn.Dequeue() : null);
        }

        public Task ShowArchiveRecoveryAsync(
            IArchiveClient client,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public void ShowError(string title, string message)
        {
            ErrorTitle = title;
            ErrorMessage = message;
        }

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
                $"RainArchives.WpfCreationTests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            var directory = new DirectoryInfo(Path);
            if (!directory.Name.StartsWith("RainArchives.WpfCreationTests.", StringComparison.Ordinal)
                || directory.Parent is null
                || !string.Equals(
                    directory.Parent.FullName.TrimEnd('\\'),
                    System.IO.Path.GetTempPath().TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Refusing to remove an unowned WPF creation test directory.");
            }

            if (directory.Exists)
            {
                directory.Delete(recursive: true);
            }
        }
    }
}
