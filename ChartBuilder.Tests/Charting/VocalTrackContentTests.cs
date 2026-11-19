using ChartShared.Core.Charting;
using ChartShared.Core.Charts;
using ChartShared.Core.Tests.Charts;
using static ChartBuilder.Tests.Charting.MidiChartWriterTests;

namespace ChartBuilder.Tests.Charting;

/// <summary>
/// A vocal track as the editor edits it (charter-spec C3 item 7, §4.2): read as the reader reads it, every other event
/// kept, and written back so the reader reads what was edited.
/// </summary>
[TestClass]
public sealed class VocalTrackContentTests
{
    /// <summary>Vocal tracks of many shapes, by name.</summary>
    public static IEnumerable<object[]> Shapes => TrackShapes.Keys.Select(k => new object[] { k });

    private static readonly Dictionary<string, Func<MidiBuilder, MidiBuilder>> TrackShapes = new()
    {
        ["notes and lyrics"] = t => t.Note(0, 60, 240).Meta(0, 0x05, "Hel-"u8.ToArray()).Note(240, 62, 240).Meta(240, 0x05, "lo"u8.ToArray()).Note(0, 105, 960),
        ["note-off events"] = t => t.Raw(0, 0x90, 60, 100).Raw(240, 0x80, 60, 0).Raw(480, 0x91, 62, 90).Raw(600, 0x81, 62, 64),
        ["percussion and overdrive"] = t => t.Note(0, 96, 10).Note(120, 97, 10).Note(0, 116, 960).Note(0, 60, 240).Meta(0, 0x05, "a"u8.ToArray()),
        ["overdrive over phrases"] = t => t.Note(0, 105, 960).Note(1920, 105, 960).Note(480, 116, 2400).Note(5000, 116, 480).Note(0, 60, 240),
        ["animations"] = t => t.Text(0, "[idle]").Note(0, 60, 240).Text(0, "word").Text(480, "[play]"),
        ["text lyrics"] = t => t.Note(0, 60, 240).Text(0, "old-").Note(240, 64, 240).Text(240, "school"),
        ["an unended note"] = t => t.Raw(0, 0x90, 60, 100).Note(480, 62, 240),
        ["a note held no ticks"] = t => t.Note(0, 60, 0).Meta(0, 0x05, "a"u8.ToArray()).Note(240, 62, 120),
        ["a 105 and a 106 together"] = t => t.Note(0, 105, 960).Note(0, 106, 960).Note(0, 60, 240),
        ["a 105 and a 106 apart"] = t => t.Note(0, 105, 960).Note(480, 106, 960).Note(1920, 105, 480).Note(0, 60, 240),
        ["the end after the last event"] = t => t.Note(0, 60, 240).Meta(5000, 0x01, "[end of vocals]"u8.ToArray()),
        ["a note on again before its end"] = t => t.Raw(0, 0x90, 60, 100).Raw(240, 0x90, 60, 100).Raw(480, 0x80, 60, 0),
        ["slides and spoken notes"] = t => t.Note(0, 60, 240).Meta(0, 0x05, "go-"u8.ToArray()).Note(240, 64, 240).Meta(240, 0x05, "+"u8.ToArray()).Note(480, 50, 120).Meta(480, 0x05, "hey#"u8.ToArray()),
        ["a blank lyric"] = t => t.Note(0, 60, 240).Meta(0, 0x05, " "u8.ToArray()),
    };

    [TestMethod]
    [DynamicData(nameof(Shapes))]
    public void ItsPartIsWhatTheReaderReadsFromTheTrack(string shape)
    {
        var track = Track(shape);

        AssertSamePart(VocalReader.ReadPart(track), VocalTrackContent.Of(track).ToPart());
    }

    [TestMethod]
    [DynamicData(nameof(Shapes))]
    public void AnEditedTrackReadsBackAsEditedAndKeepsEveryOtherEvent(string shape)
    {
        var track = Track(shape);
        var content = VocalTrackContent.Of(track);
        var edited = content.With(notes: [.. content.Notes, new VocalNoteEvent(9600, 120, 70)], lyrics: [.. content.Lyrics, new VocalLyricEvent(9600, "new")]);

        var written = edited.Write();

        Assert.AreNotSame(track, written);
        Assert.IsFalse(written.IsOriginal);
        Assert.AreEqual(track.Name, written.Name);
        AssertSamePart(edited.ToPart(), VocalReader.ReadPart(written));
        var reread = VocalTrackContent.Of(written);
        Assert.AreSequenceEqual(Described(content), Described(reread));
        Assert.IsGreaterThanOrEqualTo(content.EndTick, reread.EndTick);
    }

