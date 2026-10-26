using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charts;
using static ChartBuilder.Tests.Editing.EditorSessions;

namespace ChartBuilder.Tests.Charting;

/// <summary>
/// Filling a part's missing difficulties without an editing session (<see cref="DifficultyGenerator"/>): what's missing
/// (CHR087), and the drafts, as Reduce all makes them.
/// </summary>
[TestClass]
public sealed class DifficultyGeneratorTests
{
    private const ChartDifficulty Expert = ChartDifficulty.Expert;
    private const ChartDifficulty Hard = ChartDifficulty.Hard;
    private const ChartDifficulty Medium = ChartDifficulty.Medium;
    private const ChartDifficulty Easy = ChartDifficulty.Easy;

    [TestMethod]
    public void WhatsMissingIsEachEmptyDifficultyBelowAPartsHighest()
    {
        var chart = EditableChart.CreateNew(new NewChartSettings("", 480, 120, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.Guitar, ChartInstrument.Bass, ChartInstrument.Drums }, DrumsVariant.Pro));
        new AddNotes(ChartInstrument.Guitar, Expert, Run(16)).Apply(chart);
        new AddNotes(ChartInstrument.Bass, Hard, Run(8)).Apply(chart);
        new AddNotes(ChartInstrument.Bass, Easy, Run(2)).Apply(chart);

        var missing = DifficultyGenerator.MissingTargets(chart);

