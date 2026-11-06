using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using RainArchives.Client;
using RainArchives.Contracts;
using RainExplorer.Controls;
using RainExplorer.Models;
using RainExplorer.Services;
using RainExplorer.ViewModels;
using Xunit;

namespace RainExplorer.Git.Tests;

public sealed class ArchiveLocationAdapterTests
{
    [Fact]
    public void PersistentEnvelopeIsStrictBoundedAndContainsNoEphemeralIdentity()
    {
        var location = new ArchiveLocation(
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Rain Explorer Space", "sample.zip")),
            Fingerprint(),
            "folder/雨",
            2);

        Assert.True(ArchiveLocationSerializer.TrySerialize(location, out string target));
        Assert.DoesNotContain("session", target, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("entryId", target, StringComparison.OrdinalIgnoreCase);
        Assert.True(ArchiveLocationSerializer.TryDeserialize(target, out var restored));
        Assert.Equal(location, restored);

        string withUnknownMember = target[..^1] + ",\"unknown\":true}";
        Assert.False(ArchiveLocationSerializer.TryDeserialize(withUnknownMember, out _));
        Assert.False(ArchiveLocationSerializer.TrySerialize(location with
        {
            FolderPath = string.Join('/', Enumerable.Repeat(new string('x', 250), 130)),
        }, out _));
        Assert.False(ArchiveLocationSerializer.TrySerialize(location with
        {
            FolderPath = string.Empty,
            FolderDuplicateOrdinal = 0,
        }, out _));
        Assert.False(ArchiveLocationSerializer.TrySerialize(location with
        {
            FolderPath = "../escape",
        }, out _));
    }

    [Fact]
    public async Task MalformedArchiveTargetIsNeverReinterpretedAsAPhysicalPath()
    {
        int factoryCalls = 0;
        var fake = FakeArchiveClient.Create([]);
        await using var runtime = new ArchiveClientRuntime(_ =>
        {
            factoryCalls++;
            return Task.FromResult(fake.Client);
        });
        await using var adapter = new ArchiveLocationAdapter(runtime);

        await adapter.OpenAsync(ArchiveLocationSerializer.Prefix + "{not-json}");

        Assert.Equal(ArchiveLocationState.Error, adapter.State);
        Assert.Equal(0, factoryCalls);
        Assert.Equal(0, fake.CloseCount);
    }

    [Fact]
    public async Task ShortWorkerPagesBuildImplicitHierarchyPreserveDuplicatesAndCloseExactlyOnce()
    {
        EntryId firstFolder = EntryId.New();
        var entries = new List<ArchiveEntryDescriptor>
        {
            Entry(firstFolder, null, "folder", "folder", ArchiveEntryKind.Directory, duplicate: 0),
            Entry(EntryId.New(), null, "folder", "folder", ArchiveEntryKind.Directory, duplicate: 1),
            Entry(EntryId.New(), firstFolder, "folder/雨.txt", "雨.txt", ArchiveEntryKind.File),
            Entry(EntryId.New(), null, "implicit/deep.txt", "deep.txt", ArchiveEntryKind.File),
        };
        for (int index = 0; index < 520; index++)
            entries.Add(Entry(EntryId.New(), null, $"root-{index:D3}.txt", $"root-{index:D3}.txt", ArchiveEntryKind.File));

        var fake = FakeArchiveClient.Create(entries, pageChunkSize: 73);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var adapter = new ArchiveLocationAdapter(runtime);

        await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "Rain Explorer Space", "sample.zip"));

        Assert.Equal(ArchiveLocationState.Ready, adapter.State);
        Assert.Equal(ArchiveLocationAdapter.MaximumVisibleItems, adapter.Items.Count);
        Assert.True(adapter.CanGoToNextPage);
        Assert.Equal(new[] { 0, 73, 146, 219, 292, 365, 438, 511 }, fake.ListOffsets);
        Assert.Equal(2, adapter.Items.Count(item => item.Name == "folder"));
        Assert.Contains(adapter.Items, item => item.Name == "implicit" && item.IsDirectory);

        adapter.SetSearch("雨", recursive: true);
        Assert.Single(adapter.Items);
        Assert.Equal("雨.txt", adapter.Items[0].Name);
        adapter.SetSearch(string.Empty, recursive: false);
        var folder = adapter.Items.First(item => item.Name == "folder" && item.IsDirectory);
        Assert.True(adapter.Open(folder));
        Assert.Equal("雨.txt", Assert.Single(adapter.Items).Name);
        Assert.Equal(2, adapter.Breadcrumbs.Count);
        Assert.True(adapter.TryGetPersistentTarget(out string target));
        Assert.True(ArchiveLocationSerializer.TryDeserialize(target, out var saved));
        Assert.Equal("folder", saved.FolderPath);
        Assert.True(adapter.GoBack());
        Assert.Single(adapter.Breadcrumbs);
        Assert.True(adapter.GoForward());
        Assert.Equal(2, adapter.Breadcrumbs.Count);
        Assert.True(adapter.GoUp());
        Assert.Single(adapter.Breadcrumbs);
        Assert.True(adapter.GoBack());
        Assert.True(adapter.CanGoForward);
        Assert.True(adapter.Navigate(nodeId: null));
        Assert.False(adapter.CanGoForward);

