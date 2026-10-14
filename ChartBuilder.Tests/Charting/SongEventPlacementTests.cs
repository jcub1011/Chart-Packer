using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charts;

namespace ChartBuilder.Tests.Charting;

/// <summary>
/// Where the editor places <c>[music_start]</c>, <c>[music_end]</c> and <c>[end]</c> (charter-spec C8 item 5), and the
/// copy a save writes them into.
/// </summary>
[TestClass]
public sealed class SongEventPlacementTests
{
    // At 480 ticks a beat, 120 BPM and 4/4: a second is 960 ticks, and a measure 1920.
    private const ChartInstrument Guitar = ChartInstrument.Guitar;
    private const ChartDifficulty Expert = ChartDifficulty.Expert;

    [TestMethod]
    public void WithNoNotesOrAudioTheyStartTheSong()
    {
        Assert.AreEqual(new SongEventMarks(0, 0, 1920, FromAudio: false), SongEventPlacement.Place(Chart(), null, TimeSpan.Zero));
    }

    [TestMethod]
    public void WithoutAudioTheMusicIsFromTheMeasureOfTheFirstNoteToTheLastNotesEnd()
    {
        // The sustain at 3840 ends at 4800, after the plain note at 4000.
        var chart = Chart();
        new AddNotes(Guitar, Expert, [new EditableNote(2500, 1, 0), new EditableNote(3840, 960, 1), new EditableNote(4000, 1, 2)]).Apply(chart);

        Assert.AreEqual(new SongEventMarks(1920, 4800, 5760, FromAudio: false), SongEventPlacement.Place(chart, null, TimeSpan.Zero));
    }

    [TestMethod]
    public void EndIsAfterTheMeasureLineTheLastNoteEndsOn()
    {
        var chart = Chart();
        new AddNotes(Guitar, Expert, [new EditableNote(3840, 1, 0)]).Apply(chart);

        Assert.AreEqual(5760, SongEventPlacement.Place(chart, null, TimeSpan.Zero).End);
    }

    [TestMethod]
    public void VocalsCountAsNotes()
    {
        var chart = Chart();
        new AddNotes(Guitar, Expert, [new EditableNote(2000, 1, 0)]).Apply(chart);

        var marks = SongEventPlacement.Place(chart, null, TimeSpan.Zero, vocals: (100, 6000));

        Assert.AreEqual(new SongEventMarks(0, 6000, 7680, FromAudio: false), marks);
    }

    [TestMethod]
    public void WithAudioTheMusicIsWhereItsHeardOnTheTicksAroundIt()
    {
        // 1000.4 and 2000.4 ticks: the start is rounded down and the end up, so both take in the sound.
        var marks = SongEventPlacement.Place(Chart(), new AudibleRange(1000.4 / 960, 2000.4 / 960), TimeSpan.Zero);

        Assert.AreEqual(new SongEventMarks(1000, 2001, 3840, FromAudio: true), marks);
    }

    [TestMethod]
    public void TheDelayIsTakenOffTheAudiosTimes()
    {
        var marks = SongEventPlacement.Place(Chart(), new AudibleRange(1, 3), TimeSpan.FromSeconds(0.5));

        Assert.AreEqual(new SongEventMarks(480, 2400, 3840, FromAudio: true), marks);
    }

    [TestMethod]
    public void MusicHeardBeforeTheChartStartsStartsItAtZero()
    {
        var marks = SongEventPlacement.Place(Chart(), new AudibleRange(1, 3), TimeSpan.FromSeconds(2));

        Assert.AreEqual(0, marks.MusicStart);
        Assert.AreEqual(960, marks.MusicEnd);
    }

    [TestMethod]
    public void TheMusicTakesInTheNotesWhereTheAudioDoesnt()
    {
        // Notes from 480 to 4800; the audio is heard from 960 to 1920.
        var chart = Chart();
        new AddNotes(Guitar, Expert, [new EditableNote(480, 1, 0), new EditableNote(3840, 960, 1)]).Apply(chart);

        var marks = SongEventPlacement.Place(chart, new AudibleRange(1, 2), TimeSpan.Zero);

        Assert.AreEqual(new SongEventMarks(480, 4800, 5760, FromAudio: true), marks);
    }