        Assert.HasCount(2, missing);
        Assert.AreSequenceEqual([Hard, Medium, Easy], [.. missing[ChartInstrument.Guitar]]);
        Assert.AreSequenceEqual([Medium], [.. missing[ChartInstrument.Bass]]);
    }

    [TestMethod]
    public void EachDifficultyIsMadeFromTheOneAboveItAndTheChartIsLeftAsItWas()
    {
        var chart = Guitar();
        new AddNotes(ChartInstrument.Guitar, Expert, Run(64, step: 120)).Apply(chart);

        var generated = DifficultyGenerator.Generate(chart, ChartInstrument.Guitar, [Easy, Hard, Medium], ReductionDefaults.Shipped, DrumsVariant.Pro);

        Assert.AreSequenceEqual([(Expert, Hard), (Hard, Medium), (Medium, Easy)], [.. generated.Drafts.Select(d => (d.Source, d.Target.Difficulty))]);
        var part = generated.Drafted.PartOf(ChartInstrument.Guitar)!;
        Assert.IsTrue(part.Tracks.All(t => t.Notes.Count > 0));
        CollectionAssert.IsSubsetOf(Ticks(part[Medium]), Ticks(part[Hard]));
        CollectionAssert.IsSubsetOf(Ticks(part[Easy]), Ticks(part[Medium]));
        Assert.AreEqual(0, chart.PartOf(ChartInstrument.Guitar)![Hard].Notes.Count);
    }

    [TestMethod]
    public void TheDraftsAreReduceAllsDrafts()
    {
        var chart = Guitar();
        new AddNotes(ChartInstrument.Guitar, Expert, Run(64, step: 120)).Apply(chart);
        var rules = ReductionDefaults.Shipped;
        var session = Of(chart.Clone());
        session.Reduce.StartAll(ChartInstrument.Guitar, new[] { Hard, Medium, Easy }.ToDictionary(t => t, t => rules.FretsFor(ChartInstrumentKind.FiveFret, t)));

        var generated = DifficultyGenerator.Generate(chart, ChartInstrument.Guitar, [Hard, Medium, Easy], rules, DrumsVariant.Pro);

        Assert.HasCount(3, session.Reduce.Drafts);
        foreach (var (reduced, made) in session.Reduce.Drafts.Zip(generated.Drafts))
        {
            Assert.AreEqual(reduced.Target, made.Target);
            Assert.AreSequenceEqual(reduced.Result.Notes.ToList(), [.. made.Result.Notes]);
        }
    }

    [TestMethod]
    public void DrumsAreFilledToo()
    {
        var chart = Drums(DrumsVariant.Pro);
        new AddNotes(ChartInstrument.Drums, Expert, Run(32, step: 240, lanes: 4)).Apply(chart);

        var generated = DifficultyGenerator.Generate(chart, ChartInstrument.Drums, DifficultyGenerator.MissingTargets(chart)[ChartInstrument.Drums], ReductionDefaults.Shipped, DifficultyGenerator.DrumsVariantOf(chart, ChartReadOptions.From([])));

        Assert.HasCount(3, generated.Drafts);
        Assert.IsTrue(generated.Drafted.PartOf(ChartInstrument.Drums)!.Tracks.All(t => t.Notes.Count > 0));
    }

    [TestMethod]
    public void KeysAreFilledByTheFiveFretRulesAsGuitarIs()
    {
        var chart = EditableChart.CreateNew(new NewChartSettings("", 480, 120, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.Guitar, ChartInstrument.Keys }, DrumsVariant.Pro));
        new AddNotes(ChartInstrument.Guitar, Expert, Run(64, step: 120)).Apply(chart);
        new AddNotes(ChartInstrument.Keys, Expert, Run(64, step: 120)).Apply(chart);
        var missing = DifficultyGenerator.MissingTargets(chart);

        var guitar = DifficultyGenerator.Generate(chart, ChartInstrument.Guitar, missing[ChartInstrument.Guitar], ReductionDefaults.Shipped, DrumsVariant.Pro);
        var keys = DifficultyGenerator.Generate(chart, ChartInstrument.Keys, missing[ChartInstrument.Keys], ReductionDefaults.Shipped, DrumsVariant.Pro);

        Assert.AreSequenceEqual([Hard, Medium, Easy], [.. missing[ChartInstrument.Keys]]);
        Assert.HasCount(3, keys.Drafts);
        foreach (var (lead, made) in guitar.Drafts.Zip(keys.Drafts))
        {
            Assert.AreEqual(new ChartTrackKey(ChartInstrument.Keys, lead.Target.Difficulty), made.Target);
            Assert.AreSequenceEqual(lead.Result.Notes.ToList(), [.. made.Result.Notes]);
            Assert.AreSequenceEqual(lead.Result.Flags.ToList(), [.. made.Result.Flags]);
        }
    }

    // A note every `step` ticks, cycling through `lanes` lanes.
    private static List<EditableNote> Run(int count, long step = 480, int lanes = 5) =>
        [.. Enumerable.Range(0, count).Select(i => new EditableNote(i * step, 1, i % lanes))];

    private static List<long> Ticks(EditableTrack track) => [.. track.Notes.AsSpan().ToArray().Select(n => n.Tick).Distinct()];

    /// <summary>A generator that never runs: every step falls back to the rules with a reason (§3.7).</summary>
    private sealed class RefusingGenerator : IDifficultyGenerator
    {
        public string MethodId => "test-refuses";
        public bool CanRunFor(ChartInstrumentKind kind) => true;

        public ReductionResult? Draft(
            EditableChart chart, ChartInstrument part, ChartDifficulty from, ChartDifficulty to,
            GenerationOptions options, (long From, long To)? range, ChartReadOptions? readOptions,
            out string? fallbackReason)
        {
            fallbackReason = $"test refuses {to}";
            return null;
        }
    }

    [TestMethod]
    public void AGeneratorThatCannotRunFallsBackToRulesWithReasons()
    {
        var chart = Guitar();
        new AddNotes(ChartInstrument.Guitar, Expert, Run(64, step: 120)).Apply(chart);
        var rules = ReductionDefaults.Shipped;
        var plain = DifficultyGenerator.Generate(chart, ChartInstrument.Guitar, [Hard, Medium, Easy], rules, DrumsVariant.Pro);
        var fallbacks = new Dictionary<ChartDifficulty, string>();

        var generated = DifficultyGenerator.Generate(chart, ChartInstrument.Guitar, [Hard, Medium, Easy], rules, DrumsVariant.Pro, options: null, new RefusingGenerator(), GenerationOptions.Default, fallbacks);

        Assert.HasCount(3, generated.Drafts);
        Assert.HasCount(3, fallbacks);
        foreach (var (expected, made) in plain.Drafts.Zip(generated.Drafts))
        {
            Assert.AreEqual(expected.Target, made.Target);
            Assert.AreSequenceEqual(expected.Result.Notes.ToList(), [.. made.Result.Notes]);
        }
    }
}
