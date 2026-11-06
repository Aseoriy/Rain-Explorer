using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;
using RainArchives.Core;
using RainArchives.Providers.Zip;
using RainArchives.Security;

namespace RainArchives.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class ZipMetadataProviderExternalProofTests
{
    private const string OfficialSevenZipSha256 =
        "35D4D69D7CD6CB44558F208C3B1334268013F9DAF82D2DDA848893A1C30C59C2";

    private static readonly ushort[] AllowedMasks =
    [
        0x0000, 0x0001, 0x0002, 0x0004, 0x0020,
        0x0001 | 0x0002, 0x0001 | 0x0004, 0x0001 | 0x0020,
        0x0002 | 0x0004, 0x0002 | 0x0020, 0x0004 | 0x0020,
        0x0001 | 0x0002 | 0x0004, 0x0001 | 0x0002 | 0x0020,
        0x0001 | 0x0004 | 0x0020, 0x0002 | 0x0004 | 0x0020,
        0x0001 | 0x0002 | 0x0004 | 0x0020,
    ];

    [TestMethod]
    [Timeout(120_000)]
    public async Task OfficialSevenZipReportsExactDosCorpusAndAllowedAttributes()
    {
        using var root = new TemporaryDirectory();
        var destination = Path.Combine(root.Path, "metadata-proof.zip");
        var sevenZip = GetOfficialSevenZipPath();
        AssertPinnedSevenZip(sevenZip);
        var timestamps = new[]
        {
            new ArchiveDosLocalTimestamp(1980, 1, 1, 0, 0, 0),
            new ArchiveDosLocalTimestamp(2024, 6, 15, 12, 34, 56),
            new ArchiveDosLocalTimestamp(2107, 12, 31, 23, 59, 58),
        };

        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        await using (var writer = await new BuiltInZipArchiveWriterProvider().CreateAsync(
            output,
            ArchiveCompressionPreset.Store,
            CancellationToken.None))
        {
            foreach (var (timestamp, timestampIndex) in timestamps.Select((value, index) => (value, index)))
            {
                var dateTime = ZipRebuildMetadataPolicy.ToDateTime(timestamp);
                foreach (var mask in AllowedMasks)
                {
                    await writer.AddFileAsync(
                        new ArchiveWriteEntry(
                            $"t{timestampIndex:D1}-m{mask:X4}.bin",
                            1,
                            new DateTimeOffset(dateTime, TimeSpan.Zero),
                            mask),
                        new MemoryStream([0x5A]),
                        _ => ValueTask.CompletedTask,
                        CancellationToken.None);
                }
            }

            await writer.CompleteAsync(CancellationToken.None);
        }

        var listing = await RunSevenZipAsync(sevenZip, root.Path, "l", "-slt", "-bd", "-y", "--", destination);
        var records = ParseListing(listing.StandardOutput);
        Assert.AreEqual(timestamps.Length * AllowedMasks.Length, records.Count);
        foreach (var (timestamp, timestampIndex) in timestamps.Select((value, index) => (value, index)))
        {
            var expectedDate = ZipRebuildMetadataPolicy.ToDateTime(timestamp);
            foreach (var mask in AllowedMasks)
            {
                var name = $"t{timestampIndex:D1}-m{mask:X4}.bin";
                Assert.IsTrue(records.TryGetValue(name, out var record), name);
                Assert.AreEqual(
                    $"{expectedDate:yyyy-MM-dd HH:mm:ss}",
                    record.Modified.Split('.', 2)[0],
                    name);
                if (record.Modified.Length > 19)
                {
                    Assert.IsTrue(
                        record.Modified[19..].All(character => character == '0'),
                        $"7-Zip reported non-zero sub-second metadata for {name}: {record.Modified}");
                }
                Assert.AreEqual(ExpectedAttributes(mask), record.Attributes, name);
            }
        }
    }

    private static string ExpectedAttributes(ushort mask)
    {
        var result = string.Empty;
        if ((mask & (ushort)FileAttributes.ReadOnly) != 0) result += "R";
        if ((mask & (ushort)FileAttributes.Hidden) != 0) result += "H";
        if ((mask & (ushort)FileAttributes.System) != 0) result += "S";
        if ((mask & (ushort)FileAttributes.Archive) != 0) result += "A";
        return result;
    }

    private static Dictionary<string, (string Modified, string Attributes)> ParseListing(string output)
    {
        var records = new Dictionary<string, (string Modified, string Attributes)>(StringComparer.Ordinal);
        string? path = null;
        string? modified = null;
        string? attributes = null;
        foreach (var line in output.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            if (line.StartsWith("Path = ", StringComparison.Ordinal))
            {
                if (path is not null && modified is not null && attributes is not null && path.Contains("-m", StringComparison.Ordinal))
                {
                    records.Add(path, (modified, attributes));
                }

                path = line[7..];
                modified = null;
                attributes = null;
            }
            else if (line.StartsWith("Modified = ", StringComparison.Ordinal))
            {
                modified = line[11..];
            }
            else if (line.StartsWith("Attributes = ", StringComparison.Ordinal))
            {
                attributes = line[13..].Replace("-", string.Empty, StringComparison.Ordinal);
            }
        }

        if (path is not null && modified is not null && attributes is not null && path.Contains("-m", StringComparison.Ordinal))
        {
            records.Add(path, (modified, attributes));
        }

        return records;
    }

    private static async Task<(string StandardOutput, string StandardError)> RunSevenZipAsync(
        string executable,
        string workingDirectory,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not launch the pinned official 7-Zip binary.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var output = await standardOutput;
        var error = await standardError;
        Assert.AreEqual(0, process.ExitCode, $"7-Zip failed: {error}");
        return (output, error);
    }

    private static void AssertPinnedSevenZip(string path)
    {
        Assert.IsTrue(File.Exists(path), path);
        Assert.AreEqual(
            OfficialSevenZipSha256,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }

    private static string GetOfficialSevenZipPath() => Path.Combine(
        Path.GetTempPath(),
        "RainArchives.Tooling",
        "7zip-26.02",
        "expanded",
        "x64",
        "7za.exe");

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "RainArchives.ZipMetadataProviderExternalProofTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
