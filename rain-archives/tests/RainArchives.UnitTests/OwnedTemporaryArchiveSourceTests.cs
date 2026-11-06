using System.ComponentModel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Core;
using RainArchives.Security;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class OwnedTemporaryArchiveSourceTests
{
    [TestMethod]
    public async Task CompletedSourceIsImmutableReadableAndDeletedWithItsOwner()
    {
        using var root = new TemporaryDirectory();
        var payload = "checked nested archive bytes"u8.ToArray();
        var source = OwnedTemporaryArchiveSource.Create(root.Path);
        var directoryPath = source.DirectoryPath;
        var sourcePath = source.SourcePath;
        try
        {
            await source.Output.WriteAsync(payload);
            await source.CompleteAsync();

            CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(sourcePath));
            var fingerprint = await ArchiveFingerprintService.CreateAsync(sourcePath, CancellationToken.None);
            Assert.AreEqual(payload.Length, fingerprint.Length);
            Assert.ThrowsExactly<IOException>(() =>
            {
                using var writer = new FileStream(
                    sourcePath,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.Read);
            });
        }
        finally
        {
            await source.DisposeAsync();
        }

        Assert.IsFalse(File.Exists(sourcePath));
        Assert.IsFalse(Directory.Exists(directoryPath));
    }

    [TestMethod]
    public async Task IncompleteSourceIsDeletedWithoutPublication()
    {
        using var root = new TemporaryDirectory();
        var source = OwnedTemporaryArchiveSource.Create(root.Path);
        var directoryPath = source.DirectoryPath;
        await source.Output.WriteAsync("partial"u8.ToArray());

        await source.DisposeAsync();

        Assert.IsFalse(Directory.Exists(directoryPath));
    }

    [TestMethod]
    public async Task CleanupRefusesToRecursivelyDeleteUnexpectedContent()
    {
        using var root = new TemporaryDirectory();
        var source = OwnedTemporaryArchiveSource.Create(root.Path);
        var directoryPath = source.DirectoryPath;
        var unexpectedPath = Path.Combine(directoryPath, "unexpected.txt");
        await source.Output.WriteAsync("archive"u8.ToArray());
        await source.CompleteAsync();
        await File.WriteAllTextAsync(unexpectedPath, "preserve me");

        await Assert.ThrowsExactlyAsync<Win32Exception>(async () =>
            await source.DisposeAsync());

        Assert.AreEqual("preserve me", await File.ReadAllTextAsync(unexpectedPath));
        File.Delete(unexpectedPath);
        Directory.Delete(directoryPath, recursive: false);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainArchives.OwnedTemporaryArchiveSourceTests",
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
}
