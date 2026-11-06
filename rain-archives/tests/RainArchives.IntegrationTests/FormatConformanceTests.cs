using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.IntegrationTests;

[TestClass]
public sealed class FormatConformanceTests
{
    private const string FixturePassword = "RainArchives-Fixture-2026!";
    private const string WrongFixturePassword = "wrong-fixture-password";
    private const string AlphaSha256 = "42C82805FB9599A724D099683BB0AD73BA6931A5A859E5B5DD2F2BD43FB00868";
    private const string UnicodeSha256 = "B550ABF52B3500720734589CE534AE1E6F5CA0575D7A8E78BA1659972C3CCE3E";

    [TestMethod]
    public void CheckedCorpusHashesAndManifestAreComplete()
    {
        var corpusRoot = FixturePath();
        var hashManifestPath = Path.Combine(corpusRoot, "generated-hashes.json");
        var manifestPath = Path.Combine(corpusRoot, "manifest.json");
        var entries = JsonSerializer.Deserialize<List<FixtureHash>>(
            File.ReadAllText(hashManifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.IsNotNull(entries);
        Assert.IsTrue(entries.Count > 0);
        var expectedPaths = entries
            .Select(entry => entry.Path)
            .ToHashSet(StringComparer.Ordinal);
        var actualPaths = Directory
            .EnumerateFiles(Path.Combine(corpusRoot, "archives"), "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(corpusRoot, path).Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);
        Assert.IsTrue(expectedPaths.SetEquals(actualPaths),
            $"Fixture checksum coverage differed. Missing: {string.Join(", ", actualPaths.Except(expectedPaths))}; stale: {string.Join(", ", expectedPaths.Except(actualPaths))}");

        foreach (var entry in entries)
        {
            var path = Path.Combine(corpusRoot, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            var information = new FileInfo(path);
            Assert.AreEqual(entry.Length, information.Length, entry.Path);
            using var stream = information.OpenRead();
            Assert.AreEqual(entry.Sha256, Convert.ToHexString(SHA256.HashData(stream)), entry.Path);
        }

        var manifest = File.ReadAllText(manifestPath);
        StringAssert.Contains(manifest, "fixture-password-v1");
        Assert.IsFalse(manifest.Contains(FixturePassword, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ContentDetectionWorksWithoutARecognizedExtension()
    {
        using var root = new TemporaryDirectory();
        var extensionless = Path.Combine(root.Path, "opaque-input.data");
        File.Copy(FixturePath("archives", "zip", "plain.zip"), extensionless);
        await using var client = await ArchiveClient.StartAsync(FindWorker());
        var opened = await client.OpenArchiveAsync(extensionless);
        try
        {
            Assert.AreEqual("zip", opened.Archive.Format);
            Assert.AreEqual(ArchiveVolumeSetKind.Single, opened.Archive.VolumeSetKind);
            var listed = await ReadAllEntriesAsync(client, opened.Archive.SessionId);
            Assert.IsTrue(listed.Any(entry => entry.RawPath == "alpha.txt"));
            Assert.IsTrue(listed.Any(entry => entry.RawPath == "unicode-雪.txt"));
        }
        finally
        {
            await client.CloseArchiveAsync(opened.Archive.SessionId);
        }
    }

    [TestMethod]
    public async Task TarAndSingleStreamFormatsListTestAndExtractThroughWorker()
    {
        var cases = new[]
        {
            new FormatCase("sample.tar", "tar", HasUnicodeEntry: true),
            new FormatCase("sample.tar.gz", "tar.gz", HasUnicodeEntry: true),
            new FormatCase("sample.tar.bz2", "tar.bz2", HasUnicodeEntry: true),
            new FormatCase("sample.tar.xz", "tar.xz", HasUnicodeEntry: true),
            new FormatCase("alpha.txt.gz", "gzip", HasUnicodeEntry: false),
            new FormatCase("alpha.txt.bz2", "bzip2", HasUnicodeEntry: false),
            new FormatCase("alpha.txt.xz", "xz", HasUnicodeEntry: false),
        };
        using var root = new TemporaryDirectory();
        await using var client = await ArchiveClient.StartAsync(FindWorker());

        foreach (var item in cases)
        {
            var opened = await client.OpenArchiveAsync(FixturePath("archives", "streams", item.FileName));
            try
            {
                Assert.AreEqual(item.ExpectedFormat, opened.Archive.Format, item.FileName);
                Assert.AreEqual(
                    ArchiveVolumeSetKind.Single,
                    opened.Archive.VolumeSetKind,
                    item.FileName);
                Assert.IsTrue(opened.Archive.Capabilities.HasFlag(ArchiveCapability.List), item.FileName);
                Assert.IsTrue(opened.Archive.Capabilities.HasFlag(ArchiveCapability.Test), item.FileName);
                Assert.IsTrue(opened.Archive.Capabilities.HasFlag(ArchiveCapability.Extract), item.FileName);
                var listed = await ReadAllEntriesAsync(client, opened.Archive.SessionId);
                Assert.IsTrue(listed.Any(entry => entry.RawPath == "alpha.txt"), item.FileName);
                Assert.AreEqual(
                    item.HasUnicodeEntry,
                    listed.Any(entry => entry.RawPath == "unicode-雪.txt"),
                    item.FileName);

                var tested = await client.StartIntegrityTestAsync(opened.Archive.SessionId, []);
                AssertCompleted(await client.WaitForTerminalAsync(tested.OperationId), item.FileName);

                var destination = Path.Combine(root.Path, item.FileName);
                var extracted = await client.StartExtractionAsync(
                    opened.Archive.SessionId,
                    [],
                    destination);
                AssertCompleted(await client.WaitForTerminalAsync(extracted.OperationId), item.FileName);
                Assert.AreEqual(AlphaSha256, HashFile(Path.Combine(destination, "alpha.txt")), item.FileName);
                AssertNoStagingArtifacts(root.Path);
            }
            finally
            {
                await client.CloseArchiveAsync(opened.Archive.SessionId);
            }
        }
    }

    [TestMethod]
    public async Task ZipEncryptionAndCorruptCrcFailuresAreStructuredAndSecretFree()
    {
        using var root = new TemporaryDirectory();
        await using var client = await ArchiveClient.StartAsync(FindWorker());

        foreach (var fileName in new[] { "aes256.zip", "zipcrypto.zip" })
        {
            var opened = await client.OpenArchiveAsync(FixturePath("archives", "zip", fileName));
            try
            {
                Assert.IsTrue(opened.Archive.Capabilities.HasFlag(ArchiveCapability.ReadEncrypted));
                Assert.IsTrue(opened.Archive.HasEncryptedEntries);

                var noPassword = await client.StartIntegrityTestAsync(opened.Archive.SessionId, []);
                var noPasswordTerminal = await client.WaitForTerminalAsync(noPassword.OperationId);
                AssertFailed(noPasswordTerminal, ArchiveErrorCategory.PasswordRequired, fileName);

                var wrongPasswordSlot = await client.CreateSecretAsync(WrongFixturePassword.AsMemory());
                var wrongPassword = await client.StartIntegrityTestAsync(
                    opened.Archive.SessionId,
                    [],
                    wrongPasswordSlot);
                var wrongPasswordTerminal = await client.WaitForTerminalAsync(wrongPassword.OperationId);
                AssertFailed(wrongPasswordTerminal, ArchiveErrorCategory.IncorrectPassword, fileName);
                AssertSecretFree(wrongPasswordTerminal.Error!);

                var passwordSlot = await client.CreateSecretAsync(FixturePassword.AsMemory());
                var tested = await client.StartIntegrityTestAsync(
                    opened.Archive.SessionId,
                    [],
                    passwordSlot);
                AssertCompleted(await client.WaitForTerminalAsync(tested.OperationId), fileName);

                var extractionSlot = await client.CreateSecretAsync(FixturePassword.AsMemory());
                var destination = Path.Combine(root.Path, $"extract-{fileName}");
                var extracted = await client.StartExtractionAsync(
                    opened.Archive.SessionId,
                    [],
                    destination,
                    extractionSlot);
                AssertCompleted(await client.WaitForTerminalAsync(extracted.OperationId), fileName);
                Assert.AreEqual(AlphaSha256, HashFile(Path.Combine(destination, "alpha.txt")), fileName);
            }
            finally
            {
                await client.CloseArchiveAsync(opened.Archive.SessionId);
            }
        }

        var corrupt = await client.OpenArchiveAsync(
            FixturePath("archives", "zip", "stored-corrupt-crc.zip"));
        try
        {
            var tested = await client.StartIntegrityTestAsync(corrupt.Archive.SessionId, []);
            var testTerminal = await client.WaitForTerminalAsync(tested.OperationId);
            AssertFailed(testTerminal, ArchiveErrorCategory.CorruptArchive, "stored-corrupt-crc.zip");
            Assert.AreEqual("archive.entry_checksum_or_decode_failed", testTerminal.Error!.Code);

            var destination = Path.Combine(root.Path, "corrupt-output");
            var extracted = await client.StartExtractionAsync(corrupt.Archive.SessionId, [], destination);
            var extractTerminal = await client.WaitForTerminalAsync(extracted.OperationId);
            AssertFailed(extractTerminal, ArchiveErrorCategory.CorruptArchive, "stored-corrupt-crc.zip");
            Assert.IsFalse(Directory.Exists(destination));
            AssertNoStagingArtifacts(root.Path);
        }
        finally
        {
            await client.CloseArchiveAsync(corrupt.Archive.SessionId);
        }
    }

    [TestMethod]
    public async Task SevenZipSolidAndEncryptionVariantsPassManagedConformance()
    {
        using var root = new TemporaryDirectory();
        await using var client = await ArchiveClient.StartAsync(FindWorker());

        var solid = await client.OpenArchiveAsync(FixturePath("archives", "7z", "solid.7z"));
        try
        {
            Assert.IsTrue(solid.Archive.IsSolid);
            Assert.AreEqual(ArchiveVolumeSetKind.Single, solid.Archive.VolumeSetKind);
            Assert.IsTrue(solid.Archive.Capabilities.HasFlag(ArchiveCapability.OpenEntryStream));
            var tested = await client.StartIntegrityTestAsync(solid.Archive.SessionId, []);
            AssertCompleted(await client.WaitForTerminalAsync(tested.OperationId), "solid.7z");
            var destination = Path.Combine(root.Path, "solid-7z");
            var extracted = await client.StartExtractionAsync(solid.Archive.SessionId, [], destination);
            AssertCompleted(await client.WaitForTerminalAsync(extracted.OperationId), "solid.7z");
            Assert.AreEqual(AlphaSha256, HashFile(Path.Combine(destination, "alpha.txt")));
        }
        finally
        {
            await client.CloseArchiveAsync(solid.Archive.SessionId);
        }

        var encrypted = await client.OpenArchiveAsync(FixturePath("archives", "7z", "encrypted.7z"));
        try
        {
            Assert.IsTrue(encrypted.Archive.Capabilities.HasFlag(ArchiveCapability.ReadEncrypted));
            Assert.IsTrue(encrypted.Archive.HasEncryptedEntries);
            var noPassword = await client.StartIntegrityTestAsync(encrypted.Archive.SessionId, []);
            AssertFailed(
                await client.WaitForTerminalAsync(noPassword.OperationId),
                ArchiveErrorCategory.PasswordRequired,
                "encrypted.7z");

            var wrongSlot = await client.CreateSecretAsync(WrongFixturePassword.AsMemory());
            var wrong = await client.StartIntegrityTestAsync(encrypted.Archive.SessionId, [], wrongSlot);
            var wrongTerminal = await client.WaitForTerminalAsync(wrong.OperationId);
            AssertFailed(wrongTerminal, ArchiveErrorCategory.IncorrectPassword, "encrypted.7z");
            AssertSecretFree(wrongTerminal.Error!);

            var correctSlot = await client.CreateSecretAsync(FixturePassword.AsMemory());
            var tested = await client.StartIntegrityTestAsync(encrypted.Archive.SessionId, [], correctSlot);
            AssertCompleted(await client.WaitForTerminalAsync(tested.OperationId), "encrypted.7z");

            var extractionSlot = await client.CreateSecretAsync(FixturePassword.AsMemory());
            var destination = Path.Combine(root.Path, "encrypted-7z");
            var extracted = await client.StartExtractionAsync(
                encrypted.Archive.SessionId,
                [],
                destination,
                extractionSlot);
            AssertCompleted(await client.WaitForTerminalAsync(extracted.OperationId), "encrypted.7z");
            Assert.AreEqual(AlphaSha256, HashFile(Path.Combine(destination, "alpha.txt")));
        }
        finally
        {
            await client.CloseArchiveAsync(encrypted.Archive.SessionId);
        }

        var noHeaderPassword = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await client.OpenArchiveAsync(FixturePath("archives", "7z", "header-encrypted.7z")));
        Assert.AreEqual(ArchiveErrorCategory.PasswordRequired, noHeaderPassword.Error.Category);

        var wrongHeaderSlot = await client.CreateSecretAsync(WrongFixturePassword.AsMemory());
        var wrongHeaderPassword = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await client.OpenArchiveAsync(
                FixturePath("archives", "7z", "header-encrypted.7z"),
                wrongHeaderSlot));
        Assert.AreEqual(ArchiveErrorCategory.IncorrectPassword, wrongHeaderPassword.Error.Category);
        AssertSecretFree(wrongHeaderPassword.Error);

        var headerSlot = await client.CreateSecretAsync(FixturePassword.AsMemory());
        var headerEncrypted = await client.OpenArchiveAsync(
            FixturePath("archives", "7z", "header-encrypted.7z"),
            headerSlot);
        try
        {
            Assert.IsTrue(headerEncrypted.Archive.HasEncryptedEntries);
            var tested = await client.StartIntegrityTestAsync(headerEncrypted.Archive.SessionId, []);
            AssertCompleted(await client.WaitForTerminalAsync(tested.OperationId), "header-encrypted.7z");
            var destination = Path.Combine(root.Path, "header-encrypted-7z");
            var extracted = await client.StartExtractionAsync(
                headerEncrypted.Archive.SessionId,
                [],
                destination);
            AssertCompleted(await client.WaitForTerminalAsync(extracted.OperationId), "header-encrypted.7z");
            Assert.AreEqual(AlphaSha256, HashFile(Path.Combine(destination, "alpha.txt")));
        }
        finally
        {
            await client.CloseArchiveAsync(headerEncrypted.Archive.SessionId);
        }
    }

    [TestMethod]
    public async Task RarDirectoryWithoutCrcMetadataOpensListsAndExtracts()
    {
        using var root = new TemporaryDirectory();
        await using var client = await ArchiveClient.StartAsync(FindWorker());
        var opened = await client.OpenArchiveAsync(
            FixturePath("archives", "rar", "directory-no-crc.rar"));
        try
        {
            Assert.AreEqual("rar", opened.Archive.Format);
            Assert.AreEqual(ArchiveVolumeSetKind.Single, opened.Archive.VolumeSetKind);
            var directory = (await ReadAllEntriesAsync(client, opened.Archive.SessionId))
                .Single(entry => entry.RawPath.TrimEnd('/', '\\') == "empty-directory");
            Assert.AreEqual(ArchiveEntryKind.Directory, directory.Kind);
            Assert.IsNull(directory.IntegrityValue);

            var destination = Path.Combine(root.Path, "rar-directory-no-crc");
            var extracted = await client.StartExtractionAsync(
                opened.Archive.SessionId,
                [],
                destination);
            AssertCompleted(
                await client.WaitForTerminalAsync(extracted.OperationId),
                "directory-no-crc.rar");
            Assert.IsTrue(Directory.Exists(Path.Combine(destination, "empty-directory")));
        }
        finally
        {
            await client.CloseArchiveAsync(opened.Archive.SessionId);
        }
    }

    [TestMethod]
    public async Task RarSolidAndHeaderEncryptionVariantsPassManagedConformance()
    {
        using var root = new TemporaryDirectory();
        await using var client = await ArchiveClient.StartAsync(FindWorker());
        var solid = await client.OpenArchiveAsync(FixturePath("archives", "rar", "solid.rar"));
        try
        {
            Assert.IsTrue(solid.Archive.IsSolid);
            Assert.AreEqual(ArchiveVolumeSetKind.Single, solid.Archive.VolumeSetKind);
            Assert.IsFalse(solid.Archive.Capabilities.HasFlag(ArchiveCapability.OpenEntryStream));
            Assert.IsTrue(solid.Archive.Warnings.Any(
                warning => warning.Code == "archive.preview_unavailable"));
            var solidEntry = (await ReadAllEntriesAsync(client, solid.Archive.SessionId))
                .Single(entry => entry.RawPath == "alpha.txt");
            var previewError = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
                await client.OpenEntryStreamAsync(solid.Archive.SessionId, solidEntry.EntryId));
            Assert.AreEqual(ArchiveErrorCategory.UnsupportedCapability, previewError.Error.Category);
            Assert.AreEqual("entry.stream_not_supported", previewError.Error.Code);
            var nestedError = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
                await client.OpenNestedArchiveAsync(solid.Archive.SessionId, solidEntry.EntryId));
            Assert.AreEqual(ArchiveErrorCategory.UnsupportedCapability, nestedError.Error.Category);
            Assert.AreEqual("nested.solid_source_deferred", nestedError.Error.Code);
            var tested = await client.StartIntegrityTestAsync(solid.Archive.SessionId, []);
            AssertCompleted(await client.WaitForTerminalAsync(tested.OperationId), "solid.rar");
            var destination = Path.Combine(root.Path, "solid-rar");
            var extracted = await client.StartExtractionAsync(solid.Archive.SessionId, [], destination);
            AssertCompleted(await client.WaitForTerminalAsync(extracted.OperationId), "solid.rar");
            Assert.AreEqual(AlphaSha256, HashFile(Path.Combine(destination, "alpha.txt")));
        }
        finally
        {
            await client.CloseArchiveAsync(solid.Archive.SessionId);
        }

        var wrongSlot = await client.CreateSecretAsync(WrongFixturePassword.AsMemory());
        var wrong = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await client.OpenArchiveAsync(
                FixturePath("archives", "rar", "header-encrypted.rar"),
                wrongSlot));
        Assert.AreEqual(ArchiveErrorCategory.IncorrectPassword, wrong.Error.Category);
        AssertSecretFree(wrong.Error);

        var correctSlot = await client.CreateSecretAsync(FixturePassword.AsMemory());
        var encrypted = await client.OpenArchiveAsync(
            FixturePath("archives", "rar", "header-encrypted.rar"),
            correctSlot);
        try
        {
            Assert.IsTrue(encrypted.Archive.Capabilities.HasFlag(ArchiveCapability.ReadEncrypted));
            var tested = await client.StartIntegrityTestAsync(encrypted.Archive.SessionId, []);
            AssertCompleted(await client.WaitForTerminalAsync(tested.OperationId), "header-encrypted.rar");
            var destination = Path.Combine(root.Path, "header-encrypted-rar");
            var extracted = await client.StartExtractionAsync(
                encrypted.Archive.SessionId,
                [],
                destination);
            AssertCompleted(await client.WaitForTerminalAsync(extracted.OperationId), "header-encrypted.rar");
            Assert.AreEqual(AlphaSha256, HashFile(Path.Combine(destination, "alpha.txt")));
        }
        finally
        {
            await client.CloseArchiveAsync(encrypted.Archive.SessionId);
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task EntryPreviewStreamsMatchCheckedPayloadAcrossManagedFormats()
    {
        var cases = new[]
        {
            new PreviewFormatCase("7z", "solid.7z"),
            new PreviewFormatCase("7z", "encrypted.7z", StreamWithPassword: true),
            new PreviewFormatCase("7z", "header-encrypted.7z", OpenWithPassword: true),
            new PreviewFormatCase("rar", "plain.rar"),
            new PreviewFormatCase("rar", "header-encrypted.rar", OpenWithPassword: true),
            new PreviewFormatCase("streams", "sample.tar"),
            new PreviewFormatCase("streams", "sample.tar.gz"),
            new PreviewFormatCase("streams", "sample.tar.bz2"),
            new PreviewFormatCase("streams", "sample.tar.xz"),
            new PreviewFormatCase("streams", "alpha.txt.gz"),
            new PreviewFormatCase("streams", "alpha.txt.bz2"),
            new PreviewFormatCase("streams", "alpha.txt.xz"),
        };
        await using var client = await ArchiveClient.StartAsync(FindWorker());

        foreach (var item in cases)
        {
            Guid? openPasswordSlot = item.OpenWithPassword
                ? await client.CreateSecretAsync(FixturePassword.AsMemory())
                : null;
            var opened = await client.OpenArchiveAsync(
                FixturePath("archives", item.Directory, item.FileName),
                openPasswordSlot);
            try
            {
                Assert.AreEqual(ArchiveVolumeSetKind.Single, opened.Archive.VolumeSetKind, item.FileName);
                Assert.IsTrue(
                    opened.Archive.Capabilities.HasFlag(ArchiveCapability.OpenEntryStream),
                    item.FileName);
                var entry = (await ReadAllEntriesAsync(client, opened.Archive.SessionId))
                    .Single(candidate => candidate.Kind == ArchiveEntryKind.File
                        && candidate.RawPath == "alpha.txt");
                Guid? streamPasswordSlot = item.StreamWithPassword
                    ? await client.CreateSecretAsync(FixturePassword.AsMemory())
                    : null;
                await using var lease = await client.OpenEntryStreamAsync(
                    opened.Archive.SessionId,
                    entry.EntryId,
                    maximumBytes: 1024 * 1024,
                    passwordSlotId: streamPasswordSlot);
                using var decoded = new MemoryStream();
                try
                {
                    await lease.Content.CopyToAsync(decoded);
                }
                catch (ArchiveClientException exception)
                {
                    Assert.Fail(
                        $"{item.FileName}: {exception.Error.Category}/{exception.Error.Code}: "
                        + exception.Error.Message);
                }

                Assert.AreEqual(entry.EntryId, lease.EntryId, item.FileName);
                Assert.AreEqual(entry.OriginalSize, lease.DeclaredLength, item.FileName);
                if (entry.OriginalSize.HasValue)
                {
                    Assert.AreEqual(entry.OriginalSize.Value, decoded.Length, item.FileName);
                }

                Assert.AreEqual(
                    AlphaSha256,
                    Convert.ToHexString(SHA256.HashData(decoded.GetBuffer().AsSpan(0, checked((int)decoded.Length)))),
                    item.FileName);
            }
            finally
            {
                await client.CloseArchiveAsync(opened.Archive.SessionId);
            }
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task SolidSevenZipPreviewStreamsCanBeReadOutOfOrderAndRepeated()
    {
        await using var client = await ArchiveClient.StartAsync(FindWorker());
        var item = new PreviewFormatCase("7z", "solid.7z");
        var opened = await client.OpenArchiveAsync(
            FixturePath("archives", item.Directory, item.FileName));
        try
        {
            Assert.IsTrue(opened.Archive.IsSolid, item.FileName);
            var entries = await ReadAllEntriesAsync(client, opened.Archive.SessionId);
            var alpha = entries.Single(entry => entry.RawPath == "alpha.txt");
            var unicode = entries.Single(entry => entry.RawPath == "unicode-雪.txt");

            foreach (var expected in new[]
            {
                (Entry: unicode, Hash: UnicodeSha256),
                (Entry: alpha, Hash: AlphaSha256),
                (Entry: unicode, Hash: UnicodeSha256),
            })
            {
                await using var lease = await client.OpenEntryStreamAsync(
                    opened.Archive.SessionId,
                    expected.Entry.EntryId,
                    maximumBytes: 1024 * 1024);
                using var decoded = new MemoryStream();
                try
                {
                    await lease.Content.CopyToAsync(decoded);
                }
                catch (ArchiveClientException exception)
                {
                    Assert.Fail(
                        $"{item.FileName}/{expected.Entry.RawPath}: "
                        + $"{exception.Error.Category}/{exception.Error.Code}: "
                        + exception.Error.Message);
                }

                Assert.AreEqual(
                    expected.Hash,
                    Convert.ToHexString(
                        SHA256.HashData(decoded.GetBuffer().AsSpan(0, checked((int)decoded.Length)))),
                    item.FileName);
            }
        }
        finally
        {
            await client.CloseArchiveAsync(opened.Archive.SessionId);
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task EncryptedSevenZipPreviewRejectsWrongPasswordWithoutBreakingSession()
    {
        await using var client = await ArchiveClient.StartAsync(FindWorker());
        var opened = await client.OpenArchiveAsync(FixturePath("archives", "7z", "encrypted.7z"));
        try
        {
            var entry = (await ReadAllEntriesAsync(client, opened.Archive.SessionId))
                .Single(candidate => candidate.RawPath == "alpha.txt");
            var wrongSlot = await client.CreateSecretAsync(WrongFixturePassword.AsMemory());
            await using (var wrongLease = await client.OpenEntryStreamAsync(
                opened.Archive.SessionId,
                entry.EntryId,
                passwordSlotId: wrongSlot))
            {
                var wrong = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
                    _ = await wrongLease.Content.ReadAsync(new byte[1024]));
                Assert.AreEqual(ArchiveErrorCategory.IncorrectPassword, wrong.Error.Category);
                AssertSecretFree(wrong.Error);
            }

            var correctSlot = await client.CreateSecretAsync(FixturePassword.AsMemory());
            await using var correctLease = await client.OpenEntryStreamAsync(
                opened.Archive.SessionId,
                entry.EntryId,
                passwordSlotId: correctSlot);
            using var decoded = new MemoryStream();
            await correctLease.Content.CopyToAsync(decoded);
            Assert.AreEqual(
                AlphaSha256,
                Convert.ToHexString(SHA256.HashData(decoded.GetBuffer().AsSpan(0, checked((int)decoded.Length)))));
        }
        finally
        {
            await client.CloseArchiveAsync(opened.Archive.SessionId);
        }
    }

    [TestMethod]
    public async Task CheckedMultipartArchivesAreListOnlyAndOperationsWriteNothing()
    {
        using var root = new TemporaryDirectory();
        await using var client = await ArchiveClient.StartAsync(FindWorker());
        var cases = new[]
        {
            FixturePath("archives", "zip", "split.zip.001"),
            FixturePath("archives", "7z", "split.7z.001"),
            FixturePath("archives", "rar", "split.part01.rar"),
        };

        foreach (var path in cases)
        {
            var opened = await client.OpenArchiveAsync(path);
            try
            {
                Assert.IsTrue(opened.Archive.IsMultipart, Path.GetFileName(path));
                Assert.AreEqual(
                    ArchiveVolumeSetKind.ConfirmedMultipart,
                    opened.Archive.VolumeSetKind,
                    Path.GetFileName(path));
                Assert.IsTrue(opened.Archive.Capabilities.HasFlag(ArchiveCapability.ReadVolumes));
                Assert.IsFalse(opened.Archive.Capabilities.HasFlag(ArchiveCapability.OpenEntryStream));
                Assert.IsFalse(opened.Archive.Capabilities.HasFlag(ArchiveCapability.Test));
                Assert.IsFalse(opened.Archive.Capabilities.HasFlag(ArchiveCapability.Extract));
                var listed = await ReadAllEntriesAsync(client, opened.Archive.SessionId);
                Assert.IsTrue(listed.Any(entry => entry.RawPath == "payload.bin"));

                var previewError = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
                    await client.OpenEntryStreamAsync(
                        opened.Archive.SessionId,
                        listed.Single(entry => entry.RawPath == "payload.bin").EntryId));
                Assert.AreEqual(ArchiveErrorCategory.UnsupportedCapability, previewError.Error.Category);
                Assert.AreEqual("archive.multipart_operation_deferred", previewError.Error.Code);

                var testError = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
                    await client.StartIntegrityTestAsync(opened.Archive.SessionId, []));
                Assert.AreEqual(ArchiveErrorCategory.UnsupportedCapability, testError.Error.Category);
                Assert.AreEqual("archive.multipart_operation_deferred", testError.Error.Code);

                var destination = Path.Combine(root.Path, Path.GetFileName(path));
                var extractError = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
                    await client.StartExtractionAsync(opened.Archive.SessionId, [], destination));
                Assert.AreEqual(ArchiveErrorCategory.UnsupportedCapability, extractError.Error.Category);
                Assert.IsFalse(Directory.Exists(destination));
            }
            finally
            {
                await client.CloseArchiveAsync(opened.Archive.SessionId);
            }
        }
    }

    [TestMethod]
    public async Task MultiDigitNumericSplitZipListsAndMissingFinalVolumeFailsClosed()
    {
        using var root = new TemporaryDirectory();
        var source = FixturePath("archives", "zip", "plain.zip");
        var completeRoot = Path.Combine(root.Path, "complete");
        var incompleteRoot = Path.Combine(root.Path, "incomplete");
        var volumeCount = CreateNumericSplitZip(source, completeRoot, omitFinalVolume: false);
        Assert.IsGreaterThan(10, volumeCount);
        Assert.IsTrue(File.Exists(Path.Combine(completeRoot, "multi.zip.010")));

        Assert.AreEqual(
            volumeCount,
            CreateNumericSplitZip(source, incompleteRoot, omitFinalVolume: true));
        Assert.IsFalse(File.Exists(Path.Combine(incompleteRoot, $"multi.zip.{volumeCount:D3}")));

        await using var client = await ArchiveClient.StartAsync(FindWorker());
        var incomplete = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await client.OpenArchiveAsync(Path.Combine(incompleteRoot, "multi.zip.001")));
        Assert.AreEqual(ArchiveErrorCategory.InvalidArchive, incomplete.Error.Category);
        Assert.AreEqual("archive.invalid", incomplete.Error.Code);

        var opened = await client.OpenArchiveAsync(Path.Combine(completeRoot, "multi.zip.001"));
        try
        {
            Assert.AreEqual("zip", opened.Archive.Format);
            Assert.IsTrue(opened.Archive.IsMultipart);
            Assert.AreEqual(ArchiveVolumeSetKind.ConfirmedMultipart, opened.Archive.VolumeSetKind);
            Assert.AreEqual(
                ArchiveCapability.List
                    | ArchiveCapability.ReadEncrypted
                    | ArchiveCapability.ReadVolumes,
                opened.Archive.Capabilities);
            CollectionAssert.AreEquivalent(
                new[] { "alpha.txt", "beta.txt", "unicode-雪.txt" },
                (await ReadAllEntriesAsync(client, opened.Archive.SessionId))
                    .Select(entry => entry.RawPath)
                    .ToArray());

            var testFailure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
                await client.StartIntegrityTestAsync(opened.Archive.SessionId, []));
            Assert.AreEqual(ArchiveErrorCategory.UnsupportedCapability, testFailure.Error.Category);
            Assert.AreEqual("archive.multipart_operation_deferred", testFailure.Error.Code);

            var destination = Path.Combine(root.Path, "multi-digit-output");
            var extractFailure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
                await client.StartExtractionAsync(opened.Archive.SessionId, [], destination));
            Assert.AreEqual(ArchiveErrorCategory.UnsupportedCapability, extractFailure.Error.Category);
            Assert.AreEqual("archive.multipart_operation_deferred", extractFailure.Error.Code);
            Assert.IsFalse(Directory.Exists(destination));
        }
        finally
        {
            await client.CloseArchiveAsync(opened.Archive.SessionId);
        }

        AssertNoStagingArtifacts(root.Path);
    }

    [TestMethod]
    public async Task ClassicSplitZipListsFromFirstAndTerminalVolumesAndFailsClosedWhenIncomplete()
    {
        using var root = new TemporaryDirectory();
        await using var client = await ArchiveClient.StartAsync(FindWorker());
        var fixtureRoot = FixturePath("archives", "zip");
        var selections = new[]
        {
            Path.Combine(fixtureRoot, "classic-split.z01"),
            Path.Combine(fixtureRoot, "classic-split.zip"),
        };

        foreach (var path in selections)
        {
            var opened = await client.OpenArchiveAsync(path);
            try
            {
                Assert.AreEqual("zip", opened.Archive.Format, Path.GetFileName(path));
                Assert.IsTrue(opened.Archive.IsMultipart, Path.GetFileName(path));
                Assert.AreEqual(
                    ArchiveVolumeSetKind.ConfirmedMultipart,
                    opened.Archive.VolumeSetKind,
                    Path.GetFileName(path));
                Assert.AreEqual(
                    ArchiveCapability.List
                        | ArchiveCapability.ReadEncrypted
                        | ArchiveCapability.ReadVolumes,
                    opened.Archive.Capabilities,
                    Path.GetFileName(path));
                CollectionAssert.AreEquivalent(
                    new[] { "classic-payload.bin", "unicode-雪.txt" },
                    (await ReadAllEntriesAsync(client, opened.Archive.SessionId))
                        .Select(entry => entry.RawPath)
                        .ToArray(),
                    Path.GetFileName(path));

                var testFailure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
                    await client.StartIntegrityTestAsync(opened.Archive.SessionId, []));
                Assert.AreEqual(ArchiveErrorCategory.UnsupportedCapability, testFailure.Error.Category);
                Assert.AreEqual("archive.multipart_operation_deferred", testFailure.Error.Code);

                var destination = Path.Combine(root.Path, $"output-{Path.GetExtension(path)[1..]}");
                var extractFailure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
                    await client.StartExtractionAsync(opened.Archive.SessionId, [], destination));
                Assert.AreEqual(ArchiveErrorCategory.UnsupportedCapability, extractFailure.Error.Category);
                Assert.AreEqual("archive.multipart_operation_deferred", extractFailure.Error.Code);
                Assert.IsFalse(Directory.Exists(destination));
            }
            finally
            {
                await client.CloseArchiveAsync(opened.Archive.SessionId);
            }
        }

