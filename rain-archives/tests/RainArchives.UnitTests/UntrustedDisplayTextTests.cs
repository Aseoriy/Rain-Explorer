using RainArchives.Contracts;

namespace RainArchives.UnitTests;

[TestClass]
public sealed class UntrustedDisplayTextTests
{
    [TestMethod]
    public void Escape_PreservesOrdinaryUnicodeText()
    {
        const string value = "資料/naïve/雨.txt";

        Assert.AreEqual(value, UntrustedDisplayText.Escape(value));
    }

    [TestMethod]
    public void Escape_MakesTerminalAndBidiControlsVisibleOnOneLine()
    {
        const string value = "safe\r\n\u001b[31m\u202Etxt.exe\u2066";

        Assert.AreEqual(
            "safe\\u000D\\u000A\\u001B[31m\\u202Etxt.exe\\u2066",
            UntrustedDisplayText.Escape(value));
    }

    [TestMethod]
    public void Escape_DoublesBackslashesSoEscapesAreUnambiguous()
    {
        Assert.AreEqual("literal\\\\u202E", UntrustedDisplayText.Escape("literal\\u202E"));
    }

    [TestMethod]
    public void Escape_MakesUnpairedSurrogatesVisible()
    {
        Assert.AreEqual("before\\uD800after", UntrustedDisplayText.Escape("before\uD800after"));
    }

    [TestMethod]
    public void Escape_MakesSupplementaryFormatCharactersVisible()
    {
        Assert.AreEqual("before\\U000E0001after", UntrustedDisplayText.Escape("before\U000E0001after"));
    }
}
