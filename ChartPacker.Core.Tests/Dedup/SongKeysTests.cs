using ChartPacker.Core.Dedup;

namespace ChartPacker.Core.Tests.Dedup;

[TestClass]
public sealed class SongKeysTests
{
    [TestMethod]
    public void CaseAccentsAndSpacingAreIgnored() =>
        Assert.AreEqual(SongKeys.For("Beyoncé", "Crazy  in Love"), SongKeys.For("  BEYONCE ", "crazy in love"));

    [TestMethod]
    public void TypographicQuotesAndDashesAreIgnored() =>
        Assert.AreEqual(SongKeys.For("Artist", "Don't Stop - Remix"), SongKeys.For("Artist", "Don’t Stop – Remix"));

    [TestMethod]
    public void LiveVersionsStayDifferent() =>
        Assert.AreNotEqual(SongKeys.For("Artist", "Song"), SongKeys.For("Artist", "Song (Live)"));

    [TestMethod]
    public void ArtistAndTitleDontRunTogether() =>
        Assert.AreNotEqual(SongKeys.For("A", "BC"), SongKeys.For("AB", "C"));

    [TestMethod]
    public void MissingArtistOrTitleHasNoKey()
    {
        Assert.IsNull(SongKeys.For(null, "Song"));
        Assert.IsNull(SongKeys.For("Artist", "  "));
    }
}