    [TestMethod]
    public void TheAudiosTicksFollowTheTempo()
    {
        // At 60 BPM a second is 480 ticks.
        var marks = SongEventPlacement.Place(Chart(bpm: 60), new AudibleRange(1, 2), TimeSpan.Zero);

        Assert.AreEqual(new SongEventMarks(480, 960, 1920, FromAudio: true), marks);
    }

    [TestMethod]
    public void TheCopyToWriteHasOneOfEachInPlaceOfAnyTheChartHas()
    {
        // A misplaced [end], a second spaced out, a section, [coda], and an [end] that's a copyright, not a text event.
        var chart = Chart();
        TextEvent[] kept = [TextEvent.Of(0, "[section Intro]"), new TextEvent(50, 0x02, "[end]"), TextEvent.Of(300, "[coda]")];
        foreach (var e in (TextEvent[])[.. kept, TextEvent.Of(0, "[music_start]"), TextEvent.Of(100, " [end] "), TextEvent.Of(200, "[end]")])
            new AddTextEvent(null, e).Apply(chart);
        var before = chart.Events.ToList();
        var marks = new SongEventMarks(10, 20, 1920, FromAudio: true);

        var copy = SongEventPlacement.WithSongEvents(chart, marks);

        Assert.AreSequenceEqual(
            [.. kept, TextEvent.Of(10, "[music_start]"), TextEvent.Of(20, "[music_end]"), TextEvent.Of(1920, "[end]")],
            copy.Events.ToList(),
            Microsoft.VisualStudio.TestTools.UnitTesting.SequenceOrder.InAnyOrder);
        Assert.IsTrue(SongEventPlacement.Matches([.. copy.Events.Where(SongEventPlacement.IsPlaced)], marks));
        Assert.AreSequenceEqual(before, chart.Events.ToList(), "The chart open isn't changed.");
    }

    [TestMethod]
    public void StripTakesOutOnlyThePlacedSongEventsAndSaysWhich()
    {
        var chart = Chart();
        foreach (var e in (TextEvent[])[TextEvent.Of(0, "[music_start]"), TextEvent.Of(960, "[coda]"), TextEvent.Of(1920, "[music_end]"), TextEvent.Of(3840, "[end]")])
            new AddTextEvent(null, e).Apply(chart);

        var stripped = SongEventPlacement.Strip(chart);

        Assert.AreSequenceEqual([TextEvent.Of(0, "[music_start]"), TextEvent.Of(1920, "[music_end]"), TextEvent.Of(3840, "[end]")], stripped.ToList());
        Assert.AreSequenceEqual([TextEvent.Of(960, "[coda]")], chart.Events.ToList());
    }

    [TestMethod]
    public void EventsMatchTheMarksOnlyWithOneOfEachWhereTheyGo()
    {
        var marks = new SongEventMarks(0, 1920, 3840, FromAudio: false);
        TextEvent[] placed = [TextEvent.Of(0, "[music_start]"), TextEvent.Of(1920, "[music_end]"), TextEvent.Of(3840, "[end]")];

        Assert.IsTrue(SongEventPlacement.Matches(placed, marks));
        Assert.IsTrue(SongEventPlacement.Matches([placed[2], placed[0], placed[1]], marks), "In any order.");
        Assert.IsFalse(SongEventPlacement.Matches(placed[..2], marks), "[end] is missing.");
        Assert.IsFalse(SongEventPlacement.Matches([.. placed, TextEvent.Of(5760, "[end]")], marks), "A second [end].");
        Assert.IsFalse(SongEventPlacement.Matches([placed[0], placed[1], TextEvent.Of(5760, "[end]")], marks), "[end] is elsewhere.");
    }

    private static EditableChart Chart(double bpm = 120) =>
        EditableChart.CreateNew(new NewChartSettings("Song", 480, bpm, 4, 4, new HashSet<ChartInstrument> { Guitar }, DrumsVariant.Pro));
}
