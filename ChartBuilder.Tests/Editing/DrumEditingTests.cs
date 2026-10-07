using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charting.Editing;
using ChartPacker.Core.Charts;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// The drums' keys, as the tools carry them out (charter-spec §9, C6): C for cymbal or tom, A and Shift+A for accents and
/// ghosts, and Shift+0 for the 2× kick.
/// </summary>
[TestClass]
public sealed class DrumEditingTests
{
    private const int Kick = 0, Red = 1, Yellow = 2, Blue = 3;

    [TestMethod]
    public void CMakesTheSelectionTomsAndSaysWhichOtherDifficultiesChangedToo()
    {
        // AC C6.2, made with the editor's tools.
        var session = Of(Drums(), focused: DrumsExpert);
        Place(session, ChartDifficulty.Expert, Pad(0, Blue), Pad(480, Blue), Pad(960, Blue));
        Place(session, ChartDifficulty.Hard, Pad(480, Blue));
        Place(session, ChartDifficulty.Medium, Pad(480, Blue));
        session.Selection.Select(DrumsExpert, [Pad(480, Blue)]);
        var posted = new List<string>();
        session.StatusPosted += (_, status) => posted.Add(status);

        session.Tools.ToggleCymbal();

        var drums = session.Chart.PartOf(ChartInstrument.Drums)!;
        Assert.AreSequenceEqual([new TomMarker(Blue, 480, 1)], drums.TomMarkers.ToList());
        Assert.HasCount(1, posted);
        StringAssert.Contains(posted[0], "Hard and Medium");
    }

    [TestMethod]
    public void CMakesASelectionOfTomsCymbalsAndAMixedOneToms()
    {
        var session = Of(Drums(), focused: DrumsExpert);
        Place(session, ChartDifficulty.Expert, Pad(0, Yellow), Pad(480, Blue));
        session.Selection.Select(DrumsExpert, [Pad(0, Yellow), Pad(480, Blue)]);
        var drums = session.Chart.PartOf(ChartInstrument.Drums)!;

        session.Tools.ToggleCymbal();
        Assert.IsTrue(DrumCymbals.IsTom(drums, Yellow, 0) && DrumCymbals.IsTom(drums, Blue, 480));

        session.Tools.ToggleCymbal();
        Assert.IsEmpty(drums.TomMarkers);

        new SetToms([(Yellow, 0L)], tom: true).Apply(session.Chart);
        session.Tools.ToggleCymbal();
        Assert.IsTrue(DrumCymbals.IsTom(drums, Yellow, 0) && DrumCymbals.IsTom(drums, Blue, 480));
    }

    [TestMethod]
    public void CIsRefusedOnARedOrOnFourLaneDrums()
    {
        var session = Of(Drums(DrumsVariant.FourLane), focused: DrumsExpert);
        Place(session, ChartDifficulty.Expert, Pad(0, Red), Pad(480, Blue));

        session.Selection.Select(DrumsExpert, [Pad(0, Red)]);
        session.Tools.ToggleCymbal();
        Assert.IsNotNull(session.Status);

        session.Selection.Select(DrumsExpert, [Pad(480, Blue)]);
        session.Tools.ToggleCymbal();
        Assert.IsNotNull(session.Status);
        Assert.IsEmpty(session.Chart.PartOf(ChartInstrument.Drums)!.TomMarkers);
    }

    [TestMethod]
    public void AAndShiftAToggleAccentsAndGhostsOnThePadsAndLeaveTheKick()
    {
        var session = Of(Drums(), focused: DrumsExpert);
        Place(session, ChartDifficulty.Expert, Pad(0, Kick), Pad(0, Red), Pad(480, Blue));
        session.Selection.Select(DrumsExpert, session.Chart.PartOf(ChartInstrument.Drums)![ChartDifficulty.Expert].Notes);
        var track = session.Chart.PartOf(ChartInstrument.Drums)![ChartDifficulty.Expert];

        session.Tools.ToggleDynamics(NoteDynamics.Accent);
        Assert.AreSequenceEqual([NoteDynamics.None, NoteDynamics.Accent, NoteDynamics.Accent], track.Notes.Select(n => n.Dynamics).ToList());

        session.Tools.ToggleDynamics(NoteDynamics.Ghost);
        Assert.AreSequenceEqual([NoteDynamics.None, NoteDynamics.Ghost, NoteDynamics.Ghost], track.Notes.Select(n => n.Dynamics).ToList());

        session.Tools.ToggleDynamics(NoteDynamics.Ghost);
        Assert.IsTrue(track.Notes.All(n => n.Dynamics == NoteDynamics.None));
        Assert.HasCount(3, session.Selection.Notes);
    }

    [TestMethod]
    public void ShiftZeroPlacesAndRemovesATwoTimesKickOnExpertOnly()
    {
        var session = Of(Drums(), focused: DrumsExpert);
        var track = session.Chart.PartOf(ChartInstrument.Drums)![ChartDifficulty.Expert];

        session.Tools.ToggleDoubleKickAtCursor();
        Assert.AreSequenceEqual([new EditableNote(0, 1, Kick, DoubleKick: true)], track.Notes.ToList());

        session.Tools.ToggleDoubleKickAtCursor();
        Assert.IsEmpty(track.Notes);

        session.Focused = DrumsExpert with { Difficulty = ChartDifficulty.Hard };
        session.Tools.ToggleDoubleKickAtCursor();
        Assert.IsEmpty(session.Chart.PartOf(ChartInstrument.Drums)![ChartDifficulty.Hard].Notes);
        Assert.IsNotNull(session.Status);
    }

    [TestMethod]
    public void ToggleDoubleKickTurnsTheSelectedKicksAndKeepsThemSelected()
    {
        var session = Of(Drums(), focused: DrumsExpert);
        Place(session, ChartDifficulty.Expert, Pad(0, Kick), Pad(0, Red), Pad(480, Kick));
        session.Selection.Select(DrumsExpert, session.Chart.PartOf(ChartInstrument.Drums)![ChartDifficulty.Expert].Notes);

        session.Tools.ToggleDoubleKick();

        var track = session.Chart.PartOf(ChartInstrument.Drums)![ChartDifficulty.Expert];
        Assert.IsTrue(track.Notes.Where(n => n.Lane == Kick).All(n => n.DoubleKick));
        Assert.HasCount(3, session.Selection.Notes);

        session.Tools.ToggleDoubleKick();
        Assert.IsTrue(track.Notes.All(n => !n.DoubleKick));
    }

    private static EditableNote Pad(long tick, int lane) => new(tick, EditableNote.PlainLength, lane);

    private static void Place(EditorSession session, ChartDifficulty difficulty, params EditableNote[] notes) =>
        Assert.IsTrue(session.Execute(new AddNotes(ChartInstrument.Drums, difficulty, notes)));
}
