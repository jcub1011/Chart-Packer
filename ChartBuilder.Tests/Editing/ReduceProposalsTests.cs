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
    public void ReducingExpertToEasyKeepsTheEasySpacingAndIsUndoneInOneStep(int seed)
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

        AssertEasySpacing([.. chords.Select(c => c.Key)]);
        AssertPlayedAsTheSource(chart, session.ReadOptions, Expert, Easy);
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
        AssertEasySpacing([.. hits.Select(h => h.Key)]);
        Assert.DoesNotContain(n => n.DoubleKick, easy.Notes);

        session.Undo();
        AssertSame(start, chart, "after one undo");
    }

    // Every chord of to played as from's at its tick is, as the games read them: a strum, a HOPO or a tap. A HOPO on the
    // fret before it plays as a strum whatever forces it, so a chord on the same frets as the one before is left out.
    private static void AssertPlayedAsTheSource(EditableChart chart, ChartReadOptions options, ChartDifficulty from, ChartDifficulty to)
    {
        static Dictionary<long, NoteFlags> States(IEnumerable<ChartNote> notes) =>
            notes.GroupBy(n => n.Tick).ToDictionary(g => g.Key, g => g.Select(n => n.Flags & (NoteFlags.Hopo | NoteFlags.Tap)).Max());
        var source = States(GameView.Project(chart, new ChartTrackKey(ChartInstrument.Guitar, from), options).Notes);
        var drafted = GameView.Project(chart, new ChartTrackKey(ChartInstrument.Guitar, to), options).Notes.ToList();
        var lanes = drafted.GroupBy(n => n.Tick).Select(g => (g.Key, Lanes: string.Join(",", g.Select(n => n.Lane).Order()))).ToList();
        var played = States(drafted);
        for (int i = 0; i < lanes.Count; i++)
        {
            long tick = lanes[i].Key;
            if (i > 0 && lanes[i].Lanes == lanes[i - 1].Lanes)
                continue;
            Assert.AreEqual(source[tick], played[tick], $"The chord at {tick}.");
        }
    }

    // Easy's spacing (AC C9.1) at a beat of 480 ticks: no two chords closer than its burst's grid (1/8), and any one more
    // than its longest burst in a row span that many of its grid's steps (1/4).
    private static void AssertEasySpacing(IReadOnlyList<long> ticks)
    {
        int burst = FretReductionRules.Default(ChartInstrumentKind.FiveFret, Easy).MaxBurst ?? 1;
        for (int i = 1; i < ticks.Count; i++)
            Assert.IsGreaterThanOrEqualTo(240L, ticks[i] - ticks[i - 1], $"Chords at {ticks[i - 1]} and {ticks[i]}.");
        for (int i = burst; i < ticks.Count; i++)
            Assert.IsGreaterThanOrEqualTo(480L * burst, ticks[i] - ticks[i - burst], $"Chords from {ticks[i - burst]} to {ticks[i]}.");
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
    public async Task TheCheckShowsTheDraftsFindingsAndRatings()
    {
        // Sixteenths held under the sustain cutoff, which Medium keeps: CHR021 on the draft, as well as on Expert.
        var chart = Guitar();
        new AddNotes(ChartInstrument.Guitar, Expert, [.. Enumerable.Range(0, 64).Select(i => new EditableNote(i * 120L, 150, i % 5))]).Apply(chart);
        var session = Of(chart);
        session.Reduce.Start(ChartInstrument.Guitar, Expert, Medium, FretReductionRules.Default(ChartInstrumentKind.FiveFret, Medium));

        var check = (await session.Reduce.CheckAsync())!;

        Assert.Contains(f => f.RuleId == ChrRules.Chr021, check.Findings);
        Assert.IsTrue(check.Findings.All(f => f.Location!.Difficulty == Medium || (f.RuleId == ChrRules.Chr081 && f.Location.Difficulty == Easy)));
        Assert.IsTrue(check.Estimates.ContainsKey(Expert));
        Assert.IsTrue(check.Estimates.ContainsKey(Medium));
        Assert.IsLessThan(check.Estimates[Expert].Basis.PeakNps, check.Estimates[Medium].Basis.PeakNps);
        Assert.IsEmpty(check.OutOfOrder);
    }

    [TestMethod]
    public async Task TheCheckSaysWhereADifficultyIsNoEasierThanTheOneAbove()
    {
        // Hard is charted denser than Expert; the Easy draft is in order, but Hard isn't.
        var chart = Guitar();
        new AddNotes(ChartInstrument.Guitar, Expert, [.. Enumerable.Range(0, 16).Select(i => new EditableNote(i * 480L, 1, i % 5))]).Apply(chart);
        new AddNotes(ChartInstrument.Guitar, Hard, [.. Enumerable.Range(0, 64).Select(i => new EditableNote(i * 120L, 1, i % 5))]).Apply(chart);
        var session = Of(chart);
        session.Reduce.Start(ChartInstrument.Guitar, Expert, Easy, FretReductionRules.Default(ChartInstrumentKind.FiveFret, Easy));

        var check = (await session.Reduce.CheckAsync())!;

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
    [DataRow(1)]
    [DataRow(2)]
    public async Task TheDiffMarksEachNoteTheDraftLostAndEachChordItChanged(int seed)
    {
        var chart = Guitar();
        DenseGuitar(chart, new Random(seed));
        var session = Of(chart);
        session.Reduce.Start(ChartInstrument.Guitar, Expert, Easy, FretReductionRules.Default(ChartInstrumentKind.FiveFret, Easy));

        var diff = (await session.Reduce.DiffAsync()).Single();

        Assert.AreEqual((Expert, Easy), (diff.Source, diff.Target));
        var kept = diff.DraftTrack.Notes.Select(n => (n.Tick, n.Lane)).ToHashSet();
        for (int i = 0; i < diff.SourceTrack.Notes.Count; i++)
        {
            var note = diff.SourceTrack.Notes[i];
            bool lost = (diff.SourceMarks[i] & (ReductionChanges.Removed | ReductionChanges.Reshaped)) != 0;
            Assert.AreEqual(!kept.Contains((note.Tick, note.Lane)), lost, $"The note at {note.Tick} on {note.Lane}.");
        }

        var changed = diff.Changes.Where(c => (c.Kinds & (ReductionChanges.Reshaped | ReductionChanges.HopoFlipped | ReductionChanges.ForcingChanged)) != 0).Select(c => c.Tick).ToHashSet();
        for (int i = 0; i < diff.DraftTrack.Notes.Count; i++)
            Assert.AreEqual(changed.Contains(diff.DraftTrack.Notes[i].Tick), (diff.DraftMarks[i] & ~ReductionChanges.SustainTrimmed) != 0);
        Assert.IsGreaterThan(0, diff.CountOf(ReductionChanges.Removed));
    }

    [TestMethod]
    public async Task InReduceAllEachDiffIsFromTheDraftAboveIt()
    {
        var chart = Guitar();
        DenseGuitar(chart, new Random(4));
        var session = Of(chart);
        session.Reduce.StartAll(ChartInstrument.Guitar, FretTargets(Hard, Medium, Easy));

        var diffs = await session.Reduce.DiffAsync();

        Assert.AreSequenceEqual([(Expert, Hard), (Hard, Medium), (Medium, Easy)], diffs.Select(d => (d.Source, d.Target)).ToList());
        Assert.AreSame(diffs[0].DraftTrack, diffs[1].SourceTrack);
        Assert.AreSequenceEqual(
            GameView.Project(session.Reduce.Drafted!, new ChartTrackKey(ChartInstrument.Guitar, Hard), session.ReadOptions).Notes.ToList(),
            diffs[1].SourceTrack.Notes.ToList());
    }

    [TestMethod]
    public async Task LeavingOutTheNoteBeforeAHopoTurnsItToAStrum()
    {
        // Green on the downbeat, green, then red a sixteenth later, a natural HOPO; Easy keeps the beats alone.
        var chart = Guitar();
        new AddNotes(ChartInstrument.Guitar, Expert, [new EditableNote(0, 1, 0), new EditableNote(360, 1, 0), new EditableNote(480, 1, 1)]).Apply(chart);
        var session = Of(chart);
        session.Reduce.Start(ChartInstrument.Guitar, Expert, Easy, FretReductionRules.Default(ChartInstrumentKind.FiveFret, Easy) with { KeepForcedHopoAndTap = false });

        var diff = (await session.Reduce.DiffAsync()).Single();

        Assert.IsTrue(diff.SourceTrack.Notes.Single(n => n.Tick == 480).Flags.HasFlag(NoteFlags.Hopo));
        Assert.AreEqual(ReductionChanges.HopoFlipped, diff.Changes.Single(c => c.Tick == 480).Kinds);
        Assert.AreEqual(ReductionChanges.Removed, diff.Changes.Single(c => c.Tick == 360).Kinds);
    }

    [TestMethod]
    public async Task ReducingDrumsBelowExpertDropsThe2xKicks()
    {
        var chart = Drums(DrumsVariant.Pro);
        DenseDrums(chart, DrumsVariant.Pro, new Random(6));
        var session = Of(chart, focused: DrumsExpert);
        session.Reduce.Start(Expert, Hard, DrumReductionRules.Default(Hard));

        var diff = (await session.Reduce.DiffAsync()).Single();

        Assert.IsGreaterThan(0, diff.CountOf(ReductionChanges.DoubleKickDropped));
        for (int i = 0; i < diff.SourceTrack.Notes.Count; i++)
        {
            if (diff.SourceTrack.Notes[i].Flags.HasFlag(NoteFlags.DoubleKick))
                Assert.AreEqual(ReductionChanges.DoubleKickDropped, diff.SourceMarks[i]);
        }
    }

    [TestMethod]
    public async Task TheDiffOfARangeComparesOutsideItWithTheTargetAsCharted()
    {
        var chart = Guitar();
        new AddNotes(ChartInstrument.Guitar, Expert, [.. Enumerable.Range(0, 32).Select(i => new EditableNote(i * 240L, 1, i % 5))]).Apply(chart);
        new AddNotes(ChartInstrument.Guitar, Easy, [new EditableNote(0, 1, 2), new EditableNote(7200, 1, 1)]).Apply(chart);
        var session = Of(chart);
        session.Reduce.Start(ChartInstrument.Guitar, Expert, Easy, FretReductionRules.Default(ChartInstrumentKind.FiveFret, Easy), (1920, 3840));

        var diff = (await session.Reduce.DiffAsync()).Single();

        Assert.IsNotEmpty(diff.Changes);
        Assert.IsTrue(diff.Changes.All(c => c.Tick is >= 1920 and <= 3840 || c.IsBoundary));
        Assert.DoesNotContain(c => c.IsBoundary, diff.Changes);
    }

    [TestMethod]
    public async Task WithoutARunThereIsNoDiff() => Assert.IsEmpty(await Of(Guitar()).Reduce.DiffAsync());

    [TestMethod]
    public void NothingToReduceSaysSo()
    {
        var session = Of(Guitar());

        Assert.IsFalse(session.Reduce.Start(ChartInstrument.Guitar, Expert, Easy, FretReductionRules.Default(ChartInstrumentKind.FiveFret, Easy)));
        Assert.IsNotNull(session.Status);
    }

    [TestMethod]
    public void ARangeWithoutTheSourcesNotesReducesNothing()
    {
        // Expert's notes are all after the range, and Easy's in it are left as they are.
        var chart = Guitar();
        new AddNotes(ChartInstrument.Guitar, Expert, [.. Enumerable.Range(8, 8).Select(i => new EditableNote(i * 480L, 1, i % 5))]).Apply(chart);
        new AddNotes(ChartInstrument.Guitar, Easy, [new EditableNote(480, 1, 0), new EditableNote(1440, 1, 1)]).Apply(chart);
        var start = chart.Clone();
        var session = Of(chart);

        Assert.IsFalse(session.Reduce.Start(ChartInstrument.Guitar, Expert, Easy, FretReductionRules.Default(ChartInstrumentKind.FiveFret, Easy), (0, 1920)));
        Assert.IsFalse(session.Reduce.StartAll(ChartInstrument.Guitar, FretTargets(Easy), (0, 1920)));
        Assert.IsNotNull(session.Status);
        Assert.IsFalse(session.Reduce.IsActive);
        AssertSame(start, chart, "after reducing nothing");
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