    [TestMethod]
    public void AnUnchangedPartWritesItsOwnTrack()
    {
        var track = Track("notes and lyrics");
        var content = VocalTrackContent.Of(track);

        Assert.AreSame(content, VocalTrackContent.Of(track));
        Assert.AreSame(content, content.With(notes: content.Notes, lyrics: content.Lyrics, phrases: content.Phrases));
        Assert.IsFalse(content.IsChanged);
        Assert.AreSame(track, content.Write());
        Assert.IsTrue(content.Write().IsOriginal);
    }

    [TestMethod]
    public void StarPowerIsReadFromNote116AndIsNotAKeptEvent()
    {
        var content = VocalTrackContent.Of(Track("overdrive over phrases"));

        Assert.AreSequenceEqual([new VocalStarPowerEvent(480, 2400), new VocalStarPowerEvent(5000, 480)], [.. content.StarPower]);
        Assert.AreSequenceEqual([new VocalPhraseEvent(0, 960), new VocalPhraseEvent(1920, 960)], [.. content.Phrases], "A phrase and the star power over it are two things.");
        Assert.DoesNotContain(e => e.Bytes.Length >= 2 && e.Bytes[1] == VocalTrackContent.StarPowerPitch, content.Kept);
    }

    [TestMethod]
    public void EditedStarPowerIsWrittenAsNote116AndReadsBack()
    {
        var content = VocalTrackContent.Of(Track("notes and lyrics"));
        var edited = content.With(starPower: [new VocalStarPowerEvent(240, 480), new VocalStarPowerEvent(2400, 120)]);

        var reread = VocalTrackContent.Of(edited.Write());

        Assert.IsTrue(edited.IsChanged);
        Assert.AreSequenceEqual([new VocalStarPowerEvent(240, 480), new VocalStarPowerEvent(2400, 120)], [.. reread.StarPower]);
        AssertSamePart(content.ToPart(), VocalReader.ReadPart(edited.Write()));
        Assert.AreSequenceEqual([.. content.Notes], [.. reread.Notes]);
    }

    [TestMethod]
    public void StarPowerSavedWithoutChangeWritesItsOwnTrack()
    {
        var track = Track("overdrive over phrases");
        var content = VocalTrackContent.Of(track);

        Assert.AreSame(content, content.With(starPower: content.StarPower));
        Assert.AreSame(track, content.Write());
    }

    [TestMethod]
    public void StarPowerIsKeptToOnePhraseAtATimeEachCutWhereTheNextStarts()
    {
        var content = VocalTrackContent.Of(Track("notes and lyrics"));

        var cut = content.With(starPower: [new VocalStarPowerEvent(0, 1000), new VocalStarPowerEvent(480, 960)]);
        var same = content.With(starPower: [new VocalStarPowerEvent(0, 240), new VocalStarPowerEvent(0, 960)]);

        Assert.AreSequenceEqual([new VocalStarPowerEvent(0, 480), new VocalStarPowerEvent(480, 960)], [.. cut.StarPower]);
        Assert.AreSequenceEqual([new VocalStarPowerEvent(0, 960)], [.. same.StarPower], "Of two at a tick the longer is kept.");
    }

