using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.App.Services;
using RainArchives.App.ViewModels;
using RainArchives.Contracts;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class SafePreviewViewModelTests
{
    [TestMethod]
    public void TextPreviewDisclosesOnlyProvenEncodingAndBoundedLength()
    {
        var sessionId = ArchiveSessionId.New();
        var entry = CreateEntry("name\u202E.txt");
        var viewModel = new SafePreviewViewModel();

        viewModel.Begin(sessionId, entry);
        Assert.AreEqual(SafePreviewState.Loading, viewModel.State);
        Assert.AreEqual("name\\u202E.txt", viewModel.EntryTitle);
        Assert.IsTrue(viewModel.IsLoading);

        viewModel.Apply(
            sessionId,
            entry,
            EntryPreviewResult.Available(
                "inert text",
                EntryPreviewTextEncoding.Utf16LittleEndian,
                24));

        Assert.AreEqual(SafePreviewState.Text, viewModel.State);
        Assert.AreEqual("inert text", viewModel.Text);
        Assert.IsTrue(viewModel.IsTextVisible);
        Assert.IsFalse(viewModel.IsStatusVisible);
        StringAssert.Contains(viewModel.Details, "UTF-16 LE");
        StringAssert.Contains(viewModel.Details, "24 B");
        StringAssert.Contains(viewModel.Details, "delete-on-close");
    }

    [TestMethod]
    [DataRow(EntryPreviewOutcome.Binary, SafePreviewState.Binary, "Binary content")]
    [DataRow(EntryPreviewOutcome.Oversize, SafePreviewState.Oversize, "Preview too large")]
    [DataRow(EntryPreviewOutcome.UnsupportedEncoding, SafePreviewState.Unsupported, "Unsupported text encoding")]
    [DataRow(EntryPreviewOutcome.UnsupportedEntry, SafePreviewState.Unsupported, "Preview unsupported")]
    public void TypedUnavailableResultsHaveFixedPresentation(
        EntryPreviewOutcome outcome,
        SafePreviewState expectedState,
        string expectedTitle)
    {
        var sessionId = ArchiveSessionId.New();
        var entry = CreateEntry("item.bin");
        var viewModel = new SafePreviewViewModel();
        viewModel.Begin(sessionId, entry);

        viewModel.Apply(
            sessionId,
            entry,
            EntryPreviewResult.Unavailable(
                outcome,
                sourceWasRead: outcome is EntryPreviewOutcome.Binary
                    or EntryPreviewOutcome.UnsupportedEncoding,
                byteLength: outcome is EntryPreviewOutcome.Binary
                    or EntryPreviewOutcome.UnsupportedEncoding ? 12 : null));

        Assert.AreEqual(expectedState, viewModel.State);
        Assert.AreEqual(expectedTitle, viewModel.StateTitle);
        Assert.AreEqual(string.Empty, viewModel.Text);
        Assert.IsTrue(viewModel.IsStatusVisible);
        StringAssert.Contains(viewModel.Details, "1 MiB display limit");
    }

    [TestMethod]
    public void ExplicitOfflineStaleCancelledAndErrorStatesAreFixedAndRedacted()
    {
        var entry = CreateEntry("entry.txt");
        var viewModel = new SafePreviewViewModel();

        viewModel.ShowOffline(entry);
        Assert.AreEqual(SafePreviewState.Offline, viewModel.State);
        StringAssert.Contains(viewModel.StateMessage, "No cached result");

        viewModel.ShowStale(entry);
        Assert.AreEqual(SafePreviewState.Stale, viewModel.State);
        StringAssert.Contains(viewModel.StateMessage, "Reopen");

        viewModel.ShowCancelled(entry);
        Assert.AreEqual(SafePreviewState.Cancelled, viewModel.State);
        StringAssert.Contains(viewModel.StateMessage, "cancelled and drained");

        viewModel.ShowError(entry);
        Assert.AreEqual(SafePreviewState.Error, viewModel.State);
        StringAssert.Contains(viewModel.StateMessage, "not displayed");
        Assert.IsFalse(viewModel.StateMessage.Contains("provider-controlled", StringComparison.Ordinal));
    }

    [TestMethod]
    public void MalformedResultDoesNotReplacePriorAuthoritativeText()
    {
        var sessionId = ArchiveSessionId.New();
        var entry = CreateEntry("entry.txt");
        var viewModel = new SafePreviewViewModel();
        viewModel.Begin(sessionId, entry);
        viewModel.Apply(
            sessionId,
            entry,
            EntryPreviewResult.Available(
                "trusted projection",
                EntryPreviewTextEncoding.Utf8,
                18));

        Assert.ThrowsExactly<InvalidDataException>(() => viewModel.Apply(
            sessionId,
            entry,
            new EntryPreviewResult(
                EntryPreviewOutcome.Binary,
                true,
                "must not display",
                null,
                16)));

        Assert.AreEqual(SafePreviewState.Text, viewModel.State);
        Assert.AreEqual("trusted projection", viewModel.Text);
        Assert.IsFalse(viewModel.Text.Contains("must not display", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ResetReturnsToIdleWithoutRetainingContent()
    {
        var sessionId = ArchiveSessionId.New();
        var entry = CreateEntry("entry.txt");
        var viewModel = new SafePreviewViewModel();
        viewModel.Begin(sessionId, entry);
        viewModel.Apply(
            sessionId,
            entry,
            EntryPreviewResult.Available(
                "sensitive preview contents",
                EntryPreviewTextEncoding.Utf8,
                26));

        viewModel.Reset();

        Assert.AreEqual(SafePreviewState.Idle, viewModel.State);
        Assert.AreEqual(string.Empty, viewModel.Text);
        Assert.AreEqual("Safe preview", viewModel.EntryTitle);
        Assert.IsFalse(viewModel.HasPresentation);
    }

    private static ArchiveEntryDescriptor CreateEntry(string name) => new(
        EntryId.New(),
        null,
        0,
        name,
        name,
        ArchiveEntryKind.File,
        24,
        12,
        null,
        null,
        null,
        null,
        IsEncrypted: false,
        IsSolid: false,
        null,
        EntrySafetyStatus.Safe,
        null,
        name,
        0);
}
