using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.App.ViewModels;
using RainArchives.Contracts;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class ArchivePropertiesViewModelTests
{
    [TestMethod]
    public void ReadyProjectionUsesOnlyValidatedFixedFieldsAndOmitsProviderText()
    {
        var archive = CreateArchive();
        var entry = CreateEntry();
        var viewModel = new ArchivePropertiesViewModel();

        viewModel.Synchronize(
            archive,
            ArchiveBrowserState.Ready,
            entry,
            persistentKeysEnabled: true);

        Assert.AreEqual(ArchivePropertiesState.Ready, viewModel.State);
        Assert.IsTrue(viewModel.HasArchiveProperties);
        Assert.IsTrue(viewModel.HasSelectedEntry);
        var archiveValues = viewModel.ArchiveItems.ToDictionary(item => item.Name, item => item.Value);
        Assert.AreEqual("ZIP", archiveValues["Format"]);
        Assert.AreEqual("Read-only session", archiveValues["Archive access"]);
        Assert.AreEqual("Rebuild to a distinct new copy available", archiveValues["Modification"]);
        Assert.AreEqual("1", archiveValues["Session warnings"]);
        var entryValues = viewModel.EntryItems.ToDictionary(item => item.Name, item => item.Value);
        Assert.AreEqual("unsafe\\u202E.txt", entryValues["Name"]);
        Assert.AreEqual("Regular file", entryValues["Type"]);
        Assert.AreEqual("#3", entryValues["Duplicate occurrence"]);
        Assert.AreEqual("25% of original size", entryValues["Compression ratio"]);
        Assert.AreEqual("2026-08-30 12:34:56.0000000 UTC", entryValues["Modified"]);
        Assert.AreEqual("0x00000021", entryValues["Windows attributes"]);
        Assert.AreEqual("Link metadata present; target is not followed", entryValues["Link state"]);
        Assert.AreEqual("Present; verification not implied", entryValues["Integrity metadata"]);
        Assert.AreEqual("Unsafe archive path", entryValues["Safety"]);

        var visible = string.Join(
            '\n',
            viewModel.ArchiveItems.Concat(viewModel.EntryItems)
                .Select(item => $"{item.Name}:{item.Value}:{item.HelpText}"));
        Assert.IsFalse(visible.Contains("provider-secret", StringComparison.Ordinal));
        Assert.IsFalse(visible.Contains("provider warning path", StringComparison.Ordinal));
        Assert.IsFalse(visible.Contains("provider compression", StringComparison.Ordinal));
        Assert.IsFalse(visible.Contains("provider integrity", StringComparison.Ordinal));
        Assert.IsFalse(visible.Contains("provider safety reason", StringComparison.Ordinal));
        Assert.IsFalse(visible.Contains("provider link target", StringComparison.Ordinal));
        Assert.IsFalse(visible.Contains(archive.Fingerprint.PrefixSha256, StringComparison.Ordinal));
        Assert.IsFalse(visible.Contains(archive.Fingerprint.FileIdentity!, StringComparison.Ordinal));
    }

    [TestMethod]
    public void OptionalAndMathematicallyInvalidValuesAreOmittedWithoutInference()
    {
        var entry = CreateEntry() with
        {
            OriginalSize = 0,
            CompressedSize = null,
            ModifiedTimeUtc = null,
            WindowsAttributes = null,
            IntegrityValue = null,
            LinkTarget = null,
            IsEncrypted = false,
            Kind = ArchiveEntryKind.File,
        };
        var viewModel = new ArchivePropertiesViewModel();

        viewModel.Synchronize(
            CreateArchive(),
            ArchiveBrowserState.Ready,
            entry,
            persistentKeysEnabled: true);

        var names = viewModel.EntryItems.Select(item => item.Name).ToArray();
        CollectionAssert.DoesNotContain(names, "Compression ratio");
        CollectionAssert.DoesNotContain(names, "Compressed size");
        CollectionAssert.DoesNotContain(names, "Modified");
        CollectionAssert.DoesNotContain(names, "Windows attributes");
        Assert.AreEqual(
            "Not supplied",
            viewModel.EntryItems.Single(item => item.Name == "Integrity metadata").Value);
    }

    [TestMethod]
    public void OfflineAndStaleStatesClearPriorValuesInsteadOfPresentingStaleMetadata()
    {
        var viewModel = new ArchivePropertiesViewModel();
        viewModel.Synchronize(
            CreateArchive(),
            ArchiveBrowserState.Ready,
            CreateEntry(),
            persistentKeysEnabled: true);

        viewModel.Synchronize(
            CreateArchive(),
            ArchiveBrowserState.Offline,
            null,
            persistentKeysEnabled: true);

        Assert.AreEqual(ArchivePropertiesState.Offline, viewModel.State);
        Assert.AreEqual(0, viewModel.ArchiveItems.Count);
        Assert.AreEqual(0, viewModel.EntryItems.Count);
        StringAssert.Contains(viewModel.StateMessage, "No stale values");

        viewModel.Synchronize(
            CreateArchive(),
            ArchiveBrowserState.Stale,
            null,
            persistentKeysEnabled: true);
        Assert.AreEqual(ArchivePropertiesState.Stale, viewModel.State);
        Assert.AreEqual(0, viewModel.ArchiveItems.Count);
    }

    [TestMethod]
    public void MalformedReplacementPreservesPriorAuthoritativeProjection()
    {
        var viewModel = new ArchivePropertiesViewModel();
        viewModel.Synchronize(
            CreateArchive(),
            ArchiveBrowserState.Ready,
            CreateEntry(),
            persistentKeysEnabled: true);
        var priorArchive = viewModel.ArchiveItems.ToArray();
        var priorEntry = viewModel.EntryItems.ToArray();
        var malformed = CreateEntry() with { RawPath = string.Empty };

        Assert.ThrowsExactly<InvalidDataException>(() => viewModel.Synchronize(
            CreateArchive(),
            ArchiveBrowserState.Ready,
            malformed,
            persistentKeysEnabled: true));

        CollectionAssert.AreEqual(priorArchive, viewModel.ArchiveItems.ToArray());
        CollectionAssert.AreEqual(priorEntry, viewModel.EntryItems.ToArray());
        Assert.AreEqual(ArchivePropertiesState.Ready, viewModel.State);
    }

    [TestMethod]
    public void UnsupportedFormatIsMappedToFixedTextRatherThanEchoed()
    {
        var viewModel = new ArchivePropertiesViewModel();
        viewModel.Synchronize(
            CreateArchive() with { Format = "provider-controlled-format" },
            ArchiveBrowserState.Unsupported,
            null,
            persistentKeysEnabled: false);

        Assert.AreEqual(
            "Other negotiated archive format",
            viewModel.ArchiveItems.Single(item => item.Name == "Format").Value);
    }

    private static ArchiveDescriptor CreateArchive() => new(
        ArchiveSessionId.New(),
        "zip",
        "provider-secret",
        ArchiveCapability.List
            | ArchiveCapability.OpenEntryStream
            | ArchiveCapability.Test
            | ArchiveCapability.Extract
            | ArchiveCapability.UpdateByRewrite,
        new ArchiveFingerprint(
            4096,
            DateTimeOffset.Parse("2026-08-31T00:00:00Z"),
            "hidden-file-identity",
            new string('A', 64)),
        1,
        IsSolid: false,
        HasEncryptedEntries: true,
        IsMultipart: false,
        IsReadOnly: true,
        [new ArchiveWarning("provider.warning", "provider warning path C:\\secret")],
        ArchiveVolumeSetKind.Single);

    private static ArchiveEntryDescriptor CreateEntry() => new(
        EntryId.New(),
        null,
        7,
        "unsafe\u202E.txt",
        "unsafe\u202E.txt",
        ArchiveEntryKind.File,
        400,
        100,
        DateTimeOffset.Parse("2026-08-30T12:34:56Z"),
        0x21,
        "provider compression",
        "provider integrity",
        IsEncrypted: true,
        IsSolid: false,
        "provider link target",
        EntrySafetyStatus.UnsafePath,
        "provider safety reason",
        "unsafe\u202E.txt",
        2);
}
