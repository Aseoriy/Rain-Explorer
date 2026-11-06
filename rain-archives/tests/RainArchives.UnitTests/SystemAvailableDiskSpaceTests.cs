using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Security;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class SystemAvailableDiskSpaceTests
{
    [TestMethod]
    public void QueryPathPreservesDriveRootAndTerminatesUncShareRoot()
    {
        Assert.AreEqual(
            @"C:\",
            SystemAvailableDiskSpace.NormalizeQueryPath(@"C:\"));
        Assert.AreEqual(
            @"\\server\share\",
            SystemAvailableDiskSpace.NormalizeQueryPath(@"\\server\share"));
    }

    [TestMethod]
    public void CurrentTemporaryVolumeReportsPositiveAvailableSpace()
    {
        var root = Path.GetPathRoot(Path.GetTempPath());
        Assert.IsFalse(string.IsNullOrEmpty(root));
        Assert.IsGreaterThan(
            0,
            SystemAvailableDiskSpace.Instance.GetAvailableFreeSpace(root));
    }
}
