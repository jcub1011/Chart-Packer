using ChartPacker.Core.Library;
using ChartPacker.Core.Scanning;

namespace ChartPacker.Core.Tests.Library;

[TestClass]
public sealed class LibrarySongFactsTests
{
    private static readonly SongEntry Entry = new(SongEntryKind.SngFile, @"C:\songs\song.sng", "song.sng", "song");

    [TestMethod]
    [DataRow("2004", 2004)]
    [DataRow(", 2004", 2004)]
    [DataRow("2004-05-01", 2004)]
    [DataRow("Released 1999 (remaster 2011)", 1999)]
    public void ParsesYear(string value, int expected) =>
        Assert.AreEqual(expected, LibrarySongFacts.ParseYear(value));

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("unknown")]
    [DataRow("99")]
    [DataRow("20045")]
    public void UnparsableYearIsNull(string? value) =>
        Assert.IsNull(LibrarySongFacts.ParseYear(value));

    [TestMethod]
    public void ReadsEachValue()
    {
        var facts = LibrarySongFacts.From(Entry, Pairs(
            ("name", "Song"), ("artist", "Band"), ("album", "Record"), ("genre", "Rock"),
            ("charter", "<color=red>Someone</color>"), ("year", ", 2004"), ("song_length", "1000")),
            totalSize: 42, fallbackLengthMs: 5000, hasDrafts: true);

        Assert.AreEqual(
            new LibrarySongFacts(SongEntryKind.SngFile, "song", "Song", "Band", "Record", "Rock", "Someone", 2004, 1000, 42, true),
            facts);
    }

    [TestMethod]
    public void LengthFallsBackToAudio() =>
        Assert.AreEqual(5000, LibrarySongFacts.From(Entry, [], 0, 5000, false).LengthMs);

    [TestMethod]
    public void CharterFallsBackToFrets() =>
        Assert.AreEqual("Legacy", LibrarySongFacts.From(Entry, Pairs(("frets", "Legacy")), 0, null, false).Charter);

    [TestMethod]
    public void BlankValuesAreNull()
    {
        var facts = LibrarySongFacts.From(Entry, Pairs(("album", "  "), ("genre", "<b></b>")), 0, null, false);

        Assert.IsNull(facts.Album);
        Assert.IsNull(facts.Genre);
    }

    [TestMethod]
    public void BeforeReadingOnlyTheEntryIsKnown() =>
        Assert.AreEqual(new LibrarySongFacts(SongEntryKind.SngFile, "song", HasDrafts: true), LibrarySongFacts.For(Entry, true));

    private static List<KeyValuePair<string, string>> Pairs(params (string Key, string Value)[] pairs) =>
        [.. pairs.Select(p => KeyValuePair.Create(p.Key, p.Value))];
}
