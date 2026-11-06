using System.IO.Compression;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;
using RainArchives.Core;
using RainArchives.Providers.Zip;
using RainArchives.Security;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class ZipRebuildMetadataPolicyTests
{
    private static readonly ushort[] AllowedMasks =
    [
        0x0000,
        0x0001,
        0x0002,
        0x0004,
        0x0020,
        0x0001 | 0x0002,
        0x0001 | 0x0004,
        0x0001 | 0x0020,
        0x0002 | 0x0004,
        0x0002 | 0x0020,
        0x0004 | 0x0020,
        0x0001 | 0x0002 | 0x0004,
        0x0001 | 0x0002 | 0x0020,
        0x0001 | 0x0004 | 0x0020,
        0x0002 | 0x0004 | 0x0020,
        0x0001 | 0x0002 | 0x0004 | 0x0020,
    ];

    [TestMethod]
    public async Task ManagedWriterRoundTripsEveryAllowedMaskAndDosBoundaryExactly()
    {
        var boundaries = new[]
        {
            new ArchiveDosLocalTimestamp(1980, 1, 1, 0, 0, 0),
            new ArchiveDosLocalTimestamp(1980, 1, 1, 0, 0, 2),
            new ArchiveDosLocalTimestamp(2107, 12, 31, 23, 59, 58),
        };

        foreach (var timestamp in boundaries)
        {
            var expectedTime = ZipRebuildMetadataPolicy.ToDateTime(timestamp);
            foreach (var mask in AllowedMasks)
            {
                await using var destination = new MemoryStream();
                var provider = new BuiltInZipArchiveWriterProvider();
                await using (var writer = await provider.CreateAsync(
                    destination,
                    ArchiveCompressionPreset.Store,
                    CancellationToken.None))
                {
                    await writer.AddFileAsync(
                        new ArchiveWriteEntry(
                            "value.bin",
                            3,
                            new DateTimeOffset(expectedTime, TimeSpan.Zero),
                            mask),
                        new MemoryStream([1, 2, 3]),
                        _ => ValueTask.CompletedTask,
                        CancellationToken.None);
                    await writer.CompleteAsync(CancellationToken.None);
                }

                destination.Position = 0;
                using var archive = new ZipArchive(destination, ZipArchiveMode.Read, leaveOpen: true);
                var entry = archive.GetEntry("value.bin")!;
                Assert.AreEqual(expectedTime, entry.LastWriteTime.DateTime, $"DOS tuple {timestamp}, mask {mask:X4}");
                Assert.AreEqual(mask, entry.ExternalAttributes & ZipRebuildMetadataPolicy.AllowedAttributes,
                    $"DOS attributes {timestamp}, mask {mask:X4}");
                Assert.AreEqual(0, entry.ExternalAttributes & (int)FileAttributes.Directory);
            }
        }
    }

    [TestMethod]
    public async Task ManagedWriterRoundTripsForcedDirectoryBitForEveryAllowedMask()
    {
        var timestamp = new ArchiveDosLocalTimestamp(2024, 6, 15, 12, 34, 56);
        var expectedTime = ZipRebuildMetadataPolicy.ToDateTime(timestamp);
        foreach (var mask in AllowedMasks)
        {
            await using var destination = new MemoryStream();
            var provider = new BuiltInZipArchiveWriterProvider();
            await using (var writer = await provider.CreateAsync(
                destination,
                ArchiveCompressionPreset.Store,
                CancellationToken.None))
            {
                await writer.AddDirectoryAsync(
                    new ArchiveWriteDirectory(
                        "folder/",
                        new DateTimeOffset(expectedTime, TimeSpan.Zero),
                        ZipRebuildMetadataPolicy.ValidateAttributes(mask, ArchiveEntryKind.Directory)),
                    CancellationToken.None);
                await writer.CompleteAsync(CancellationToken.None);
            }

            destination.Position = 0;
            using var archive = new ZipArchive(destination, ZipArchiveMode.Read, leaveOpen: true);
            var entry = archive.GetEntry("folder/")!;
            Assert.AreEqual(expectedTime, entry.LastWriteTime.DateTime, $"DOS tuple, mask {mask:X4}");
            Assert.AreEqual(mask, entry.ExternalAttributes & ZipRebuildMetadataPolicy.AllowedAttributes,
                $"DOS attributes, mask {mask:X4}");
            Assert.AreNotEqual(0, entry.ExternalAttributes & (int)FileAttributes.Directory);
        }
    }

    [TestMethod]
    public void InvalidDosTuplesAndUnsupportedAttributesAreRejectedWithoutQuantization()
    {
        var invalid = new[]
        {
            new ArchiveDosLocalTimestamp(1979, 12, 31, 23, 59, 58),
            new ArchiveDosLocalTimestamp(2108, 1, 1, 0, 0, 0),
            new ArchiveDosLocalTimestamp(2024, 2, 30, 0, 0, 0),
            new ArchiveDosLocalTimestamp(2024, 1, 1, 0, 0, 1),
            new ArchiveDosLocalTimestamp(2024, 1, 1, 0, 0, 59),
        };
        foreach (var timestamp in invalid)
        {
            Assert.ThrowsExactly<ArchiveProviderException>(() => ZipRebuildMetadataPolicy.ToDateTime(timestamp));
        }

        foreach (var attributes in new ushort[]
        {
            0x0008, 0x0010, 0x0080, 0x0100, 0x0400, 0x0800, 0x1000, 0x2000,
            0x4000, 0x8000,
        })
        {
            Assert.ThrowsExactly<ArchiveProviderException>(() =>
                ZipRebuildMetadataPolicy.ValidateAttributes(attributes, ArchiveEntryKind.File),
                $"mask {attributes:X4}");
        }
    }

    [TestMethod]
    public void MetadataTargetsResolveAgainstFinalPlanAndRejectDeletedOrWrongKind()
    {
        var finalPlan = new ZipRebuildCopyPlan(
            "output.zip",
            [
                new PlannedZipRebuildCopyEntry(
                    "keep.txt",
                    ArchiveEntryKind.File,
                    1,
                    DateTimeOffset.UnixEpoch,
                    0,
                    ZipRebuildCopyEntrySource.RetainedArchive,
                    new IndexedArchiveEntry(
                        new ArchiveEntryDescriptor(
                            new EntryId(Guid.NewGuid()),
                            null,
                            0,
                            "keep.txt",
                            "keep.txt",
                            ArchiveEntryKind.File,
                            1,
                            1,
                            DateTimeOffset.UnixEpoch,
                            0,
                            null,
                            null,
                            false,
                            false,
                            null,
                            EntrySafetyStatus.Safe,
                            null,
                            "keep.txt",
                            0),
                        null,
                        false)),
                new PlannedZipRebuildCopyEntry(
                    "nested/mapped.txt",
                    ArchiveEntryKind.File,
                    1,
                    DateTimeOffset.UnixEpoch,
                    0,
                    ZipRebuildCopyEntrySource.NewFile),
            ],
            [],
            2,
            2,
            100,
            0);

        var policy = new ZipRebuildMetadataPolicy();
        var result = policy.Apply(
            finalPlan,
            [
                new ArchiveRebuildMetadataDirective(
                    new ArchiveRebuildMetadataTarget(
                        ArchiveRebuildMetadataTargetKind.RetainedEntryKey,
                        new ArchiveEntryKey("keep.txt", 0)),
                    new ArchiveDosLocalTimestamp(2026, 8, 25, 12, 34, 56),
                    0x20),
                new ArchiveRebuildMetadataDirective(
                    new ArchiveRebuildMetadataTarget(
                        ArchiveRebuildMetadataTargetKind.MappedFinalPath,
                        MappedFinalArchivePath: "nested/mapped.txt",
                        MappedKind: ArchiveEntryKind.File),
                    new ArchiveDosLocalTimestamp(2026, 8, 25, 12, 34, 58),
                    0x02),
            ]);

        Assert.AreEqual(new DateTime(2026, 8, 25, 12, 34, 56), result.Entries[0].ZipLastWriteTime.DateTime);
        Assert.AreEqual(0x20, result.Entries[0].WindowsAttributes);
        Assert.AreEqual(new DateTime(2026, 8, 25, 12, 34, 58), result.Entries[1].ZipLastWriteTime.DateTime);
        Assert.AreEqual(0x02, result.Entries[1].WindowsAttributes);

        var casePlan = finalPlan with
        {
            Entries = [finalPlan.Entries[1] with { ArchivePath = "nested/CAFÉ.txt" }],
        };
        var caseResult = policy.Apply(
            casePlan,
            [new ArchiveRebuildMetadataDirective(
                new ArchiveRebuildMetadataTarget(
                    ArchiveRebuildMetadataTargetKind.MappedFinalPath,
                    MappedFinalArchivePath: "NESTED/CAFÉ.txt",
                    MappedKind: ArchiveEntryKind.File),
                AttributesLowWord: 0x04)]);
        Assert.AreEqual(0x04, caseResult.Entries[0].WindowsAttributes);

        Assert.ThrowsExactly<ArchiveProviderException>(() => policy.Apply(
            casePlan,
            [new ArchiveRebuildMetadataDirective(
                new ArchiveRebuildMetadataTarget(
                    ArchiveRebuildMetadataTargetKind.MappedFinalPath,
                    MappedFinalArchivePath: "nested/cafe\u0301.txt",
                    MappedKind: ArchiveEntryKind.File),
                AttributesLowWord: 0x04)]));

        Assert.ThrowsExactly<ArchiveProviderException>(() => policy.Apply(
            finalPlan,
            [new ArchiveRebuildMetadataDirective(
                new ArchiveRebuildMetadataTarget(
                    ArchiveRebuildMetadataTargetKind.MappedFinalPath,
                    MappedFinalArchivePath: "nested/mapped.txt",
                    MappedKind: ArchiveEntryKind.Directory),
                AttributesLowWord: 0)]));
        Assert.ThrowsExactly<ArchiveProviderException>(() => policy.Apply(
            finalPlan,
            [new ArchiveRebuildMetadataDirective(
                new ArchiveRebuildMetadataTarget(
                    ArchiveRebuildMetadataTargetKind.RetainedEntryKey,
                    new ArchiveEntryKey("deleted.txt", 0)),
                AttributesLowWord: 0)]));
    }
}
