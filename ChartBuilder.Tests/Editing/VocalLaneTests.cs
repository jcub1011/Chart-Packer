using ChartBuilder.Tests.Charting;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charting.Editing;
using ChartPacker.Core.Charting.Layout;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Tests.Charts;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// The vocal parts as the editor's lanes draw them: read from the tracks it keeps as they were loaded (charter-spec §4.2),
/// with their syllables worked out, and read again when a track is replaced.
/// </summary>
[TestClass]
public sealed class VocalLaneTests
{
    // Reading the parts from the chart.

    [TestMethod]
    public void ThePartIsReadFromItsPassthroughTrack()
    {
        var session = Of(Loaded(MidiFixtures.Passthrough()));

        var vocals = session.VocalsOf(VocalTrack.Vocals);

        Assert.IsNotNull(vocals);
        Assert.AreEqual("PART VOCALS", vocals.TrackName);
        Assert.AreSequenceEqual(new[] { new VocalLaneNote(0, 240, 60, false, false), new VocalLaneNote(240, 480, 62, false, false) }, vocals.Notes.ToArray());
        Assert.AreSequenceEqual(new[] { new VocalLaneLyric(0, "Hel", true), new VocalLaneLyric(240, "lo", false) }, vocals.Lyrics.ToArray());
    }

    [TestMethod]
    public void APartTheChartHasntIsNull()
    {
        var session = Of(Loaded(MidiFixtures.Passthrough()));

        Assert.IsNull(session.VocalsOf(VocalTrack.Harmony2));
        Assert.IsNull(Of(Guitar()).VocalsOf(VocalTrack.Vocals));
    }

    [TestMethod]
    public void EachHarmonyIsReadFromItsOwnTrackAndTheFirstOfANameWins()
    {
        byte[] midi = new MidiBuilder(480).Track("Song").Tempo(0, 120).TimeSignature(0, 4, 2)
            .Track("HARM1").Note(0, 60, 240)
            .Track("harm2 ").Note(0, 64, 240)
            .Track("HARM2").Note(0, 67, 240)
            .Build();
        var session = Of(Loaded(midi));

        Assert.AreEqual(60, session.VocalsOf(VocalTrack.Harmony1)!.Notes.Single().Pitch);
        Assert.AreEqual(64, session.VocalsOf(VocalTrack.Harmony2)!.Notes.Single().Pitch);
        Assert.IsNull(session.VocalsOf(VocalTrack.Vocals));
    }

    [TestMethod]
    public void ReadPartReadsATrackAsTheWholeFileIsRead()
    {
        byte[] midi = MidiFixtures.Passthrough();
        var chart = Loaded(midi);
        var whole = VocalReader.Read(midi, "notes.mid").Parts.Single();

        var part = VocalReader.ReadPart(chart.Passthrough.First(t => t.Name == "PART VOCALS"));

        Assert.AreEqual(whole.TrackName, part.TrackName);
        Assert.AreSequenceEqual(whole.Notes.ToArray(), part.Notes.ToArray());
        Assert.AreSequenceEqual(whole.Lyrics.ToArray(), part.Lyrics.ToArray());
        Assert.AreSequenceEqual(whole.Phrases.ToArray(), part.Phrases.ToArray());
    }

    // Reading again after the track is replaced.

    [TestMethod]
    public void ThePartIsKeptUntilItsTrackIsReplacedThenReadAgainAndAnUndoPutsItBack()
    {
        var session = Of(Loaded(MidiFixtures.Passthrough()));
        var before = session.VocalsOf(VocalTrack.Vocals)!;
        Assert.AreSame(before, session.VocalsOf(VocalTrack.Vocals));

        Assert.IsTrue(session.Execute(new ChangeResolution(960)));
        var doubled = session.VocalsOf(VocalTrack.Vocals)!;

        Assert.AreNotSame(before, doubled);
        Assert.AreSequenceEqual(new long[] { 0, 480 }, doubled.Notes.Select(n => n.Tick).ToArray());
        Assert.AreSequenceEqual(new long[] { 0, 480 }, doubled.Lyrics.Select(l => l.Tick).ToArray());

        session.UndoStack.Undo();

        Assert.AreSequenceEqual(new long[] { 0, 240 }, session.VocalsOf(VocalTrack.Vocals)!.Notes.Select(n => n.Tick).ToArray());
    }

