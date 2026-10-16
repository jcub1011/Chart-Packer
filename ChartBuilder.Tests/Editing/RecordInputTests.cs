using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using ChartShared.Core.Input;
using static ChartBuilder.Tests.Editing.EditorSessions;
using static ChartBuilder.Tests.Playtest.Plays;
using static ChartShared.Core.Input.InstrumentAction;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// Record mode from controllers and MIDI devices (charter-spec C15 item 5, C19 item 7). At 120 BPM and 480 ticks a beat, a
/// second is 960 ticks; the snap is 1/16 (120 ticks) and the sustain cutoff a third of a beat (160 ticks).
/// </summary>
[TestClass]
public sealed class RecordInputTests
{
    [TestMethod]
    public void AStrumPlacesTheFretsHeldAndLettingGoOfThemLaterHoldsTheirSustains()
    {
        var (session, input) = Guitar(GuitarRecordStyle.Strum);

        Feed(input, Down(Green, 0.4), Down(Red, 0.45), Down(StrumDown, 0.5), Up(Green, 1.0), Up(Red, 1.0));

        input.Pass.Finish(2);
        Assert.AreSequenceEqual([(480L, 0, 480L), (480L, 1, 480L)], Notes(session, GuitarExpert));
    }

    [TestMethod]
    public void AStrumWithNoFretsIsAnOpenNote()
    {
        var (session, input) = Guitar(GuitarRecordStyle.Strum);

        Feed(input, Down(StrumUp, 0.5));

        input.Pass.Finish(2);
        Assert.AreSequenceEqual([(480L, ChartNote.OpenLane, EditableNote.PlainLength)], Notes(session, GuitarExpert));
    }

    [TestMethod]
    public void AFretHeldOnlyUntilItsStrummedAgainOrLetGoSoonIsAPlainNote()
    {
        var (session, input) = Guitar(GuitarRecordStyle.Strum);

        Feed(input, Down(Green, 0.4), Down(StrumDown, 0.5), Down(StrumDown, 0.75), Up(Green, 0.8));

        input.Pass.Finish(2);
        Assert.AreSequenceEqual([(480L, 0, EditableNote.PlainLength), (720L, 0, EditableNote.PlainLength)], Notes(session, GuitarExpert));
    }

    [TestMethod]
    public void InTheFretsStyleEachFretPlacesANoteAsItsPressed()
    {
        var (session, input) = Guitar(GuitarRecordStyle.Frets);

        Feed(input, Down(Yellow, 0.5), Up(Yellow, 0.52), Down(Blue, 1.0), Up(Blue, 1.5));

        input.Pass.Finish(2);
        Assert.AreSequenceEqual([(480L, 2, EditableNote.PlainLength), (960L, 3, 480L)], Notes(session, GuitarExpert));
    }

    [TestMethod]
    public void AKitsHitsPlaceNotesOnTheirPadsWithDynamicsFromTheirVelocityButNotOnTheKick()
    {
        var session = Of(Drums(), focused: DrumsExpert);
        var input = new RecordInput(session, session.StartRecording(DrumsExpert, RecordMode.Merge, 0), GuitarRecordStyle.Strum, dynamicsFromVelocity: true);

        Feed(input, Down(RedPad, 0.5, strength: HitStrength.Accent), Down(Kick, 0.5, strength: HitStrength.Accent), Down(YellowCymbal, 1.0, strength: HitStrength.Ghost), Down(RedPad, 1.0));

        input.Pass.Finish(2);
        Assert.AreSequenceEqual(
            [(480L, 0, NoteDynamics.None), (480L, 1, NoteDynamics.Accent), (960L, 1, NoteDynamics.None), (960L, 2, NoteDynamics.Ghost)],
            session.TrackOf(DrumsExpert)!.Notes.Select(n => (n.Tick, n.Lane, n.Dynamics)).ToList());
    }

    [TestMethod]
    public void WithoutDynamicsFromVelocityEveryHitIsPlain()
    {
        var session = Of(Drums(), focused: DrumsExpert);
        var input = new RecordInput(session, session.StartRecording(DrumsExpert, RecordMode.Merge, 0), GuitarRecordStyle.Strum, dynamicsFromVelocity: false);

        Feed(input, Down(RedPad, 0.5, strength: HitStrength.Accent));

        input.Pass.Finish(2);
        Assert.AreEqual(NoteDynamics.None, session.TrackOf(DrumsExpert)!.Notes.Single().Dynamics);
    }

    [TestMethod]
    public void OnFiveLaneDrumsOrangeAndGreenRecordInTheirOwnLanes()
    {
        var session = Of(Drums(DrumsVariant.FiveLane), focused: DrumsExpert);
        var input = new RecordInput(session, session.StartRecording(DrumsExpert, RecordMode.Merge, 0), GuitarRecordStyle.Strum, dynamicsFromVelocity: false);

        Feed(input, Down(OrangePad, 0.5), Down(GreenTom, 1.0));

        input.Pass.Finish(2);
        Assert.AreSequenceEqual([(480L, 4), (960L, 5)], session.TrackOf(DrumsExpert)!.Notes.Select(n => (n.Tick, n.Lane)).ToList());
    }

    private static (EditorSession Session, RecordInput Input) Guitar(GuitarRecordStyle style)
    {
        var session = Of(EditorSessions.Guitar());
        return (session, new RecordInput(session, session.StartRecording(GuitarExpert, RecordMode.Merge, 0), style, dynamicsFromVelocity: false));
    }

    private static void Feed(RecordInput input, params TimedAction[] actions)
    {
        foreach (var action in actions)
            input.Handle(action);
    }

    private static List<(long Tick, int Lane, long Length)> Notes(EditorSession session, ChartTrackKey key) =>
        [.. session.TrackOf(key)!.Notes.Select(n => (n.Tick, n.Lane, n.Length))];
}
