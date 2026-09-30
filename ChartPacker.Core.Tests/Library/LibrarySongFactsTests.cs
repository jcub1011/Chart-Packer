using ChartPacker.Core.Library;
using ChartPacker.Core.Metadata;
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
    public void SummaryGivesTheChartStemsAndAssets()
    {
        var facts = LibrarySongFacts.From(Entry, Summary("notes.mid", "song.ogg", "drums_1.ogg", "drums_2.ogg", "vocals.opus", "video.webm", "album.png"), null, false);

        Assert.AreEqual(LibraryInstruments.Guitar, facts.Instruments);
        Assert.AreEqual(LibraryDifficulties.Expert, facts.Difficulties);
        Assert.AreEqual(LibraryStems.Song | LibraryStems.Drums | LibraryStems.Vocals, facts.Stems);
        Assert.IsTrue(facts.HasVideo);
        Assert.IsFalse(facts.HasImage);
        Assert.IsTrue(facts.HasAlbumArt);
    }

    [TestMethod]
    public void SummaryWithDraftAppliesItsMetadata()
    {
        var draft = new MetadataDraft();
        draft.Set("name", "Renamed");

        Assert.AreEqual("Renamed", LibrarySongFacts.From(Entry, Summary(), draft, true).Title);
    }

    [TestMethod]
    public void DraftAddsAndRemovesStems()
    {
        var draft = new MetadataDraft();
        draft.RemoveFile("song.ogg");
        draft.AddFile(new AddedFile("keys.ogg", @"C:\new\keys.ogg", 10));

        Assert.AreEqual(LibraryStems.Guitar | LibraryStems.Keys, LibrarySongFacts.From(Entry, Summary("song.ogg", "guitar.ogg"), draft, true).Stems);
    }

    [TestMethod]
    public void DraftSetsAndRemovesBackgroundsAndAlbumArt()
    {
        var draft = new MetadataDraft();
        draft.SetBackground(BackgroundKind.Image, @"C:\new\bg.jpg");
        draft.RemoveBackground(BackgroundKind.Video);
        draft.RemoveAlbumArt();

        var facts = LibrarySongFacts.From(Entry, Summary("video.mp4", "album.jpg"), draft, true);

        Assert.IsTrue(facts.HasImage);
        Assert.IsFalse(facts.HasVideo);
        Assert.IsFalse(facts.HasAlbumArt);
    }

    [TestMethod]
    public void RemovedStoredFileIsGone()
    {
        var draft = new MetadataDraft();
        draft.RemoveFile("background.png");

        Assert.IsFalse(LibrarySongFacts.From(Entry, Summary("background.png"), draft, true).HasImage);
    }

    [TestMethod]
    public void DraftThatRemovesTheChartLeavesNone()
    {
        var draft = new MetadataDraft();
        draft.RemoveFile("notes.mid");

        var facts = LibrarySongFacts.From(Entry, Summary("notes.mid"), draft, true);

        Assert.AreEqual(LibraryInstruments.None, facts.Instruments);
        Assert.AreEqual(LibraryDifficulties.None, facts.Difficulties);
    }

    [TestMethod]
    public void ChartTheDraftAddsIsNotKnown()
    {
        var draft = new MetadataDraft();
        draft.AddFile(new AddedFile("notes.chart", @"C:\new\notes.chart", 10));

        var facts = LibrarySongFacts.From(Entry, Summary("notes.mid"), draft, true);

        Assert.IsNull(facts.Instruments);
        Assert.IsNull(facts.Difficulties);
    }

    [TestMethod]
    public void DraftWithoutFileChangesKeepsTheStoredChart()
    {
        var draft = new MetadataDraft();
        draft.Set("name", "Renamed");

        Assert.AreEqual(LibraryInstruments.Guitar, LibrarySongFacts.From(Entry, Summary("notes.mid"), draft, true).Instruments);
    }

    [TestMethod]
    public void BeforeReadingOnlyTheEntryIsKnown() =>
        Assert.AreEqual(new LibrarySongFacts(SongEntryKind.SngFile, "song", HasDrafts: true), LibrarySongFacts.For(Entry, true));

    /// <summary>A song with <paramref name="storedNames"/>, whose chart is an expert guitar part.</summary>
    private static SongSummary Summary(params string[] storedNames) =>
        new(Pairs(("name", "Song")), 0, storedNames, new ChartContents(LibraryInstruments.Guitar, LibraryDifficulties.Expert));

    private static List<KeyValuePair<string, string>> Pairs(params (string Key, string Value)[] pairs) =>
        [.. pairs.Select(p => KeyValuePair.Create(p.Key, p.Value))];
}
