using ChartPacker.Core.Charting.Playtest;
using ChartPacker.Core.Charts;
using static ChartBuilder.Tests.Playtest.Plays;
using static ChartPacker.Core.Input.InstrumentAction;

namespace ChartBuilder.Tests.Playtest;

/// <summary>The 5-fret rules (charter-spec C19 item 5, AC C19.2), with a 70 ms window either side.</summary>
[TestClass]
public sealed class FiveFretEngineTests
{
    [TestMethod]
    public void AStrumWithTheFretHeldHitsTheNoteAndItsOffsetIsReal()
    {
        var engine = Engine(Note(1.0, 0));

        var judged = Play(engine, 2, Down(Green, 0.9), Down(StrumDown, 1.02));

        Assert.AreSequenceEqual([(JudgeKind.Hit, 0)], Kinds(judged));
        Assert.AreEqual(20, judged[0].OffsetMs, 1e-9);
        Assert.AreEqual(1, engine.Combo);
    }

    [TestMethod]
    [DataRow(0.931, true)]
    [DataRow(1.069, true)]
    [DataRow(0.92, false)]
    [DataRow(1.08, false)]
    public void ANoteCanBeHitOnlyInsideTheWindow(double strum, bool hit)
    {
        var engine = Engine(Note(1.0, 0));

        var judged = Play(engine, 2, Down(Green, 0.5), Down(StrumDown, strum));

        Assert.AreEqual(hit, judged.Any(j => j.Kind == JudgeKind.Hit));
        Assert.AreEqual(!hit, judged.Any(j => j.Kind == JudgeKind.Miss));
    }

    [TestMethod]
    public void ASingleNoteCanBeAnchoredBelowButNotAbove()
    {
        var anchored = Play(Engine(Note(1.0, 2)), 2, Down(Green, 0.5), Down(Yellow, 0.6), Down(StrumDown, 1.0));
        var above = Play(Engine(Note(1.0, 2)), 2, Down(Yellow, 0.5), Down(Blue, 0.6), Down(StrumDown, 1.0));

        Assert.AreSequenceEqual([(JudgeKind.Hit, 0)], Kinds(anchored));
        Assert.AreSequenceEqual([(JudgeKind.Overstrum, -1), (JudgeKind.Miss, 0)], Kinds(above));
    }

    [TestMethod]
    public void AChordNeedsExactlyItsFrets()
    {
        var extra = Play(Engine(Note(1.0, 0), Note(1.0, 1)), 2, Down(Green, 0.5), Down(Red, 0.5), Down(Yellow, 0.5), Down(StrumDown, 1.0));
        var exact = Play(Engine(Note(1.0, 0), Note(1.0, 1)), 2, Down(Green, 0.5), Down(Red, 0.5), Down(StrumDown, 1.0));

        Assert.IsFalse(extra.Any(j => j.Kind == JudgeKind.Hit));
        Assert.AreSequenceEqual([(JudgeKind.Hit, 0)], Kinds(exact));
    }

    [TestMethod]
    public void AnOpenNoteNeedsNoFrets()
    {
        var held = Play(Engine(Note(1.0, ChartNote.OpenLane)), 2, Down(Green, 0.5), Down(StrumDown, 1.0));
        var open = Play(Engine(Note(1.0, ChartNote.OpenLane)), 2, Down(StrumDown, 1.0));

        Assert.IsFalse(held.Any(j => j.Kind == JudgeKind.Hit));
        Assert.AreSequenceEqual([(JudgeKind.Hit, 0)], Kinds(open));
    }

    [TestMethod]
    public void AHopoIsHitByTheFretsWhileTheComboIsGoingAndAPullOffToo()
    {
        var engine = Engine(Note(1.0, 1), Note(1.2, 2, NoteFlags.Hopo), Note(1.4, 1, NoteFlags.Hopo));

        var judged = Play(engine, 2, Down(Red, 0.5), Down(StrumDown, 1.0), Down(Yellow, 1.2), Up(Yellow, 1.4));

        Assert.AreSequenceEqual([(JudgeKind.Hit, 0), (JudgeKind.Hit, 1), (JudgeKind.Hit, 2)], Kinds(judged));
    }

    [TestMethod]
    public void AHopoWithoutAComboMustBeStrummed()
    {
        var engine = Engine(Note(1.0, 2, NoteFlags.Hopo));

        var byFrets = Play(engine, 2, Down(Yellow, 1.0));
        var strummed = Play(engine, 2, Down(Yellow, 0.99), Down(StrumDown, 1.0));

        Assert.AreSequenceEqual([(JudgeKind.Miss, 0)], Kinds(byFrets));
        Assert.AreSequenceEqual([(JudgeKind.Hit, 0)], Kinds(strummed));
    }

    [TestMethod]
    public void ATapIsHitByTheFretsWithoutACombo()
    {
        var judged = Play(Engine(Note(1.0, 3, NoteFlags.Tap)), 2, Down(Blue, 1.01));

        Assert.AreSequenceEqual([(JudgeKind.Hit, 0)], Kinds(judged));
    }

    [TestMethod]
    public void AStrumWaitsAMomentForTheFrets()
    {
        var inTime = Play(Engine(Note(1.0, 0)), 2, Down(StrumDown, 0.99), Down(Green, 1.03));
        var tooLate = Play(Engine(Note(1.0, 0)), 2, Down(StrumDown, 0.95), Down(Green, 1.01));

        Assert.AreSequenceEqual([(JudgeKind.Hit, 0)], Kinds(inTime));
        Assert.AreEqual(30, inTime[0].OffsetMs, 1e-9);
        Assert.AreSequenceEqual([(JudgeKind.Overstrum, -1), (JudgeKind.Miss, 0)], Kinds(tooLate));
    }

