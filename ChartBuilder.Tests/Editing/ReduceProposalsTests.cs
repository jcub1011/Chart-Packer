using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using ChartShared.Core.Validation;
using static ChartBuilder.Tests.Charting.CommandChecks;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Editing;

/// <summary>A run of Reduce (charter-spec C9 item 2): the draft as a ghost, added as one undoable step (AC C9.1).</summary>
[TestClass]
public sealed class ReduceProposalsTests
{
    private const ChartDifficulty Expert = ChartDifficulty.Expert;
    private const ChartDifficulty Hard = ChartDifficulty.Hard;
    private const ChartDifficulty Medium = ChartDifficulty.Medium;
    private const ChartDifficulty Easy = ChartDifficulty.Easy;

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public void ReducingExpertToEasyMakesNoChordOrNoteCloserThanTheEasyGridAndIsUndoneInOneStep(int seed)
    {
        // AC C9.1, on a dense chart: 1/32 runs, triplets, chords of up to five, opens, forcing, taps and sustains, and a
        // change to 3/4 part way.
        var chart = Guitar();
        DenseGuitar(chart, new Random(seed));
        var start = chart.Clone();
        var session = Of(chart);

        Assert.IsTrue(session.Reduce.Start(ChartInstrument.Guitar, Expert, Easy, FretReductionRules.Default(ChartInstrumentKind.FiveFret, Easy)));
        Assert.IsNotNull(session.GhostTrack(new ChartTrackKey(ChartInstrument.Guitar, Easy)));
        AssertSame(start, chart, "while the draft is shown");
        Assert.IsTrue(session.Reduce.Accept());

        var easy = chart.PartOf(ChartInstrument.Guitar)![Easy];
        var chords = easy.Notes.GroupBy(n => n.Tick).ToList();
        Assert.IsNotEmpty(chords);
        foreach (var chord in chords)
        {
            Assert.HasCount(1, chord.Select(n => n.Lane).Distinct(), $"The chord at {chord.Key} has more than one fret.");
            Assert.IsTrue(chord.All(n => n.Lane is ChartNote.OpenLane or >= 0 and <= 2), $"The chord at {chord.Key} is past yellow.");
        }

        for (int i = 1; i < chords.Count; i++)
            Assert.IsGreaterThanOrEqualTo(480L, chords[i].Key - chords[i - 1].Key, $"Chords at {chords[i - 1].Key} and {chords[i].Key}.");
        Assert.DoesNotContain(f => (f.Flags & (ChordFlags.ForceHopo | ChordFlags.Tap)) != 0, easy.FlaggedChords);
        Assert.DoesNotContain(n => n.Length > EditableNote.PlainLength && n.Length < 480, easy.Notes);
        Assert.IsFalse(session.Reduce.IsActive);

        session.Undo();
        AssertSame(start, chart, "after one undo");
    }

    [TestMethod]
    [DataRow(DrumsVariant.Pro)]
    [DataRow(DrumsVariant.FiveLane)]
    public void ReducingDrumsToEasyKeepsOnePadAndTheKickAtMostOnTheGridAndIsUndoneInOneStep(DrumsVariant variant)
    {
        var chart = Drums(variant);
        DenseDrums(chart, variant, new Random(5));
        var start = chart.Clone();
        var session = Of(chart, focused: DrumsExpert);

        Assert.IsTrue(session.Reduce.Start(Expert, Easy, DrumReductionRules.Default(Easy)));
        Assert.IsTrue(session.Reduce.Accept());

        var easy = chart.PartOf(ChartInstrument.Drums)![Easy];
        var hits = easy.Notes.GroupBy(n => n.Tick).ToList();
        Assert.IsNotEmpty(hits);
        foreach (var hit in hits)
            Assert.IsLessThanOrEqualTo(1, hit.Count(n => n.Lane > 0), $"The hit at {hit.Key} has more than one pad.");
        for (int i = 1; i < hits.Count; i++)
            Assert.IsGreaterThanOrEqualTo(480L, hits[i].Key - hits[i - 1].Key);
        Assert.DoesNotContain(n => n.DoubleKick, easy.Notes);

        session.Undo();
        AssertSame(start, chart, "after one undo");
    }

