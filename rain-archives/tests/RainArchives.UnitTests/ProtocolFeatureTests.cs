using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Contracts;
using System.Text.Json;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class ProtocolFeatureTests
{
    [TestMethod]
    public void OptionalFeaturesAppearOnlyInTheirNegotiatedMinorVersion()
    {
        var minorOne = ProtocolFeatures.SupportedForMinorVersion(1).ToArray();
        var minorTwo = ProtocolFeatures.SupportedForMinorVersion(2).ToArray();
        var minorThree = ProtocolFeatures.SupportedForMinorVersion(3).ToArray();
        var minorFour = ProtocolFeatures.SupportedForMinorVersion(4).ToArray();
        var minorFive = ProtocolFeatures.SupportedForMinorVersion(5).ToArray();
        var minorSix = ProtocolFeatures.SupportedForMinorVersion(6).ToArray();
        var minorSeven = ProtocolFeatures.SupportedForMinorVersion(7).ToArray();
        var minorEight = ProtocolFeatures.SupportedForMinorVersion(8).ToArray();
        var minorNine = ProtocolFeatures.SupportedForMinorVersion(9).ToArray();
        var minorTen = ProtocolFeatures.SupportedForMinorVersion(10).ToArray();
        var minorEleven = ProtocolFeatures.SupportedForMinorVersion(11).ToArray();
        var minorTwelve = ProtocolFeatures.SupportedForMinorVersion(12).ToArray();
        var minorThirteen = ProtocolFeatures.SupportedForMinorVersion(13).ToArray();
        var minorFourteen = ProtocolFeatures.SupportedForMinorVersion(14).ToArray();

        CollectionAssert.DoesNotContain(minorOne, ProtocolFeatures.RequestCancellation);
        CollectionAssert.Contains(minorTwo, ProtocolFeatures.RequestCancellation);
        CollectionAssert.Contains(minorThree, ProtocolFeatures.RequestCancellation);
        CollectionAssert.DoesNotContain(minorOne, ProtocolFeatures.PersistentEntryKeys);
        CollectionAssert.DoesNotContain(minorTwo, ProtocolFeatures.PersistentEntryKeys);
        CollectionAssert.Contains(minorThree, ProtocolFeatures.PersistentEntryKeys);
        CollectionAssert.Contains(minorFour, ProtocolFeatures.PersistentEntryKeys);
        CollectionAssert.DoesNotContain(minorOne, ProtocolFeatures.VolumeSetIdentity);
        CollectionAssert.DoesNotContain(minorTwo, ProtocolFeatures.VolumeSetIdentity);
        CollectionAssert.DoesNotContain(minorThree, ProtocolFeatures.VolumeSetIdentity);
        CollectionAssert.Contains(minorFour, ProtocolFeatures.VolumeSetIdentity);
        CollectionAssert.DoesNotContain(minorFour, ProtocolFeatures.EntryStreamLeases);
        CollectionAssert.Contains(minorFive, ProtocolFeatures.EntryStreamLeases);
        CollectionAssert.Contains(minorSix, ProtocolFeatures.EntryStreamLeases);
        CollectionAssert.DoesNotContain(minorFive, ProtocolFeatures.NestedArchiveOpen);
        CollectionAssert.Contains(minorSix, ProtocolFeatures.NestedArchiveOpen);
        CollectionAssert.DoesNotContain(minorSix, ProtocolFeatures.ZipCreation);
        CollectionAssert.Contains(minorSeven, ProtocolFeatures.ZipCreation);
        CollectionAssert.Contains(minorEight, ProtocolFeatures.ZipCreation);
        CollectionAssert.DoesNotContain(minorSeven, ProtocolFeatures.ZipDirectoryCreation);
        CollectionAssert.Contains(minorEight, ProtocolFeatures.ZipDirectoryCreation);
        CollectionAssert.DoesNotContain(
            minorEight,
            ProtocolFeatures.ArchiveCreationRecoveryInspection);
        CollectionAssert.Contains(
            minorNine,
            ProtocolFeatures.ArchiveCreationRecoveryInspection);
        CollectionAssert.DoesNotContain(
            minorNine,
            ProtocolFeatures.ArchiveCreationRecoveryDisposition);
        CollectionAssert.Contains(
            minorTen,
            ProtocolFeatures.ArchiveCreationRecoveryInspection);
        CollectionAssert.Contains(
            minorTen,
            ProtocolFeatures.ArchiveCreationRecoveryDisposition);
        CollectionAssert.DoesNotContain(
            minorTen,
            ProtocolFeatures.NestedWorkspaceRecovery);
        CollectionAssert.Contains(
            minorEleven,
            ProtocolFeatures.NestedWorkspaceRecovery);
        CollectionAssert.DoesNotContain(minorTwelve, ProtocolFeatures.ZipRebuildRename);
        CollectionAssert.Contains(minorTwelve, ProtocolFeatures.ZipRebuildCopy);
        CollectionAssert.Contains(minorThirteen, ProtocolFeatures.ZipRebuildCopy);
        CollectionAssert.Contains(minorThirteen, ProtocolFeatures.ZipRebuildRename);
        CollectionAssert.DoesNotContain(minorThirteen, ProtocolFeatures.ZipRebuildTreeEdit);
        CollectionAssert.DoesNotContain(minorThirteen, ProtocolFeatures.ZipRebuildSourceFolderReview);
        CollectionAssert.Contains(minorFourteen, ProtocolFeatures.ZipRebuildTreeEdit);
        CollectionAssert.Contains(minorFourteen, ProtocolFeatures.ZipRebuildSourceFolderReview);
        CollectionAssert.Contains(minorFourteen, ProtocolFeatures.ZipDirectoryCreation);
        foreach (var required in ProtocolFeatures.RequiredVersion1)
        {
            CollectionAssert.Contains(minorOne, required);
            CollectionAssert.Contains(minorTwo, required);
            CollectionAssert.Contains(minorThree, required);
            CollectionAssert.Contains(minorFour, required);
            CollectionAssert.Contains(minorFive, required);
            CollectionAssert.Contains(minorSix, required);
            CollectionAssert.Contains(minorSeven, required);
            CollectionAssert.Contains(minorEight, required);
            CollectionAssert.Contains(minorNine, required);
            CollectionAssert.Contains(minorTen, required);
            CollectionAssert.Contains(minorEleven, required);
        }
    }

    [TestMethod]
    public void MinorSevenZipCreationWireShapeRemainsDirectoryFree()
    {
        var json = JsonSerializer.Serialize(
            new StartZipCreationRequest(
                @"C:\created.zip",
                [new ArchiveCreationFile(@"C:\source.txt", "source.txt")]),
            ProtocolJson.Options);

        StringAssert.Contains(json, "\"files\"");
        Assert.IsFalse(json.Contains("directories", StringComparison.Ordinal));
    }
}
