using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using ChartShared.UI.Controls.Highway;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Preview;

/// <summary>
/// The highway preview's timing (charter-spec C18, AC C18.1): its scene, built from the game view as the preview builds
/// it, puts every note where the editor's lanes do. Both read the same clock in the same frame, which allows for the
/// latency and the speed, so the preview stays within a frame of the lanes.
/// </summary>
[TestClass]
public sealed class PreviewTimingTests
{
    [TestMethod]
    public void EveryNoteIsWhereTheLanesDrawIt()
    {
        var chart = Guitar();
        new AddTempo(new TempoChange(1920, 300_000)).Apply(chart);
        new AddTempo(new TempoChange(4800, 750_000)).Apply(chart);
        new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [new(0, 1, 0), new(1000, 480, 1), new(2400, 1, 2), new(5000, 960, 3), new(7000, 1, 4)]).Apply(chart);
        var session = Of(chart, delay: TimeSpan.FromMilliseconds(250));

        var lane = LaneOf(session, GuitarExpert);

        var notes = session.GameTrack(GuitarExpert).Notes;
        Assert.HasCount(notes.Count, lane.Notes);
        for (int i = 0; i < notes.Count; i++)
        {
            Assert.AreEqual(session.AudioAt(notes[i].Tick), lane.Starts[i], 1e-9);
            Assert.AreEqual(session.AudioAt(notes[i].Tick + notes[i].Length), lane.Ends[i], 1e-9);
        }
    }

    [TestMethod]
    public void DrumsHaveTheLanesTheirVariantDraws()
    {
        Assert.AreEqual(4, LaneOf(Of(Drums(DrumsVariant.Pro), focused: DrumsExpert), DrumsExpert).LaneCount);
        Assert.AreEqual(5, LaneOf(Of(Drums(DrumsVariant.FiveLane), focused: DrumsExpert), DrumsExpert).LaneCount);
        Assert.AreEqual(3, LaneOf(Of(Ghl(), focused: GhlExpert), GhlExpert).LaneCount);
    }

    [TestMethod]
    public void AnEditTouchesTheTracksItChangesAndATempoEditTouchesEvery()
    {
        var hard = new ChartTrackKey(ChartInstrument.Guitar, ChartDifficulty.Hard);
        var bass = new ChartTrackKey(ChartInstrument.Bass, ChartDifficulty.Expert);
        var note = ChartChange.Of(ChartInstrument.Guitar, ChartDifficulty.Expert, 0, 10);
        var phrase = ChartChange.Of(ChartInstrument.Guitar, null, 0, 10);
        var drums = ChartChange.Of(ChartInstrument.Drums, ChartDifficulty.Hard, 0, 10);
        var tempo = ChartChange.Of(SongChanges.TempoMap);

        Assert.IsTrue(note.Touches(GuitarExpert));
        Assert.IsFalse(note.Touches(hard));
        Assert.IsFalse(note.Touches(bass));
        Assert.IsTrue(phrase.Touches(hard));
        Assert.IsTrue(drums.Touches(DrumsExpert));
        Assert.IsTrue(tempo.Touches(bass));
        Assert.IsTrue(tempo.RebuildsAllTracks);
        Assert.IsFalse(ChartChange.Of(SongChanges.Events).Touches(GuitarExpert));
    }

    [TestMethod]
    public void TheGameViewIsWorkedOutAgainOnlyForTheTracksAnEditTouches()
    {
        var chart = Guitar();
        var hard = new ChartTrackKey(ChartInstrument.Guitar, ChartDifficulty.Hard);
        var session = Of(chart);
        var expert = session.GameTrack(GuitarExpert);
        var before = session.GameTrack(hard);

        session.Execute(new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [new(0, 1, 0)]));

        Assert.AreNotSame(expert, session.GameTrack(GuitarExpert));
        Assert.AreSame(before, session.GameTrack(hard));
    }

    // The preview's lane for a track, as PreviewViewModel builds it.
    private static HighwayLane LaneOf(EditorSession session, ChartTrackKey key)
    {
        var kind = ChartInstruments.KindOf(key.Instrument);
        return new HighwayLane(kind, HighwayLane.LaneCountOf(kind, session.IsFiveLaneDrums), "", session.GameTrack(key).Notes, session.TempoMap, session.Delay);
    }
}
