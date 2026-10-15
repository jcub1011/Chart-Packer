using ChartShared.Core.Dedup;
using ChartShared.Core.Scanning;
using static ChartShared.Core.Tests.Dedup.DedupFixtures;

namespace ChartShared.Core.Tests.Dedup;

[TestClass]
public sealed class KeeperRulesTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "library");

    [TestMethod]
    [DataRow(DedupKindPreference.PreferSng, SongEntryKind.SngFile)]
    [DataRow(DedupKindPreference.PreferFolder, SongEntryKind.SongFolder)]
    public void MixedGroupsKeepThePreferredKind(DedupKindPreference preference, SongEntryKind expected)
    {
        var folder = Fingerprint(SongEntryKind.SongFolder, P("Song"));
        var sng = Fingerprint(SongEntryKind.SngFile, P("Some other longer name.sng"));

        Assert.AreEqual(expected, KeeperRules.Choose([folder, sng], preference).Entry.Kind);
    }

    [TestMethod]
    [DataRow("Song (1)")]
    [DataRow("Song [2]")]
    [DataRow("Song - Copy")]
    [DataRow("Song - Copy (2)")]
    [DataRow("Song copy 2")]
    [DataRow("Song (copy)")]
    [DataRow("Song (another copy)")]
    [DataRow("Song (2nd copy)")]
    [DataRow("Song copy")]
    [DataRow("Song - copy")]
    public void DownloadSuffixesAreRecognised(string name) => Assert.IsTrue(KeeperRules.HasDownloadSuffix(name));

    [TestMethod]
    [DataRow("Song (Live)")]
    [DataRow("Song 2")]
    [DataRow("Copycat")]
    [DataRow("1999")]
    [DataRow("Artist - Song (2004)")]
    [DataRow("Song [1999]")]
    [DataRow("Song (Live) (1999)")]
    [DataRow("Carbon Copy")]
    [DataRow("Artist - Carbon Copy")]
    public void OrdinaryNamesHaveNoSuffix(string name) => Assert.IsFalse(KeeperRules.HasDownloadSuffix(name));

    [TestMethod]
    public void TheCopyWithoutASuffixIsKeptEvenWhenItsPathIsLonger()
    {
        var copy = Fingerprint(SongEntryKind.SngFile, P("Song (1).sng"));
        var original = Fingerprint(SongEntryKind.SngFile, P("Artist - A Long Song Name.sng"));

        Assert.AreSame(original, KeeperRules.Choose([copy, original], DedupKindPreference.PreferSng));
    }

    [TestMethod]
    public void TheShortestPathIsKeptThenTheOldest()
    {
        var deep = Fingerprint(SongEntryKind.SngFile, P("sub", "Song.sng"));
        var shallow = Fingerprint(SongEntryKind.SngFile, P("Song.sng"));
        Assert.AreSame(shallow, KeeperRules.Choose([deep, shallow], DedupKindPreference.PreferSng));

        var newer = Fingerprint(SongEntryKind.SngFile, P("a", "Song.sng"), new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var older = Fingerprint(SongEntryKind.SngFile, P("b", "Song.sng"), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.AreSame(older, KeeperRules.Choose([newer, older], DedupKindPreference.PreferSng));
    }

    [TestMethod]
    public void ACopyWithUnsavedEditsIsAlwaysKept()
    {
        var edited = Fingerprint(SongEntryKind.SongFolder, P("deeper", "Song (1)"), hasDrafts: true);
        var sng = Fingerprint(SongEntryKind.SngFile, P("Song.sng"));

        Assert.AreSame(edited, KeeperRules.Choose([sng, edited], DedupKindPreference.PreferSng));
    }

    [TestMethod]
    public void TiesAlwaysBreakTheSameWay()
    {
        var a = Fingerprint(SongEntryKind.SngFile, P("a", "Song.sng"));
        var b = Fingerprint(SongEntryKind.SngFile, P("b", "Song.sng"));

        Assert.AreSame(a, KeeperRules.Choose([a, b], DedupKindPreference.PreferSng));
        Assert.AreSame(a, KeeperRules.Choose([b, a], DedupKindPreference.PreferSng));
    }

    [TestMethod]
    public void TheVariantHoldingTheBestCopyIsKept()
    {
        var sng = Fingerprint(SongEntryKind.SngFile, P("Song.sng"));
        var folder = Fingerprint(SongEntryKind.SongFolder, P("Song"));
        var withSng = new DedupVariant([sng]);
        var withFolder = new DedupVariant([folder]);

        Assert.AreSame(withFolder, KeeperRules.Choose([withSng, withFolder], DedupKindPreference.PreferFolder));
    }

    private static string P(params string[] parts) => Path.Combine([Root, .. parts]);
}
