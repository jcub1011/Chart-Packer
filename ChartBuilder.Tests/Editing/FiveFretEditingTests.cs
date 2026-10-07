using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Editing;
using ChartPacker.Core.Charts;
using static ChartBuilder.Tests.Editing.EditorSessions;
using static ChartBuilder.Tests.Charting.MidiChartWriterTests;

namespace ChartBuilder.Tests.Editing;

[TestClass]
public sealed class FiveFretEditingTests
{
    public static IEnumerable<object[]> Views => [[LaneView.Highway], [LaneView.Timeline]];

    [TestMethod]
    [DynamicData(nameof(Views))]
    public void NotesPlacedAndForcedWithTheToolsSaveAndReadBackWithTheirTypes(LaneView view)
    {
        // AC C5.1, made with the editor's tools rather than the model.
        var session = Of(Guitar());
        session.Tool = EditorTool.Place;
        Click(session, At(session, 0, 0, view));
        Click(session, At(session, 120, 1, view));
        Click(session, At(session, 960, 2, view));
        Click(session, At(session, 1920, 3, view));
        Click(session, At(session, 2880, 0, view), EditorModifiers.Shift);
        DragFromTo(session, At(session, 3840, 0, view), At(session, 4320, 0, view));
        DragFromTo(session, At(session, 3840, 1, view), At(session, 4080, 1, view));

        Force(session, 120, ChordFlags.ForceStrum);
        Force(session, 960, ChordFlags.ForceHopo);
        Force(session, 1920, ChordFlags.Tap);

        var chart = session.Chart;
        var after = MidiChartWriter.SettingsAfterSave(chart, ChartReadOptions.Default);
        var result = MidiChartWriter.WriteChecked(chart, after);
        Assert.IsTrue(result.IsValid, DescribeModel(result.ModelDifferences));

        var read = ChartReader.Read(result.Bytes, "notes.mid", after);
        Assert.AreSequenceEqual(
            [
                new ChartNote(0, 0, 0, NoteFlags.None),
                new ChartNote(120, 0, 1, NoteFlags.None),
                new ChartNote(960, 0, 2, NoteFlags.Hopo),
                new ChartNote(1920, 0, 3, NoteFlags.Tap),
                new ChartNote(2880, 0, ChartNote.OpenLane, NoteFlags.None),
                new ChartNote(3840, 480, 0, NoteFlags.None),
                new ChartNote(3840, 240, 1, NoteFlags.None),
            ],
            read.Tracks[GuitarExpert].Notes.ToList());

        // The model reads back with the forcing as it was set, per chord.
        var reloaded = MidiChartLoader.Load(result.Bytes, after).Chart.PartOf(ChartInstrument.Guitar)![ChartDifficulty.Expert];
        Assert.AreSequenceEqual(
            [new FlaggedChord(120, ChordFlags.ForceStrum), new FlaggedChord(960, ChordFlags.ForceHopo), new FlaggedChord(1920, ChordFlags.Tap)],
            reloaded.FlaggedChords.ToList());
    }

    [TestMethod]
    public void TheEditorShowsTheGamesReadingOfAForcedChord()
    {
        var session = Of(Guitar());
        Expert(session).Notes.Add(new EditableNote(0, 1, 0));
        Expert(session).Notes.Add(new EditableNote(120, 1, 1));
        Assert.AreEqual(NoteFlags.Hopo, session.GameTrack(GuitarExpert).Notes[1].Flags);

        Force(session, 120, ChordFlags.ForceStrum);

        Assert.AreEqual(NoteFlags.None, session.GameTrack(GuitarExpert).Notes[1].Flags);
    }

    private static void Force(EditorSession session, long tick, ChordFlags flag)
    {
        session.Selection.Select(GuitarExpert, Expert(session).Notes.Where(n => n.Tick == tick));
        session.Tools.ToggleChordFlag(flag);
    }
}
