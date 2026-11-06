using System.Diagnostics;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.App.ViewModels;
using RainArchives.Contracts;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class ArchiveBrowserViewModelTests
{
    private static readonly DateTimeOffset FixedTime =
        new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    public TestContext? TestContext { get; set; }

    [TestMethod]
    public void NavigationHistoryBranchesAndTreatsUpAsARegularNewNavigation()
    {
        var rootFolder = Entry("a", "A", ArchiveEntryKind.Directory);
        var firstChild = Entry("a/first", "First", ArchiveEntryKind.Directory, rootFolder.EntryId);
        var secondChild = Entry("a/second", "Second", ArchiveEntryKind.Directory, rootFolder.EntryId);
        var file = Entry("a/file.txt", "file.txt", ArchiveEntryKind.File, rootFolder.EntryId);
        var browser = new ArchiveBrowserViewModel();
        var focusRequests = new List<ArchiveBrowserFocusTarget>();
        browser.FocusRequested += (_, args) => focusRequests.Add(args.Target);

        browser.OpenSession(
            Archive([rootFolder, firstChild, secondChild, file]),
            [rootFolder, firstChild, secondChild, file],
            "sample.zip",
            persistentKeysEnabled: true,
            sameAuthoritativeSource: false);

        Assert.AreEqual(ArchiveBrowserState.Ready, browser.State);
        Assert.AreEqual(rootFolder.EntryId, browser.SelectedEntry?.EntryId);
        Assert.IsTrue(browser.NavigateTo(rootFolder.EntryId));
        Assert.AreEqual(rootFolder.EntryId, browser.CurrentFolderId);
        CollectionAssert.AreEqual(
            new[] { "sample.zip", "A" },
            browser.Breadcrumbs.Select(item => item.Name).ToArray());
        CollectionAssert.AreEqual(
            new[] { firstChild.EntryId, secondChild.EntryId, file.EntryId },
            browser.Entries.Select(item => item.EntryId).ToArray());

        Assert.IsTrue(browser.NavigateTo(firstChild.EntryId));
        Assert.IsTrue(browser.CanGoBack);
        browser.BackCommand.Execute(null);
        Assert.AreEqual(rootFolder.EntryId, browser.CurrentFolderId);
        Assert.IsTrue(browser.CanGoForward);

        Assert.IsTrue(browser.NavigateTo(secondChild.EntryId));
        Assert.AreEqual(secondChild.EntryId, browser.CurrentFolderId);
        Assert.IsFalse(browser.CanGoForward, "A new navigation must discard the forward branch.");

        browser.UpCommand.Execute(null);
        Assert.AreEqual(rootFolder.EntryId, browser.CurrentFolderId);
        Assert.IsTrue(browser.CanGoBack, "Up is a new history item, so Back returns to the child.");
        browser.BackCommand.Execute(null);
        Assert.AreEqual(secondChild.EntryId, browser.CurrentFolderId);

        browser.RootCommand.Execute(null);
        Assert.IsNull(browser.CurrentFolderId);
        Assert.IsFalse(browser.CanGoUp);
        Assert.IsTrue(focusRequests.All(target => target == ArchiveBrowserFocusTarget.EntryList));
    }

    [TestMethod]
    public void NavigationHistoryRetainsOnlyTheNewestBoundedLocations()
    {
        var folder = Entry("folder", "folder", ArchiveEntryKind.Directory);
        var browser = new ArchiveBrowserViewModel();
        browser.OpenSession(
            Archive([folder]),
            [folder],
            "bounded.zip",
            persistentKeysEnabled: true,
            sameAuthoritativeSource: false);

        for (var index = 0; index < ArchiveBrowserViewModel.MaximumHistoryEntries * 2; index++)
        {
            Assert.IsTrue(browser.NavigateTo(index % 2 == 0 ? folder.EntryId : null));
        }

        Assert.AreEqual(ArchiveBrowserViewModel.MaximumHistoryEntries, browser.HistoryCount);
        for (var index = 1; index < ArchiveBrowserViewModel.MaximumHistoryEntries; index++)
        {
            Assert.IsTrue(browser.BackCommand.CanExecute(null));
            browser.BackCommand.Execute(null);
        }

        Assert.IsFalse(browser.BackCommand.CanExecute(null));
    }

    [TestMethod]
    public void UnicodeSearchAndDuplicatePersistentKeysRestoreSelectionAcrossExactReopen()
    {
        var first = Entry("same.txt", "Ångström.txt", ArchiveEntryKind.File, duplicateOrdinal: 0);
        var second = Entry("same.txt", "ångström.txt", ArchiveEntryKind.File, duplicateOrdinal: 1);
        var other = Entry("other.txt", "雨.txt", ArchiveEntryKind.File);
        var fingerprint = Fingerprint();
        var browser = new ArchiveBrowserViewModel();
        browser.OpenSession(
            Archive([first, second, other], fingerprint: fingerprint),
            [first, second, other],
            "unicode.zip",
            persistentKeysEnabled: true,
            sameAuthoritativeSource: false);

        browser.SearchText = "ÅNGSTRÖM";
        Assert.AreEqual(2, browser.TotalResults);
        Assert.AreEqual(first.EntryId, browser.SelectedEntry?.EntryId);
        Assert.IsTrue(browser.TrySelectPersistentKey(new ArchiveEntryKey("same.txt", 1)));
        Assert.AreEqual("#2", browser.SelectedEntry?.Occurrence);

        var reopenedFirst = first with { EntryId = EntryId.New() };
        var reopenedSecond = second with { EntryId = EntryId.New() };
        var reopenedOther = other with { EntryId = EntryId.New() };
        browser.BeginLoading("unicode.zip");
        browser.OpenSession(
            Archive(
                [reopenedFirst, reopenedSecond, reopenedOther],
                sessionId: ArchiveSessionId.New(),
                fingerprint: fingerprint),
            [reopenedFirst, reopenedSecond, reopenedOther],
            "unicode.zip",
            persistentKeysEnabled: true,
            sameAuthoritativeSource: true);

        Assert.AreEqual("ÅNGSTRÖM", browser.SearchText);
        Assert.AreEqual(reopenedSecond.EntryId, browser.SelectedEntry?.EntryId);
        Assert.AreEqual(1, browser.SelectedPersistentKey?.DuplicateOrdinal);
        Assert.AreEqual("same.txt", browser.SelectedPersistentKey?.NormalizedPath);

        browser.SearchText = "雪";
        Assert.AreEqual(ArchiveBrowserState.Empty, browser.State);
        Assert.IsNull(browser.SelectedEntry);
        browser.SearchText = string.Empty;
        Assert.AreEqual(ArchiveBrowserState.Ready, browser.State);
        Assert.IsNotNull(browser.SelectedEntry);
    }

    [TestMethod]
    public void MaximumDuplicateOrdinalRendersOverflowFreeAndEscapesArchiveText()
    {
        const string archiveControlledName = "unsafe\r\n\u202E.txt";
        var entry = Entry(
            "same.txt",
            archiveControlledName,
            ArchiveEntryKind.File,
            duplicateOrdinal: int.MaxValue);
        var browser = new ArchiveBrowserViewModel();
        browser.OpenSession(
            Archive([entry]),
            [entry],
            "maximum-duplicate.zip",
            persistentKeysEnabled: true,
            sameAuthoritativeSource: false);

        var rendered = browser.SelectedEntry
            ?? throw new AssertFailedException("The accepted entry was not selected.");
        Assert.AreEqual("unsafe\\u000D\\u000A\\u202E.txt", rendered.Name);
        Assert.AreEqual("#2,147,483,648", rendered.Occurrence);
        Assert.AreEqual(
            "unsafe\\u000D\\u000A\\u202E.txt, File, archive occurrence 2,147,483,648, security status Safe, not encrypted",
            rendered.AutomationName);
        Assert.IsFalse(rendered.AutomationName.Contains(archiveControlledName, StringComparison.Ordinal));
    }

    [TestMethod]
    public void ReopenWithChangedFingerprintOrDifferentSourceResetsNavigationAndSelection()
    {
        var folder = Entry("folder", "folder", ArchiveEntryKind.Directory);
        var child = Entry("folder/child.txt", "child.txt", ArchiveEntryKind.File, folder.EntryId);
        var browser = new ArchiveBrowserViewModel();
        browser.OpenSession(
            Archive([folder, child]),
            [folder, child],
            "source.zip",
            persistentKeysEnabled: true,
            sameAuthoritativeSource: false);
        Assert.IsTrue(browser.NavigateTo(folder.EntryId));
        browser.SearchText = "child";

        var newFolder = folder with { EntryId = EntryId.New() };
        var newChild = child with
        {
            EntryId = EntryId.New(),
            ParentEntryId = newFolder.EntryId,
        };
        var changed = Fingerprint() with { PrefixSha256 = new string('B', 64) };
        browser.BeginLoading("source.zip");
        browser.OpenSession(
            Archive([newFolder, newChild], fingerprint: changed),
            [newFolder, newChild],
            "source.zip",
            persistentKeysEnabled: true,
            sameAuthoritativeSource: true);

        Assert.IsNull(browser.CurrentFolderId);
        Assert.AreEqual(string.Empty, browser.SearchText);
        Assert.AreEqual(newFolder.EntryId, browser.SelectedEntry?.EntryId);
        Assert.IsFalse(browser.CanGoBack);

        Assert.IsTrue(browser.NavigateTo(newFolder.EntryId));
        var thirdFolder = newFolder with { EntryId = EntryId.New() };
        var thirdChild = newChild with
        {
            EntryId = EntryId.New(),
            ParentEntryId = thirdFolder.EntryId,
        };
        browser.BeginLoading("other.zip");
        browser.OpenSession(
            Archive([thirdFolder, thirdChild], fingerprint: changed),
            [thirdFolder, thirdChild],
            "other.zip",
            persistentKeysEnabled: true,
            sameAuthoritativeSource: false);
        Assert.IsNull(browser.CurrentFolderId);
        Assert.IsFalse(browser.CanGoBack);
    }

    [TestMethod]
    public void StaleOfflineUnsupportedAndCancelledLoadingStatesNeverAuthorizeBrowsing()
    {
        var folder = Entry("folder", "folder", ArchiveEntryKind.Directory);
        var child = Entry("folder/child.txt", "child.txt", ArchiveEntryKind.File, folder.EntryId);
        var browser = new ArchiveBrowserViewModel();

        browser.BeginLoading("cancelled.zip");
        browser.ReportLoadingProgress(2, 10);
        StringAssert.Contains(browser.StateMessage, "2 of 10");
        browser.CancelLoading();
        Assert.AreEqual(ArchiveBrowserState.NoArchive, browser.State);

        browser.OpenUnsupportedSession(
            Archive([folder, child]) with { Capabilities = ArchiveCapability.None },
            "unsupported.bin");
        Assert.AreEqual(ArchiveBrowserState.Unsupported, browser.State);
        Assert.IsFalse(browser.CanBrowse);
        Assert.IsTrue(browser.CanChooseArchiveFromState);
        Assert.IsFalse(browser.StateMessage.Contains("fake-provider", StringComparison.Ordinal));

        browser.OpenSession(
            Archive([folder, child]),
            [folder, child],
            "source.zip",
            persistentKeysEnabled: true,
            sameAuthoritativeSource: false);
        Assert.IsTrue(browser.NavigateTo(folder.EntryId));
        browser.MarkStale();
        Assert.AreEqual(ArchiveBrowserState.Stale, browser.State);
        Assert.IsFalse(browser.CanBrowse);
        browser.SearchText = "child";
        Assert.AreEqual(ArchiveBrowserState.Stale, browser.State);

        var reopenedFolder = folder with { EntryId = EntryId.New() };
        var reopenedChild = child with
        {
            EntryId = EntryId.New(),
            ParentEntryId = reopenedFolder.EntryId,
        };
        browser.BeginLoading("source.zip");
        browser.OpenSession(
            Archive([reopenedFolder, reopenedChild]),
            [reopenedFolder, reopenedChild],
            "source.zip",
            persistentKeysEnabled: true,
            sameAuthoritativeSource: true);
        Assert.IsNull(browser.CurrentFolderId, "A stale state must not restore navigation even if fingerprint fields repeat.");

        browser.MarkOffline();
        Assert.AreEqual(ArchiveBrowserState.Offline, browser.State);
        Assert.IsFalse(browser.CanBrowse);
        Assert.IsFalse(browser.BackCommand.CanExecute(null));
    }

    [TestMethod]
    public void EntryMaterializationIsBoundedAndLargeListNavigationRemainsDeterministic()
    {
        const int entryCount = 50_000;
        var entries = Enumerable.Range(0, entryCount)
            .Select(index => Entry(
                $"item-{index:D5}.txt",
                $"item-{index:D5}.txt",
                ArchiveEntryKind.File,
                providerOrdinal: index))
            .Reverse()
            .ToArray();
        var browser = new ArchiveBrowserViewModel();
        var elapsed = Stopwatch.StartNew();
        browser.OpenSession(
            Archive(entries),
            entries,
            "large.zip",
            persistentKeysEnabled: true,
            sameAuthoritativeSource: false);
        elapsed.Stop();

        Assert.HasCount(ArchiveBrowserViewModel.VisiblePageSize, browser.Entries);
        Assert.AreEqual(entryCount, browser.TotalResults);
        Assert.AreEqual(98, browser.PageCount);
        Assert.AreEqual("item-00000.txt", browser.Entries[0].Name);
        Assert.IsTrue(browser.CanGoToNextPage);
        browser.NextPageCommand.Execute(null);
        Assert.AreEqual(1, browser.PageIndex);
        Assert.AreEqual("item-00512.txt", browser.Entries[0].Name);

        browser.SearchText = "item-49999";
        Assert.HasCount(1, browser.Entries);
        Assert.AreEqual("item-49999.txt", browser.SelectedEntry?.Name);
        Assert.IsTrue(elapsed.Elapsed < TimeSpan.FromSeconds(15), $"Large-list index took {elapsed.Elapsed}.");
        TestContext?.WriteLine(
            $"M5B large-list model: {entryCount:N0} descriptors, {ArchiveBrowserViewModel.VisiblePageSize:N0} materialized rows, {elapsed.Elapsed.TotalMilliseconds:N1} ms.");
    }

    [TestMethod]
    public void InvalidCountsHierarchyAndPersistentKeysAreRefusedBeforeReplacingCurrentState()
    {
        var valid = Entry("valid.txt", "valid.txt", ArchiveEntryKind.File);
        var browser = new ArchiveBrowserViewModel();
        browser.OpenSession(
            Archive([valid]),
            [valid],
            "valid.zip",
            persistentKeysEnabled: true,
            sameAuthoritativeSource: false);

        Assert.ThrowsExactly<InvalidDataException>(() => browser.OpenSession(
            Archive([valid]) with { EntryCount = 2 },
            [valid],
            "invalid.zip",
            persistentKeysEnabled: true,
            sameAuthoritativeSource: false));

        var missingParent = Entry(
            "orphan.txt",
            "orphan.txt",
            ArchiveEntryKind.File,
            parent: EntryId.New());
        Assert.ThrowsExactly<InvalidDataException>(() => browser.OpenSession(
            Archive([missingParent]),
            [missingParent],
            "invalid.zip",
            persistentKeysEnabled: true,
            sameAuthoritativeSource: false));

        var firstId = EntryId.New();
        var secondId = EntryId.New();
        var first = Entry("first", "first", ArchiveEntryKind.Directory, secondId, entryId: firstId);
        var second = Entry("second", "second", ArchiveEntryKind.Directory, firstId, entryId: secondId);
        Assert.ThrowsExactly<InvalidDataException>(() => browser.OpenSession(
            Archive([first, second]),
            [first, second],
            "invalid.zip",
            persistentKeysEnabled: true,
            sameAuthoritativeSource: false));

        var duplicateKey = valid with { EntryId = EntryId.New() };
        Assert.ThrowsExactly<InvalidDataException>(() => browser.OpenSession(
            Archive([valid, duplicateKey]),
            [valid, duplicateKey],
            "invalid.zip",
            persistentKeysEnabled: true,
            sameAuthoritativeSource: false));

        Assert.AreEqual("valid.zip", browser.ArchiveTitle);
        Assert.AreEqual(valid.EntryId, browser.SelectedEntry?.EntryId);
        Assert.AreEqual(ArchiveBrowserState.Ready, browser.State);
    }

    [TestMethod]
    public void ReverseOrderedDeepHierarchyIsValidatedIterativelyAtTheExactDepthBoundary()
    {
        var boundaryEntries = DirectoryChain(ArchivePathRules.MaximumPathDepth);
        var browser = new ArchiveBrowserViewModel();
        browser.OpenSession(
            Archive(boundaryEntries),
            boundaryEntries,
            "boundary.zip",
            persistentKeysEnabled: true,
            sameAuthoritativeSource: false);
        var selectedBefore = browser.SelectedPersistentKey;

        var excessiveEntries = DirectoryChain(ArchivePathRules.MaximumPathDepth + 4_096);
        var depthError = Assert.ThrowsExactly<InvalidDataException>(() => browser.OpenSession(
            Archive(excessiveEntries),
            excessiveEntries,
            "excessive.zip",
            persistentKeysEnabled: true,
            sameAuthoritativeSource: false));
        StringAssert.Contains(depthError.Message, "depth limit");

        Assert.AreEqual("boundary.zip", browser.ArchiveTitle);
        Assert.AreEqual(selectedBefore, browser.SelectedPersistentKey);
        Assert.AreEqual(ArchiveBrowserState.Ready, browser.State);
        Assert.AreEqual(1, browser.TotalResults);
    }

    [TestMethod]
    public void LargeReverseOrderedCycleIsRefusedIterativelyWithoutReplacingCurrentState()
    {
        const int cycleLength = 32_768;
        var valid = Entry("valid.txt", "valid.txt", ArchiveEntryKind.File);
        var browser = new ArchiveBrowserViewModel();
        browser.OpenSession(
            Archive([valid]),
            [valid],
            "valid.zip",
            persistentKeysEnabled: true,
            sameAuthoritativeSource: false);
        var selectedBefore = browser.SelectedPersistentKey;
        var ids = Enumerable.Range(0, cycleLength)
            .Select(_ => EntryId.New())
            .ToArray();
        var cycle = Enumerable.Range(0, cycleLength)
            .Select(index => Entry(
                $"cycle-{index:D5}",
                $"cycle-{index:D5}",
                ArchiveEntryKind.Directory,
                parent: ids[(index + 1) % cycleLength],
                providerOrdinal: index,
                entryId: ids[index]))
            .ToArray();

        var cycleError = Assert.ThrowsExactly<InvalidDataException>(() => browser.OpenSession(
            Archive(cycle),
            cycle,
            "cycle.zip",
            persistentKeysEnabled: true,
            sameAuthoritativeSource: false));
        StringAssert.Contains(cycleError.Message, "contains a cycle");

        Assert.AreEqual("valid.zip", browser.ArchiveTitle);
        Assert.AreEqual(selectedBefore, browser.SelectedPersistentKey);
        Assert.AreEqual(ArchiveBrowserState.Ready, browser.State);
        Assert.AreEqual(valid.EntryId, browser.SelectedEntry?.EntryId);
    }

    [TestMethod]
    public void DowngradedEntriesRemainBrowsableButCannotClaimPersistentResolution()
    {
        var entry = Entry("legacy.txt", "legacy.txt", ArchiveEntryKind.File) with
        {
            NormalizedPath = null,
            DuplicateOrdinal = 0,
        };
        var browser = new ArchiveBrowserViewModel();
        browser.OpenSession(
            Archive([entry]),
            [entry],
            "legacy.zip",
            persistentKeysEnabled: false,
            sameAuthoritativeSource: false);

        Assert.IsTrue(browser.CanBrowse);
        Assert.IsFalse(browser.PersistentKeysEnabled);
        Assert.IsNull(browser.SelectedPersistentKey);
        Assert.IsFalse(browser.TrySelectPersistentKey(new ArchiveEntryKey("legacy.txt", 0)));

        Assert.ThrowsExactly<InvalidDataException>(() => browser.OpenSession(
            Archive([entry with { NormalizedPath = "legacy.txt" }]),
            [entry with { NormalizedPath = "legacy.txt" }],
            "legacy.zip",
            persistentKeysEnabled: false,
            sameAuthoritativeSource: true));
    }

    [TestMethod]
    public void CapabilityPresentationIsFixedFieldAndNeverDisplaysProviderIdentity()
    {
        var entry = Entry("file.txt", "file.txt", ArchiveEntryKind.File);
        var archive = Archive([entry]) with
        {
            ProviderId = "provider-controlled-text",
            Capabilities = ArchiveCapability.List
                | ArchiveCapability.Extract
                | ArchiveCapability.OpenEntryStream
                | ArchiveCapability.UpdateByRewrite,
        };
        var browser = new ArchiveBrowserViewModel();
        browser.OpenSession(
            archive,
            [entry],
            "safe.zip",
            persistentKeysEnabled: true,
            sameAuthoritativeSource: false);

        StringAssert.Contains(browser.CapabilitySummary, "Read-only source");
        StringAssert.Contains(browser.CapabilitySummary, "Extract available");
        StringAssert.Contains(browser.CapabilitySummary, "Test unavailable");
        StringAssert.Contains(browser.CapabilitySummary, "Rebuild to a new copy available");
        Assert.IsFalse(browser.CapabilitySummary.Contains(archive.ProviderId, StringComparison.Ordinal));
    }

    private static ArchiveDescriptor Archive(
        IReadOnlyCollection<ArchiveEntryDescriptor> entries,
        ArchiveSessionId? sessionId = null,
        ArchiveFingerprint? fingerprint = null) =>
        new(
            sessionId ?? ArchiveSessionId.New(),
            "zip",
            "fake-provider",
            ArchiveCapability.List
                | ArchiveCapability.OpenEntryStream
                | ArchiveCapability.Test
                | ArchiveCapability.Extract,
            fingerprint ?? Fingerprint(),
            entries.Count,
            IsSolid: false,
            HasEncryptedEntries: false,
            IsMultipart: false,
            IsReadOnly: true,
            Warnings: [],
            ArchiveVolumeSetKind.Single);

    private static ArchiveFingerprint Fingerprint() =>
        new(1024, FixedTime, "volume:file", new string('A', 64));

    private static ArchiveEntryDescriptor[] DirectoryChain(int depth)
    {
        var ids = Enumerable.Range(0, depth)
            .Select(_ => EntryId.New())
            .ToArray();
        return Enumerable.Range(0, depth)
            .Select(index => Entry(
                $"level-{index:D5}",
                $"level-{index:D5}",
                ArchiveEntryKind.Directory,
                parent: index == 0 ? null : ids[index - 1],
                providerOrdinal: index,
                entryId: ids[index]))
            .Reverse()
            .ToArray();
    }

    private static ArchiveEntryDescriptor Entry(
        string path,
        string name,
        ArchiveEntryKind kind,
        EntryId? parent = null,
        int providerOrdinal = 0,
        int duplicateOrdinal = 0,
        EntryId? entryId = null) =>
        new(
            entryId ?? EntryId.New(),
            parent,
            providerOrdinal,
            path,
            name,
            kind,
            kind == ArchiveEntryKind.Directory ? null : 16,
            kind == ArchiveEntryKind.Directory ? null : 16,
            FixedTime,
            null,
            kind == ArchiveEntryKind.Directory ? null : "Store",
            null,
            IsEncrypted: false,
            IsSolid: false,
            LinkTarget: null,
            EntrySafetyStatus.Safe,
            SafetyReason: null,
            path,
            duplicateOrdinal);
}
