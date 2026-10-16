using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Playtest;
using ChartShared.Core.Charts;
using ChartShared.Core.Input;
using static ChartBuilder.Tests.Playtest.Plays;
using static ChartShared.Core.Input.InstrumentAction;

namespace ChartBuilder.Tests.Playtest;

/// <summary>The drums rules (charter-spec C19 item 6, AC C19.2). Game view lanes: −1 the kick, 0 red, 1 yellow, 2 blue, 3 green.</summary>
[TestClass]
public sealed class DrumsEngineTests
{
    [TestMethod]
    public void EachNoteIsHitByItsOwnPad()
    {
        var engine = Engine(DrumsVariant.FourLane, Note(1.0, ChartNote.OpenLane), Note(1.0, 0), Note(1.5, 2));

        var judged = Play(engine, 2, Down(Kick, 1.01), Down(RedPad, 0.99), Down(BlueTom, 1.5));

        Assert.AreSequenceEqual([(JudgeKind.Hit, 0), (JudgeKind.Hit, 1), (JudgeKind.Hit, 2)], Kinds(judged).OrderBy(k => k.Note).ToList());
        Assert.AreEqual(3, engine.Combo);
    }

    [TestMethod]
    public void APadHitWithNoNoteIsAnOverhitThatBreaksTheComboUnlessThatsOff()
    {
        var breaking = Engine(DrumsVariant.FourLane, Note(1.0, 0));
        var lenient = Engine(DrumsVariant.FourLane, new DrumRules(OverhitBreaksCombo: false), CymbalSplit.None, Note(1.0, 0));

        var judged = Play(breaking, 2, Down(RedPad, 1.0), Down(YellowTom, 1.0));
        Play(lenient, 2, Down(RedPad, 1.0), Down(YellowTom, 1.0));

        Assert.AreSequenceEqual([(JudgeKind.Hit, 0), (JudgeKind.Overhit, -1)], Kinds(judged));
        Assert.AreEqual(1, judged[1].Lane);
        Assert.AreEqual(0, breaking.Combo);
        Assert.AreEqual(1, lenient.Combo);
    }

    [TestMethod]
    public void OnProDrumsACymbalNeedsACymbalWhereTheKitSplitsThatColour()
    {
        var cymbal = Note(1.0, 1, NoteFlags.Cymbal);

        var tomOnCymbal = Play(Engine(DrumsVariant.Pro, new DrumRules(), CymbalSplit.Yellow, cymbal), 2, Down(YellowTom, 1.0));
        var cymbalOnCymbal = Play(Engine(DrumsVariant.Pro, new DrumRules(), CymbalSplit.Yellow, cymbal), 2, Down(YellowCymbal, 1.0));
        var unsplit = Play(Engine(DrumsVariant.Pro, new DrumRules(), CymbalSplit.None, cymbal), 2, Down(YellowTom, 1.0));
        var ruleOff = Play(Engine(DrumsVariant.Pro, new DrumRules(CymbalsMustMatch: false), CymbalSplit.Yellow, cymbal), 2, Down(YellowTom, 1.0));

        Assert.AreSequenceEqual([(JudgeKind.Overhit, -1), (JudgeKind.Miss, 0)], Kinds(tomOnCymbal));
        Assert.AreSequenceEqual([(JudgeKind.Hit, 0)], Kinds(cymbalOnCymbal));
        Assert.AreSequenceEqual([(JudgeKind.Hit, 0)], Kinds(unsplit));
        Assert.AreSequenceEqual([(JudgeKind.Hit, 0)], Kinds(ruleOff));
    }

    [TestMethod]
    public void OnFourLaneDrumsCymbalsDontMatter()
    {
        var judged = Play(Engine(DrumsVariant.FourLane, new DrumRules(), CymbalSplit.Green, Note(1.0, 3)), 2, Down(GreenCymbal, 1.0));

        Assert.AreSequenceEqual([(JudgeKind.Hit, 0)], Kinds(judged));
    }

    [TestMethod]
    public void OnFiveLaneDrumsOrangeAndGreenAreTheirOwnLanes()
    {
        var engine = Engine(DrumsVariant.FiveLane, Note(1.0, 3), Note(1.5, 4));

        var judged = Play(engine, 2, Down(OrangePad, 1.0), Down(GreenTom, 1.5));

        Assert.AreSequenceEqual([(JudgeKind.Hit, 0), (JudgeKind.Hit, 1)], Kinds(judged));
    }

    [TestMethod]
    public void TheTwoTimesKickCanBeLeftOut()
    {
        var notes = new NoteTrack([Note(1.0, ChartNote.OpenLane), Note(1.1, ChartNote.OpenLane, NoteFlags.DoubleKick)], []);

        var with = PlaytestChart.DrumNotes(notes, static t => t / TicksPerSecond, static s => s * TicksPerSecond, DrumsVariant.Pro, includeDoubleKick: true);
        var without = PlaytestChart.DrumNotes(notes, static t => t / TicksPerSecond, static s => s * TicksPerSecond, DrumsVariant.Pro, includeDoubleKick: false);

        Assert.HasCount(2, with.Notes);
        Assert.HasCount(1, without.Notes);
    }

    [TestMethod]
    public void TwoNotesOnAPadInTheWindowAreHitEarliestFirst()
    {
        var engine = Engine(DrumsVariant.FourLane, Note(1.0, 0), Note(1.05, 0));

        var judged = Play(engine, 2, Down(RedPad, 1.03), Down(RedPad, 1.06));

        Assert.AreSequenceEqual([(JudgeKind.Hit, 0), (JudgeKind.Hit, 1)], Kinds(judged));
        Assert.AreEqual(30, judged[0].OffsetMs, 1e-9);
    }

    [TestMethod]
    public void AnAccentHitSoftlyIsCountedWhenDynamicsAreJudged()
    {
        var engine = Engine(DrumsVariant.Pro, new DrumRules(JudgeDynamics: true), CymbalSplit.None, Note(1.0, 0, NoteFlags.Accent), Note(1.5, 0, NoteFlags.Accent));

        var judged = Play(engine, 2, Down(RedPad, 1.0, strength: HitStrength.Normal), Down(RedPad, 1.5, strength: HitStrength.Accent));

        Assert.AreSequenceEqual([(JudgeKind.Hit, 0), (JudgeKind.WrongDynamics, 0), (JudgeKind.Hit, 1)], Kinds(judged));
    }

    private static DrumsEngine Engine(DrumsVariant variant, params ChartNote[] notes) => Engine(variant, new DrumRules(), CymbalSplit.None, notes);

    private static DrumsEngine Engine(DrumsVariant variant, DrumRules rules, CymbalSplit split, params ChartNote[] notes) =>
        new(Drums(variant, notes), new HitWindow(), rules, split);
}
