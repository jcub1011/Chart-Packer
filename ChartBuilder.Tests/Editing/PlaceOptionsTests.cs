using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// The place tool's options, as the toolbar picks them (<see cref="PlaceOptions"/>): the chord a note goes in made a forced
/// HOPO, a forced strum or a tap, open notes, a drag that draws out a sustain, and on drums toms, accents, ghosts and 2×
/// kicks. The defaults change nothing already in the chart.
/// </summary>
[TestClass]
public sealed class PlaceOptionsTests
{
    private const int Open = ChartNote.OpenLane;
    private const int Kick = 0, Yellow = 2, Blue = 3;

    public static IEnumerable<object[]> Views => [[LaneView.Highway], [LaneView.Timeline]];

    public static IEnumerable<object[]> ChordTypes =>
        from view in new[] { LaneView.Highway, LaneView.Timeline }
        from type in new[] { ChordFlags.ForceHopo, ChordFlags.ForceStrum, ChordFlags.Tap }
        select new object[] { view, type };

    [TestMethod]
    [DynamicData(nameof(ChordTypes))]
    public void ANotePlacedMakesItsChordTheTypePickedAsOneStep(LaneView view, ChordFlags type)
    {
        var session = Placing(Of(Guitar()), new PlaceOptions(ChordType: type));
        var track = Expert(session);

        Click(session, At(session, 130, 2, view));

        Assert.AreSequenceEqual([new EditableNote(120, EditableNote.PlainLength, 2)], track.Notes.ToList());
        Assert.AreEqual(type, track.FlagsAt(120));
        Assert.AreEqual(1, session.UndoStack.UndoCount);
        session.Undo();
        Assert.IsEmpty(track.Notes);
        Assert.AreEqual(ChordFlags.None, track.FlagsAt(120));
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void EveryChordOfARunIsTheTypePicked(LaneView view)
    {
        var session = Placing(Of(Guitar()), new PlaceOptions(ChordType: ChordFlags.Tap));
        var track = Expert(session);

        DragFromTo(session, At(session, 0, 1, view), At(session, 480, 1, view));

        Assert.HasCount(5, track.Notes);
        Assert.IsTrue(track.Notes.All(n => track.FlagsAt(n.Tick) == ChordFlags.Tap));
        Assert.AreEqual(1, session.UndoStack.UndoCount);
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void NaturalLeavesAChordsMarkingAndATypePickedReplacesIt(LaneView view)
    {
        var session = Of(Guitar());
        var track = Expert(session);
        track.Notes.Add(new EditableNote(0, 1, 0));
        new SetChordFlags(ChartInstrument.Guitar, ChartDifficulty.Expert, [0], ChordFlags.ForceHopo, on: true).Apply(session.Chart);
        Placing(session, PlaceOptions.Default);

        Click(session, At(session, 0, 2, view));
        Assert.AreEqual(ChordFlags.ForceHopo, track.FlagsAt(0), "A note joining a chord leaves its marking as it is.");

        session.Placing = new PlaceOptions(ChordType: ChordFlags.Tap);
        Click(session, At(session, 0, 3, view));
        Assert.AreEqual(ChordFlags.Tap, track.FlagsAt(0), "The chord is made a tap, and no longer a HOPO.");
        Assert.HasCount(3, track.Notes);
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void DragSustainsDrawsOutASustainAndCtrlLaysARunInstead(LaneView view)
    {
        var session = Placing(Of(Guitar()), new PlaceOptions(DragSustains: true));
        var track = Expert(session);

        DragFromTo(session, At(session, 0, 1, view), At(session, 950, 1, view));
        Assert.AreSequenceEqual([new EditableNote(0, 960, 1)], track.Notes.ToList());

        DragFromTo(session, At(session, 1920, 1, view), At(session, 2160, 1, view), EditorModifiers.Control);
        Assert.AreSequenceEqual(
            [new EditableNote(1920, EditableNote.PlainLength, 1), new EditableNote(2040, EditableNote.PlainLength, 1), new EditableNote(2160, EditableNote.PlainLength, 1)],
            track.Notes.Where(static n => n.Tick >= 1920).ToList());
    }

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void OpenPlacesAnOpenNoteAndAltAFret(LaneView view)
    {
        var session = Placing(Of(Guitar()), new PlaceOptions(Open: true));
        var track = Expert(session);

        Click(session, At(session, 130, 2, view));
        Click(session, At(session, 480, 2, view), EditorModifiers.Alt);

        Assert.AreSequenceEqual(
            [new EditableNote(120, EditableNote.PlainLength, Open), new EditableNote(480, EditableNote.PlainLength, 2)],
            track.Notes.ToList());
    }

    [TestMethod]
    public void ThePlaceOptionsForFretsAreLeftOutOnDrums()
    {
        var session = Placing(Of(Drums(), focused: DrumsExpert), new PlaceOptions(ChordType: ChordFlags.Tap, Open: true, DragSustains: true));

        Click(session, At(session, 0, Yellow, key: DrumsExpert));
        DragFromTo(session, At(session, 480, Yellow, key: DrumsExpert), At(session, 720, Yellow, key: DrumsExpert));

        Assert.AreSequenceEqual(
            [Pad(0, Yellow), Pad(480, Yellow), Pad(600, Yellow), Pad(720, Yellow)],
            DrumsTrack(session, ChartDifficulty.Expert).Notes.ToList(),
            "A pad, not the kick, and a drag lays a run, as drums have no sustains.");
        Assert.IsNull(session.Status);
    }

    [TestMethod]
    [DataRow(NoteDynamics.Accent)]
    [DataRow(NoteDynamics.Ghost)]
    public void PadsArePlacedWithTheDynamicsPickedButKicksArePlain(NoteDynamics dynamics)
    {
        var session = Placing(Of(Drums(), focused: DrumsExpert), new PlaceOptions(Dynamics: dynamics));

        Click(session, At(session, 0, Yellow, key: DrumsExpert));
        Click(session, At(session, 480, Yellow, key: DrumsExpert), EditorModifiers.Alt);

        Assert.AreSequenceEqual(
            [new EditableNote(0, EditableNote.PlainLength, Yellow, dynamics), Pad(480, Kick)],
            DrumsTrack(session, ChartDifficulty.Expert).Notes.ToList());
    }

    [TestMethod]
    public void TomMakesAPadPlacedATomOnEveryDifficultyAsOneStep()
    {
        var session = Placing(Of(Drums(), focused: DrumsExpert), new PlaceOptions(Tom: true));
        DrumsTrack(session, ChartDifficulty.Hard).Notes.Add(Pad(0, Yellow));
        var drums = session.Chart.PartOf(ChartInstrument.Drums)!;

        Click(session, At(session, 0, Yellow, key: DrumsExpert));

        Assert.IsTrue(DrumCymbals.IsTom(drums, Yellow, 0));
        Assert.Contains("Hard", session.Status ?? string.Empty, "The status says which other difficulties changed.");
        Assert.AreEqual(1, session.UndoStack.UndoCount);
        session.Undo();
        Assert.IsEmpty(drums.TomMarkers);
        Assert.IsEmpty(DrumsTrack(session, ChartDifficulty.Expert).Notes);
    }

    [TestMethod]
    public void TomLeavesAPadPlacedAsItIsOnDrumsReadAsProFromTheirMarkersAlone()
    {
        // A tom marker but no Pro setting: the drums are read as Pro, but only drums set to Pro can have toms set, so the
        // toolbar doesn't offer Tom, and Tom picked before places the pad plain.
        var chart = Drums();
        chart.PartOf(ChartInstrument.Drums)![ChartDifficulty.Hard].Notes.Add(Pad(1920, Blue));
        new SetToms([(Blue, 1920L)], tom: true).Apply(chart);
        chart.DrumsVariant = null;
        var session = Placing(Of(chart, focused: DrumsExpert), new PlaceOptions(Tom: true));
        Assert.IsTrue(session.IsProDrums);

        Click(session, At(session, 0, Yellow, key: DrumsExpert));

        Assert.AreSequenceEqual([Pad(0, Yellow)], DrumsTrack(session, ChartDifficulty.Expert).Notes.ToList());
        Assert.IsFalse(DrumCymbals.IsTom(chart.PartOf(ChartInstrument.Drums)!, Yellow, 0));
    }

    [TestMethod]
    public void CymbalLeavesAPadATomMarkerCovers()
    {
        var session = Placing(Of(Drums(), focused: DrumsExpert), PlaceOptions.Default);
        DrumsTrack(session, ChartDifficulty.Hard).Notes.Add(Pad(0, Yellow));
        new SetToms([(Yellow, 0L)], tom: true).Apply(session.Chart);
        var drums = session.Chart.PartOf(ChartInstrument.Drums)!;

        Click(session, At(session, 0, Yellow, key: DrumsExpert));

        Assert.IsTrue(DrumCymbals.IsTom(drums, Yellow, 0));
    }

    [TestMethod]
    public void AKickPlacedIsA2xKickOnExpertOnly()
    {
        var session = Placing(Of(Drums(), focused: DrumsExpert), new PlaceOptions(DoubleKick: true));
        var hard = new ChartTrackKey(ChartInstrument.Drums, ChartDifficulty.Hard);

        Click(session, At(session, 0, Yellow, key: DrumsExpert), EditorModifiers.Alt);
        Click(session, At(session, 0, Yellow, key: hard), EditorModifiers.Alt);

        Assert.AreSequenceEqual([new EditableNote(0, EditableNote.PlainLength, Kick, DoubleKick: true)], DrumsTrack(session, ChartDifficulty.Expert).Notes.ToList());
        Assert.AreSequenceEqual([Pad(0, Kick)], DrumsTrack(session, ChartDifficulty.Hard).Notes.ToList());
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void AKickPlacedOnAKickOfTheOtherKindSelectsItRatherThanStackingOne(bool doubleKick)
    {
        var session = Placing(Of(Drums(), focused: DrumsExpert), new PlaceOptions(DoubleKick: doubleKick));
        var track = DrumsTrack(session, ChartDifficulty.Expert);
        var there = new EditableNote(0, EditableNote.PlainLength, Kick, DoubleKick: !doubleKick);
        track.Notes.Add(there);

        Click(session, At(session, 0, Yellow, key: DrumsExpert), EditorModifiers.Alt);

        Assert.AreSequenceEqual([there], track.Notes.ToList());
        Assert.IsTrue(session.Selection.Contains(DrumsExpert, there));
        Assert.AreEqual(0, session.UndoStack.UndoCount);
    }

    [TestMethod]
    public void PickingOptionsChangesTheState()
    {
        var session = Of(Guitar());
        int changes = 0;
        session.StateChanged += (_, _) => changes++;

        session.Placing = session.Placing with { ChordType = ChordFlags.Tap };
        session.Placing = session.Placing with { ChordType = ChordFlags.Tap };

        Assert.AreEqual(1, changes, "Picking what's picked already changes nothing.");
    }

    private static EditorSession Placing(EditorSession session, PlaceOptions options)
    {
        session.Tool = EditorTool.Place;
        session.Placing = options;
        return session;
    }

    private static EditableNote Pad(long tick, int lane) => new(tick, EditableNote.PlainLength, lane);

    private static EditableTrack DrumsTrack(EditorSession session, ChartDifficulty difficulty) => session.Chart.PartOf(ChartInstrument.Drums)![difficulty];
}
