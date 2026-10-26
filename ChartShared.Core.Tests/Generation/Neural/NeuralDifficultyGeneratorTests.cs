using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charts;
using ChartShared.Core.Generation;
using ChartShared.Core.Generation.Neural;
using ChartShared.Core.Settings;

namespace ChartShared.Core.Tests.Generation;

[TestClass]
public sealed class NeuralDifficultyGeneratorTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-neural-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    private GeneratorModelStore Store() =>
        new(new GeneratorModelFolderFile(Path.Combine(_root.FullName, "settings.json")), Path.Combine(_root.FullName, "models"));

    private static EditableChart Chart()
    {
        var chart = EditableChart.CreateNew(new NewChartSettings(
            "Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.Guitar }, DrumsVariant.FourLane));
        // Eight beats of "1 &": onsets every eighth note.
        var notes = Enumerable.Range(0, 16)
            .Select(i => new EditableNote(i * 240L, EditableNote.PlainLength, i % 4 == 0 ? 0 : 1))
            .ToArray();
        new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, notes).Apply(chart);
        return chart;
    }

    // Every beat scores "1" (the downbeat) highest and nothing else: official Easy is mostly "1" (§5.2).
    private static NeuralPack DownbeatPack(string[]? instruments = null, string[]? pairs = null)
    {
        var manifest = NeuralTestPacks.Manifest(styles: ["Any", "Harmonix"], instruments: instruments, pairs: pairs);
        var bias = new float[BeatPattern.Vocabulary.Length];
        bias[1] = 5;
        return NeuralTestPacks.Pack(manifest, NeuralTestPacks.BiasWeights(manifest, bias));
    }

    private static readonly Func<ChartInstrument, ChartDifficulty, FretReductionRules> Shipped =
        static (part, target) => FretReductionRules.Default(ChartInstruments.KindOf(part), target);

    private async Task InstallAsync(GeneratorModelStore store, NeuralPack pack, string name)
    {
        using var stream = new MemoryStream(pack.ToBytes());
        await store.SaveAsync(new GeneratorModelId(GeneratorType.NeuralDifficulty, name), stream, overwrite: true);
    }

    [TestMethod]
    public async Task DraftKeepsASubsetOfTheSourceOnItsOnsets()
    {
        var store = Store();
        await InstallAsync(store, DownbeatPack(), "test");
        var generator = new NeuralDifficultyGenerator(store, Shipped);
        var chart = Chart();

        var result = generator.Draft(chart, ChartInstrument.Guitar, ChartDifficulty.Expert, ChartDifficulty.Easy,
            new GenerationOptions(Style: "Any"), range: null, ChartReadOptions.Default, out string? reason);

        Assert.IsNotNull(result, $"Neural should run with a pack installed; fallback reason: {reason}");
        var sourceTicks = chart.PartOf(ChartInstrument.Guitar)![ChartDifficulty.Expert].Notes.AsSpan().ToArray()
            .Select(static n => n.Tick).ToHashSet();
        Assert.IsTrue(result.Notes.Count > 0, "The downbeat-biased pack keeps notes.");
        foreach (var note in result.Notes)
            Assert.IsTrue(sourceTicks.Contains(note.Tick), $"Draft onset {note.Tick} is on a source onset (§5.1).");
        var kept = result.Notes.Select(static n => n.Tick).Distinct().Order().ToList();
        for (int i = 1; i < kept.Count; i++)
            Assert.IsGreaterThanOrEqualTo(120L, kept[i] - kept[i - 1], "Draft honors the pack's min-gap floor.");
    }

    [TestMethod]
    public async Task DraftsAreDeterministic()
    {
        var store = Store();
        await InstallAsync(store, DownbeatPack(), "test");
        var generator = new NeuralDifficultyGenerator(store, Shipped);

        var first = generator.Draft(Chart(), ChartInstrument.Guitar, ChartDifficulty.Expert, ChartDifficulty.Easy,
            new GenerationOptions(Style: "Any"), null, ChartReadOptions.Default, out _);
        var second = generator.Draft(Chart(), ChartInstrument.Guitar, ChartDifficulty.Expert, ChartDifficulty.Easy,
            new GenerationOptions(Style: "Any"), null, ChartReadOptions.Default, out _);

        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        CollectionAssert.AreEqual(
            first.Notes.Select(static n => (n.Tick, n.Lane)).ToList(),
            second.Notes.Select(static n => (n.Tick, n.Lane)).ToList());
    }

    [TestMethod]
    public void RangeReductionsDraftOnlyTheirRange()
    {
        // Eight beats of "1 &"; draft the middle two beats: no draft note may fall outside.
        var store = Store();
        var chart = Chart();
        var task = InstallAsync(store, DownbeatPack(), "test");
        task.GetAwaiter().GetResult();
        var generator = new NeuralDifficultyGenerator(store, Shipped);

        var result = generator.Draft(chart, ChartInstrument.Guitar, ChartDifficulty.Expert, ChartDifficulty.Easy,
            new GenerationOptions(Style: "Any", ModelName: "test"), (960, 1919), ChartReadOptions.Default, out string? reason);

        Assert.IsNotNull(result, $"Neural should run; fallback reason: {reason}");
        foreach (var note in result.Notes)
            Assert.IsTrue(note.Tick >= 960 && note.Tick <= 1919, $"Draft note at {note.Tick} leaked outside the range.");
    }

    private static EditableChart HighChart()
    {
        var chart = EditableChart.CreateNew(new NewChartSettings(
            "Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.Guitar }, DrumsVariant.FourLane));
        // Orange downbeats, plus a green-orange chord: above Easy's yellow and Medium's blue.
        new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Expert, [
            new EditableNote(0, EditableNote.PlainLength, 4),
            new EditableNote(480, EditableNote.PlainLength, 1),
            new EditableNote(480, EditableNote.PlainLength, 4),
            new EditableNote(960, EditableNote.PlainLength, 4),
        ]).Apply(chart);
        return chart;
    }

    private static NeuralDifficultyGenerator CappedGenerator(GeneratorModelStore store, bool capLanes, out GenerationOptions options)
    {
        options = new GenerationOptions(Style: "Any", ModelName: "test", CapLanes: capLanes);
        return new NeuralDifficultyGenerator(store, Shipped);
    }

    [TestMethod]
    public async Task CappedDraftsStayInTheTargetsLaneRangeWithoutDroppingOnsets()
    {
        var store = Store();
        await InstallAsync(store, DownbeatPack(), "test");
        var generator = CappedGenerator(store, true, out var options);

        var result = generator.Draft(HighChart(), ChartInstrument.Guitar, ChartDifficulty.Expert, ChartDifficulty.Easy,
            options, null, ChartReadOptions.Default, out string? reason);

        Assert.IsNotNull(result, $"Neural should run; fallback reason: {reason}");
        // The easy cap is yellow (lane 2): the orange downbeats move down, none are dropped.
        Assert.IsTrue(result.Notes.All(static n => n.Lane <= 2), "Capped Easy draft exceeds yellow.");
        CollectionAssert.AreEquivalent(
            new[] { 0L, 480L, 960L },
            result.Notes.Select(static n => n.Tick).Distinct().Order().ToList());
    }

    [TestMethod]
    public async Task UncappedDraftsKeepTheSourcesLanes()
    {
        var store = Store();
        await InstallAsync(store, DownbeatPack(), "test");
        var generator = CappedGenerator(store, false, out var options);

        var result = generator.Draft(HighChart(), ChartInstrument.Guitar, ChartDifficulty.Expert, ChartDifficulty.Easy,
            options, null, ChartReadOptions.Default, out string? reason);

        Assert.IsNotNull(result, $"Neural should run; fallback reason: {reason}");
        Assert.IsTrue(result.Notes.Any(static n => n.Lane == 4), "Uncapped draft lost its orange notes.");
    }

    [TestMethod]
    public void KeptChordsAreVoicedAsRulesVoicesThem()
    {
        // Spec §3.4 steps 5–6: the network picks the onsets, Rules plays them. Given the chords Rules keeps, the voicing is
        // Rules' own, note for note.
        var chart = HighChart();
        var rules = FretReductionRules.Default(ChartInstrumentKind.FiveFret, ChartDifficulty.Easy);
        var byRules = Reducer.Reduce(chart, ChartInstrument.Guitar, ChartDifficulty.Expert, ChartDifficulty.Easy, rules);

        var voiced = Reducer.Voice(chart, ChartInstrument.Guitar, ChartDifficulty.Expert, ChartDifficulty.Easy, rules,
            byRules.Notes.Select(static n => n.Tick).ToHashSet());

        CollectionAssert.AreEqual(
            byRules.Notes.Select(static n => (n.Tick, n.Lane, n.Length)).ToList(),
            voiced.Notes.Select(static n => (n.Tick, n.Lane, n.Length)).ToList());
        CollectionAssert.AreEqual(byRules.Flags.ToList(), voiced.Flags.ToList());
        Assert.AreEqual(byRules.Removed, voiced.Removed);
    }

    [TestMethod]
    public async Task APartThePackWasNotTrainedOnFallsBack()
    {
        var store = Store();
        await InstallAsync(store, DownbeatPack(instruments: ["Bass"]), "test");
        var generator = new NeuralDifficultyGenerator(store, Shipped);

        var result = generator.Draft(Chart(), ChartInstrument.Guitar, ChartDifficulty.Expert, ChartDifficulty.Easy,
            new GenerationOptions(ModelName: "test"), null, ChartReadOptions.Default, out string? note);

        Assert.IsNull(result);
        StringAssert.Contains(note, "Guitar");
    }

    [TestMethod]
    public async Task APairThePackLacksDraftsAsFromExpertAndSaysSo()
    {
        var store = Store();
        await InstallAsync(store, DownbeatPack(), "test");
        var generator = new NeuralDifficultyGenerator(store, Shipped);
        var chart = Chart();
        new AddNotes(ChartInstrument.Guitar, ChartDifficulty.Hard,
            chart.PartOf(ChartInstrument.Guitar)![ChartDifficulty.Expert].Notes.AsSpan().ToArray()).Apply(chart);

        var result = generator.Draft(chart, ChartInstrument.Guitar, ChartDifficulty.Hard, ChartDifficulty.Easy,
            new GenerationOptions(ModelName: "test"), null, ChartReadOptions.Default, out string? note);

        Assert.IsNotNull(result, note);
        Assert.IsNotNull(note, "The dialog says when the pair isn't one the pack has (§3.6).");
        StringAssert.Contains(note, "Hard->Easy");
    }

    [TestMethod]
    public async Task ADamagedPackFallsBackInsteadOfThrowing()
    {
        var store = Store();
        using (var stream = new MemoryStream("{\"Manifest\":{\"FormatVersion\":2},\"Weights\":{}}"u8.ToArray()))
            await store.SaveAsync(new GeneratorModelId(GeneratorType.NeuralDifficulty, "broken"), stream, overwrite: true);
        var generator = new NeuralDifficultyGenerator(store, Shipped);

        var result = generator.Draft(Chart(), ChartInstrument.Guitar, ChartDifficulty.Expert, ChartDifficulty.Easy,
            new GenerationOptions(ModelName: "broken"), null, ChartReadOptions.Default, out string? note);

        Assert.IsNull(result);
        StringAssert.Contains(note, "Rules");
    }

    [TestMethod]
    public async Task CappedPhrasesKeepTheirShape()
    {
        var store = Store();
        await InstallAsync(store, DownbeatPack(), "test");
        var generator = CappedGenerator(store, true, out var options);

        // Medium caps at blue (lane 3) with two-note chords: the green-orange chord fits as green-blue.
        var result = generator.Draft(HighChart(), ChartInstrument.Guitar, ChartDifficulty.Expert, ChartDifficulty.Medium,
            options, null, ChartReadOptions.Default, out string? reason);

        Assert.IsNotNull(result, $"Neural should run; fallback reason: {reason}");
        Assert.IsTrue(result.Notes.All(static n => n.Lane <= 3), "Capped Medium draft exceeds blue.");
        CollectionAssert.AreEquivalent(
            new[] { 0, 3 },
            result.Notes.Where(static n => n.Tick == 480).Select(static n => n.Lane).Order().ToList());
    }

    [TestMethod]
    public void MissingPackFallsBackToRulesWithAReason()
    {
        var generator = new NeuralDifficultyGenerator(Store(), Shipped);

        var result = generator.Draft(Chart(), ChartInstrument.Guitar, ChartDifficulty.Expert, ChartDifficulty.Easy,
            new GenerationOptions(Style: "Any"), null, ChartReadOptions.Default, out string? reason);

        Assert.IsNull(result);
        Assert.IsNotNull(reason);
        StringAssert.Contains(reason, "Rules");
    }

    [TestMethod]
    public void DrumsFallBackToRules()
    {
        var generator = new NeuralDifficultyGenerator(Store(), Shipped);

        Assert.IsFalse(generator.CanRunFor(ChartInstrumentKind.Drums));
        var result = generator.Draft(Chart(), ChartInstrument.Drums, ChartDifficulty.Expert, ChartDifficulty.Easy,
            new GenerationOptions(Style: "Any"), null, ChartReadOptions.Default, out string? reason);

        Assert.IsNull(result);
        Assert.IsNotNull(reason);
    }
}
