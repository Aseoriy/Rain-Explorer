using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.App.ViewModels;
using RainArchives.Contracts;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class ArchiveCommandContextTests
{
    [TestMethod]
    public void ReadyFolderUsesOnlyCanonicalVisibleDescriptorAndFixedStatus()
    {
        var id = EntryId.New();
        var canonical = Entry(id, ArchiveEntryKind.Directory, "folder\u202E C:\\controlled");
        var substituted = Entry(id, ArchiveEntryKind.File, "substituted");
        var archive = Archive(
            ArchiveCapability.List
            | ArchiveCapability.OpenEntryStream
            | ArchiveCapability.Test
            | ArchiveCapability.Extract);

        var context = ArchiveCommandContext.Create(
            archive,
            ArchiveBrowserState.Ready,
            isBusy: false,
            [canonical],
            [id],
            substituted);

        Assert.AreEqual(id, context.PrimaryEntryId);
        Assert.AreEqual(1, context.SelectionCount);
        Assert.IsTrue(context.CanNavigate);
        Assert.IsFalse(context.CanPreview);
        Assert.IsTrue(context.CanShowProperties);
        Assert.IsTrue(context.CanTestIntegrity);
        Assert.IsTrue(context.CanExtractArchive);
        Assert.IsTrue(context.CanExtractSelected);
        Assert.IsFalse(context.StatusAnnouncement.Contains("controlled", StringComparison.Ordinal));
        Assert.IsFalse(context.StatusAnnouncement.Contains('\u202E'));
    }

    [TestMethod]
    public void ReadyFileRequiresCurrentStreamCapabilityAndIgnoresStaleSelectionIds()
    {
        var file = Entry(EntryId.New(), ArchiveEntryKind.File, "file.txt");
        var stale = EntryId.New();
        var context = ArchiveCommandContext.Create(
            Archive(ArchiveCapability.List | ArchiveCapability.OpenEntryStream),
            ArchiveBrowserState.Ready,
            isBusy: false,
            [file],
            [stale],
            file);

        Assert.AreEqual(1, context.SelectionCount);
        Assert.IsFalse(context.CanNavigate);
        Assert.IsTrue(context.CanPreview);
        Assert.IsTrue(context.CanShowProperties);
        Assert.IsFalse(context.CanTestIntegrity);
        Assert.IsFalse(context.CanExtractArchive);
        Assert.IsFalse(context.CanExtractSelected);

        var downgraded = ArchiveCommandContext.Create(
            Archive(ArchiveCapability.List),
            ArchiveBrowserState.Ready,
            isBusy: false,
            [file],
            [file.EntryId],
            file);
        Assert.IsFalse(downgraded.CanPreview);
        Assert.IsFalse(downgraded.CanActivatePrimary);
    }

    [TestMethod]
    public void EmptySessionKeepsOnlySupportedSessionCommands()
    {
        var context = ArchiveCommandContext.Create(
            Archive(ArchiveCapability.List | ArchiveCapability.Test | ArchiveCapability.Extract),
            ArchiveBrowserState.Empty,
            isBusy: false,
            [],
            [],
            null);

        Assert.IsNull(context.PrimaryEntryId);
        Assert.AreEqual(0, context.SelectionCount);
        Assert.IsFalse(context.CanNavigate);
        Assert.IsFalse(context.CanPreview);
        Assert.IsTrue(context.CanShowProperties);
        Assert.IsTrue(context.CanTestIntegrity);
        Assert.IsTrue(context.CanExtractArchive);
        Assert.IsFalse(context.CanExtractSelected);
    }

    [TestMethod]
    public void BusyOfflineStaleAndMissingSessionDisableEveryCommand()
    {
        var file = Entry(EntryId.New(), ArchiveEntryKind.File, "file.txt");
        var archive = Archive(
            ArchiveCapability.List
            | ArchiveCapability.OpenEntryStream
            | ArchiveCapability.Test
            | ArchiveCapability.Extract);

        foreach (var context in new[]
                 {
                     ArchiveCommandContext.Create(
                         archive,
                         ArchiveBrowserState.Ready,
                         isBusy: true,
                         [file],
                         [file.EntryId],
                         file),
                     ArchiveCommandContext.Create(
                         archive,
                         ArchiveBrowserState.Offline,
                         isBusy: false,
                         [],
                         [],
                         null),
                     ArchiveCommandContext.Create(
                         archive,
                         ArchiveBrowserState.Stale,
                         isBusy: false,
                         [],
                         [],
                         null),
                     ArchiveCommandContext.Create(
                         null,
                         ArchiveBrowserState.Ready,
                         isBusy: false,
                         [file],
                         [file.EntryId],
                         file),
                 })
        {
            Assert.AreEqual(ArchiveCommandContext.Unavailable, context);
            Assert.IsFalse(context.CanActivatePrimary);
        }
    }

    [TestMethod]
    public void VisibleAndRequestedSelectionsRejectAnythingBeyondExistingPageBound()
    {
        var oversized = Enumerable.Range(0, ArchiveBrowserViewModel.VisiblePageSize + 1)
            .Select(index => Entry(EntryId.New(), ArchiveEntryKind.File, $"{index}.txt"))
            .ToArray();

        Assert.ThrowsExactly<InvalidDataException>(() => ArchiveCommandContext.Create(
            Archive(ArchiveCapability.List),
            ArchiveBrowserState.Ready,
            isBusy: false,
            oversized,
            [],
            null));
        Assert.ThrowsExactly<InvalidDataException>(() => ArchiveCommandContext.Create(
            Archive(ArchiveCapability.List),
            ArchiveBrowserState.Ready,
            isBusy: false,
            [],
            oversized.Select(entry => entry.EntryId).ToArray(),
            null));
    }

    [TestMethod]
    public void DuplicateVisibleIdentityIsRejectedBeforeAnyCommandIsAuthorized()
    {
        var id = EntryId.New();
        var first = Entry(id, ArchiveEntryKind.File, "first.txt");
        var second = Entry(id, ArchiveEntryKind.Directory, "second");

        Assert.ThrowsExactly<InvalidDataException>(() => ArchiveCommandContext.Create(
            Archive(
                ArchiveCapability.List
                | ArchiveCapability.OpenEntryStream
                | ArchiveCapability.Test
                | ArchiveCapability.Extract),
            ArchiveBrowserState.Ready,
            isBusy: false,
            [first, second],
            [id],
            first));
    }

    private static ArchiveDescriptor Archive(ArchiveCapability capabilities) => new(
        ArchiveSessionId.New(),
        "zip",
        "provider-not-for-presentation",
        capabilities,
        new ArchiveFingerprint(1, DateTimeOffset.UnixEpoch, null, new string('A', 64)),
        1,
        IsSolid: false,
        HasEncryptedEntries: false,
        IsMultipart: false,
        IsReadOnly: true,
        Warnings: [],
        ArchiveVolumeSetKind.Single);

    private static ArchiveEntryViewModel Entry(
        EntryId id,
        ArchiveEntryKind kind,
        string name) => new(new ArchiveEntryDescriptor(
            id,
            null,
            0,
            name,
            name,
            kind,
            kind == ArchiveEntryKind.File ? 1 : null,
            kind == ArchiveEntryKind.File ? 1 : null,
            null,
            null,
            null,
            null,
            IsEncrypted: false,
            IsSolid: false,
            LinkTarget: null,
            EntrySafetyStatus.Safe,
            SafetyReason: null));
}
