using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RainArchives.App.Services;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class TextEntryPreviewServiceTests
{
    [TestMethod]
    public void Utf8PreviewPreservesLayoutAndEscapesAmbiguousText()
    {
        var result = TextEntryPreviewService.Decode(
            Encoding.UTF8.GetBytes("first\\path\nsecond\u202E.txt\tok"));

        Assert.IsTrue(result.IsAvailable);
        Assert.IsTrue(result.SourceWasRead);
        Assert.AreEqual(EntryPreviewOutcome.Text, result.Outcome);
        Assert.AreEqual(EntryPreviewTextEncoding.Utf8, result.Encoding);
        Assert.AreEqual(27, result.ByteLength);
        Assert.AreEqual("first\\\\path\nsecond\\u202E.txt\tok", result.Text);
    }

    [TestMethod]
    public void Utf16BomIsDetectedWithoutDisplayingTheBom()
    {
        var encoding = new UnicodeEncoding(false, true, true);
        var payload = encoding.GetPreamble().Concat(encoding.GetBytes("雨 archives")).ToArray();

        var result = TextEntryPreviewService.Decode(payload);

        Assert.IsTrue(result.IsAvailable);
        Assert.AreEqual("雨 archives", result.Text);
        Assert.AreEqual(EntryPreviewTextEncoding.Utf16LittleEndian, result.Encoding);
        Assert.AreEqual(payload.Length, result.ByteLength);
    }

    [TestMethod]
    public void BinaryNullIsRejectedWithoutPublishingContent()
    {
        var result = TextEntryPreviewService.Decode([0x41, 0x00, 0x42]);

        Assert.IsFalse(result.IsAvailable);
        Assert.IsTrue(result.SourceWasRead);
        Assert.AreEqual(EntryPreviewOutcome.Binary, result.Outcome);
        Assert.AreEqual(3, result.ByteLength);
        Assert.AreEqual(string.Empty, result.Text);
    }

    [TestMethod]
    public void InvalidUtf8IsRejectedWithoutReplacementCharacters()
    {
        var result = TextEntryPreviewService.Decode([0xC3, 0x28]);

        Assert.IsFalse(result.IsAvailable);
        Assert.IsTrue(result.SourceWasRead);
        Assert.AreEqual(EntryPreviewOutcome.UnsupportedEncoding, result.Outcome);
        Assert.AreEqual(2, result.ByteLength);
        Assert.AreEqual(string.Empty, result.Text);
    }

    [TestMethod]
    public void OversizedPayloadIsRejectedBeforeDecoding()
    {
        var result = TextEntryPreviewService.Decode(
            new byte[TextEntryPreviewService.MaximumTextPreviewBytes + 1]);

        Assert.IsFalse(result.IsAvailable);
        Assert.IsTrue(result.SourceWasRead);
        Assert.AreEqual(EntryPreviewOutcome.Oversize, result.Outcome);
        Assert.AreEqual(TextEntryPreviewService.MaximumTextPreviewBytes + 1, result.ByteLength);
        Assert.AreEqual(string.Empty, result.Text);
    }

    [TestMethod]
    public void ExactOneMebibyteUtf8PayloadRemainsWithinTheDisplayContract()
    {
        var payload = Enumerable.Repeat((byte)'a', TextEntryPreviewService.MaximumTextPreviewBytes)
            .ToArray();

        var result = TextEntryPreviewService.Decode(payload);

        Assert.AreEqual(EntryPreviewOutcome.Text, result.Outcome);
        Assert.AreEqual(TextEntryPreviewService.MaximumTextPreviewBytes, result.ByteLength);
        Assert.AreEqual(TextEntryPreviewService.MaximumTextPreviewBytes, result.Text.Length);
    }

    [TestMethod]
    public void BomlessUtf16LikePayloadIsClassifiedAsBinaryRatherThanEncodingGuessed()
    {
        var result = TextEntryPreviewService.Decode(Encoding.Unicode.GetBytes("not guessed"));

        Assert.AreEqual(EntryPreviewOutcome.Binary, result.Outcome);
        Assert.IsNull(result.Encoding);
        Assert.AreEqual(22, result.ByteLength);
        Assert.AreEqual(string.Empty, result.Text);
    }
}