    [TestMethod]
    public void AStrumWithNothingToHitIsAnOverstrumThatBreaksTheCombo()
    {
        var engine = Engine(Note(1.0, 0), Note(3.0, 0));

        var judged = Play(engine, 2.5, Down(Green, 0.5), Down(StrumDown, 1.0), Down(StrumDown, 2.0));

        Assert.AreSequenceEqual([(JudgeKind.Hit, 0), (JudgeKind.Overstrum, -1)], Kinds(judged));
        Assert.AreEqual(0, engine.Combo);
    }

    [TestMethod]
    public void AStrumJustAfterAHopoHitByTheFretsIsPartOfIt()
    {
        var engine = Engine(Note(1.0, 0), Note(1.2, 1, NoteFlags.Hopo));

        var judged = Play(engine, 2, Down(Green, 0.5), Down(StrumDown, 1.0), Down(Red, 1.2), Down(StrumDown, 1.25));

        Assert.AreSequenceEqual([(JudgeKind.Hit, 0), (JudgeKind.Hit, 1)], Kinds(judged));
        Assert.AreEqual(2, engine.Combo);
    }

    [TestMethod]
    public void AWrongFretBeforeAHopoGhostsItSoItMustBeStrummed()
    {
        var engine = Engine(Note(1.0, 0), Note(1.2, 1, NoteFlags.Hopo));

        var ghosted = Play(engine, 2, Down(Green, 0.5), Down(StrumDown, 1.0), Down(Yellow, 1.18), Up(Yellow, 1.19), Down(Red, 1.2));
        var strummed = Play(engine, 2, Down(Green, 0.5), Down(StrumDown, 1.0), Down(Yellow, 1.18), Up(Yellow, 1.19), Down(Red, 1.2), Down(StrumDown, 1.21));

        Assert.IsTrue(ghosted.Any(j => j.Kind == JudgeKind.Ghost));
        Assert.AreSequenceEqual([(JudgeKind.Hit, 0), (JudgeKind.Miss, 1)], Kinds(ghosted));
        Assert.AreSequenceEqual([(JudgeKind.Hit, 0), (JudgeKind.Hit, 1)], Kinds(strummed));
    }

    [TestMethod]
    public void PressingAChordsFretsOneAtATimeDoesntGhostIt()
    {
        var engine = Engine(Note(1.0, 2), Note(1.2, 0, NoteFlags.Hopo), Note(1.2, 1, NoteFlags.Hopo));

        var judged = Play(engine, 2, Down(Yellow, 0.5), Down(StrumDown, 1.0), Up(Yellow, 1.19), Down(Green, 1.195), Down(Red, 1.2));

        Assert.AreSequenceEqual([(JudgeKind.Hit, 0), (JudgeKind.Hit, 1)], Kinds(judged));
    }

    [TestMethod]
    public void HittingALaterNoteMissesTheOnesBeforeIt()
    {
        var engine = Engine(Note(1.0, 0), Note(1.05, 1));

        var judged = Play(engine, 2, Down(Red, 0.5), Down(StrumDown, 1.05));

        Assert.AreSequenceEqual([(JudgeKind.Miss, 0), (JudgeKind.Hit, 1)], Kinds(judged));
    }

    [TestMethod]
    public void ASustainLetGoEarlyIsDroppedButTheComboGoesOn()
    {
        var held = Play(Engine(Note(1.0, 0, holdSeconds: 1)), 3, Down(Green, 0.5), Down(StrumDown, 1.0), Up(Green, 1.97));
        var dropped = Play(Engine(Note(1.0, 0, holdSeconds: 1)), 3, Down(Green, 0.5), Down(StrumDown, 1.0), Up(Green, 1.5));

        Assert.IsTrue(held.Any(j => j.Kind == JudgeKind.SustainDone));
        Assert.IsFalse(held.Any(j => j.Kind == JudgeKind.SustainDrop));
        var drop = dropped.Single(j => j.Kind == JudgeKind.SustainDrop);
        Assert.AreEqual(-500, drop.OffsetMs, 1e-6);
        Assert.AreEqual(1, drop.Combo);
    }

    [TestMethod]
    public void AtHalfSpeedTheWindowIsHalfAsLongInTheSongAndOffsetsAreInRealTime()
    {
        var hit = Play(Engine(Note(1.0, 0)), 2, Down(Green, 0.5, 0.5), Down(StrumDown, 1.03, 0.5));
        var missed = Play(Engine(Note(1.0, 0)), 2, Down(Green, 0.5, 0.5), Down(StrumDown, 1.04, 0.5));

        Assert.AreEqual(60, hit.Single(j => j.Kind == JudgeKind.Hit).OffsetMs, 1e-6);
        Assert.IsTrue(missed.Any(j => j.Kind == JudgeKind.Miss));
    }

    [TestMethod]
    public void StartingAgainPassesOverTheNotesBeforeWithoutJudgingThem()
    {
        var engine = Engine(Note(1.0, 0), Note(2.0, 0));
        var output = new List<JudgeEvent>();

        engine.Reset(1.5, 1, static a => a == Green);
        engine.Input(Down(StrumDown, 2.0), output);
        engine.Advance(3, 1, output);

        Assert.AreSequenceEqual([(JudgeKind.Hit, 1)], Kinds(output));
    }

    private static FiveFretEngine Engine(params ChartNote[] notes) => new(Frets(notes), new HitWindow(), new FretRules());
}
