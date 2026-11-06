using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.Core;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class ArchiveSignatureTests
{
    [TestMethod]
    public void DetectsZipByContent()
    {
        Assert.AreEqual("zip", ArchiveSignatures.Detect([0x50, 0x4B, 0x03, 0x04, 0x00]));
    }

    [TestMethod]
    public void DetectsSevenZipByContent()
    {
        Assert.AreEqual("7z", ArchiveSignatures.Detect([0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00]));
    }

    [TestMethod]
    public void DoesNotTreatExtensionAsContent()
    {
        Assert.IsNull(ArchiveSignatures.Detect("not an archive"u8));
    }
}
