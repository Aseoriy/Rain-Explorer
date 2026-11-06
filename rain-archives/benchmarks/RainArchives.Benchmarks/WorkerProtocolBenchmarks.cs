using System.IO.Compression;
using System.Security.Cryptography;
using BenchmarkDotNet.Attributes;
using RainArchives.Client;

namespace RainArchives.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 1, iterationCount: 5, invocationCount: 1)]
public class WorkerProtocolBenchmarks
{
    private static readonly DateTimeOffset FixtureTimestamp =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private string _fixtureRoot = null!;
    private string _archivePath = null!;
    private string _workerPath = null!;

    [GlobalSetup]
    public void Setup()
    {
        _fixtureRoot = Path.Combine(
            Path.GetTempPath(),
            "RainArchives.Benchmarks",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_fixtureRoot);
        _archivePath = Path.Combine(_fixtureRoot, "listing.zip");
        _workerPath = Path.Combine(AppContext.BaseDirectory, "RainArchives.Worker.exe");
        if (!File.Exists(_workerPath))
        {
            throw new FileNotFoundException("Benchmark output does not contain the worker executable.", _workerPath);
        }

        using (var file = new FileStream(_archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            for (var index = 0; index < 10_000; index++)
            {
                var entry = archive.CreateEntry(
                    $"folder-{index / 1000:D2}/file-{index:D5}.txt",
                    CompressionLevel.NoCompression);
                entry.LastWriteTime = FixtureTimestamp;
                using var stream = entry.Open();
                stream.WriteByte((byte)(index % 251));
            }
        }

        using var fixture = File.OpenRead(_archivePath);
        var fixtureHash = Convert.ToHexString(SHA256.HashData(fixture));
        Console.WriteLine(
            $"// Rain Archives fixture: SHA256={fixtureHash}; ArchiveBytes={fixture.Length}; " +
            $"Entries=10000; OriginalBytes=10000; Compression=NoCompression; " +
            $"Timestamp={FixtureTimestamp:O}; Generator=.NET-{Environment.Version}");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_fixtureRoot))
        {
            Directory.Delete(_fixtureRoot, recursive: true);
        }
    }

    [Benchmark(Baseline = true)]
    public async Task StartupAndHandshake()
    {
        await using var client = await ArchiveClient.StartAsync(_workerPath).ConfigureAwait(false);
    }

    [Benchmark]
    public async Task StartupOpenAndFirstListingPage()
    {
        await using var client = await ArchiveClient.StartAsync(_workerPath).ConfigureAwait(false);
        var opened = await client.OpenArchiveAsync(_archivePath).ConfigureAwait(false);
        try
        {
            _ = await client
                .ListEntriesAsync(opened.Archive.SessionId, 0, 512)
                .ConfigureAwait(false);
        }
        finally
        {
            await client.CloseArchiveAsync(opened.Archive.SessionId).ConfigureAwait(false);
        }
    }

    [Benchmark]
    public async Task StartupOpenAndFullListing()
    {
        await using var client = await ArchiveClient.StartAsync(_workerPath).ConfigureAwait(false);
        var opened = await client.OpenArchiveAsync(_archivePath).ConfigureAwait(false);
        try
        {
            var offset = 0;
            while (true)
            {
                var page = await client
                    .ListEntriesAsync(opened.Archive.SessionId, offset, 1024)
                    .ConfigureAwait(false);
                if (page.NextOffset is not { } next)
                {
                    break;
                }

                offset = next;
            }
        }
        finally
        {
            await client.CloseArchiveAsync(opened.Archive.SessionId).ConfigureAwait(false);
        }
    }
}
