using System.IO.Compression;
using RainArchives.Client;
using RainArchives.Contracts;

namespace RainArchives.IntegrationTests;

[TestClass]
public sealed class ProtocolResponseBudgetTests
{
    [TestMethod]
    [Timeout(30_000)]
    public async Task ListEntriesUsesByteBoundedAdvancingPagesForLongMetadata()
    {
        using var root = new TemporaryDirectory();
        var archivePath = Path.Combine(root.Path, "long-metadata.zip");
        using (var file = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            for (var index = 0; index < 12; index++)
            {
                var prefix = new string((char)('a' + index), 30_000);
                _ = archive.CreateEntry($"{prefix}-{index:D2}.txt", CompressionLevel.NoCompression);
            }
        }

        await using var client = await ArchiveClient.StartAsync(FindWorker());
        var opened = await client.OpenArchiveAsync(archivePath);
        try
        {
            var entries = new List<ArchiveEntryDescriptor>();
            var offset = 0;
            var pageCount = 0;
            do
            {
                var page = await client.ListEntriesAsync(
                    opened.Archive.SessionId,
                    offset,
                    ProtocolConstants.MaximumPageSize);
                Assert.IsTrue(page.Entries.Count > 0);
                entries.AddRange(page.Entries);
                pageCount++;
                if (page.NextOffset is not { } next)
                {
                    break;
                }

                Assert.IsTrue(next > offset);
                offset = next;
            }
            while (true);

            Assert.IsTrue(pageCount > 1);
            Assert.AreEqual(12, entries.Count);
            Assert.AreEqual(12, entries.Select(entry => entry.EntryId).Distinct().Count());
        }
        finally
        {
            await client.CloseArchiveAsync(opened.Archive.SessionId);
        }
    }

    private static string FindWorker()
    {
        var worker = Path.Combine(AppContext.BaseDirectory, "RainArchives.Worker.exe");
        return File.Exists(worker)
            ? worker
            : throw new FileNotFoundException(
                "The integration test output does not contain RainArchives.Worker.exe.",
                worker);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainArchives.ProtocolResponseBudgetTests",
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