    [TestMethod]
    public void AChangeToTheChartEndsTheRun()
    {
        var chart = Guitar();
        new AddNotes(ChartInstrument.Guitar, Expert, [new EditableNote(0, 1, 0)]).Apply(chart);
        var session = Of(chart);
        session.Reduce.Start(ChartInstrument.Guitar, Expert, Easy, FretReductionRules.Default(ChartInstrumentKind.FiveFret, Easy));
        var said = new List<string>();
        session.StatusPosted += (_, status) => said.Add(status);

        session.Execute(new AddNotes(ChartInstrument.Guitar, Expert, [new EditableNote(480, 1, 1)]));

        Assert.IsFalse(session.Reduce.IsActive);
        Assert.IsNull(session.GhostTrack(new ChartTrackKey(ChartInstrument.Guitar, Easy)));
        Assert.HasCount(1, said);
    }

    [TestMethod]
    public void CancellingAddsNothing()
    {
        var chart = Guitar();
        new AddNotes(ChartInstrument.Guitar, Expert, [new EditableNote(0, 1, 0)]).Apply(chart);
        var session = Of(chart);
        session.Reduce.Start(ChartInstrument.Guitar, Expert, Easy, FretReductionRules.Default(ChartInstrumentKind.FiveFret, Easy));

        session.Reduce.Cancel();

        Assert.IsFalse(session.UndoStack.CanUndo);
        Assert.IsEmpty(chart.PartOf(ChartInstrument.Guitar)![Easy].Notes);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public void ReduceAllFillsEachFromTheOneAboveItAndIsUndoneInOneStep(int seed)
    {
        // AC C9.2: Hard from Expert, Medium from Hard's draft, Easy from Medium's, so each rhythm is the one above's, thinned.
        var chart = Guitar();
        DenseGuitar(chart, new Random(seed));
        var start = chart.Clone();
        var session = Of(chart);

        Assert.IsTrue(session.Reduce.StartAll(ChartInstrument.Guitar, FretTargets(Hard, Medium, Easy)));
        Assert.AreSequenceEqual([(Expert, Hard), (Hard, Medium), (Medium, Easy)], session.Reduce.Drafts.Select(d => (d.Source, d.Target.Difficulty)).ToList());
        foreach (var difficulty in new[] { Hard, Medium, Easy })
            Assert.IsNotNull(session.GhostTrack(new ChartTrackKey(ChartInstrument.Guitar, difficulty)), $"No ghost over {difficulty}.");
        Assert.IsEmpty(session.Reduce.Overwrites);
        AssertSame(start, chart, "while the drafts are shown");
        Assert.IsTrue(session.Reduce.Accept());

        var part = chart.PartOf(ChartInstrument.Guitar)!;
        Assert.IsNotEmpty(part[Easy].Notes);
        CollectionAssert.IsSubsetOf(Ticks(part[Hard]), Ticks(part[Expert]));
        CollectionAssert.IsSubsetOf(Ticks(part[Medium]), Ticks(part[Hard]));
        CollectionAssert.IsSubsetOf(Ticks(part[Easy]), Ticks(part[Medium]));

        session.Undo();
        AssertSame(start, chart, "after one undo");
    }

    [TestMethod]
    public void AHandChartedHardFeedsMedium()
    {
        // Hard is charted on beats Expert's sixteenths don't have, and isn't in the run: Medium is made from it.
        var chart = Guitar();
        new AddNotes(ChartInstrument.Guitar, Expert, [.. Enumerable.Range(0, 64).Select(i => new EditableNote(i * 120L, 1, i % 5))]).Apply(chart);
        new AddNotes(ChartInstrument.Guitar, Hard, [.. Enumerable.Range(0, 8).Select(i => new EditableNote((i * 960L) + 60, 1, i % 4))]).Apply(chart);
        var session = Of(chart);

        Assert.IsTrue(session.Reduce.StartAll(ChartInstrument.Guitar, FretTargets(Medium, Easy)));
        Assert.IsTrue(session.Reduce.Accept());

        var part = chart.PartOf(ChartInstrument.Guitar)!;
        Assert.IsNotEmpty(part[Medium].Notes);
        CollectionAssert.IsSubsetOf(Ticks(part[Medium]), Ticks(part[Hard]));
    }

    [TestMethod]
    public void ReduceAllSaysWhichTargetsItReplaces()
    {
        var chart = Guitar();
        new AddNotes(ChartInstrument.Guitar, Expert, [.. Enumerable.Range(0, 16).Select(i => new EditableNote(i * 240L, 1, i % 5))]).Apply(chart);
        new AddNotes(ChartInstrument.Guitar, Hard, [new EditableNote(0, 1, 0)]).Apply(chart);
        var session = Of(chart);

        Assert.IsTrue(session.Reduce.StartAll(ChartInstrument.Guitar, FretTargets(Hard, Medium)));

        Assert.AreSequenceEqual([Hard], session.Reduce.Overwrites.ToList());
    }

    [TestMethod]
    public void ReducingDrumsAllFillsEachFromTheOneAboveIt()
    {
        var chart = Drums(DrumsVariant.Pro);
        DenseDrums(chart, DrumsVariant.Pro, new Random(3));
        var session = Of(chart, focused: DrumsExpert);
        var targets = new[] { Hard, Medium, Easy }.ToDictionary(d => d, DrumReductionRules.Default);

        Assert.IsTrue(session.Reduce.StartAll(targets));
        Assert.IsTrue(session.Reduce.Accept());

        var part = chart.PartOf(ChartInstrument.Drums)!;
        CollectionAssert.IsSubsetOf(Ticks(part[Medium]), Ticks(part[Hard]));
        CollectionAssert.IsSubsetOf(Ticks(part[Easy]), Ticks(part[Medium]));
    }

    [TestMethod]
    public void TheCheckShowsTheDraftsFindingsAndRatings()
    {
        // Sixteenths held under the sustain cutoff, which Medium keeps: CHR021 on the draft, as well as on Expert.
        var chart = Guitar();
        new AddNotes(ChartInstrument.Guitar, Expert, [.. Enumerable.Range(0, 64).Select(i => new EditableNote(i * 120L, 150, i % 5))]).Apply(chart);
        var session = Of(chart);
        session.Reduce.Start(ChartInstrument.Guitar, Expert, Medium, FretReductionRules.Default(ChartInstrumentKind.FiveFret, Medium));

        var check = session.Reduce.Check()!;

        Assert.Contains(f => f.RuleId == ChrRules.Chr021, check.Findings);
        Assert.IsTrue(check.Findings.All(f => f.Location!.Difficulty == Medium || (f.RuleId == ChrRules.Chr081 && f.Location.Difficulty == Easy)));
        Assert.IsTrue(check.Estimates.ContainsKey(Expert));
        Assert.IsTrue(check.Estimates.ContainsKey(Medium));
        Assert.IsLessThan(check.Estimates[Expert].Basis.PeakNps, check.Estimates[Medium].Basis.PeakNps);
        Assert.IsEmpty(check.OutOfOrder);
    }

    [TestMethod]
    public void TheCheckSaysWhereADifficultyIsNoEasierThanTheOneAbove()
    {
        // Hard is charted denser than Expert; the Easy draft is in order, but Hard isn't.
        var chart = Guitar();
        new AddNotes(ChartInstrument.Guitar, Expert, [.. Enumerable.Range(0, 16).Select(i => new EditableNote(i * 480L, 1, i % 5))]).Apply(chart);
        new AddNotes(ChartInstrument.Guitar, Hard, [.. Enumerable.Range(0, 64).Select(i => new EditableNote(i * 120L, 1, i % 5))]).Apply(chart);
        var session = Of(chart);
        session.Reduce.Start(ChartInstrument.Guitar, Expert, Easy, FretReductionRules.Default(ChartInstrumentKind.FiveFret, Easy));

        var check = session.Reduce.Check()!;

        Assert.Contains(Hard, check.OutOfOrder);
    }

    [TestMethod]
    public void ReducingTheSelectionKeepsEditsElsewhere()
    {
        var chart = Guitar();
        new AddNotes(ChartInstrument.Guitar, Expert, [.. Enumerable.Range(0, 32).Select(i => new EditableNote(i * 240L, 1, i % 5))]).Apply(chart);
        new AddNotes(ChartInstrument.Guitar, Easy, [new EditableNote(0, 1, 2), new EditableNote(7200, 1, 1)]).Apply(chart);
        var session = Of(chart);

        Assert.IsTrue(session.Reduce.Start(ChartInstrument.Guitar, Expert, Easy, FretReductionRules.Default(ChartInstrumentKind.FiveFret, Easy), (1920, 3840)));
        Assert.AreEqual((1920L, 3840L), session.Reduce.Range);
        Assert.IsTrue(session.Reduce.Accept());

        var easy = chart.PartOf(ChartInstrument.Guitar)![Easy].Notes.AsSpan().ToArray();
        Assert.AreSequenceEqual([new EditableNote(0, 1, 2), new EditableNote(7200, 1, 1)], easy.Where(n => n.Tick is < 1920 or > 3840).ToList());
        Assert.IsNotEmpty(easy.Where(n => n.Tick is >= 1920 and <= 3840));
    }

    [TestMethod]
    public void NothingToReduceSaysSo()
    {
        var session = Of(Guitar());

        Assert.IsFalse(session.Reduce.Start(ChartInstrument.Guitar, Expert, Easy, FretReductionRules.Default(ChartInstrumentKind.FiveFret, Easy)));
        Assert.IsNotNull(session.Status);
    }

    private static Dictionary<ChartDifficulty, FretReductionRules> FretTargets(params ChartDifficulty[] targets) =>
        targets.ToDictionary(t => t, t => FretReductionRules.Default(ChartInstrumentKind.FiveFret, t));

    private static List<long> Ticks(EditableTrack track) => [.. track.Notes.AsSpan().ToArray().Select(n => n.Tick).Distinct()];

    private static void DenseGuitar(EditableChart chart, Random random)
    {
        new AddTimeSignature(new TimeSignatureMarker(1920 * 8, 3, 4)).Apply(chart);
        var notes = new List<EditableNote>();
        var flags = new List<(long Tick, ChordFlags Flags)>();
        long tick = 0;
        while (tick < 1920 * 16)
        {
            int step = random.Next(4) switch { 0 => 60, 1 => 160, 2 => 120, _ => 240 };
            if (random.Next(8) == 0)
            {
                notes.Add(new EditableNote(tick, random.Next(3) == 0 ? 960 : 1, ChartNote.OpenLane));
            }
            else
            {
                var lanes = Enumerable.Range(0, 5).Where(_ => random.Next(2) == 0).ToList();
                if (lanes.Count == 0)
                    lanes.Add(random.Next(5));
                long length = random.Next(4) switch { 0 => 120, 1 => 600, _ => 1 };
                notes.AddRange(lanes.Select(l => new EditableNote(tick, length, l)));
                if (random.Next(4) == 0)
                    flags.Add((tick, (ChordFlags)(1 << random.Next(3))));
            }

            tick += step;
        }

        new AddNotes(ChartInstrument.Guitar, Expert, notes).Apply(chart);
        foreach (var (at, flag) in flags)
            new SetChordFlags(ChartInstrument.Guitar, Expert, [at], flag, on: true).Apply(chart);
    }

    private static void DenseDrums(EditableChart chart, DrumsVariant variant, Random random)
    {
        int last = variant == DrumsVariant.FiveLane ? 5 : 4;
        var notes = new List<EditableNote>();
        for (long tick = 0; tick < 1920 * 16; tick += 120)
        {
            if (random.Next(2) == 0)
                notes.Add(new EditableNote(tick, 1, 0));
            if (random.Next(5) == 0)
                notes.Add(new EditableNote(tick, 1, 0, DoubleKick: true));
            foreach (int pad in Enumerable.Range(1, last).Where(_ => random.Next(3) == 0))
                notes.Add(new EditableNote(tick, 1, pad, (NoteDynamics)random.Next(3)));
        }

        new AddNotes(ChartInstrument.Drums, Expert, notes).Apply(chart);
        if (variant == DrumsVariant.Pro)
            new AddDiscoFlip(Expert, 1920 * 4, 1920 * 6).Apply(chart);
    }
}