    [TestMethod]
    public void StarPowerOfNoLengthOrBeforeTheStartIsRefused()
    {
        var content = VocalTrackContent.Of(Track("notes and lyrics"));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => content.With(starPower: [new VocalStarPowerEvent(0, 0)]));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => content.With(starPower: [new VocalStarPowerEvent(-1, 10)]));
    }

    [TestMethod]
    public void TheVelocitiesAndHowLyricsAreWrittenAreKept()
    {
        var content = VocalTrackContent.Of(Track("note-off events"));
        Assert.AreSequenceEqual([100, 90], [.. content.Notes.Select(n => n.Velocity)]);

        var text = VocalTrackContent.Of(Track("text lyrics"));
        Assert.IsTrue(text.Lyrics.All(l => l.Type == VocalTrackContent.TextType));
        var rewritten = VocalTrackContent.Of(text.With(notes: text.Notes.Select(n => n with { Pitch = n.Pitch + 1 })).Write());
        Assert.AreSequenceEqual([.. text.Lyrics], [.. rewritten.Lyrics]);
    }

    [TestMethod]
    public void A105AndA106OverTheSameTicksAreOnePhraseThatStaysTogether()
    {
        var content = VocalTrackContent.Of(Track("a 105 and a 106 together"));

        var phrase = content.Phrases.Single();
        Assert.AreEqual(VocalPhraseMarkers.Primary | VocalPhraseMarkers.Secondary, phrase.Markers);

        var moved = VocalTrackContent.Of(content.With(phrases: [phrase with { Tick = 480 }]).Write());
        Assert.AreEqual(new VocalPhraseEvent(480, 960, VocalPhraseMarkers.Primary | VocalPhraseMarkers.Secondary), moved.Phrases.Single());
        Assert.HasCount(3, VocalTrackContent.Of(Track("a 105 and a 106 apart")).Phrases);
    }

    [TestMethod]
    public void NotesAreKeptOneToATickAndPitchAndEndWhereTheNextOnTheirPitchStarts()
    {
        var content = VocalTrackContent.Of(Track("notes and lyrics"));

        var edited = content.With(notes: [new(0, 960, 60), new(0, 100, 60), new(480, 240, 60), new(240, 240, 62)]);

        Assert.AreSequenceEqual(
            [new VocalNoteEvent(0, 480, 60), new VocalNoteEvent(240, 240, 62), new VocalNoteEvent(480, 240, 60)],
            [.. edited.Notes]);
        AssertSamePart(edited.ToPart(), VocalReader.ReadPart(edited.Write()));
    }

    [TestMethod]
    public void WhatTheGamesDontSingIsRefused()
    {
        var content = VocalTrackContent.Of(Track("notes and lyrics"));

        Assert.Throws<ArgumentOutOfRangeException>(() => content.With(notes: [new(0, 10, 35)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => content.With(notes: [new(-1, 10, 60)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => content.With(phrases: [new(0, 0)]));
    }

    [TestMethod]
    public void AnEmptyPartIsATrackOfItsNameAlone()
    {
        var written = VocalTrackContent.Empty("HARM2").Write();

        Assert.AreEqual("HARM2", written.Name);
        Assert.IsTrue(written.IsTrack);
        var part = VocalReader.ReadPart(written);
        Assert.AreEqual("HARM2", part.TrackName);
        Assert.IsEmpty(part.Notes);
        Assert.IsEmpty(part.Lyrics);
        Assert.IsEmpty(part.Phrases);
    }

    [TestMethod]
    public void AnEditedTrackInAChartSavesAndReadsBackAsEdited()
    {
        var chart = MidiChartLoader.Load(MidiFixtures.Passthrough(), ChartReadOptions.Default).Chart;
        int index = chart.Passthrough.ToList().FindIndex(t => t.Name == "PART VOCALS");
        var content = VocalTrackContent.Of(chart.Passthrough[index]);
        new ChartShared.Core.Charting.Commands.ChangeVocals(
            ChartShared.Core.Charting.Layout.VocalTrack.Vocals,
            content.With(notes: content.Notes.Select(n => n with { Pitch = n.Pitch + 2 }))).Apply(chart);
        var after = MidiChartWriter.SettingsAfterSave(chart, ChartReadOptions.Default);

        var result = MidiChartWriter.WriteChecked(chart, after);

        Assert.IsTrue(result.IsValid, DescribeModel(result.ModelDifferences));
        var vocals = VocalReader.Read(result.Bytes, "notes.mid").Main!;
        Assert.AreSequenceEqual([62, 64], [.. vocals.Notes.Select(n => n.Pitch)]);
        Assert.AreSequenceEqual(["Hel-", "lo"], [.. vocals.Lyrics.Select(l => l.Text)]);
    }

    private static PassthroughTrack Track(string shape)
    {
        byte[] midi = TrackShapes[shape](new MidiBuilder(480).Track("Song").Tempo(0, 120).TimeSignature(0, 4, 2).Track("PART VOCALS")).Build();
        return MidiChartLoader.Load(midi, ChartReadOptions.Default).Chart.Passthrough.Single(t => t.Name == "PART VOCALS");
    }

    private static void AssertSamePart(VocalPart expected, VocalPart actual)
    {
        Assert.AreEqual(expected.TrackName, actual.TrackName);
        Assert.AreSequenceEqual([.. expected.Notes], [.. actual.Notes], "notes");
        Assert.AreSequenceEqual([.. expected.Lyrics], [.. actual.Lyrics], "lyrics");
        Assert.AreSequenceEqual([.. expected.Phrases], [.. actual.Phrases], "phrases");
    }

    // The events kept, each as its tick and bytes.
    private static string[] Described(VocalTrackContent content) =>
        [.. content.Kept.Select(e => $"{e.Tick}: {Convert.ToHexString(e.Bytes)}"), .. content.StarPower.Select(p => $"star power {p.Tick}+{p.Length}")];
}