    // The syllables, rows and searches.

    [TestMethod]
    public void SpokenNotesAreUnpitchedAndASlideJoinsTheNoteBeforeWithoutAWordOfItsOwn()
    {
        var lane = new VocalLane(Part(
            [new(0, 100, 60), new(120, 100, 64), new(240, 100, 50), new(360, 100, 52)],
            [new(0, "Shout#"), new(120, "yeah^"), new(240, "go-"), new(360, "+")]));

        Assert.AreSequenceEqual(new[] { true, true, false, false }, lane.Notes.Select(n => n.IsUnpitched).ToArray());
        Assert.AreSequenceEqual(new[] { false, false, false, true }, lane.Notes.Select(n => n.IsSlide).ToArray());
        Assert.AreSequenceEqual(new[] { "Shout", "yeah", "go" }, lane.Lyrics.Select(l => l.Text).ToArray());
        Assert.AreSequenceEqual(new[] { false, false, true }, lane.Lyrics.Select(l => l.JoinsNext).ToArray());

        // The rows span the sung notes only.
        Assert.AreEqual(50, lane.LowestPitch);
        Assert.AreEqual(52, lane.HighestPitch);
    }

    [TestMethod]
    public void TheRowsReachPastTheNotesAndSpanAtLeastAnOctaveFromTheHighestDown()
    {
        var narrow = new VocalLane(Part([new(0, 100, 60), new(120, 100, 62)], []));
        var wide = new VocalLane(Part([new(0, 100, 40), new(120, 100, 70)], []));
        var empty = new VocalLane(Part([], []));

        Assert.AreEqual(13, narrow.Pitches.Count);
        Assert.IsGreaterThanOrEqualTo(64, narrow.Pitches[0]);
        Assert.IsLessThanOrEqualTo(58, narrow.Pitches[^1]);
        Assert.AreSequenceEqual(Enumerable.Range(38, 35).Reverse().ToArray(), wide.Pitches.ToArray());
        Assert.HasCount(13, empty.Pitches);
        Assert.IsTrue(empty.IsEmpty);
    }

    [TestMethod]
    public void NotesBetweenFindsANoteHeldIntoTheSpanAndNoteAtTheOneBeingSung()
    {
        var lane = new VocalLane(Part([new(0, 1000, 60), new(1200, 100, 62), new(2000, 100, 64)], []));

        var (from, to) = lane.NotesBetween(500, 1300);
        var found = Enumerable.Range(from, to - from).Select(i => lane.Notes[i]).Where(n => n.EndTick >= 500).Select(n => n.Tick).ToArray();

        Assert.AreSequenceEqual(new long[] { 0, 1200 }, found);
        Assert.AreEqual(0, lane.NoteAt(999));
        Assert.IsNull(lane.NoteAt(1100));
        Assert.AreEqual(1, lane.NoteAt(1200));
    }

    [TestMethod]
    public void LyricsBetweenFindsTheSyllablesInTheSpan()
    {
        var lane = new VocalLane(Part([], [new(0, "a"), new(480, "b"), new(960, "c"), new(1440, "d")]));

        var (from, to) = lane.LyricsBetween(400, 960);

        Assert.AreSequenceEqual(new[] { "b", "c" }, lane.Lyrics.Skip(from).Take(to - from).Select(l => l.Text).ToArray());
    }

    private static EditableChart Loaded(byte[] midi) => MidiChartLoader.Load(midi, ChartReadOptions.Default).Chart;

    private static VocalPart Part(VocalNote[] notes, VocalLyric[] lyrics) => new("PART VOCALS", notes, lyrics, []);
}