        var fakeZip = Path.Combine(root.Path, "extension-is-not-proof.zip");
        await File.WriteAllBytesAsync(fakeZip, "not an archive"u8.ToArray());
        var fakeFailure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await client.OpenArchiveAsync(fakeZip));
        Assert.AreEqual(ArchiveErrorCategory.UnsupportedFormat, fakeFailure.Error.Category);
        Assert.AreEqual("format.not_detected", fakeFailure.Error.Code);

        var missingTerminalRoot = Path.Combine(root.Path, "missing-terminal");
        Directory.CreateDirectory(missingTerminalRoot);
        for (var volumeNumber = 1; volumeNumber <= 6; volumeNumber++)
        {
            var fileName = $"classic-split.z{volumeNumber:D2}";
            File.Copy(
                Path.Combine(fixtureRoot, fileName),
                Path.Combine(missingTerminalRoot, fileName));
        }

        var missingTerminal = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await client.OpenArchiveAsync(Path.Combine(missingTerminalRoot, "classic-split.z01")));
        Assert.AreEqual(ArchiveErrorCategory.InvalidArchive, missingTerminal.Error.Category);
        Assert.AreEqual("archive.missing_zip_terminal_volume", missingTerminal.Error.Code);

        var missingMiddleRoot = Path.Combine(root.Path, "missing-middle");
        Directory.CreateDirectory(missingMiddleRoot);
        foreach (var volume in Directory.EnumerateFiles(fixtureRoot, "classic-split.*"))
        {
            if (!string.Equals(Path.GetFileName(volume), "classic-split.z03", StringComparison.Ordinal))
            {
                File.Copy(volume, Path.Combine(missingMiddleRoot, Path.GetFileName(volume)));
            }
        }

        var missingMiddle = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
            await client.OpenArchiveAsync(Path.Combine(missingMiddleRoot, "classic-split.z01")));
        Assert.AreEqual(ArchiveErrorCategory.InvalidArchive, missingMiddle.Error.Category);

        var recovered = await client.OpenArchiveAsync(selections[1]);
        try
        {
            Assert.IsTrue(recovered.Archive.IsMultipart);
            Assert.AreEqual(ArchiveVolumeSetKind.ConfirmedMultipart, recovered.Archive.VolumeSetKind);
            Assert.AreEqual(2, (await ReadAllEntriesAsync(client, recovered.Archive.SessionId)).Count);
        }
        finally
        {
            await client.CloseArchiveAsync(recovered.Archive.SessionId);
        }

        AssertNoStagingArtifacts(root.Path);
    }

    [TestMethod]
    public async Task TruncatedArchivesFailWithStableArchiveErrorsAndNoPartialOutput()
    {
        using var root = new TemporaryDirectory();
        await using var client = await ArchiveClient.StartAsync(FindWorker());

        foreach (var (path, expectedCode) in new[]
                 {
                     (
                         FixturePath("archives", "zip", "plain-truncated.zip"),
                         "archive.invalid_zip_central_directory"),
                     (FixturePath("archives", "rar", "plain-truncated.rar"), "archive.invalid"),
                 })
        {
            var exception = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
                await client.OpenArchiveAsync(path));
            Assert.AreEqual(ArchiveErrorCategory.InvalidArchive, exception.Error.Category);
            Assert.AreEqual(expectedCode, exception.Error.Code);
        }

        var truncatedGzip = await client.OpenArchiveAsync(
            FixturePath("archives", "streams", "alpha-truncated.gz"));
        try
        {
            var tested = await client.StartIntegrityTestAsync(truncatedGzip.Archive.SessionId, []);
            var testTerminal = await client.WaitForTerminalAsync(tested.OperationId);
            AssertFailed(testTerminal, ArchiveErrorCategory.CorruptArchive, "alpha-truncated.gz");
            Assert.AreEqual("archive.entry_size_mismatch", testTerminal.Error!.Code);

            var destination = Path.Combine(root.Path, "truncated-gzip-output");
            var extracted = await client.StartExtractionAsync(
                truncatedGzip.Archive.SessionId,
                [],
                destination);
            var extractTerminal = await client.WaitForTerminalAsync(extracted.OperationId);
            AssertFailed(extractTerminal, ArchiveErrorCategory.CorruptArchive, "alpha-truncated.gz");
            Assert.IsFalse(Directory.Exists(destination));
            AssertNoStagingArtifacts(root.Path);
        }
        finally
        {
            await client.CloseArchiveAsync(truncatedGzip.Archive.SessionId);
        }
    }

    [TestMethod]
    public async Task EmptyZeroByteAndSelectedEntryOperationsAreCorrect()
    {
        using var root = new TemporaryDirectory();
        var emptyArchivePath = Path.Combine(root.Path, "empty.zip");
        using (var file = new FileStream(emptyArchivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
        }

        var mixedArchivePath = Path.Combine(root.Path, "zero-and-files.zip");
        using (var file = new FileStream(mixedArchivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            var zero = archive.CreateEntry("zero.txt", CompressionLevel.NoCompression);
            zero.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using (zero.Open())
            {
            }

            var alpha = archive.CreateEntry("alpha.txt", CompressionLevel.NoCompression);
            alpha.LastWriteTime = zero.LastWriteTime;
            using (var stream = alpha.Open())
            {
                stream.Write("selected-alpha"u8);
            }

            var beta = archive.CreateEntry("beta.txt", CompressionLevel.NoCompression);
            beta.LastWriteTime = zero.LastWriteTime;
            using (var stream = beta.Open())
            {
                stream.Write("unselected-beta"u8);
            }
        }

        await using var client = await ArchiveClient.StartAsync(FindWorker());
        var empty = await client.OpenArchiveAsync(emptyArchivePath);
        try
        {
            Assert.AreEqual(0, empty.Archive.EntryCount);
            Assert.AreEqual(0, (await ReadAllEntriesAsync(client, empty.Archive.SessionId)).Count);

            var tested = await client.StartIntegrityTestAsync(empty.Archive.SessionId, []);
            AssertCompleted(await client.WaitForTerminalAsync(tested.OperationId), "empty.zip");

            var destination = Path.Combine(root.Path, "empty-output");
            var extracted = await client.StartExtractionAsync(empty.Archive.SessionId, [], destination);
            AssertCompleted(await client.WaitForTerminalAsync(extracted.OperationId), "empty.zip");
            Assert.IsTrue(Directory.Exists(destination));
            Assert.AreEqual(0, Directory.EnumerateFileSystemEntries(destination).Count());
        }
        finally
        {
            await client.CloseArchiveAsync(empty.Archive.SessionId);
        }

        var mixed = await client.OpenArchiveAsync(mixedArchivePath);
        try
        {
            var entries = await ReadAllEntriesAsync(client, mixed.Archive.SessionId);
            var zero = entries.Single(entry => entry.RawPath == "zero.txt");
            var alpha = entries.Single(entry => entry.RawPath == "alpha.txt");

            var tested = await client.StartIntegrityTestAsync(mixed.Archive.SessionId, [zero.EntryId]);
            AssertCompleted(await client.WaitForTerminalAsync(tested.OperationId), "zero.txt");

            var destination = Path.Combine(root.Path, "selected-output");
            var extracted = await client.StartExtractionAsync(
                mixed.Archive.SessionId,
                [alpha.EntryId],
                destination);
            AssertCompleted(await client.WaitForTerminalAsync(extracted.OperationId), "selected alpha.txt");
            Assert.AreEqual("selected-alpha", await File.ReadAllTextAsync(Path.Combine(destination, "alpha.txt")));
            Assert.IsFalse(File.Exists(Path.Combine(destination, "zero.txt")));
            Assert.IsFalse(File.Exists(Path.Combine(destination, "beta.txt")));
            AssertNoStagingArtifacts(root.Path);
        }
        finally
        {
            await client.CloseArchiveAsync(mixed.Archive.SessionId);
        }
    }

    [TestMethod]
    public async Task Zip64EntryCountListsAndExtractsSelectedLastEntry()
    {
        const int entryCount = ushort.MaxValue + 1;
        using var root = new TemporaryDirectory();
        var archivePath = Path.Combine(root.Path, "entry-count-zip64.zip");
        using (var file = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            for (var index = 0; index < entryCount; index++)
            {
                var entry = archive.CreateEntry($"entry-{index:D6}.txt", CompressionLevel.NoCompression);
                entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                if (index == entryCount - 1)
                {
                    using var stream = entry.Open();
                    stream.WriteByte(0x5A);
                }
            }
        }

        await using var client = await ArchiveClient.StartAsync(FindWorker());
        var opened = await client.OpenArchiveAsync(archivePath);
        try
        {
            Assert.AreEqual("zip", opened.Archive.Format);
            Assert.AreEqual(entryCount, opened.Archive.EntryCount);
            var first = await client.ListEntriesAsync(opened.Archive.SessionId, 0, 1);
            var last = await client.ListEntriesAsync(opened.Archive.SessionId, entryCount - 1, 1);
            Assert.AreEqual("entry-000000.txt", first.Entries.Single().RawPath);
            var lastEntry = last.Entries.Single();
            Assert.AreEqual("entry-065535.txt", lastEntry.RawPath);

            var tested = await client.StartIntegrityTestAsync(opened.Archive.SessionId, [lastEntry.EntryId]);
            AssertCompleted(await client.WaitForTerminalAsync(tested.OperationId), "ZIP64 selected test");

            var destination = Path.Combine(root.Path, "zip64-output");
            var extracted = await client.StartExtractionAsync(
                opened.Archive.SessionId,
                [lastEntry.EntryId],
                destination);
            AssertCompleted(await client.WaitForTerminalAsync(extracted.OperationId), "ZIP64 selected extract");
            CollectionAssert.AreEqual(
                new byte[] { 0x5A },
                await File.ReadAllBytesAsync(Path.Combine(destination, lastEntry.RawPath)));
        }
        finally
        {
            await client.CloseArchiveAsync(opened.Archive.SessionId);
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Zip64CentralDirectoryPastFourGiBListsTestsAndExtracts()
    {
        using var root = new TemporaryDirectory();
        var archivePath = Path.Combine(root.Path, "large-offset-zip64.zip");
        CreateSparseLargeOffsetZip64(archivePath);
        var archiveInfo = new FileInfo(archivePath);
        Assert.IsGreaterThan((long)uint.MaxValue, archiveInfo.Length);
        Assert.IsTrue(
            (File.GetAttributes(archivePath) & FileAttributes.SparseFile) != 0,
            "The ZIP64 fixture must remain sparse rather than consuming four GiB of physical disk.");

        await using var client = await ArchiveClient.StartAsync(FindWorker());
        var opened = await client.OpenArchiveAsync(archivePath);
        try
        {
            Assert.AreEqual("zip", opened.Archive.Format);
            Assert.AreEqual(1, opened.Archive.EntryCount);
            Assert.IsFalse(opened.Archive.IsMultipart);
            Assert.AreEqual(ArchiveVolumeSetKind.Single, opened.Archive.VolumeSetKind);
            var listed = await client.ListEntriesAsync(opened.Archive.SessionId, 0, 1);
            var entry = listed.Entries.Single();
            Assert.AreEqual("payload.bin", entry.RawPath);
            Assert.AreEqual(1, entry.OriginalSize);

            var tested = await client.StartIntegrityTestAsync(opened.Archive.SessionId, [entry.EntryId]);
            AssertCompleted(await client.WaitForTerminalAsync(tested.OperationId), "large-offset ZIP64 test");

            var destination = Path.Combine(root.Path, "large-offset-output");
            var extracted = await client.StartExtractionAsync(
                opened.Archive.SessionId,
                [entry.EntryId],
                destination);
            AssertCompleted(
                await client.WaitForTerminalAsync(extracted.OperationId),
                "large-offset ZIP64 extract");
            CollectionAssert.AreEqual(
                new byte[] { 0x5A },
                await File.ReadAllBytesAsync(Path.Combine(destination, "payload.bin")));
        }
        finally
        {
            await client.CloseArchiveAsync(opened.Archive.SessionId);
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task Zip64PerEntryExtraFieldsRoundTripAndMalformedVariantsFailStructurally()
    {
        using var root = new TemporaryDirectory();
        var validPath = Path.Combine(root.Path, "zip64-entry-extra-valid.zip");
        CreateZip64PerEntryExtraFixture(validPath, Zip64EntryExtraMode.Valid);

        var zeroDiskPath = Path.Combine(root.Path, "zip64-entry-extra-zero-disk.zip");
        var malformed = new[]
        {
            (
                Path: zeroDiskPath,
                Mode: Zip64EntryExtraMode.ZeroDisk,
                Category: ArchiveErrorCategory.UnsupportedFormat,
                Code: "archive.unsupported_zip64_disk_start"),
            (
                Path: Path.Combine(root.Path, "zip64-entry-extra-nonzero-disk.zip"),
                Mode: Zip64EntryExtraMode.NonZeroDisk,
                Category: ArchiveErrorCategory.InvalidArchive,
                Code: "archive.invalid_zip_volume_reference"),
            (
                Path: Path.Combine(root.Path, "zip64-entry-extra-missing.zip"),
                Mode: Zip64EntryExtraMode.Missing,
                Category: ArchiveErrorCategory.InvalidArchive,
                Code: "archive.invalid_zip_central_directory"),
            (
                Path: Path.Combine(root.Path, "zip64-entry-extra-truncated.zip"),
                Mode: Zip64EntryExtraMode.Truncated,
                Category: ArchiveErrorCategory.InvalidArchive,
                Code: "archive.invalid_zip_central_directory"),
        };
        foreach (var item in malformed)
        {
            CreateZip64PerEntryExtraFixture(item.Path, item.Mode);
        }

        AssertBuiltInZipReadsPayload(validPath);
        AssertBuiltInZipReadsPayload(zeroDiskPath);

        await using var client = await ArchiveClient.StartAsync(FindWorker());
        var opened = await client.OpenArchiveAsync(validPath);
        try
        {
            Assert.AreEqual("zip", opened.Archive.Format);
            Assert.IsFalse(opened.Archive.IsMultipart);
            Assert.AreEqual(ArchiveVolumeSetKind.Single, opened.Archive.VolumeSetKind);
            Assert.AreEqual(1, opened.Archive.EntryCount);
            Assert.AreEqual(
                ArchiveCapability.List
                    | ArchiveCapability.OpenEntryStream
                    | ArchiveCapability.Test
                    | ArchiveCapability.Extract
                    | ArchiveCapability.ReadEncrypted
                    | ArchiveCapability.UpdateByRewrite,
                opened.Archive.Capabilities);

            var entry = (await client.ListEntriesAsync(opened.Archive.SessionId, 0, 1))
                .Entries
                .Single();
            Assert.AreEqual("payload.bin", entry.RawPath);
            Assert.AreEqual(1, entry.OriginalSize);
            Assert.AreEqual(1, entry.CompressedSize);

            var tested = await client.StartIntegrityTestAsync(opened.Archive.SessionId, [entry.EntryId]);
            AssertCompleted(await client.WaitForTerminalAsync(tested.OperationId), "per-entry ZIP64 test");

            var destination = Path.Combine(root.Path, "zip64-entry-extra-output");
            var extracted = await client.StartExtractionAsync(
                opened.Archive.SessionId,
                [entry.EntryId],
                destination);
            AssertCompleted(
                await client.WaitForTerminalAsync(extracted.OperationId),
                "per-entry ZIP64 extract");
            CollectionAssert.AreEqual(
                new byte[] { 0x5A },
                await File.ReadAllBytesAsync(Path.Combine(destination, "payload.bin")));
        }
        finally
        {
            await client.CloseArchiveAsync(opened.Archive.SessionId);
        }

        foreach (var item in malformed)
        {
            var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
                await client.OpenArchiveAsync(item.Path));
            Assert.AreEqual(item.Category, failure.Error.Category, item.Path);
            Assert.AreEqual(item.Code, failure.Error.Code, item.Path);

            var control = await client.OpenArchiveAsync(validPath);
            try
            {
                Assert.AreEqual(1, control.Archive.EntryCount, item.Path);
                Assert.AreEqual(
                    "payload.bin",
                    (await client.ListEntriesAsync(control.Archive.SessionId, 0, 1))
                        .Entries
                        .Single()
                        .RawPath,
                    item.Path);
            }
            finally
            {
                await client.CloseArchiveAsync(control.Archive.SessionId);
            }
        }

        AssertNoStagingArtifacts(root.Path);
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task MalformedZip64TrailersFailStructurallyAndWorkerRecovers()
    {
        using var root = new TemporaryDirectory();
        var knownGoodPath = Path.Combine(root.Path, "known-good.zip");
        File.Copy(FixturePath("archives", "zip", "plain.zip"), knownGoodPath);
        var knownGoodBytes = await File.ReadAllBytesAsync(knownGoodPath);
        var endRecordOffset = knownGoodBytes.AsSpan().LastIndexOf(
            new byte[] { 0x50, 0x4B, 0x05, 0x06 });
        Assert.IsGreaterThanOrEqualTo(0, endRecordOffset);

        var missingEndRecord = Path.Combine(root.Path, "zip-missing-terminal-record.data");
        await File.WriteAllBytesAsync(
            missingEndRecord,
            knownGoodBytes.AsSpan(0, endRecordOffset).ToArray());

        var missingLocatorBytes = knownGoodBytes.ToArray();
        missingLocatorBytes.AsSpan(endRecordOffset + 16, sizeof(uint)).Fill(0xFF);
        var missingLocator = Path.Combine(root.Path, "zip64-missing-locator.data");
        await File.WriteAllBytesAsync(missingLocator, missingLocatorBytes);

        var outsideLocator = Path.Combine(root.Path, "zip64-locator-outside.data");
        await File.WriteAllBytesAsync(
            outsideLocator,
            CreateZip64LocatorMutation(knownGoodBytes, endRecordOffset, ulong.MaxValue));

        var missingZip64End = Path.Combine(root.Path, "zip64-end-record-missing.data");
        await File.WriteAllBytesAsync(
            missingZip64End,
            CreateZip64LocatorMutation(knownGoodBytes, endRecordOffset, 0));

        var invalidZip64Length = Path.Combine(root.Path, "zip64-end-record-invalid-length.data");
        await File.WriteAllBytesAsync(
            invalidZip64Length,
            CreateInvalidLengthZip64Trailer(knownGoodBytes, endRecordOffset));

        await using var client = await ArchiveClient.StartAsync(FindWorker());
        foreach (var path in new[]
                 {
                     missingEndRecord,
                     missingLocator,
                     outsideLocator,
                     missingZip64End,
                     invalidZip64Length,
                 })
        {
            var failure = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
                await client.OpenArchiveAsync(path));
            Assert.AreEqual(ArchiveErrorCategory.InvalidArchive, failure.Error.Category, path);
            Assert.AreEqual("archive.invalid_zip_central_directory", failure.Error.Code, path);
        }

        var control = await client.OpenArchiveAsync(knownGoodPath);
        try
        {
            Assert.IsGreaterThan(0, control.Archive.EntryCount);
            Assert.IsNotEmpty(
                (await client.ListEntriesAsync(control.Archive.SessionId, 0, 1)).Entries);
        }
        finally
        {
            await client.CloseArchiveAsync(control.Archive.SessionId);
        }

        AssertNoStagingArtifacts(root.Path);
    }

    [TestMethod]
    public async Task MissingAndInterchangedVolumesFailClosed()
    {
        using var root = new TemporaryDirectory();
        await using var client = await ArchiveClient.StartAsync(FindWorker());
        var cases = new[]
        {
            new VolumeCase("zip", "split.zip.*", "split.zip.001", "split.zip.009", "split.zip.002", "split.zip.003"),
            new VolumeCase("7z", "split.7z.*", "split.7z.001", "split.7z.009", "split.7z.002", "split.7z.003"),
            new VolumeCase("rar", "split.part*.rar", "split.part01.rar", "split.part09.rar", "split.part02.rar", "split.part03.rar"),
        };

        foreach (var item in cases)
        {
            var source = FixturePath("archives", item.Format);
            var missingRoot = Path.Combine(root.Path, $"{item.Format}-missing");
            Directory.CreateDirectory(missingRoot);
            foreach (var volume in Directory.EnumerateFiles(source, item.Pattern))
            {
                if (!string.Equals(Path.GetFileName(volume), item.LastVolume, StringComparison.Ordinal))
                {
                    File.Copy(volume, Path.Combine(missingRoot, Path.GetFileName(volume)));
                }
            }

            var missing = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
                await client.OpenArchiveAsync(Path.Combine(missingRoot, item.FirstVolume)));
            Assert.AreEqual(ArchiveErrorCategory.InvalidArchive, missing.Error.Category, item.Format);
            Assert.AreEqual("archive.invalid", missing.Error.Code, item.Format);

            var swappedRoot = Path.Combine(root.Path, $"{item.Format}-swapped");
            Directory.CreateDirectory(swappedRoot);
            foreach (var volume in Directory.EnumerateFiles(source, item.Pattern))
            {
                File.Copy(volume, Path.Combine(swappedRoot, Path.GetFileName(volume)));
            }

            var secondPath = Path.Combine(swappedRoot, item.SecondVolume);
            var thirdPath = Path.Combine(swappedRoot, item.ThirdVolume);
            var secondBytes = await File.ReadAllBytesAsync(secondPath);
            var thirdBytes = await File.ReadAllBytesAsync(thirdPath);
            await File.WriteAllBytesAsync(secondPath, thirdBytes);
            await File.WriteAllBytesAsync(thirdPath, secondBytes);

            var opened = await client.OpenArchiveAsync(Path.Combine(swappedRoot, item.FirstVolume));
            try
            {
                Assert.IsTrue(opened.Archive.IsMultipart, item.Format);
                Assert.AreEqual(
                    ArchiveVolumeSetKind.ConfirmedMultipart,
                    opened.Archive.VolumeSetKind,
                    item.Format);
                Assert.IsTrue(opened.Archive.Capabilities.HasFlag(ArchiveCapability.ReadVolumes), item.Format);
                Assert.IsFalse(opened.Archive.Capabilities.HasFlag(ArchiveCapability.OpenEntryStream), item.Format);
                Assert.IsFalse(opened.Archive.Capabilities.HasFlag(ArchiveCapability.Test), item.Format);
                Assert.IsFalse(opened.Archive.Capabilities.HasFlag(ArchiveCapability.Extract), item.Format);
                Assert.IsTrue((await ReadAllEntriesAsync(client, opened.Archive.SessionId))
                    .Any(entry => entry.RawPath == "payload.bin"), item.Format);

                var testError = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
                    await client.StartIntegrityTestAsync(opened.Archive.SessionId, []));
                Assert.AreEqual(ArchiveErrorCategory.UnsupportedCapability, testError.Error.Category, item.Format);
                Assert.AreEqual("archive.multipart_operation_deferred", testError.Error.Code, item.Format);

                var destination = Path.Combine(root.Path, $"{item.Format}-swapped-output");
                var extractError = await Assert.ThrowsExactlyAsync<ArchiveClientException>(async () =>
                    await client.StartExtractionAsync(opened.Archive.SessionId, [], destination));
                Assert.AreEqual(ArchiveErrorCategory.UnsupportedCapability, extractError.Error.Category, item.Format);
                Assert.IsFalse(Directory.Exists(destination), item.Format);
            }
            finally
            {
                await client.CloseArchiveAsync(opened.Archive.SessionId);
            }
        }
    }

    private static async Task<IReadOnlyList<ArchiveEntryDescriptor>> ReadAllEntriesAsync(
        ArchiveClient client,
        ArchiveSessionId sessionId)
    {
        var entries = new List<ArchiveEntryDescriptor>();
        var offset = 0;
        while (true)
        {
            var page = await client.ListEntriesAsync(sessionId, offset, 1024);
            entries.AddRange(page.Entries);
            if (page.NextOffset is not { } next)
            {
                return entries;
            }

            offset = next;
        }
    }

    private static void AssertCompleted(OperationEvent terminal, string fixture)
    {
        Assert.IsTrue(
            terminal.Phase is OperationPhase.Completed or OperationPhase.CompletedWithWarnings,
            $"{fixture}: {terminal.Error?.Code} {terminal.Error?.Message}");
    }

    private static void AssertFailed(
        OperationEvent terminal,
        ArchiveErrorCategory category,
        string fixture)
    {
        Assert.AreEqual(OperationPhase.Failed, terminal.Phase, fixture);
        Assert.IsNotNull(terminal.Error, fixture);
        Assert.AreEqual(category, terminal.Error.Category, $"{fixture}: {terminal.Error.Code}");
    }

    private static void AssertSecretFree(ArchiveError error)
    {
        Assert.IsFalse(error.Code.Contains(FixturePassword, StringComparison.Ordinal));
        Assert.IsFalse(error.Message.Contains(FixturePassword, StringComparison.Ordinal));
        Assert.IsFalse(error.Code.Contains(WrongFixturePassword, StringComparison.Ordinal));
        Assert.IsFalse(error.Message.Contains(WrongFixturePassword, StringComparison.Ordinal));
    }

    private static void AssertNoStagingArtifacts(string parent)
    {
        var leftovers = Directory
            .EnumerateFileSystemEntries(parent, "*.rainarchives-*.staging", SearchOption.TopDirectoryOnly)
            .ToArray();
        Assert.AreEqual(0, leftovers.Length, string.Join(Environment.NewLine, leftovers));
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string FixturePath(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, "fixtures", "corpus", .. parts]);

    private static int CreateNumericSplitZip(
        string sourcePath,
        string destinationDirectory,
        bool omitFinalVolume)
    {
        const int chunkSize = 40;
        var archive = File.ReadAllBytes(sourcePath);
        var volumeCount = checked((archive.Length + chunkSize - 1) / chunkSize);
        var volumesToWrite = omitFinalVolume ? volumeCount - 1 : volumeCount;
        Directory.CreateDirectory(destinationDirectory);

        for (var volume = 1; volume <= volumesToWrite; volume++)
        {
            var offset = checked((volume - 1) * chunkSize);
            var length = Math.Min(chunkSize, archive.Length - offset);
            File.WriteAllBytes(
                Path.Combine(destinationDirectory, $"multi.zip.{volume:D3}"),
                archive.AsSpan(offset, length).ToArray());
        }

        return volumeCount;
    }

    private static void CreateZip64PerEntryExtraFixture(
        string path,
        Zip64EntryExtraMode extraMode)
    {
        const uint localHeaderSignature = 0x04034B50;
        const uint centralDirectorySignature = 0x02014B50;
        const uint endOfCentralDirectorySignature = 0x06054B50;
        const ushort versionNeeded = 45;
        const ushort dosTime = (3 << 11) | (4 << 5) | 3;
        const ushort dosDate = ((2026 - 1980) << 9) | (1 << 5) | 2;
        var name = "payload.bin"u8.ToArray();
        const byte payload = 0x5A;
        var crc32 = ComputeCrc32([payload]);
        var centralExtra = CreateCentralZip64Extra(extraMode);

        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(file, System.Text.Encoding.UTF8, leaveOpen: true);

        writer.Write(localHeaderSignature);
        writer.Write(versionNeeded);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(dosTime);
        writer.Write(dosDate);
        writer.Write(crc32);
        writer.Write(uint.MaxValue);
        writer.Write(uint.MaxValue);
        writer.Write((ushort)name.Length);
        writer.Write((ushort)20);
        writer.Write(name);
        writer.Write((ushort)0x0001);
        writer.Write((ushort)16);
        writer.Write(1UL);
        writer.Write(1UL);
        writer.Write(payload);

        var centralDirectoryOffset = file.Position;
        writer.Write(centralDirectorySignature);
        writer.Write(versionNeeded);
        writer.Write(versionNeeded);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(dosTime);
        writer.Write(dosDate);
        writer.Write(crc32);
        writer.Write(uint.MaxValue);
        writer.Write(uint.MaxValue);
        writer.Write((ushort)name.Length);
        writer.Write(checked((ushort)centralExtra.Length));
        writer.Write((ushort)0);
        writer.Write(extraMode == Zip64EntryExtraMode.Valid ? (ushort)0 : ushort.MaxValue);
        writer.Write((ushort)0);
        writer.Write(0u);
        writer.Write(uint.MaxValue);
        writer.Write(name);
        writer.Write(centralExtra);
        var centralDirectorySize = checked(file.Position - centralDirectoryOffset);

        writer.Write(endOfCentralDirectorySignature);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write(checked((uint)centralDirectorySize));
        writer.Write(checked((uint)centralDirectoryOffset));
        writer.Write((ushort)0);
        writer.Flush();
    }

    private static byte[] CreateCentralZip64Extra(Zip64EntryExtraMode extraMode)
    {
        if (extraMode == Zip64EntryExtraMode.Missing)
        {
            return [];
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write((ushort)0x0001);
        writer.Write(checked((ushort)(extraMode switch
        {
            Zip64EntryExtraMode.Valid => 24,
            Zip64EntryExtraMode.Truncated => 29,
            _ => 28,
        })));
        writer.Write(1UL);
        writer.Write(1UL);
        writer.Write(0UL);
        if (extraMode is Zip64EntryExtraMode.ZeroDisk or Zip64EntryExtraMode.NonZeroDisk)
        {
            writer.Write(extraMode == Zip64EntryExtraMode.NonZeroDisk ? 1u : 0u);
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static void AssertBuiltInZipReadsPayload(string path)
    {
        using var file = File.OpenRead(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);
        var entry = archive.Entries.Single();
        Assert.AreEqual("payload.bin", entry.FullName);
        Assert.AreEqual(1, entry.Length);
        using var stream = entry.Open();
        Assert.AreEqual(0x5A, stream.ReadByte());
        Assert.AreEqual(-1, stream.ReadByte());
    }

    private static byte[] CreateZip64LocatorMutation(
        byte[] source,
        int endRecordOffset,
        ulong zip64EndOffset)
    {
        const uint zip64LocatorSignature = 0x07064B50;
        var endRecord = source.AsSpan(endRecordOffset).ToArray();
        endRecord.AsSpan(16, sizeof(uint)).Fill(0xFF);

        using var output = new MemoryStream(source.Length + 20);
        output.Write(source.AsSpan(0, endRecordOffset));
        using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(zip64LocatorSignature);
        writer.Write(0u);
        writer.Write(zip64EndOffset);
        writer.Write(1u);
        writer.Write(endRecord);
        writer.Flush();
        return output.ToArray();
    }

    private static byte[] CreateInvalidLengthZip64Trailer(byte[] source, int endRecordOffset)
    {
        const uint zip64EndSignature = 0x06064B50;
        const uint zip64LocatorSignature = 0x07064B50;
        var endRecord = source.AsSpan(endRecordOffset).ToArray();
        endRecord.AsSpan(16, sizeof(uint)).Fill(0xFF);

        using var output = new MemoryStream(source.Length + 76);
        output.Write(source.AsSpan(0, endRecordOffset));
        using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        var zip64EndOffset = checked((ulong)output.Position);
        writer.Write(zip64EndSignature);
        writer.Write(45UL);
        writer.Write(new byte[44]);
        writer.Write(zip64LocatorSignature);
        writer.Write(0u);
        writer.Write(zip64EndOffset);
        writer.Write(1u);
        writer.Write(endRecord);
        writer.Flush();
        return output.ToArray();
    }

    private static void CreateSparseLargeOffsetZip64(string path)
    {
        const uint fsctlSetSparse = 0x000900C4;
        const long centralDirectoryOffset = (long)uint.MaxValue + 4_096;
        const uint localHeaderSignature = 0x04034B50;
        const uint centralDirectorySignature = 0x02014B50;
        const uint zip64EndSignature = 0x06064B50;
        const uint zip64LocatorSignature = 0x07064B50;
        const uint endOfCentralDirectorySignature = 0x06054B50;
        const ushort versionNeeded = 45;
        const ushort dosTime = (3 << 11) | (4 << 5) | 3;
        const ushort dosDate = ((2026 - 1980) << 9) | (1 << 5) | 2;
        var name = "payload.bin"u8.ToArray();
        const byte payload = 0x5A;
        var crc32 = ComputeCrc32([payload]);

        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        if (!NativeMethods.DeviceIoControl(
                file.SafeFileHandle,
                fsctlSetSparse,
                IntPtr.Zero,
                0,
                IntPtr.Zero,
                0,
                out _,
                IntPtr.Zero))
        {
            throw new IOException(
                "Could not mark the generated ZIP64 fixture sparse.",
                new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }

        using var writer = new BinaryWriter(file, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(localHeaderSignature);
        writer.Write(versionNeeded);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(dosTime);
        writer.Write(dosDate);
        writer.Write(crc32);
        writer.Write(1u);
        writer.Write(1u);
        writer.Write((ushort)name.Length);
        writer.Write((ushort)0);
        writer.Write(name);
        writer.Write(payload);

        file.Position = centralDirectoryOffset;
        writer.Write(centralDirectorySignature);
        writer.Write(versionNeeded);
        writer.Write(versionNeeded);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(dosTime);
        writer.Write(dosDate);
        writer.Write(crc32);
        writer.Write(1u);
        writer.Write(1u);
        writer.Write((ushort)name.Length);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(name);
        var centralDirectorySize = checked(file.Position - centralDirectoryOffset);
        var zip64EndOffset = file.Position;

        writer.Write(zip64EndSignature);
        writer.Write(44UL);
        writer.Write(versionNeeded);
        writer.Write(versionNeeded);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(1UL);
        writer.Write(1UL);
        writer.Write(checked((ulong)centralDirectorySize));
        writer.Write(checked((ulong)centralDirectoryOffset));

        writer.Write(zip64LocatorSignature);
        writer.Write(0u);
        writer.Write(checked((ulong)zip64EndOffset));
        writer.Write(1u);

        writer.Write(endOfCentralDirectorySignature);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write(checked((uint)centralDirectorySize));
        writer.Write(uint.MaxValue);
        writer.Write((ushort)0);
        writer.Flush();
        file.Flush(flushToDisk: true);
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xEDB88320u);
            }
        }

        return ~crc;
    }

    private static string FindWorker()
    {
        var worker = Path.Combine(AppContext.BaseDirectory, "RainArchives.Worker.exe");
        if (!File.Exists(worker))
        {
            throw new FileNotFoundException(
                "The integration test output does not contain RainArchives.Worker.exe.",
                worker);
        }

        return worker;
    }

    private sealed record FixtureHash(string Path, long Length, string Sha256);
    private sealed record FormatCase(string FileName, string ExpectedFormat, bool HasUnicodeEntry);
    private sealed record PreviewFormatCase(
        string Directory,
        string FileName,
        bool OpenWithPassword = false,
        bool StreamWithPassword = false);
    private sealed record VolumeCase(
        string Format,
        string Pattern,
        string FirstVolume,
        string LastVolume,
        string SecondVolume,
        string ThirdVolume);

    private enum Zip64EntryExtraMode
    {
        Valid,
        ZeroDisk,
        NonZeroDisk,
        Missing,
        Truncated,
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainArchives.FormatConformanceTests",
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
#pragma warning disable SYSLIB1054 // Test-only sparse-fixture control avoids enabling unsafe source generation.
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(
            SafeFileHandle device,
            uint ioControlCode,
            IntPtr inputBuffer,
            uint inputBufferSize,
            IntPtr outputBuffer,
            uint outputBufferSize,
            out uint bytesReturned,
            IntPtr overlapped);
#pragma warning restore SYSLIB1054
    }
}