        await adapter.DisposeAsync();
        Assert.Equal(1, fake.CloseCount);
        Assert.Single(fake.ClosedSessions);
    }

    [Fact]
    public async Task MalformedPagingAndCyclesFailClosedAndCleanTheOpenedSession()
    {
        var malformed = FakeArchiveClient.Create(
            [
                Entry(EntryId.New(), null, "a.txt", "a.txt", ArchiveEntryKind.File),
                Entry(EntryId.New(), null, "b.txt", "b.txt", ArchiveEntryKind.File),
            ],
            pageChunkSize: 1,
            malformedNextOffset: true);
        await using (var runtime = new ArchiveClientRuntime(_ => Task.FromResult(malformed.Client)))
        await using (var adapter = new ArchiveLocationAdapter(runtime))
        {
            await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "bad.zip"));
            Assert.Equal(ArchiveLocationState.Error, adapter.State);
            Assert.Empty(adapter.Items);
            Assert.Equal(1, malformed.CloseCount);
        }

        EntryId left = EntryId.New();
        EntryId right = EntryId.New();
        var cycle = FakeArchiveClient.Create(
        [
            Entry(left, right, "left", "left", ArchiveEntryKind.Directory),
            Entry(right, left, "right", "right", ArchiveEntryKind.Directory),
        ]);
        await using (var runtime = new ArchiveClientRuntime(_ => Task.FromResult(cycle.Client)))
        await using (var adapter = new ArchiveLocationAdapter(runtime))
        {
            await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "cycle.zip"));
            Assert.Equal(ArchiveLocationState.Error, adapter.State);
            Assert.Empty(adapter.Items);
            Assert.Equal(1, cycle.CloseCount);
        }

        var unsafePath = FakeArchiveClient.Create(
            [Entry(EntryId.New(), null, "../escape.txt", "escape.txt", ArchiveEntryKind.File)]);
        await using (var runtime = new ArchiveClientRuntime(_ => Task.FromResult(unsafePath.Client)))
        await using (var adapter = new ArchiveLocationAdapter(runtime))
        {
            await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "unsafe-path.zip"));
            Assert.Equal(ArchiveLocationState.Error, adapter.State);
            Assert.Empty(adapter.Items);
            Assert.Equal(1, unsafePath.CloseCount);
        }

        foreach (var paging in new[]
                 {
                     FakeArchiveClient.Create(
                         [Entry(EntryId.New(), null, "a.txt", "a.txt", ArchiveEntryKind.File)],
                         reportedTotalDelta: 1),
                     FakeArchiveClient.Create(
                         [Entry(EntryId.New(), null, "a.txt", "a.txt", ArchiveEntryKind.File),
                          Entry(EntryId.New(), null, "b.txt", "b.txt", ArchiveEntryKind.File)],
                         pageChunkSize: 1, endListingEarly: true),
                 })
        {
            await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(paging.Client));
            await using var adapter = new ArchiveLocationAdapter(runtime);
            await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "bad-page.zip"));
            Assert.Equal(ArchiveLocationState.Error, adapter.State);
            Assert.Equal(1, paging.CloseCount);
        }
    }

    [Fact]
    public async Task ListingCancellationUsesASeparateCleanupTokenAndClosesTheExactSession()
    {
        var fake = FakeArchiveClient.Create(
            [Entry(EntryId.New(), null, "a.txt", "a.txt", ArchiveEntryKind.File)],
            blockListing: true);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var adapter = new ArchiveLocationAdapter(runtime);
        using var cancellation = new CancellationTokenSource();

        Task opening = adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "cancel.zip"), cancellation.Token);
        await fake.ListingStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await opening;

        Assert.Equal(ArchiveLocationState.Error, adapter.State);
        Assert.Equal(1, fake.CloseCount);
        Assert.Single(fake.ClosedSessions);
    }

    [Fact]
    public async Task FingerprintMismatchRestoresNothingAndMarksLocationStale()
    {
        string source = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "changed.zip"));
        var expected = new ArchiveLocation(source, Fingerprint(prefix: new string('A', 64)), string.Empty, null);
        Assert.True(ArchiveLocationSerializer.TrySerialize(expected, out string target));
        var fake = FakeArchiveClient.Create(
            [Entry(EntryId.New(), null, "a.txt", "a.txt", ArchiveEntryKind.File)],
            fingerprint: Fingerprint(prefix: new string('B', 64)));
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var adapter = new ArchiveLocationAdapter(runtime);

        await adapter.OpenAsync(target);

        Assert.Equal(ArchiveLocationState.Stale, adapter.State);
        Assert.Empty(adapter.Items);
        Assert.False(adapter.TryGetPersistentTarget(out _));
        Assert.Equal(1, fake.CloseCount);
    }

    [Fact]
    public async Task RefreshSourceChangeClosesBothSessionsAndMarksTheLocationStale()
    {
        var fake = FakeArchiveClient.Create(
            [Entry(EntryId.New(), null, "a.txt", "a.txt", ArchiveEntryKind.File)]);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var adapter = new ArchiveLocationAdapter(runtime);
        await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "refresh.zip"));
        fake.CurrentFingerprint = Fingerprint(prefix: new string('B', 64));

        await adapter.RefreshAsync();

        Assert.Equal(ArchiveLocationState.Stale, adapter.State);
        Assert.Empty(adapter.Items);
        Assert.Equal(2, fake.CloseCount);
    }

    [Fact]
    public async Task FailedReplacementBeforePriorClosePreservesTheAuthoritativeTabSession()
    {
        var fake = FakeArchiveClient.Create(
            [Entry(EntryId.New(), null, "keep.txt", "keep.txt", ArchiveEntryKind.File)]);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var adapter = new ArchiveLocationAdapter(runtime);
        await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "first.zip"));
        fake.FailOpen = true;

        await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "second.zip"));

        Assert.Equal(ArchiveLocationState.Ready, adapter.State);
        Assert.Equal("keep.txt", Assert.Single(adapter.Items).Name);
        Assert.Equal(0, fake.CloseCount);
        Assert.EndsWith("first.zip", adapter.SourcePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FailedFirstOpenKeepsSelectedFilenameAndUsesAHelpfulFixedReason()
    {
        var fake = FakeArchiveClient.Create(
            [],
            passwordlessOpenError: ArchiveErrorCategory.InvalidArchive);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var adapter = new ArchiveLocationAdapter(runtime);
        string source = Path.Combine(Path.GetTempPath(), "four-byte-fake.zip");

        await adapter.OpenAsync(source);

        Assert.Equal(ArchiveLocationState.Error, adapter.State);
        Assert.Equal(Path.GetFullPath(source), adapter.SourcePath);
        Assert.Equal("four-byte-fake.zip", adapter.Title);
        Assert.Contains("not a complete, valid archive", adapter.Status, StringComparison.Ordinal);
        Assert.Contains("only named like an archive", adapter.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("provider", adapter.Status, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(adapter.Items);
        Assert.Equal(0, fake.CloseCount);
    }

    [Fact]
    public async Task OverlappingOpenCancelsAndCleansTheOlderGenerationBeforeAdoptingTheNewOne()
    {
        var fake = FakeArchiveClient.Create(
            [Entry(EntryId.New(), null, "new.txt", "new.txt", ArchiveEntryKind.File)],
            blockFirstListing: true);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var adapter = new ArchiveLocationAdapter(runtime);
        string first = Path.Combine(Path.GetTempPath(), "first-overlap.zip");
        string second = Path.Combine(Path.GetTempPath(), "second-overlap.zip");

        Task older = adapter.OpenAsync(first);
        await fake.ListingStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task newer = adapter.OpenAsync(second);
        await Task.WhenAll(older, newer);

        Assert.Equal(ArchiveLocationState.Ready, adapter.State);
        Assert.Equal(Path.GetFullPath(second), adapter.SourcePath);
        Assert.Equal("new.txt", Assert.Single(adapter.Items).Name);
        Assert.Equal(1, fake.CloseCount);
    }

    [Fact]
    public async Task MissingPersistedFolderFailsClosedInsteadOfFallingBackToRoot()
    {
        string source = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "missing-folder.zip"));
        var fake = FakeArchiveClient.Create(
            [Entry(EntryId.New(), null, "real", "real", ArchiveEntryKind.Directory)]);
        var location = new ArchiveLocation(source, Fingerprint(), "missing", 0);
        Assert.True(ArchiveLocationSerializer.TrySerialize(location, out string target));
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var adapter = new ArchiveLocationAdapter(runtime);

        await adapter.OpenAsync(target);

        Assert.Equal(ArchiveLocationState.Error, adapter.State);
        Assert.Empty(adapter.Items);
        Assert.Equal(1, fake.CloseCount);
    }

    [Fact]
    public async Task MissingPersistentKeyFeatureKeepsBrowsingButDisablesRestoreAndDuplicationTarget()
    {
        var entry = Entry(EntryId.New(), null, "folder", "folder", ArchiveEntryKind.Directory) with
        {
            NormalizedPath = null,
            DuplicateOrdinal = 0,
        };
        var fake = FakeArchiveClient.Create([entry], supportsPersistentKeys: false);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var adapter = new ArchiveLocationAdapter(runtime);

        await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "legacy.zip"));

        Assert.Equal(ArchiveLocationState.Ready, adapter.State);
        Assert.Single(adapter.Items);
        Assert.False(adapter.TryGetPersistentTarget(out _));
        Assert.False(adapter.TryGetPersistentTarget(adapter.Items[0], out _));
        Assert.True(adapter.Open(adapter.Items[0]));
        await adapter.RefreshAsync();
        Assert.Equal(1, fake.OpenCount);
        Assert.Equal(0, fake.CloseCount);
        Assert.Contains("stable entry keys", adapter.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OversizedPersistentEnvelopeDoesNotTruncateAndLeavesLocationBrowseableOnly()
    {
        string path = string.Join('/', Enumerable.Repeat(new string('d', 255), 125));
        var fingerprint = Fingerprint(fileIdentity: new string('i', 1000));
        var fake = FakeArchiveClient.Create(
            [Entry(EntryId.New(), null, path, "deep-target", ArchiveEntryKind.Directory)],
            fingerprint: fingerprint);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var adapter = new ArchiveLocationAdapter(runtime);

        await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "large-location.zip"));
        adapter.SetSearch("deep-target", recursive: true);
        var deepFolder = Assert.Single(adapter.Items);
        Assert.True(adapter.Open(deepFolder));

        Assert.Equal(ArchiveLocationState.Empty, adapter.State);
        Assert.False(adapter.TryGetPersistentTarget(out _));
        Assert.True(adapter.IsOpen);
    }

    [Fact]
    public async Task ArchiveTabRowsHaveNoPhysicalPathAndDisablePhysicalCommandAuthority()
    {
        var fake = FakeArchiveClient.Create(
            [Entry(EntryId.New(), null, "folder", "folder", ArchiveEntryKind.Directory)]);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        var tab = new TabViewModel(new FileSystemService(), runtime);

        var location = new ArchiveLocation(
            Path.GetFullPath(Path.Combine(Path.GetTempPath(), "virtual.zip")),
            Fingerprint(),
            string.Empty,
            null);
        Assert.True(ArchiveLocationSerializer.TrySerialize(location, out string target));
        await tab.NavigateAsync(target, pushHistory: true);
        await fake.Opened.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => tab.IsArchiveView && !tab.Busy);

        var row = Assert.Single(tab.Items);
        Assert.True(row.IsArchiveEntry);
        Assert.False(row.HasPhysicalPath);
        Assert.Equal(string.Empty, row.FullPath);
        _ = row.Icon;
        await WaitUntilAsync(() => row.Icon is not null);
        Assert.NotNull(row.Icon);
        Assert.Null(row.GridThumbnail);
        Assert.False(tab.CanUsePhysicalFileCommands);
        Assert.True(tab.TryGetArchiveTarget(row, out string childTarget));
        Assert.True(ArchiveLocationSerializer.IsArchiveTarget(childTarget));

        await tab.DisposeAsync();
        Assert.Equal(1, fake.CloseCount);
    }

    [Fact]
    public async Task ArchiveRootBackReturnsToSourceFolderAndForwardReopensArchive()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "RainExplorer.ArchiveNavigationTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string archive = Path.Combine(root, "sample.zip");
        await File.WriteAllTextAsync(archive, "archive fixture");

        try
        {
            var fake = FakeArchiveClient.Create(
                [Entry(EntryId.New(), null, "folder", "folder", ArchiveEntryKind.Directory)]);
            await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
            var tab = new TabViewModel(new FileSystemService(), runtime);

            await tab.NavigateAsync(root, pushHistory: true);
            FileItem archiveRow = Assert.Single(tab.Items, item =>
                string.Equals(item.FullPath, archive, StringComparison.OrdinalIgnoreCase));
            tab.Open(archiveRow);
            await WaitUntilAsync(() => tab.IsArchiveView && !tab.Busy);

            Assert.True(tab.CanGoBack);
            Assert.Equal(1, fake.OpenCount);
            tab.BackCommand.Execute(null);
            await WaitUntilAsync(() => tab.IsFolderView
                && string.Equals(tab.CurrentPath, root, StringComparison.OrdinalIgnoreCase));

            Assert.True(tab.CanGoForward);
            Assert.Equal(1, fake.CloseCount);
            tab.ForwardCommand.Execute(null);
            await WaitUntilAsync(() => tab.IsArchiveView && !tab.Busy && fake.OpenCount == 2);

            Assert.True(tab.CanGoBack);
            await tab.DisposeAsync();
            Assert.Equal(2, fake.CloseCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DirectArchiveStartSeedsItsSourceFolderAsBackDestination()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "RainExplorer.ArchiveDirectNavigationTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string archive = Path.Combine(root, "sample.zip");
        await File.WriteAllTextAsync(archive, "archive fixture");

        try
        {
            var fake = FakeArchiveClient.Create([]);
            await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
            var tab = new TabViewModel(new FileSystemService(), runtime);

            await tab.NavigateAsync(archive, pushHistory: true);
            await WaitUntilAsync(() => tab.IsArchiveView && !tab.Busy);
            Assert.True(tab.CanGoBack);

            tab.BackCommand.Execute(null);
            await WaitUntilAsync(() => tab.IsFolderView
                && string.Equals(tab.CurrentPath, root, StringComparison.OrdinalIgnoreCase));

            Assert.True(tab.CanGoForward);
            await tab.DisposeAsync();
            Assert.Equal(1, fake.OpenCount);
            Assert.Equal(1, fake.CloseCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ClosingATabIsDrainedBeforePaneShutdownCompletes()
    {
        string source = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "close-race.zip"));
        var fake = FakeArchiveClient.Create(
            [Entry(EntryId.New(), null, "a.txt", "a.txt", ArchiveEntryKind.File)],
            blockClose: true);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        var pane = new PaneViewModel(new FileSystemService(), runtime);
        var location = new ArchiveLocation(source, Fingerprint(), string.Empty, null);
        Assert.True(ArchiveLocationSerializer.TrySerialize(location, out string target));
        TabViewModel tab = pane.NewTab(target);
        await fake.Opened.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => tab.IsArchiveView && !tab.Busy);

        pane.CloseTab(tab);
        await fake.CloseStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task shutdown = pane.DisposeAsync().AsTask();
        await Task.Delay(50);
        Assert.False(shutdown.IsCompleted);
        fake.ReleaseClose.TrySetResult();
        await shutdown;

        Assert.Equal(1, fake.CloseCount);
        Assert.Single(fake.ClosedSessions);
    }

    [Fact]
    public void SynthesizedNodesCannotGrowPastTheTotalVirtualIndexBound()
    {
        Assert.True(ArchiveLocationAdapter.CanAddVirtualNode(
            ArchiveLocationAdapter.MaximumIndexedEntries - 1));
        Assert.False(ArchiveLocationAdapter.CanAddVirtualNode(
            ArchiveLocationAdapter.MaximumIndexedEntries));
    }

    [Fact]
    public async Task ApplicationRuntimeCleanupStillRunsWhenWindowCleanupFaults()
    {
        bool applicationCleanupRan = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MainWindow.DisposeWithFinalCleanupAsync(
                () => Task.FromException(new InvalidOperationException("window cleanup failed")),
                () =>
                {
                    applicationCleanupRan = true;
                    return Task.CompletedTask;
                }));

        Assert.True(applicationCleanupRan);
    }

    [Fact]
    public async Task RealProtocol122WorkerBrowsesTestsAndExtractsZipWithoutResidueOrWorkerLeak()
    {
        string workerPath = Environment.GetEnvironmentVariable(
            "RAINEXPLORER_TEST_WORKER_PATH")
            ?? Path.Combine(AppContext.BaseDirectory, "worker", "RainArchives.Worker.exe");
        Assert.True(File.Exists(workerPath), $"Worker payload missing: {workerPath}");
        var priorWorkerIds = Process.GetProcessesByName("RainArchives.Worker")
            .Select(process => process.Id).ToHashSet();
        string root = Path.Combine(Path.GetTempPath(), "RainExplorer-M6A-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string archivePath = Path.Combine(root, "path with spaces 雨.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("folder/hello.txt").Open()))
            writer.Write("hello");
        string sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archivePath)));
        string destination = Path.Combine(root, "destination with spaces 雨");
        string transferRoot = Path.Combine(root, "transfer-root");
        string existingDestination = Path.Combine(root, "existing destination");
        Directory.CreateDirectory(existingDestination);
        string sentinelPath = Path.Combine(existingDestination, "keep.txt");
        File.WriteAllText(sentinelPath, "keep me");

        try
        {
            var activity = new RecordingActivitySink();
            await using var runtime = new ArchiveClientRuntime(async cancellationToken =>
                await ArchiveClient.StartAsync(workerPath, cancellationToken));
            var transferStorage = new ArchiveTransferStorage(transferRoot);
            await using (var adapter = new ArchiveLocationAdapter(runtime, activity, transferStorage))
            {
                await adapter.OpenAsync(archivePath);
                Assert.Equal(ArchiveLocationState.Ready, adapter.State);
                FileItem folder = Assert.Single(adapter.Items,
                    item => item.Name == "folder" && item.IsDirectory);
                Assert.True(adapter.TryGetPersistentTarget(out string target));
                Assert.True(ArchiveLocationSerializer.TryDeserialize(target, out var location));
                Assert.Equal(archivePath, location.SourcePath);

                Assert.True(adapter.Open(folder));
                FileItem file = Assert.Single(adapter.Items);
                ArchiveEntryPreviewResult preview = await adapter.LoadPreviewAsync(file);
                Assert.Equal(ArchiveEntryPreviewState.Text, preview.State);
                Assert.Equal("hello", preview.Text);
                Assert.Equal(ArchiveEntryPreviewEncoding.Utf8, preview.Encoding);

                ArchiveOperationResult integrity = await adapter.TestIntegrityAsync()
                    .WaitAsync(TimeSpan.FromSeconds(20));
                Assert.Equal(ArchiveOperationOutcome.Completed, integrity.Outcome);
                Assert.Equal(sourceHash,
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archivePath))));

                adapter.UpdateSelection([file]);
                ArchiveOperationResult extraction = await adapter.ExtractSelectionAsync(destination)
                    .WaitAsync(TimeSpan.FromSeconds(20));
                Assert.Equal(ArchiveOperationOutcome.Completed, extraction.Outcome);
                string extractedPath = Path.Combine(destination, "folder", "hello.txt");
                Assert.Equal("hello", File.ReadAllText(extractedPath));
                Assert.Equal(
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(extractedPath))),
                    Convert.ToHexString(SHA256.HashData("hello"u8)));
                Assert.Equal(sourceHash,
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archivePath))));

                ArchiveOperationResult conflict = await adapter.ExtractSelectionAsync(existingDestination);
                Assert.Equal(ArchiveOperationOutcome.DestinationConflict, conflict.Outcome);
                Assert.Equal(ArchiveDestinationConflictReason.ExistingDestination,
                    conflict.DestinationConflictReason);
                Assert.Equal("keep me", File.ReadAllText(sentinelPath));
                Assert.Equal("keep.txt", Path.GetFileName(
                    Assert.Single(Directory.EnumerateFiles(existingDestination))));

                adapter.UpdateSelection([file]);
                Assert.Null(await adapter.BeginArchiveTransferCoordinatorAsync());
                Assert.False(adapter.CanCopyArchiveSelection);
                Assert.Contains("protocol 1.22", adapter.ArchiveCopyDisabledReason);
                Assert.Contains("IDataObjectAsyncCapability", adapter.ArchiveCopyDisabledReason);
                Assert.False(Directory.Exists(transferRoot));
                Assert.Contains(activity.Updates,
                    update => update.Kind == ActivityOperationKind.IntegrityTest
                        && update.Phase == ActivityOperationPhase.Completed);
                Assert.Contains(activity.Updates,
                    update => update.Kind == ActivityOperationKind.Extraction
                        && update.Phase == ActivityOperationPhase.Completed);
                Assert.DoesNotContain(activity.Updates,
                    update => update.Kind == ActivityOperationKind.TransferOut);

                Assert.Equal(
                    [destination, existingDestination, archivePath],
                    Directory.EnumerateFileSystemEntries(root).Order(StringComparer.Ordinal));
                Directory.Delete(destination, recursive: true);
                Directory.Delete(existingDestination, recursive: true);
            }
            await runtime.DisposeAsync();
            Assert.Equal([archivePath], Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
            await Task.Delay(250);
            var newWorkers = Process.GetProcessesByName("RainArchives.Worker")
                .Where(process => !priorWorkerIds.Contains(process.Id)).Select(process => process.Id).ToArray();
            Assert.Empty(newWorkers);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RealProtocol122WorkerOpensRar5DirectoryWithoutOptionalCrcMetadata()
    {
        string workerPath = Environment.GetEnvironmentVariable(
            "RAINEXPLORER_TEST_WORKER_PATH")
            ?? Path.Combine(AppContext.BaseDirectory, "worker", "RainArchives.Worker.exe");
        Assert.True(File.Exists(workerPath), $"Worker payload missing: {workerPath}");
        int launchedWorkerId = 0;
        string root = Path.Combine(
            Path.GetTempPath(),
            "RainExplorer-RarOptionalCrc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string archivePath = Path.Combine(root, "directory without crc.rar");
        File.WriteAllBytes(
            archivePath,
            Convert.FromBase64String(
                "UmFyIRoHAQAzkrXlCgEFBgAFAQGAgAAmZFo1JQIDCwABABCAAAAPZW1wdHktZGlyZWN0b3J5CgMCUgpUpNc83QEdd1ZRAwUEAA=="));
        string archiveHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archivePath)));

        try
        {
            await using var runtime = new ArchiveClientRuntime(async cancellationToken =>
            {
                ArchiveClient client = await ArchiveClient.StartAsync(workerPath, cancellationToken);
                launchedWorkerId = GetWorkerProcessId(client);
                return client;
            });
            await using (var adapter = new ArchiveLocationAdapter(runtime))
            {
                await adapter.OpenAsync(archivePath).WaitAsync(TimeSpan.FromSeconds(20));

                Assert.Equal(ArchiveLocationState.Ready, adapter.State);
                FileItem directory = Assert.Single(adapter.Items);
                Assert.True(directory.IsDirectory);
                Assert.Equal("empty-directory", directory.Name);
                Assert.Equal(archiveHash,
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archivePath))));
            }

            await runtime.DisposeAsync();
            await Task.Delay(250);
            Assert.True(launchedWorkerId > 0);
            Assert.True(HasExited(launchedWorkerId),
                $"The exact Worker launched by the RAR regression is still running: {launchedWorkerId}.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static int GetWorkerProcessId(ArchiveClient client)
    {
        var property = typeof(ArchiveClient).GetProperty(
            "WorkerProcessId",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return Assert.IsType<int>(property?.GetValue(client));
    }

    private static bool HasExited(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    [Fact]
    public async Task SelectionUsesOnlyFreshVisibleMaterialAndDelegatesMaterialDirectoryExpansion()
    {
        EntryId folderId = EntryId.New();
        EntryId childId = EntryId.New();
        EntryId implicitChildId = EntryId.New();
        var fake = FakeArchiveClient.Create(
            [Entry(folderId, null, "folder", "folder", ArchiveEntryKind.Directory),
             Entry(childId, folderId, "folder/child.txt", "child.txt", ArchiveEntryKind.File),
             Entry(implicitChildId, null, "implicit/deep.txt", "deep.txt", ArchiveEntryKind.File)],
            capabilities: ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var adapter = new ArchiveLocationAdapter(runtime);
        await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "selection.zip"));

        FileItem folder = Assert.Single(adapter.Items, item => item.Name == "folder");
        FileItem implicitFolder = Assert.Single(adapter.Items, item => item.Name == "implicit");
        adapter.UpdateSelection([folder, implicitFolder]);
        string destination = Path.Combine(Path.GetTempPath(), "RainExplorer-selection-" + Guid.NewGuid().ToString("N"));

        ArchiveOperationResult result = await adapter.ExtractSelectionAsync(destination);

        Assert.Equal(ArchiveOperationOutcome.Completed, result.Outcome);
        Assert.Equal([folderId, implicitChildId], fake.LastExtractionEntryIds);
        Assert.Equal(1, fake.ExtractionStartCount);
    }

    [Fact]
    public async Task EmptyStaleAndSubstitutedSelectionsAreRefusedBeforeStart()
    {
        var fake = FakeArchiveClient.Create(
            [Entry(EntryId.New(), null, "a.txt", "a.txt", ArchiveEntryKind.File)],
            capabilities: ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var adapter = new ArchiveLocationAdapter(runtime);
        await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "selection-refusal.zip"));
        FileItem current = Assert.Single(adapter.Items);
        string destination = Path.Combine(Path.GetTempPath(), "RainExplorer-refusal-" + Guid.NewGuid().ToString("N"));

        adapter.UpdateSelection([]);
        Assert.False(adapter.CanExtractSelection);
        Assert.Equal(ArchiveOperationOutcome.InvalidSelection,
            (await adapter.ExtractSelectionAsync(destination)).Outcome);
        Assert.Equal(0, fake.ExtractionStartCount);

        adapter.UpdateSelection([new FileItem
        {
            Name = current.Name,
            FullPath = string.Empty,
            IsArchiveEntry = true,
            ArchiveNodeId = "material:" + Guid.NewGuid().ToString("N"),
        }]);
        Assert.Equal(ArchiveOperationOutcome.InvalidSelection,
            (await adapter.ExtractSelectionAsync(destination)).Outcome);
        Assert.Equal(0, fake.ExtractionStartCount);

        fake.CurrentFingerprint = Fingerprint(prefix: new string('B', 64));
        await adapter.RefreshAsync();
        Assert.Equal(ArchiveLocationState.Stale, adapter.State);
        Assert.Equal(ArchiveOperationOutcome.Stale,
            (await adapter.ExtractSelectionAsync(destination)).Outcome);
        Assert.Equal(0, fake.ExtractionStartCount);
    }

    [Fact]
    public async Task CapabilityVolumeAndProtocolDowngradeRefuseOperations()
    {
        foreach (var configuration in new[]
                 {
                     (ArchiveCapability.List, ArchiveVolumeSetKind.Single, false, true),
                     (ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract,
                         ArchiveVolumeSetKind.ConfirmedMultipart, true, true),
                     (ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract,
                         ArchiveVolumeSetKind.Single, false, false),
                 })
        {
            var fake = FakeArchiveClient.Create(
                [Entry(EntryId.New(), null, "a.txt", "a.txt", ArchiveEntryKind.File)],
                capabilities: configuration.Item1,
                volumeSetKind: configuration.Item2,
                isMultipart: configuration.Item3,
                supportsOperationProtocol: configuration.Item4);
            await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
            await using var adapter = new ArchiveLocationAdapter(runtime);
            await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "capability-refusal-" + Guid.NewGuid() + ".zip"));
            FileItem item = Assert.Single(adapter.Items);
            adapter.UpdateSelection([item]);

            Assert.False(adapter.CanTestIntegrity);
            Assert.False(adapter.CanExtractSelection);
            Assert.Equal(ArchiveOperationOutcome.Unsupported,
                (await adapter.TestIntegrityAsync()).Outcome);
            Assert.Equal(ArchiveOperationOutcome.Unsupported,
                (await adapter.ExtractSelectionAsync(Path.Combine(Path.GetTempPath(), "unsupported-" + Guid.NewGuid()))).Outcome);
            Assert.Equal(0, fake.ExtractionStartCount);
            Assert.Equal(0, fake.IntegrityStartCount);
        }
    }

    [Fact]
    public async Task ExistingAndRacedDestinationsNeverStartExtraction()
    {
        var fake = FakeArchiveClient.Create(
            [Entry(EntryId.New(), null, "a.txt", "a.txt", ArchiveEntryKind.File)],
            capabilities: ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var adapter = new ArchiveLocationAdapter(runtime);
        await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "destination-refusal.zip"));
        adapter.UpdateSelection([Assert.Single(adapter.Items)]);

        Assert.Equal(ArchiveOperationOutcome.InvalidDestination,
            (await adapter.ExtractSelectionAsync("relative destination")).Outcome);
        string missingParent = Path.Combine(
            Path.GetTempPath(), "RainExplorer-missing-" + Guid.NewGuid().ToString("N"), "child");
        Assert.Equal(ArchiveOperationOutcome.InvalidDestination,
            (await adapter.ExtractSelectionAsync(missingParent)).Outcome);

        string existing = Path.Combine(Path.GetTempPath(), "RainExplorer-existing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(existing);
        Assert.Equal(ArchiveOperationOutcome.DestinationConflict,
            (await adapter.ExtractSelectionAsync(existing)).Outcome);
        Assert.Equal(0, fake.ExtractionStartCount);

        string raced = Path.Combine(Path.GetTempPath(), "RainExplorer-raced-" + Guid.NewGuid().ToString("N"));
        fake.CreateDestinationOnNewDestinationFeature = raced;
        Assert.Equal(ArchiveOperationOutcome.DestinationConflict,
            (await adapter.ExtractSelectionAsync(raced)).Outcome);
        Assert.Equal(0, fake.ExtractionStartCount);
        Directory.Delete(existing);
        if (Directory.Exists(raced)) Directory.Delete(raced);
    }

    [Fact]
    public async Task EntireArchiveExtractionUsesEmptySelectionAndExistingOperationAuthority()
    {
        var sink = new RecordingActivitySink();
        var fake = FakeArchiveClient.Create(
            [
                Entry(EntryId.New(), null, "folder", "folder", ArchiveEntryKind.Directory),
                Entry(EntryId.New(), null, "folder/a.txt", "a.txt", ArchiveEntryKind.File),
            ],
            capabilities: ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract,
            hasEncryptedEntries: true);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var adapter = new ArchiveLocationAdapter(runtime, sink);
        await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "entire-archive.zip"));

        Assert.True(adapter.CanExtractEntireArchive);
        Assert.False(adapter.CanExtractSelection);
        char[] password = "one-shot".ToCharArray();
        string destination = Path.Combine(
            Path.GetTempPath(),
            "RainExplorer-entire-" + Guid.NewGuid().ToString("N"));
        ArchiveOperationResult result = await adapter.ExtractEntireArchiveAsync(
            destination,
            password);

        Assert.Equal(ArchiveOperationOutcome.Completed, result.Outcome);
        Assert.Empty(fake.LastExtractionEntryIds);
        Assert.Equal(destination, fake.LastExtractionDestination);
        Assert.Single(fake.CreatedSecretIds);
        Assert.Empty(fake.DiscardedSecretIds);
        Assert.All(password, character => Assert.Equal('\0', character));
        Assert.Equal(ActivityOperationPhase.Completed, sink.LastUpdate!.Phase);
    }

    [Fact]
    public async Task HeaderEncryptedPhysicalExtractionReusesPasswordOpenedSessionWithoutSecondSecret()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "RainExplorer-header-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "header archive.data");
        await File.WriteAllBytesAsync(source, [1, 2, 3]);
        try
        {
            var sink = new RecordingActivitySink();
            var fake = FakeArchiveClient.Create(
                [Entry(EntryId.New(), null, "secret.txt", "secret.txt", ArchiveEntryKind.File)],
                capabilities: ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract,
                hasEncryptedEntries: true,
                passwordlessOpenError: ArchiveErrorCategory.PasswordRequired);
            await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
            await using var service = new PhysicalArchiveExtractionService(runtime, sink);
            char[] password = "one-shot-header-password".ToCharArray();
            int promptCount = 0;

            PhysicalArchiveExtractionBatchResult result = await service.ExtractAsync(
                [source],
                _ =>
                {
                    promptCount++;
                    return password;
                });

            Assert.Equal(ArchiveOperationOutcome.Completed, Assert.Single(result.Results).Outcome);
            Assert.Equal(1, promptCount);
            Assert.Equal(2, fake.OpenCount);
            Guid secret = Assert.Single(fake.CreatedSecretIds);
            Assert.Equal(new Guid?[] { null, secret }, fake.OpenPasswordSlots);
            Assert.Empty(fake.DiscardedSecretIds);
            Assert.Equal(1, fake.ExtractionStartCount);
            Assert.Equal(fake.LastOpenedSessionId, fake.LastExtractionSessionId);
            Assert.Null(fake.LastExtractionPasswordSlot);
            Assert.Equal(fake.LastOpenedSessionId, Assert.Single(fake.ClosedSessions));
            Assert.All(password, character => Assert.Equal('\0', character));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task HeaderEncryptedWrongPasswordUsesOneSlotAndNeverRetriesOrExtracts()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "RainExplorer-header-wrong-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "header archive.bin");
        await File.WriteAllBytesAsync(source, [4, 5, 6]);
        try
        {
            var fake = FakeArchiveClient.Create(
                [Entry(EntryId.New(), null, "secret.txt", "secret.txt", ArchiveEntryKind.File)],
                capabilities: ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract,
                hasEncryptedEntries: true,
                passwordlessOpenError: ArchiveErrorCategory.PasswordRequired,
                passwordOpenError: ArchiveErrorCategory.IncorrectPassword);
            await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
            await using var service = new PhysicalArchiveExtractionService(
                runtime,
                new RecordingActivitySink());
            char[] password = "wrong-header-password".ToCharArray();
            int promptCount = 0;

            PhysicalArchiveExtractionBatchResult result = await service.ExtractAsync(
                [source],
                _ =>
                {
                    promptCount++;
                    return password;
                });

            Assert.Equal(ArchiveOperationOutcome.Failed, Assert.Single(result.Results).Outcome);
            Assert.Equal(1, promptCount);
            Assert.Equal(2, fake.OpenCount);
            Guid secret = Assert.Single(fake.CreatedSecretIds);
            Assert.Equal(new Guid?[] { null, secret }, fake.OpenPasswordSlots);
            Assert.Empty(fake.DiscardedSecretIds);
            Assert.Equal(0, fake.ExtractionStartCount);
            Assert.Empty(fake.ClosedSessions);
            Assert.All(password, character => Assert.Equal('\0', character));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task IntegrityTestUsesFreshAuthorityAndSettlesAtTerminal()
    {
        var sink = new RecordingActivitySink();
        var fake = FakeArchiveClient.Create(
            [Entry(EntryId.New(), null, "a.txt", "a.txt", ArchiveEntryKind.File)],
            capabilities: ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var adapter = new ArchiveLocationAdapter(runtime, sink);
        await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "integrity.zip"));

        Assert.True(adapter.CanTestIntegrity);
        ArchiveOperationResult result = await adapter.TestIntegrityAsync();

        Assert.Equal(ArchiveOperationOutcome.Completed, result.Outcome);
        Assert.Equal(1, fake.IntegrityStartCount);
        Assert.Equal(ActivityOperationPhase.Completed, sink.LastUpdate!.Phase);
    }

    [Fact]
    public async Task PasswordSlotIsDiscardedOnlyWhenAdmissionIsRefused()
    {
        string refusedDestination = Path.Combine(
            Path.GetTempPath(), "password-refused-" + Guid.NewGuid().ToString("N"));
        var refused = FakeArchiveClient.Create(
            [Entry(EntryId.New(), null, "secret.txt", "secret.txt", ArchiveEntryKind.File)],
            capabilities: ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract,
            hasEncryptedEntries: true);
        try
        {
            await using (var runtime = new ArchiveClientRuntime(_ => Task.FromResult(refused.Client)))
            await using (var adapter = new ArchiveLocationAdapter(runtime))
            {
                await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "password-refusal.zip"));
                adapter.UpdateSelection([Assert.Single(adapter.Items)]);
                refused.CreateDestinationOnSecretCreation = refusedDestination;
                Assert.Equal(ArchiveOperationOutcome.DestinationConflict,
                    (await adapter.ExtractSelectionAsync(refusedDestination, ['p'])).Outcome);
                Assert.True(Directory.Exists(refusedDestination));
                Assert.Single(refused.CreatedSecretIds);
                Assert.Equal(refused.CreatedSecretIds, refused.DiscardedSecretIds);
                Assert.Equal(0, refused.ExtractionStartCount);
            }
        }
        finally
        {
            if (Directory.Exists(refusedDestination)
                && !Directory.EnumerateFileSystemEntries(refusedDestination).Any())
            {
                Directory.Delete(refusedDestination);
            }
        }
        Assert.False(Directory.Exists(refusedDestination));

        var transferred = FakeArchiveClient.Create(
            [Entry(EntryId.New(), null, "secret.txt", "secret.txt", ArchiveEntryKind.File)],
            capabilities: ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract,
            hasEncryptedEntries: true);
        await using var runtime2 = new ArchiveClientRuntime(_ => Task.FromResult(transferred.Client));
        await using var adapter2 = new ArchiveLocationAdapter(runtime2);
        await adapter2.OpenAsync(Path.Combine(Path.GetTempPath(), "password-transfer.zip"));
        adapter2.UpdateSelection([Assert.Single(adapter2.Items)]);
        Assert.Equal(ArchiveOperationOutcome.Completed,
            (await adapter2.ExtractSelectionAsync(Path.Combine(Path.GetTempPath(), "password-transfer-" + Guid.NewGuid()), ['p'])).Outcome);
        Assert.Single(transferred.CreatedSecretIds);
        Assert.Empty(transferred.DiscardedSecretIds);
        Assert.Equal(transferred.CreatedSecretIds[0], transferred.LastExtractionPasswordSlot);
    }

    [Theory]
    [InlineData(OperationPhase.Running)]
    [InlineData(OperationPhase.Finalizing)]
    public async Task CancellationTargetsExactOperationAndWaitsForTerminal(
        OperationPhase activePhase)
    {
        var sink = new RecordingActivitySink();
        var fake = FakeArchiveClient.Create(
            [Entry(EntryId.New(), null, "a.txt", "a.txt", ArchiveEntryKind.File)],
            capabilities: ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract,
            blockTerminal: true);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var adapter = new ArchiveLocationAdapter(runtime, sink);
        await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "cancel-operation.zip"));
        adapter.UpdateSelection([Assert.Single(adapter.Items)]);
        Task<ArchiveOperationResult> operation = adapter.ExtractSelectionAsync(
            Path.Combine(Path.GetTempPath(), "cancel-operation-" + Guid.NewGuid()));
        await fake.OperationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fake.Emit(new OperationEvent(
            fake.StartedOperationId,
            1,
            OperationEventKind.Progress,
            activePhase));
        Assert.Contains(sink.Updates, update =>
            update.Phase == (activePhase == OperationPhase.Finalizing
                ? ActivityOperationPhase.Finalizing
                : ActivityOperationPhase.Running));

        sink.Handle!.Cancel();
        await fake.CancelCalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(fake.StartedOperationId, fake.CancelledOperationId);
        Assert.False(operation.IsCompleted);
        fake.CompleteTerminal(OperationPhase.Cancelled);
        Assert.Equal(ArchiveOperationOutcome.Cancelled, (await operation).Outcome);
        Assert.Equal(ActivityOperationPhase.Cancelled, sink.LastUpdate!.Phase);
    }

    [Fact]
    public async Task TerminalWarningMapsCurrentItemFromEscapedNodeName()
    {
        EntryId entryId = EntryId.New();
        var sink = new RecordingActivitySink();
        var fake = FakeArchiveClient.Create(
            [Entry(entryId, null, "raw.txt", "shown\nname.txt", ArchiveEntryKind.File)],
            capabilities: ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract,
            blockTerminal: true);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var adapter = new ArchiveLocationAdapter(runtime, sink);
        await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "activity-mapping.zip"));
        adapter.UpdateSelection([Assert.Single(adapter.Items)]);
        Task<ArchiveOperationResult> operation = adapter.ExtractSelectionAsync(
            Path.Combine(Path.GetTempPath(), "activity-mapping-" + Guid.NewGuid()));
        await fake.OperationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        int updatesBeforeForeignEvent = sink.Updates.Count;
        fake.Emit(new OperationEvent(
            OperationId.New(),
            99,
            OperationEventKind.Progress,
            OperationPhase.Finalizing,
            new OperationProgress(99, 99, 99, 99, entryId, "FOREIGN-OPERATION",
                TimeSpan.Zero, null, null)));
        Assert.Equal(updatesBeforeForeignEvent, sink.Updates.Count);

        fake.Emit(new OperationEvent(
            fake.StartedOperationId, 1, OperationEventKind.Progress, OperationPhase.Running,
            new OperationProgress(1, 2, 1, 1, entryId, "RAW-CURRENT-NAME", TimeSpan.Zero, null, null)));
        fake.Emit(new OperationEvent(
            fake.StartedOperationId, 2, OperationEventKind.Warning, OperationPhase.Running,
            Warning: new ArchiveWarning("warning", "warning text", entryId)));
        fake.CompleteTerminal(OperationPhase.CompletedWithWarnings);

        Assert.Equal(ArchiveOperationOutcome.CompletedWithWarnings, (await operation).Outcome);
        Assert.Contains(sink.Updates, update => update.CurrentItem == adapter.Items[0].Name);
        Assert.DoesNotContain(sink.Updates, update => update.CurrentItem == "RAW-CURRENT-NAME");
        Assert.Equal(ActivityOperationPhase.CompletedWithWarnings, sink.LastUpdate!.Phase);
        Assert.True(sink.LastUpdate.WarningCount > 0);
    }

    [Fact]
    public async Task TransferOutActivityKeepsProgressWarningAndTerminalKindsBound()
    {
        EntryId entryId = EntryId.New();
        var sink = new RecordingActivitySink();
        var fake = FakeArchiveClient.Create(
            [Entry(entryId, null, "raw.txt", "shown\nname.txt", ArchiveEntryKind.File)],
            capabilities: ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract,
            blockTerminal: true);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        string temporaryRoot = Path.Combine(
            Path.GetTempPath(), "RainExplorer-TransferActivity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            var storage = new ArchiveTransferStorage(Path.Combine(temporaryRoot, "transfer-root"));
            await using var adapter = new ArchiveLocationAdapter(runtime, sink, storage);
            await adapter.OpenAsync(Path.Combine(temporaryRoot, "activity.zip"));
            adapter.UpdateSelection([Assert.Single(adapter.Items)]);
            ArchiveTransferOwner? owner = await adapter.BeginArchiveTransferCoordinatorAsync();
            if (owner is null)
            {
                Assert.False(ArchiveOleTransferGate.HasWorkerPublicationIdentity);
                Assert.Equal(0, fake.ExtractionStartCount);
                Assert.False(Directory.Exists(storage.TransferRootPath));
                return;
            }
            Task<ArchiveTransferRenderResult> render = owner.RenderAsync();
            await fake.OperationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.NotNull(fake.LastExtractionDestination);
            Directory.CreateDirectory(fake.LastExtractionDestination!);
            await File.WriteAllTextAsync(
                Path.Combine(fake.LastExtractionDestination!, "raw.txt"),
                "0123456789");
            fake.Emit(new OperationEvent(
                fake.StartedOperationId, 1, OperationEventKind.Progress, OperationPhase.Running,
                new OperationProgress(5, 10, 1, 1, entryId, "UNTRUSTED",
                    TimeSpan.FromMilliseconds(1), 5, null)));
            fake.Emit(new OperationEvent(
                fake.StartedOperationId, 2, OperationEventKind.Warning, OperationPhase.Running,
                Warning: new ArchiveWarning("warning", "provider text", entryId)));
            fake.CompleteTerminal(OperationPhase.CompletedWithWarnings);

            ArchiveTransferRenderResult result = await render.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(result.IsReady);
            Assert.Contains(sink.Updates, update =>
                update.Kind == ActivityOperationKind.TransferOut
                && update.Phase == ActivityOperationPhase.Running
                && update.CurrentItem == adapter.Items[0].Name);
            ActivityOperationUpdate terminal = Assert.Single(sink.Updates, update =>
                update.Kind == ActivityOperationKind.TransferOut
                && update.Phase == ActivityOperationPhase.CompletedWithWarnings);
            Assert.True(terminal.WarningCount > 0);
            Assert.Equal(
                ArchiveTransferCleanupOutcome.Removed,
                await owner.ReleaseAsync(ArchiveTransferReleaseReason.DragCompleted));
            Assert.Empty(Directory.EnumerateFileSystemEntries(storage.TransferRootPath));
        }
        finally
        {
            try { Directory.Delete(temporaryRoot, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task TransferOutUnsafeManifestReportsFixedPathFreeFailure()
    {
        var sink = new RecordingActivitySink();
        var fake = FakeArchiveClient.Create(
            [Entry(EntryId.New(), null, "missing.txt", "missing.txt", ArchiveEntryKind.File)],
            capabilities: ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        string temporaryRoot = Path.Combine(
            Path.GetTempPath(), "RainExplorer-TransferUnsafe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            var storage = new ArchiveTransferStorage(Path.Combine(temporaryRoot, "transfer-root"));
            await using var adapter = new ArchiveLocationAdapter(runtime, sink, storage);
            await adapter.OpenAsync(Path.Combine(temporaryRoot, "unsafe.zip"));
            adapter.UpdateSelection([Assert.Single(adapter.Items)]);
            ArchiveTransferOwner? owner = await adapter.BeginArchiveTransferCoordinatorAsync();
            if (owner is null)
            {
                Assert.False(ArchiveOleTransferGate.HasWorkerPublicationIdentity);
                Assert.Equal(0, fake.ExtractionStartCount);
                Assert.False(Directory.Exists(storage.TransferRootPath));
                return;
            }

            ArchiveTransferRenderResult result = await owner.RenderAsync()
                .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(ArchiveTransferOutcome.UnsafeOutput, result.Outcome);
            Assert.Empty(result.FileDropPaths);
            ActivityOperationUpdate terminal = Assert.Single(sink.Updates, update =>
                update.Kind == ActivityOperationKind.TransferOut
                && update.Phase == ActivityOperationPhase.Failed);
            Assert.Equal(ActivityOperationFailure.UnsafeOutput, terminal.Failure);
            Assert.Equal(1, fake.ExtractionStartCount);
            Assert.DoesNotContain(temporaryRoot, result.Status, StringComparison.OrdinalIgnoreCase);
            await owner.ReleaseAsync(ArchiveTransferReleaseReason.TargetException);
            Assert.Empty(Directory.EnumerateFileSystemEntries(storage.TransferRootPath));
        }
        finally
        {
            try { Directory.Delete(temporaryRoot, recursive: true); }
            catch { }
        }
    }

    [Theory]
    [InlineData(OperationPhase.Cancelled)]
    [InlineData(OperationPhase.Failed)]
    public async Task TransferOutNonSuccessWithAbsentDestinationKeepsOriginalOutcome(
        OperationPhase workerPhase)
    {
        var sink = new RecordingActivitySink();
        var fake = FakeArchiveClient.Create(
            [Entry(EntryId.New(), null, "selected.txt", "selected.txt", ArchiveEntryKind.File)],
            capabilities: ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract,
            blockTerminal: true);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        string temporaryRoot = Path.Combine(
            Path.GetTempPath(), "RainExplorer-TransferAbsent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            var storage = new ArchiveTransferStorage(Path.Combine(temporaryRoot, "transfer-root"));
            await using var adapter = new ArchiveLocationAdapter(runtime, sink, storage);
            await adapter.OpenAsync(Path.Combine(temporaryRoot, "absent.zip"));
            adapter.UpdateSelection([Assert.Single(adapter.Items)]);
            ArchiveTransferOwner? owner = await adapter.BeginArchiveTransferCoordinatorAsync();
            if (owner is null)
            {
                Assert.False(ArchiveOleTransferGate.HasWorkerPublicationIdentity);
                Assert.Equal(0, fake.ExtractionStartCount);
                Assert.False(Directory.Exists(storage.TransferRootPath));
                return;
            }
            Task<ArchiveTransferRenderResult> render = owner.RenderAsync();
            await fake.OperationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            fake.CompleteTerminal(workerPhase);
            ArchiveTransferRenderResult result = await render.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(
                workerPhase == OperationPhase.Cancelled
                    ? ArchiveTransferOutcome.Cancelled
                    : ArchiveTransferOutcome.Failed,
                result.Outcome);
            Assert.NotNull(fake.LastExtractionDestination);
            Assert.False(Directory.Exists(fake.LastExtractionDestination!));
            Assert.Equal(
                ArchiveTransferCleanupOutcome.NotNeeded,
                await owner.ReleaseAsync(ArchiveTransferReleaseReason.TargetRefused));
            Assert.Empty(Directory.EnumerateFileSystemEntries(storage.TransferRootPath));
            Assert.Contains(sink.Updates, update =>
                update.Kind == ActivityOperationKind.TransferOut
                && update.Phase == (workerPhase == OperationPhase.Cancelled
                    ? ActivityOperationPhase.Cancelled
                    : ActivityOperationPhase.Failed));
        }
        finally
        {
            try { Directory.Delete(temporaryRoot, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task TransferOutFailedWithExactLookingDestinationPreservesForeignTree()
    {
        var sink = new RecordingActivitySink();
        var fake = FakeArchiveClient.Create(
            [Entry(EntryId.New(), null, "selected.txt", "selected.txt", ArchiveEntryKind.File)],
            capabilities: ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract,
            blockTerminal: true);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        string temporaryRoot = Path.Combine(
            Path.GetTempPath(), "RainExplorer-TransferForeign-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            var storage = new ArchiveTransferStorage(Path.Combine(temporaryRoot, "transfer-root"));
            await using var adapter = new ArchiveLocationAdapter(runtime, sink, storage);
            await adapter.OpenAsync(Path.Combine(temporaryRoot, "foreign.zip"));
            adapter.UpdateSelection([Assert.Single(adapter.Items)]);
            ArchiveTransferOwner? owner = await adapter.BeginArchiveTransferCoordinatorAsync();
            if (owner is null)
            {
                Assert.False(ArchiveOleTransferGate.HasWorkerPublicationIdentity);
                Assert.Equal(0, fake.ExtractionStartCount);
                Assert.False(Directory.Exists(storage.TransferRootPath));
                return;
            }
            Task<ArchiveTransferRenderResult> render = owner.RenderAsync();
            await fake.OperationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            string destination = Assert.IsType<string>(fake.LastExtractionDestination);
            Directory.CreateDirectory(destination);
            string selected = Path.Combine(destination, "selected.txt");
            await File.WriteAllTextAsync(selected, "0123456789");
            fake.CompleteTerminal(OperationPhase.Failed);

            ArchiveTransferRenderResult result = await render.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ArchiveTransferOutcome.Preserved, result.Outcome);
            Assert.Equal("0123456789", await File.ReadAllTextAsync(selected));
            Assert.Equal(
                ArchiveTransferCleanupOutcome.Preserved,
                await owner.ReleaseAsync(ArchiveTransferReleaseReason.TargetException));
            Assert.Equal("0123456789", await File.ReadAllTextAsync(selected));
            Assert.Contains(sink.Updates, update =>
                update.Kind == ActivityOperationKind.TransferOut
                && update.Phase == ActivityOperationPhase.Failed);
        }
        finally
        {
            try { Directory.Delete(temporaryRoot, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task TransferOutCancellationAfterSuccessfulReviewCleansExactOutputAndReportsCancelled()
    {
        var reviewReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReview = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new RecordingActivitySink();
        var fake = FakeArchiveClient.Create(
            [Entry(EntryId.New(), null, "selected.txt", "selected.txt", ArchiveEntryKind.File)],
            capabilities: ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract,
            blockTerminal: true);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        string temporaryRoot = Path.Combine(
            Path.GetTempPath(), "RainExplorer-TransferPostReviewCancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            var storage = new ArchiveTransferStorage(Path.Combine(temporaryRoot, "transfer-root"));
            await using var adapter = new ArchiveLocationAdapter(
                runtime,
                sink,
                storage,
                async () =>
                {
                    reviewReached.TrySetResult();
                    await releaseReview.Task;
                });
            await adapter.OpenAsync(Path.Combine(temporaryRoot, "post-review-cancel.zip"));
            adapter.UpdateSelection([Assert.Single(adapter.Items)]);
            ArchiveTransferOwner? owner = await adapter.BeginArchiveTransferCoordinatorAsync();
            if (owner is null)
            {
                Assert.False(ArchiveOleTransferGate.HasWorkerPublicationIdentity);
                Assert.Equal(0, fake.ExtractionStartCount);
                Assert.False(Directory.Exists(storage.TransferRootPath));
                return;
            }
            Task<ArchiveTransferRenderResult> render = owner.RenderAsync();
            await fake.OperationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            string destination = Assert.IsType<string>(fake.LastExtractionDestination);
            Directory.CreateDirectory(destination);
            await File.WriteAllTextAsync(
                Path.Combine(destination, "selected.txt"),
                "0123456789");
            fake.CompleteTerminal(OperationPhase.Completed);
            await reviewReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

            adapter.UpdateSelection([]);
            await fake.CancelCalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            releaseReview.TrySetResult();

            ArchiveTransferRenderResult result = await render.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ArchiveTransferOutcome.Cancelled, result.Outcome);
            Assert.DoesNotContain(sink.Updates, update =>
                update.Kind == ActivityOperationKind.TransferOut
                && update.Phase is ActivityOperationPhase.Completed
                    or ActivityOperationPhase.CompletedWithWarnings);
            Assert.Contains(sink.Updates, update =>
                update.Kind == ActivityOperationKind.TransferOut
                && update.Phase == ActivityOperationPhase.Cancelled);
            Assert.Equal(
                ArchiveTransferCleanupOutcome.Removed,
                await owner.ReleaseAsync(ArchiveTransferReleaseReason.SelectionChanged));
            Assert.False(Directory.Exists(destination));
            Assert.Empty(Directory.EnumerateFileSystemEntries(storage.TransferRootPath));
        }
        finally
        {
            releaseReview.TrySetResult();
            try { Directory.Delete(temporaryRoot, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task DisposeForcesBlockedTransferWorkerTeardownBeforeCoordinatorDrain()
    {
        var fake = FakeArchiveClient.Create(
            [Entry(EntryId.New(), null, "selected.txt", "selected.txt", ArchiveEntryKind.File)],
            capabilities: ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract,
            blockTerminal: true);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        string temporaryRoot = Path.Combine(
            Path.GetTempPath(), "RainExplorer-TransferShutdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        var storage = new ArchiveTransferStorage(Path.Combine(temporaryRoot, "transfer-root"));
        var adapter = new ArchiveLocationAdapter(runtime, new RecordingActivitySink(), storage);
        try
        {
            await adapter.OpenAsync(Path.Combine(temporaryRoot, "shutdown.zip"));
            adapter.UpdateSelection([Assert.Single(adapter.Items)]);
            ArchiveTransferOwner? owner = await adapter.BeginArchiveTransferCoordinatorAsync();
            if (owner is null)
            {
                Assert.False(ArchiveOleTransferGate.HasWorkerPublicationIdentity);
                Assert.Equal(0, fake.ExtractionStartCount);
                Assert.False(Directory.Exists(storage.TransferRootPath));
                return;
            }
            Task<ArchiveTransferRenderResult> render = owner.RenderAsync();
            await fake.OperationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Task dispose = adapter.DisposeAsync().AsTask();
            await fake.CancelCalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(dispose.IsCompleted);
            await dispose.WaitAsync(TimeSpan.FromSeconds(8));

            Assert.Equal(ArchiveTransferOutcome.Cancelled, (await render).Outcome);
            Assert.Equal(1, fake.CancelCallCount);
            Assert.Equal(1, fake.DisposeClientCount);
            Assert.Equal(ArchiveTransferCleanupOutcome.NotNeeded, owner.CleanupOutcome);
            Assert.Empty(Directory.EnumerateFileSystemEntries(storage.TransferRootPath));
        }
        finally
        {
            await adapter.DisposeAsync();
            try { Directory.Delete(temporaryRoot, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task SelectionInvalidationDuringOwnerAdmissionPreventsEscapedOwner()
    {
        var admissionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAdmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int admissionCount = 0;
        var fake = FakeArchiveClient.Create(
            [Entry(EntryId.New(), null, "selected.txt", "selected.txt", ArchiveEntryKind.File)],
            capabilities: ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        string temporaryRoot = Path.Combine(
            Path.GetTempPath(), "RainExplorer-TransferAdmission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        var storage = new ArchiveTransferStorage(Path.Combine(temporaryRoot, "transfer-root"));
        await using var adapter = new ArchiveLocationAdapter(
            runtime,
            new RecordingActivitySink(),
            storage,
            TimeSpan.FromMilliseconds(50),
            async () =>
            {
                if (Interlocked.Increment(ref admissionCount) != 2) return;
                admissionEntered.TrySetResult();
                await releaseAdmission.Task;
            });
        try
        {
            await adapter.OpenAsync(Path.Combine(temporaryRoot, "admission.zip"));
            FileItem selected = Assert.Single(adapter.Items);
            adapter.UpdateSelection([selected]);
            ArchiveTransferOwner? first = await adapter.BeginArchiveTransferCoordinatorAsync();
            if (first is null)
            {
                Assert.False(ArchiveOleTransferGate.HasWorkerPublicationIdentity);
                Assert.Equal(0, fake.ExtractionStartCount);
                Assert.False(Directory.Exists(storage.TransferRootPath));
                return;
            }

            Task<ArchiveTransferOwner?> replacement = adapter.BeginArchiveTransferCoordinatorAsync();
            await admissionEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            adapter.UpdateSelection([]);
            releaseAdmission.TrySetResult();

            Assert.Null(await replacement.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(ArchiveTransferReleaseReason.ClipboardReplaced, first.ReleaseReason);
            Assert.Equal(0, fake.ExtractionStartCount);
        }
        finally
        {
            releaseAdmission.TrySetResult();
            try { Directory.Delete(temporaryRoot, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public async Task SourceChangedAndWorkerLossTransitionState()
    {
        foreach (var category in new[] { ArchiveErrorCategory.SourceChanged, ArchiveErrorCategory.WorkerFailure })
        {
            var fake = FakeArchiveClient.Create(
                [Entry(EntryId.New(), null, "a.txt", "a.txt", ArchiveEntryKind.File)],
                capabilities: ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract,
                terminalError: category);
            await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
            await using var adapter = new ArchiveLocationAdapter(runtime);
            await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "terminal-state-" + Guid.NewGuid() + ".zip"));
            adapter.UpdateSelection([Assert.Single(adapter.Items)]);
            ArchiveOperationResult result = await adapter.ExtractSelectionAsync(
                Path.Combine(Path.GetTempPath(), "terminal-state-" + Guid.NewGuid()));

            Assert.Equal(category == ArchiveErrorCategory.SourceChanged
                    ? ArchiveOperationOutcome.Stale : ArchiveOperationOutcome.Offline,
                result.Outcome);
            Assert.Equal(category == ArchiveErrorCategory.SourceChanged
                    ? ArchiveLocationState.Stale : ArchiveLocationState.Offline,
                adapter.State);
        }
    }

    [Theory]
    [InlineData("destination.must_not_exist", ArchiveDestinationConflictReason.ExistingDestination)]
    [InlineData("destination.appeared_before_commit", ArchiveDestinationConflictReason.AppearedBeforeCommit)]
    [InlineData("destination.identity_changed", ArchiveDestinationConflictReason.StagingValidationConflict)]
    [InlineData("destination.path_appeared", ArchiveDestinationConflictReason.StagingValidationConflict)]
    [InlineData("destination.path_replaced", ArchiveDestinationConflictReason.StagingValidationConflict)]
    [InlineData("destination.reparse_point_appeared", ArchiveDestinationConflictReason.StagingValidationConflict)]
    [InlineData("destination.reparse_point_encountered", ArchiveDestinationConflictReason.StagingValidationConflict)]
    [InlineData("destination.unexpected_path_appeared", ArchiveDestinationConflictReason.StagingValidationConflict)]
    [InlineData("destination.hard_link_appeared", ArchiveDestinationConflictReason.TemporaryFileHardLinkConflict)]
    [InlineData("destination.staging_shape_changed", ArchiveDestinationConflictReason.StagingValidationConflict)]
    public async Task ExtractionDestinationConflictRetainsOnlyFixedAllowlistedReason(
        string code,
        ArchiveDestinationConflictReason expectedReason)
    {
        var sink = new RecordingActivitySink();
        var fake = FakeArchiveClient.Create(
            [Entry(EntryId.New(), null, "a.txt", "a.txt", ArchiveEntryKind.File)],
            terminalError: ArchiveErrorCategory.DestinationConflict,
            terminalErrorCode: code,
            terminalErrorMessage: "provider path/member/native details");
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var adapter = new ArchiveLocationAdapter(runtime, sink);
        await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "conflict-reason-" + Guid.NewGuid() + ".zip"));
        adapter.UpdateSelection([Assert.Single(adapter.Items)]);

        ArchiveOperationResult result = await adapter.ExtractSelectionAsync(
            Path.Combine(Path.GetTempPath(), "conflict-reason-" + Guid.NewGuid()));

        Assert.Equal(ArchiveOperationOutcome.DestinationConflict, result.Outcome);
        Assert.Equal(expectedReason, result.DestinationConflictReason);
        Assert.Equal(expectedReason, sink.LastUpdate!.DestinationConflictReason);
        Assert.Equal(ActivityOperationFailure.DestinationConflict, sink.LastUpdate.Failure);
        Assert.Equal(
            PaneView.ArchiveDestinationConflictStatus(expectedReason),
            PaneView.ArchiveOperationStatus(result, extraction: true));
        Assert.Equal(
            PaneView.ArchiveDestinationConflictStatus(expectedReason),
            ActivityService.FormatOperationDetail(new ActivityOperationUpdate(
                ActivityOperationKind.Extraction,
                ActivityOperationPhase.Failed,
                Failure: ActivityOperationFailure.DestinationConflict,
                DestinationConflictReason: expectedReason)));
    }

    [Theory]
    [InlineData("destination.hard_link_appeared_extra")]
    [InlineData("destination.link_appeared")]
    public async Task UnknownOrNearMatchConflictCodeUsesNeutralRedactedStatus(string code)
    {
        const string sensitive = "C:\\private\\Vencord.zip member.txt native exception";
        var fake = FakeArchiveClient.Create(
            [Entry(EntryId.New(), null, "a.txt", "a.txt", ArchiveEntryKind.File)],
            terminalError: ArchiveErrorCategory.DestinationConflict,
            terminalErrorCode: code,
            terminalErrorMessage: sensitive);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var adapter = new ArchiveLocationAdapter(runtime);
        await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "unknown-conflict-" + Guid.NewGuid() + ".zip"));
        adapter.UpdateSelection([Assert.Single(adapter.Items)]);

        ArchiveOperationResult result = await adapter.ExtractSelectionAsync(
            Path.Combine(Path.GetTempPath(), "unknown-conflict-" + Guid.NewGuid()));
        string status = PaneView.ArchiveDestinationConflictStatus(result.DestinationConflictReason);
        string activityDetail = ActivityService.FormatOperationDetail(new ActivityOperationUpdate(
            ActivityOperationKind.Extraction,
            ActivityOperationPhase.Failed,
            Failure: ActivityOperationFailure.DestinationConflict,
            DestinationConflictReason: result.DestinationConflictReason));

        Assert.Equal(ArchiveDestinationConflictReason.Unknown, result.DestinationConflictReason);
        Assert.Equal(status, activityDetail);
        Assert.DoesNotContain("appeared before commit", status, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(code, status, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitive, status, StringComparison.Ordinal);
        Assert.DoesNotContain(code, activityDetail, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitive, activityDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TerminalCallbackRetainsHardLinkAsStagingValidationConflict()
    {
        var sink = new RecordingActivitySink();
        var fake = FakeArchiveClient.Create(
            [Entry(EntryId.New(), null, "a.txt", "a.txt", ArchiveEntryKind.File)],
            terminalError: ArchiveErrorCategory.DestinationConflict,
            terminalErrorCode: "destination.hard_link_appeared",
            terminalErrorMessage: "unexpected hard link in operation-owned staging",
            blockTerminal: true);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var adapter = new ArchiveLocationAdapter(runtime, sink);
        await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "callback-conflict-" + Guid.NewGuid() + ".zip"));
        adapter.UpdateSelection([Assert.Single(adapter.Items)]);
        Task<ArchiveOperationResult> operation = adapter.ExtractSelectionAsync(
            Path.Combine(Path.GetTempPath(), "callback-conflict-" + Guid.NewGuid()));
        await fake.OperationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        fake.CompleteTerminal(OperationPhase.Failed, emitCallback: true);
        ArchiveOperationResult result = await operation.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ArchiveDestinationConflictReason.TemporaryFileHardLinkConflict,
            result.DestinationConflictReason);
        Assert.Equal(ArchiveDestinationConflictReason.TemporaryFileHardLinkConflict,
            sink.LastUpdate!.DestinationConflictReason);
        Assert.DoesNotContain("appeared before commit",
            PaneView.PhysicalArchiveExtractionStatus(
                new PhysicalArchiveExtractionBatchResult(1, [result])),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "⚠️ Another program linked a temporary extracted file. Choose a folder that is not being synced. No retry was started.",
            ActivityService.FormatOperationDetail(new ActivityOperationUpdate(
                ActivityOperationKind.Extraction,
                ActivityOperationPhase.Failed,
                Failure: ActivityOperationFailure.DestinationConflict,
                DestinationConflictReason: result.DestinationConflictReason)));
    }

    [Fact]
    public async Task ImmediateRemoteHardLinkErrorRetainsFixedReasonAndRedactsDetails()
    {
        const string sensitive = "C:\\private\\Vencord.zip member.txt native exception";
        var sink = new RecordingActivitySink();
        var fake = FakeArchiveClient.Create(
            [Entry(EntryId.New(), null, "a.txt", "a.txt", ArchiveEntryKind.File)],
            immediateExtractionError: new ArchiveError(
                "destination.hard_link_appeared",
                ArchiveErrorCategory.DestinationConflict,
                sensitive));
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var adapter = new ArchiveLocationAdapter(runtime, sink);
        await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "immediate-conflict-" + Guid.NewGuid() + ".zip"));
        adapter.UpdateSelection([Assert.Single(adapter.Items)]);

        ArchiveOperationResult result = await adapter.ExtractSelectionAsync(
            Path.Combine(Path.GetTempPath(), "immediate-conflict-" + Guid.NewGuid()));

        Assert.Equal(ArchiveOperationOutcome.DestinationConflict, result.Outcome);
        Assert.Equal(ArchiveDestinationConflictReason.TemporaryFileHardLinkConflict,
            result.DestinationConflictReason);
        Assert.Equal(ArchiveDestinationConflictReason.TemporaryFileHardLinkConflict,
            sink.LastUpdate!.DestinationConflictReason);
        string status = PaneView.ArchiveDestinationConflictStatus(result.DestinationConflictReason);
        Assert.DoesNotContain(sensitive, status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedSelectionIsTypedRefusalWithTerminalActivityAndAuthorityPreserved()
    {
        const int entryCount = 30_000;
        ArchiveEntryDescriptor[] entries = Enumerable.Range(0, entryCount)
            .Select(index => Entry(
                EntryId.New(),
                null,
                $"implicit/file-{index:D5}.txt",
                $"file-{index:D5}.txt",
                ArchiveEntryKind.File))
            .ToArray();
        var sink = new RecordingActivitySink();
        var fake = FakeArchiveClient.Create(
            entries,
            capabilities: ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var adapter = new ArchiveLocationAdapter(runtime, sink);
        string source = Path.Combine(Path.GetTempPath(), "oversized-selection.zip");
        await adapter.OpenAsync(source);
        FileItem implicitFolder = Assert.Single(adapter.Items);

        adapter.UpdateSelection([implicitFolder]);
        ArchiveOperationResult result = await adapter.ExtractSelectionAsync(
            Path.Combine(Path.GetTempPath(), "oversized-selection-" + Guid.NewGuid()), ['p']);

        Assert.Equal(ArchiveOperationOutcome.InvalidSelection, result.Outcome);
        Assert.Equal(source, adapter.SourcePath);
        Assert.Equal(ArchiveLocationState.Ready, adapter.State);
        Assert.Same(implicitFolder, Assert.Single(adapter.Items));
        Assert.Equal(0, fake.ExtractionStartCount);
        Assert.Equal(fake.CreatedSecretIds, fake.DiscardedSecretIds);
        Assert.Equal(ActivityOperationPhase.Failed, sink.LastUpdate!.Phase);
        Assert.Equal(ActivityOperationFailure.InvalidRequest, sink.LastUpdate.Failure);
    }

    [Fact]
    public async Task OversizedPasswordIsTypedRefusalAndClearedBeforeSecretCreation()
    {
        var sink = new RecordingActivitySink();
        var fake = FakeArchiveClient.Create(
            [Entry(EntryId.New(), null, "secret.txt", "secret.txt", ArchiveEntryKind.File)],
            capabilities: ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract,
            hasEncryptedEntries: true);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var adapter = new ArchiveLocationAdapter(runtime, sink);
        await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "oversized-password.zip"));
        adapter.UpdateSelection([Assert.Single(adapter.Items)]);
        char[] password = Enumerable.Repeat(
            'p', ProtocolConstants.MaximumSecretBytes + 1).ToArray();

        ArchiveOperationResult result = await adapter.ExtractSelectionAsync(
            Path.Combine(Path.GetTempPath(), "oversized-password-" + Guid.NewGuid()), password);

        Assert.Equal(ArchiveOperationOutcome.InvalidRequest, result.Outcome);
        Assert.True(password.All(character => character == '\0'));
        Assert.Empty(fake.CreatedSecretIds);
        Assert.Equal(0, fake.ExtractionStartCount);
        Assert.Equal(ArchiveLocationState.Ready, adapter.State);
        Assert.Equal(ActivityOperationPhase.Failed, sink.LastUpdate!.Phase);
        Assert.Equal(ActivityOperationFailure.InvalidRequest, sink.LastUpdate.Failure);
    }

    [Fact]
    public async Task CloseWaitsForDelayedInvalidatedOperationAndDoesNotRetry()
    {
        var fake = FakeArchiveClient.Create(
            [Entry(EntryId.New(), null, "a.txt", "a.txt", ArchiveEntryKind.File)],
            capabilities: ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract,
            blockTerminal: true);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var adapter = new ArchiveLocationAdapter(runtime);
        await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "close-invalidated.zip"));
        adapter.UpdateSelection([Assert.Single(adapter.Items)]);
        Task<ArchiveOperationResult> operation = adapter.ExtractSelectionAsync(
            Path.Combine(Path.GetTempPath(), "close-invalidated-" + Guid.NewGuid()));
        await fake.OperationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await runtime.InvalidateAsync(fake.Client);
        Task close = adapter.CloseAsync();
        await fake.CancelCalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        Assert.False(close.IsCompleted);
        fake.CompleteTerminal(OperationPhase.Cancelled);
        Assert.Equal(ArchiveOperationOutcome.Cancelled, (await operation).Outcome);
        await close;

        Assert.Equal(1, fake.CancelCallCount);
        Assert.Equal(1, fake.DisposeClientCount);
        Assert.Equal(0, fake.CloseCount);
        await adapter.DisposeAsync();
        Assert.Equal(1, fake.DisposeClientCount);
    }

    [Fact]
    public async Task DisposeWaitsForDelayedInvalidatedOperationAndDoesNotRetry()
    {
        var fake = FakeArchiveClient.Create(
            [Entry(EntryId.New(), null, "a.txt", "a.txt", ArchiveEntryKind.File)],
            capabilities: ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract,
            blockTerminal: true);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        var adapter = new ArchiveLocationAdapter(runtime);
        await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "dispose-invalidated.zip"));
        adapter.UpdateSelection([Assert.Single(adapter.Items)]);
        Task<ArchiveOperationResult> operation = adapter.ExtractSelectionAsync(
            Path.Combine(Path.GetTempPath(), "dispose-invalidated-" + Guid.NewGuid()));
        await fake.OperationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await runtime.InvalidateAsync(fake.Client);
        Task dispose = adapter.DisposeAsync().AsTask();
        await fake.CancelCalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        Assert.False(dispose.IsCompleted);
        fake.CompleteTerminal(OperationPhase.Cancelled);
        Assert.Equal(ArchiveOperationOutcome.Cancelled, (await operation).Outcome);
        await dispose;

        Assert.Equal(1, fake.CancelCallCount);
        Assert.Equal(1, fake.DisposeClientCount);
        Assert.Equal(0, fake.CloseCount);
        await adapter.DisposeAsync();
        Assert.Equal(1, fake.DisposeClientCount);
    }

    [Fact]
    public async Task CloseSettlesTerminalWaitAfterBoundedClientTeardown()
    {
        var fake = FakeArchiveClient.Create(
            [Entry(EntryId.New(), null, "a.txt", "a.txt", ArchiveEntryKind.File)],
            capabilities: ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract,
            blockTerminal: true);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var adapter = new ArchiveLocationAdapter(runtime);
        await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "close-teardown.zip"));
        adapter.UpdateSelection([Assert.Single(adapter.Items)]);
        Task<ArchiveOperationResult> operation = adapter.ExtractSelectionAsync(
            Path.Combine(Path.GetTempPath(), "close-teardown-" + Guid.NewGuid()));
        await fake.OperationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task close = adapter.CloseAsync();
        await fake.CancelCalled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await close.WaitAsync(TimeSpan.FromSeconds(8));

        Assert.Equal(ArchiveOperationOutcome.Cancelled, (await operation).Outcome);
        Assert.Equal(1, fake.CancelCallCount);
        Assert.Equal(1, fake.DisposeClientCount);
        Assert.Equal(0, fake.CloseCount);
    }

    [Fact]
    public async Task LongControlAndBidiNameIsEscapedAndBoundedForCurrentItem()
    {
        EntryId entryId = EntryId.New();
        string longDisplayName = "visible-" + string.Concat(Enumerable.Repeat("\u202E\u000A", 300));
        var sink = new RecordingActivitySink();
        var fake = FakeArchiveClient.Create(
            [Entry(entryId, null, "safe.txt", longDisplayName, ArchiveEntryKind.File)],
            capabilities: ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract,
            blockTerminal: true);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var adapter = new ArchiveLocationAdapter(runtime, sink);
        await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "long-activity-name.zip"));
        adapter.UpdateSelection([Assert.Single(adapter.Items)]);
        Task<ArchiveOperationResult> operation = adapter.ExtractSelectionAsync(
            Path.Combine(Path.GetTempPath(), "long-activity-name-" + Guid.NewGuid()));
        await fake.OperationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        fake.Emit(new OperationEvent(
            fake.StartedOperationId, 1, OperationEventKind.Progress, OperationPhase.Running,
            new OperationProgress(1, 2, 1, 1, entryId, longDisplayName, TimeSpan.Zero, null, null)));
        fake.CompleteTerminal(OperationPhase.Completed);
        Assert.Equal(ArchiveOperationOutcome.Completed, (await operation).Outcome);

        ActivityOperationUpdate update = Assert.Single(
            sink.Updates, item => item.CurrentItem is not null);
        Assert.True(update.CurrentItem!.Length <= 140);
        Assert.StartsWith("visible-", update.CurrentItem, StringComparison.Ordinal);
        Assert.DoesNotContain('\u202E', update.CurrentItem);
        Assert.DoesNotContain('\n', update.CurrentItem);
        Assert.DoesNotContain(longDisplayName, update.CurrentItem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuppliedPasswordArrayIsClearedImmediatelyAndOnRefusalOrPreCancel()
    {
        var fake = FakeArchiveClient.Create(
            [Entry(EntryId.New(), null, "secret.txt", "secret.txt", ArchiveEntryKind.File)],
            capabilities: ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract,
            hasEncryptedEntries: true,
            blockTerminal: true);
        await using var runtime = new ArchiveClientRuntime(_ => Task.FromResult(fake.Client));
        await using var adapter = new ArchiveLocationAdapter(runtime);
        await adapter.OpenAsync(Path.Combine(Path.GetTempPath(), "password-clear.zip"));
        adapter.UpdateSelection([Assert.Single(adapter.Items)]);
        char[] blockedPassword = ['s', 'e', 'c', 'r', 'e', 't'];
        Task<ArchiveOperationResult> blocked = adapter.ExtractSelectionAsync(
            Path.Combine(Path.GetTempPath(), "password-clear-" + Guid.NewGuid()), blockedPassword);
        await fake.SecretCreated.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => blockedPassword.All(character => character == '\0'));
        Assert.True(blockedPassword.All(character => character == '\0'));
        fake.CompleteTerminal(OperationPhase.Cancelled);
        Assert.Equal(ArchiveOperationOutcome.Cancelled, (await blocked).Outcome);

        var refusalFake = FakeArchiveClient.Create(
            [Entry(EntryId.New(), null, "secret.txt", "secret.txt", ArchiveEntryKind.File)],
            capabilities: ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract,
            hasEncryptedEntries: true);
        await using var refusalRuntime = new ArchiveClientRuntime(_ => Task.FromResult(refusalFake.Client));
        await using var refusalAdapter = new ArchiveLocationAdapter(refusalRuntime);
        await refusalAdapter.OpenAsync(Path.Combine(Path.GetTempPath(), "password-refusal-clear.zip"));
        refusalAdapter.UpdateSelection([]);
        char[] refusedPassword = ['r', 'e', 'f', 'u', 's', 'e'];
        Assert.Equal(ArchiveOperationOutcome.InvalidSelection,
            (await refusalAdapter.ExtractSelectionAsync(
                Path.Combine(Path.GetTempPath(), "password-refusal-clear-" + Guid.NewGuid()),
                refusedPassword)).Outcome);
        Assert.True(refusedPassword.All(character => character == '\0'));
        Assert.Empty(refusalFake.CreatedSecretIds);

        var cancelledFake = FakeArchiveClient.Create(
            [Entry(EntryId.New(), null, "secret.txt", "secret.txt", ArchiveEntryKind.File)],
            capabilities: ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract,
            hasEncryptedEntries: true);
        await using var cancelledRuntime = new ArchiveClientRuntime(_ => Task.FromResult(cancelledFake.Client));
        await using var cancelledAdapter = new ArchiveLocationAdapter(cancelledRuntime);
        await cancelledAdapter.OpenAsync(Path.Combine(Path.GetTempPath(), "password-cancel-clear.zip"));
        cancelledAdapter.UpdateSelection([Assert.Single(cancelledAdapter.Items)]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        char[] cancelledPassword = ['c', 'a', 'n', 'c', 'e', 'l'];
        Assert.Equal(ArchiveOperationOutcome.Cancelled,
            (await cancelledAdapter.ExtractSelectionAsync(
                Path.Combine(Path.GetTempPath(), "password-cancel-clear-" + Guid.NewGuid()),
                cancelledPassword, cancellation.Token)).Outcome);
        Assert.True(cancelledPassword.All(character => character == '\0'));
        Assert.Empty(cancelledFake.CreatedSecretIds);
    }


    private static ArchiveFingerprint Fingerprint(string? prefix = null, string? fileIdentity = "volume:file") => new(
        123,
        new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero),
        fileIdentity,
        prefix ?? new string('A', 64));

    private static ArchiveEntryDescriptor Entry(
        EntryId id,
        EntryId? parent,
        string path,
        string name,
        ArchiveEntryKind kind,
        int duplicate = 0) => new(
            id,
            parent,
            ProviderOrdinal: 0,
            RawPath: path,
            DisplayName: name,
            Kind: kind,
            OriginalSize: kind == ArchiveEntryKind.Directory ? null : 10,
            CompressedSize: null,
            ModifiedTimeUtc: null,
            WindowsAttributes: null,
            CompressionMethod: null,
            IntegrityValue: null,
            IsEncrypted: false,
            IsSolid: false,
            LinkTarget: null,
            SafetyStatus: EntrySafetyStatus.Safe,
            SafetyReason: null,
            NormalizedPath: path,
            DuplicateOrdinal: duplicate);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed class RecordingActivitySink : IArchiveActivitySink
    {
        public RecordingActivityHandle? Handle { get; private set; }
        public List<ActivityOperationUpdate> Updates { get; } = [];
        public ActivityOperationUpdate? LastUpdate => Updates.LastOrDefault();

        public IArchiveActivityHandle Begin(ActivityOperationKind kind)
        {
            Handle = new RecordingActivityHandle(this, kind);
            return Handle;
        }

        public sealed class RecordingActivityHandle(
            RecordingActivitySink owner,
            ActivityOperationKind kind) : IArchiveActivityHandle
        {
            private readonly RecordingActivitySink _owner = owner;
            private Action? _cancel;

            public ActivityOperationKind Kind { get; } = kind;
            public bool CancellationPending { get; private set; }

            public void AttachCancel(Action cancel) => _cancel = cancel;
            public void Cancel() => _cancel?.Invoke();
            public void MarkCancellationPending() => CancellationPending = true;
            public void Report(ActivityOperationUpdate update) => _owner.Updates.Add(update);
        }
    }

    public class FakeArchiveClient : DispatchProxy
    {
        private IReadOnlyList<ArchiveEntryDescriptor> _entries = Array.Empty<ArchiveEntryDescriptor>();
        private ArchiveFingerprint _fingerprint = Fingerprint();
        private int _pageChunkSize = int.MaxValue;
        private bool _malformedNextOffset;
        private bool _supportsPersistentKeys = true;
        private bool _blockListing;
        private bool _blockFirstListing;
        private int _listingCallCount;
        private int _reportedTotalDelta;
        private bool _endListingEarly;
        private bool _blockClose;
        private ArchiveCapability _capabilities = ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract;
        private ArchiveVolumeSetKind? _volumeSetKind = ArchiveVolumeSetKind.Single;
        private bool _isMultipart;
        private bool _hasEncryptedEntries;
        private bool _supportsOperationProtocol = true;
        private bool _changeFingerprintOnSecretCreation;
        private string? _createDestinationOnSecretCreation;
        private ArchiveErrorCategory? _terminalError;
        private string? _terminalErrorCode;
        private string? _terminalErrorMessage;
        private ArchiveError? _immediateExtractionError;
        private bool _blockTerminal;
        private ArchiveErrorCategory? _passwordlessOpenError;
        private ArchiveErrorCategory? _passwordOpenError;
        private EventHandler<OperationEvent>? _operationEventReceived;
        private readonly TaskCompletionSource<OperationEvent> _terminal =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ArchiveSessionId _session;

        public IArchiveClient Client { get; private set; } = null!;
        public List<int> ListOffsets { get; } = [];
        public List<ArchiveSessionId> ClosedSessions { get; } = [];
        public int CloseCount { get; private set; }
        public int DisposeClientCount { get; private set; }
        public int OpenCount { get; private set; }
        public int ExtractionStartCount { get; private set; }
        public int IntegrityStartCount { get; private set; }
        public List<EntryId> LastExtractionEntryIds { get; } = [];
        public Guid? LastExtractionPasswordSlot { get; private set; }
        public string? LastExtractionDestination { get; private set; }
        public List<Guid> CreatedSecretIds { get; } = [];
        public List<Guid> DiscardedSecretIds { get; } = [];
        public List<Guid?> OpenPasswordSlots { get; } = [];
        public ArchiveSessionId? LastOpenedSessionId { get; private set; }
        public ArchiveSessionId? LastExtractionSessionId { get; private set; }
        public OperationId StartedOperationId { get; private set; }
        public OperationId? CancelledOperationId { get; private set; }
        public int CancelCallCount { get; private set; }
        public bool FailOpen { get; set; }
        public string? CreateDestinationOnNewDestinationFeature { get; set; }
        public string? CreateDestinationOnSecretCreation
        {
            get => _createDestinationOnSecretCreation;
            set => _createDestinationOnSecretCreation = value;
        }
        public TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ListingStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CloseStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseClose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource OperationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecretCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancelCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ArchiveFingerprint CurrentFingerprint { set => _fingerprint = value; }

        public static FakeArchiveClient Create(
            IReadOnlyList<ArchiveEntryDescriptor> entries,
            int pageChunkSize = int.MaxValue,
            bool malformedNextOffset = false,
            ArchiveFingerprint? fingerprint = null,
            bool supportsPersistentKeys = true,
            bool blockListing = false,
            bool blockFirstListing = false,
            int reportedTotalDelta = 0,
            bool endListingEarly = false,
            bool blockClose = false,
            ArchiveCapability capabilities = ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract,
            ArchiveVolumeSetKind? volumeSetKind = ArchiveVolumeSetKind.Single,
            bool isMultipart = false,
            bool hasEncryptedEntries = false,
            bool supportsOperationProtocol = true,
            bool changeFingerprintOnSecretCreation = false,
            string? createDestinationOnSecretCreation = null,
            ArchiveErrorCategory? terminalError = null,
            bool blockTerminal = false,
            ArchiveErrorCategory? passwordlessOpenError = null,
            ArchiveErrorCategory? passwordOpenError = null,
            string? terminalErrorCode = null,
            string? terminalErrorMessage = null,
            ArchiveError? immediateExtractionError = null)
        {
            IArchiveClient client = DispatchProxy.Create<IArchiveClient, FakeArchiveClient>();
            var proxy = (FakeArchiveClient)(object)client;
            proxy.Client = client;
            proxy._entries = entries;
            proxy._pageChunkSize = pageChunkSize;
            proxy._malformedNextOffset = malformedNextOffset;
            proxy._fingerprint = fingerprint ?? Fingerprint();
            proxy._supportsPersistentKeys = supportsPersistentKeys;
            proxy._blockListing = blockListing;
            proxy._blockFirstListing = blockFirstListing;
            proxy._reportedTotalDelta = reportedTotalDelta;
            proxy._endListingEarly = endListingEarly;
            proxy._blockClose = blockClose;
            proxy._capabilities = capabilities;
            proxy._volumeSetKind = volumeSetKind;
            proxy._isMultipart = isMultipart;
            proxy._hasEncryptedEntries = hasEncryptedEntries;
            proxy._supportsOperationProtocol = supportsOperationProtocol;
            proxy._changeFingerprintOnSecretCreation = changeFingerprintOnSecretCreation;
            proxy._createDestinationOnSecretCreation = createDestinationOnSecretCreation;
            proxy._terminalError = terminalError;
            proxy._terminalErrorCode = terminalErrorCode;
            proxy._terminalErrorMessage = terminalErrorMessage;
            proxy._immediateExtractionError = immediateExtractionError;
            proxy._blockTerminal = blockTerminal;
            proxy._passwordlessOpenError = passwordlessOpenError;
            proxy._passwordOpenError = passwordOpenError;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            args ??= Array.Empty<object>();
            CancellationToken cancellationToken = args.OfType<CancellationToken>().LastOrDefault();
            cancellationToken.ThrowIfCancellationRequested();
            switch (targetMethod.Name)
            {
                case "add_OperationEventReceived":
                    _operationEventReceived += (EventHandler<OperationEvent>)args[0]!;
                    return null;
                case "remove_OperationEventReceived":
                    _operationEventReceived -= (EventHandler<OperationEvent>)args[0]!;
                    return null;
                case nameof(IArchiveClient.SupportsFeature):
                    string feature = (string)args[0]!;
                    if (string.Equals(feature, ProtocolFeatures.PersistentEntryKeys,
                        StringComparison.Ordinal)) return _supportsPersistentKeys;
                    if (string.Equals(feature, ProtocolFeatures.EntryStreamLeases,
                        StringComparison.Ordinal)) return _capabilities.HasFlag(ArchiveCapability.OpenEntryStream);
                    if (feature is ProtocolFeatures.OperationEvents
                        or ProtocolFeatures.RequestCancellation
                        or ProtocolFeatures.SourceFingerprintValidation
                        or ProtocolFeatures.VolumeSetIdentity
                        or ProtocolFeatures.OperationActivity
                        or ProtocolFeatures.NewDestinationExtraction)
                    {
                        if (string.Equals(feature, ProtocolFeatures.NewDestinationExtraction,
                            StringComparison.Ordinal)
                            && !string.IsNullOrWhiteSpace(CreateDestinationOnNewDestinationFeature))
                            Directory.CreateDirectory(CreateDestinationOnNewDestinationFeature);
                        return _supportsOperationProtocol;
                    }
                    if (feature is ProtocolFeatures.SecretSlots or ProtocolFeatures.SecretSlotDiscard)
                        return true;
                    return false;
                case nameof(IArchiveClient.CreateSecretAsync):
                    var secret = Guid.NewGuid();
                    CreatedSecretIds.Add(secret);
                    SecretCreated.TrySetResult();
                    if (_changeFingerprintOnSecretCreation)
                        _fingerprint = Fingerprint(prefix: new string('B', 64));
                    if (_createDestinationOnSecretCreation is { } destination)
                        Directory.CreateDirectory(destination);
                    return Task.FromResult(secret);
                case nameof(IArchiveClient.DiscardSecretAsync):
                    DiscardedSecretIds.Add((Guid)args[0]!);
                    return Task.FromResult(true);
                case nameof(IArchiveClient.OpenArchiveAsync):
                    OpenCount++;
                    Guid? openPasswordSlot = (Guid?)args[1];
                    OpenPasswordSlots.Add(openPasswordSlot);
                    if (FailOpen)
                        return Task.FromException<OpenArchiveResponse>(new ArchiveClientException(
                            new ArchiveError("archive.invalid", ArchiveErrorCategory.InvalidArchive,
                                "provider-controlled failure")));
                    ArchiveErrorCategory? openError = openPasswordSlot is null
                        ? _passwordlessOpenError
                        : _passwordOpenError;
                    if (openError is ArchiveErrorCategory category)
                    {
                        return Task.FromException<OpenArchiveResponse>(new ArchiveClientException(
                            new ArchiveError(
                                "archive.open_refused",
                                category,
                                "The archive open attempt was refused.")));
                    }
                    _session = ArchiveSessionId.New();
                    LastOpenedSessionId = _session;
                    Opened.TrySetResult();
                    return Task.FromResult(new OpenArchiveResponse(new ArchiveDescriptor(
                        _session,
                        "ZIP",
                        "test-provider",
                        _capabilities,
                        _fingerprint,
                        _entries.Count,
                        IsSolid: false,
                        HasEncryptedEntries: _hasEncryptedEntries,
                        IsMultipart: _isMultipart,
                        IsReadOnly: true,
                        Warnings: Array.Empty<ArchiveWarning>(),
                        VolumeSetKind: _volumeSetKind)));
                case nameof(IArchiveClient.ListEntriesAsync):
                    int offset = (int)args[1]!;
                    int requested = (int)args[2]!;
                    if (_blockListing || _blockFirstListing && Interlocked.Increment(ref _listingCallCount) == 1)
                    {
                        ListingStarted.TrySetResult();
                        return BlockListingAsync(cancellationToken);
                    }
                    ListOffsets.Add(offset);
                    int count = Math.Min(Math.Min(requested, _pageChunkSize), _entries.Count - offset);
                    var page = _entries.Skip(offset).Take(count).ToArray();
                    int? next = offset + count < _entries.Count ? offset + count : null;
                    if (_endListingEarly) next = null;
                    if (_malformedNextOffset && next is not null) next = offset;
                    return Task.FromResult(new ListEntriesResponse(
                        page, offset, _entries.Count + _reportedTotalDelta, next));
                case nameof(IArchiveClient.CloseArchiveAsync):
                    CloseCount++;
                    ClosedSessions.Add((ArchiveSessionId)args[0]!);
                    if (_blockClose)
                    {
                        CloseStarted.TrySetResult();
                        return CompleteCloseAsync(cancellationToken);
                    }
                    return Task.FromResult(new CloseArchiveResponse(true));
                case nameof(IArchiveClient.StartExtractionAsync):
                    ExtractionStartCount++;
                    LastExtractionSessionId = (ArchiveSessionId)args[0]!;
                    LastExtractionEntryIds.Clear();
                    LastExtractionEntryIds.AddRange((IReadOnlyList<EntryId>)args[1]!);
                    LastExtractionDestination = (string)args[2]!;
                    LastExtractionPasswordSlot = (Guid?)args[3];
                    if (_immediateExtractionError is { } extractionError)
                        return Task.FromException<StartOperationResponse>(
                            new ArchiveClientException(extractionError));
                    return Task.FromResult(StartOperation(OperationKind.Extract));
                case nameof(IArchiveClient.StartIntegrityTestAsync):
                    IntegrityStartCount++;
                    return Task.FromResult(StartOperation(OperationKind.IntegrityTest));
                case nameof(IArchiveClient.CancelOperationAsync):
                    CancelCallCount++;
                    CancelledOperationId = (OperationId)args[0]!;
                    CancelCalled.TrySetResult();
                    return Task.FromResult(new CancelOperationResponse(true));
                case nameof(IArchiveClient.WaitForTerminalAsync):
                    return _blockTerminal
                        ? _terminal.Task.WaitAsync(cancellationToken)
                        : Task.FromResult(CreateTerminal());
                case nameof(IArchiveClient.GetOperationActivityAsync):
                    return Task.FromResult(new GetOperationActivityResponse(
                        DateTimeOffset.UtcNow, 8, 1, OperationQueueOrdering.Unspecified, 32,
                        false, false, false, false, false, Array.Empty<OperationActivitySnapshot>()));
                case nameof(IAsyncDisposable.DisposeAsync):
                    DisposeClientCount++;
                    return ValueTask.CompletedTask;
                default:
                    throw new NotSupportedException($"Fake operation not configured: {targetMethod.Name}");
            }
        }

        public void Emit(OperationEvent operationEvent) =>
            _operationEventReceived?.Invoke(Client, operationEvent);

        public void CompleteTerminal(OperationPhase phase, bool emitCallback = false)
        {
            OperationEvent terminal = new(
                StartedOperationId, 3, OperationEventKind.Terminal, phase,
                Error: _terminalError is ArchiveErrorCategory category
                    ? new ArchiveError(
                        _terminalErrorCode ?? "test.error",
                        category,
                        _terminalErrorMessage ?? "test terminal error")
                    : null);
            if (emitCallback) _operationEventReceived?.Invoke(Client, terminal);
            _terminal.TrySetResult(terminal);
        }

        private StartOperationResponse StartOperation(OperationKind kind)
        {
            StartedOperationId = OperationId.New();
            OperationStarted.TrySetResult();
            return new StartOperationResponse(StartedOperationId);
        }

        private OperationEvent CreateTerminal() => new(
            StartedOperationId, 3, OperationEventKind.Terminal,
            _terminalError is ArchiveErrorCategory ? OperationPhase.Failed : OperationPhase.Completed,
            Error: _terminalError is ArchiveErrorCategory category
                ? new ArchiveError(
                    _terminalErrorCode ?? "test.error",
                    category,
                    _terminalErrorMessage ?? "test terminal error") : null);


        private static async Task<ListEntriesResponse> BlockListingAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }

        private async Task<CloseArchiveResponse> CompleteCloseAsync(CancellationToken cancellationToken)
        {
            await ReleaseClose.Task.WaitAsync(cancellationToken);
            return new CloseArchiveResponse(true);
        }
    }
}
