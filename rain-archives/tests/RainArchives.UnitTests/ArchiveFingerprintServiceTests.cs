using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;
using RainArchives.Core;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class ArchiveFingerprintServiceTests
{
    [TestMethod]
    public async Task UnchangedFilePassesValidation()
    {
        using var file = new TemporaryFile("stable archive bytes");
        var fingerprint = await ArchiveFingerprintService.CreateAsync(file.Path, CancellationToken.None);

        await ArchiveFingerprintService.EnsureUnchangedAsync(
            file.Path,
            fingerprint,
            CancellationToken.None);

        Assert.AreEqual(new FileInfo(file.Path).Length, fingerprint.Length);
        Assert.AreEqual(64, fingerprint.PrefixSha256.Length);
        if (OperatingSystem.IsWindows())
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(fingerprint.FileIdentity));
        }
    }

    [TestMethod]
    public async Task ChangedFileIsRejected()
    {
        using var file = new TemporaryFile("original archive bytes");
        var fingerprint = await ArchiveFingerprintService.CreateAsync(file.Path, CancellationToken.None);
        await File.AppendAllTextAsync(file.Path, " changed");

        var exception = await Assert.ThrowsExactlyAsync<ArchiveProviderException>(async () =>
            await ArchiveFingerprintService.EnsureUnchangedAsync(
                file.Path,
                fingerprint,
                CancellationToken.None));

        Assert.AreEqual(ArchiveErrorCategory.SourceChanged, exception.Category);
        Assert.AreEqual("archive.source_changed", exception.Code);
    }

    [TestMethod]
    public async Task SourceLeasePreventsNewWriteAndDeleteSharingOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows sharing semantics are required for this test.");
        }

        using var file = new TemporaryFile("locked archive bytes");
        await using var lease = ArchiveSourceLease.Acquire(file.Path);

        await Assert.ThrowsExactlyAsync<IOException>(
            () => File.AppendAllTextAsync(file.Path, " mutation"));
        Assert.ThrowsExactly<IOException>(() => File.Delete(file.Path));
    }

    private sealed class TemporaryFile : IDisposable
    {
        public TemporaryFile(string content)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainArchives.Tests",
                Guid.NewGuid().ToString("N"),
                "archive.bin");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, content);
        }

        public string Path { get; }

        public void Dispose()
        {
            var parent = System.IO.Path.GetDirectoryName(Path)!;
            if (Directory.Exists(parent))
            {
                Directory.Delete(parent, recursive: true);
            }
        }
    }
}
