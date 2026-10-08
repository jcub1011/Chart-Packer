using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charting.Difficulty;
using ChartPacker.Core.Charting.Lint;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Validation;
using static ChartBuilder.Tests.Charting.MidiChartWriterTests;

namespace ChartBuilder.Tests.Charting;

/// <summary>The suggested rating (charter-spec C18 item 3), and using it as a pending change.</summary>
[TestClass]
public sealed class DifficultyEstimatorTests
{
    private const ChartInstrument Guitar = ChartInstrument.Guitar;
    private const ChartInstrument Drums = ChartInstrument.Drums;
    private const ChartDifficulty Expert = ChartDifficulty.Expert;
    private static readonly ChartReadOptions Options = new();

    [TestMethod]
    public void SingleNotesOnTheBeatAtAnEasyTempoAreRatedLow()
    {
        // 120 BPM, a note a beat: two a second.
        var chart = Chart(Guitar, Enumerable.Range(0, 64).Select(i => new EditableNote(i * 480L, 1, i % 3)));

        var estimate = DifficultyEstimator.EstimatePart(chart, Guitar, Options)!;

        Assert.AreEqual(2.0, estimate.Basis.AverageNps, 0.05);
        Assert.IsLessThanOrEqualTo(1, estimate.Tier);
        Assert.AreEqual(0, estimate.Basis.ChordShare);
    }

    [TestMethod]
    public void FastHopoChordsAreRatedTheHardest()
    {
        // Sixteenth-note chords at 200 BPM, every one forced a HOPO.
        var chart = Chart(Guitar, Enumerable.Range(0, 256).SelectMany(i => new[] { new EditableNote(i * 120L, 1, i % 3), new EditableNote(i * 120L, 1, (i % 3) + 2) }), bpm: 200);
        new SetChordFlags(Guitar, Expert, Enumerable.Range(0, 256).Select(i => i * 120L), ChordFlags.ForceHopo, on: true).Apply(chart);

        var estimate = DifficultyEstimator.EstimatePart(chart, Guitar, Options)!;

        Assert.AreEqual(6, estimate.Tier);
        Assert.AreEqual(1, estimate.Basis.ChordShare);
        Assert.AreEqual(1, estimate.Basis.HopoTapShare);
    }

    [TestMethod]
    public void NaturalHoposCount()
    {
        // Eighth notes moving between frets at 120 BPM are close enough to be HOPOs as the games read them.
        var chart = Chart(Guitar, Enumerable.Range(0, 64).Select(i => new EditableNote(i * 120L, 1, i % 2)));

        Assert.IsGreaterThan(0.5, DifficultyEstimator.EstimatePart(chart, Guitar, Options)!.Basis.HopoTapShare);
    }

    [TestMethod]
    public void AddingNotesNeverLowersTheTier()
    {
        var random = new Random(9);
        var notes = new List<EditableNote>();
        int last = -1;
        for (int round = 0; round < 40; round++)
        {
            for (int i = 0; i < 20; i++)
                notes.Add(new EditableNote(random.Next(0, 400) * 60L, 1, random.Next(0, 5)));
            var chart = Chart(Guitar, notes.DistinctBy(n => (n.Tick, n.Lane)));
            int tier = DifficultyEstimator.EstimatePart(chart, Guitar, Options)!.Tier;
            Assert.IsGreaterThanOrEqualTo(last, tier);
            Assert.IsTrue(tier is >= 0 and <= 6);
            last = tier;
        }
    }

    [TestMethod]
    public void KicksApartFromTheHandsRaiseTheDrumsRating()
    {
        var together = Chart(Drums, Enumerable.Range(0, 64).SelectMany(i => new[] { new EditableNote(i * 240L, 1, 0), new EditableNote(i * 240L, 1, 1) }));
        var apart = Chart(Drums, Enumerable.Range(0, 64).Select(i => new EditableNote(i * 240L, 1, i % 2 == 0 ? 0 : 1)));

        var a = DifficultyEstimator.EstimatePart(together, Drums, Options)!;
        var b = DifficultyEstimator.EstimatePart(apart, Drums, Options)!;

        Assert.AreEqual(0.0, a.Basis.KickIndependence);
        Assert.AreEqual(1.0, b.Basis.KickIndependence);
        Assert.IsGreaterThan(a.Score - (DifficultyWeights.DrumChords * a.Basis.ChordShare), b.Score);
    }

    [TestMethod]
    public void FewerThanTwoChordsGiveNoRating()
    {
        Assert.IsNull(DifficultyEstimator.EstimatePart(Chart(Guitar, [new EditableNote(0, 1, 0)]), Guitar, Options));
        Assert.IsNull(DifficultyEstimator.EstimatePart(NewChart(Guitar), Guitar, Options));
    }

    [TestMethod]
    public void ThePartsRatingIsItsHighestDifficultys()
    {
        var chart = Chart(Guitar, Enumerable.Range(0, 128).Select(i => new EditableNote(i * 120L, 1, i % 5)));
        new AddNotes(Guitar, ChartDifficulty.Easy, [.. Enumerable.Range(0, 16).Select(i => new EditableNote(i * 960L, 1, 0))]).Apply(chart);

        var all = DifficultyEstimator.EstimateDifficulties(chart, Guitar, Options);

        Assert.HasCount(2, all);
        Assert.AreEqual(all[Expert], DifficultyEstimator.EstimatePart(chart, Guitar, Options));
        Assert.IsGreaterThan(all[ChartDifficulty.Easy].Tier, all[Expert].Tier);
    }

    // Using it as a pending change.

    [TestMethod]
    public void AUsedRatingIsAPendingChangeInPlaceOfUnrated()
    {
        var chart = NewChart(Guitar);
        new AddPart(ChartInstrument.Bass).Apply(chart);
        var inputs = new PendingSongIniInputs(new HashSet<ChartInstrument> { Guitar }, [KeyValuePair.Create("diff_guitar", "-1")])
        {
            Ratings = new Dictionary<string, int> { ["diff_guitar"] = 4, ["diff_bass"] = 2 },
        };

        var changes = PendingSongIniChanges.Of(chart, inputs);

        Assert.AreSequenceEqual(
            [new PendingSongIniChange("diff_bass", "2", PendingChangeReason.SuggestedRating), new PendingSongIniChange("diff_guitar", "4", PendingChangeReason.SuggestedRating)],
            changes.ToList());
    }

    [TestMethod]
    public void NothingIsWrittenUntilARatingIsUsedAndRevertTakesItBack()
    {
        var song = Song();
        Assert.IsFalse(song.PendingChanges().Any(c => c.Key == "diff_guitar"));

        song.SetRating("diff_guitar", 5);
        Assert.AreEqual("5", Value(song.EffectivePairs(), "diff_guitar"));

        song.Revert("diff_guitar");
        Assert.AreEqual("-1", Value(song.EffectivePairs(), "diff_guitar"));
        song.Restore("diff_guitar");
        Assert.AreEqual("5", Value(song.EffectivePairs(), "diff_guitar"));
    }

    [TestMethod]
    public void TheUnratedFindingsFixIsTheSuggestedRating()
    {
        var song = Song();
        var finding = new Finding(ChrRules.Chr083, Severity.Warning, "", Key: "diff_guitar", Location: new ChartLocation(Guitar, null, 0, 0));

        var rating = PendingSongIniFixes.SuggestedRating(finding, song.Chart, Options);

        Assert.IsNotNull(rating);
        Assert.AreEqual("diff_guitar", rating.Value.Key);
        song.SetRating(rating.Value.Key, rating.Value.Tier);
        var lint = new ChartLinter().LintAll(new ChartLintInput(song.Chart, song.EffectivePairs()));
        Assert.IsFalse(lint.Findings.Any(f => f.RuleId == ChrRules.Chr083));
    }

    [TestMethod]
    public void OnlyTheKeysSongIniRatesOtherwiseAreSet()
    {
        var chart = NewChart(Drums);
        chart.DrumsVariant = DrumsVariant.Pro;

        var keys = PendingSongIniFixes.RatingKeysToSet(chart, Drums, [KeyValuePair.Create("diff_drums", "3")], 3);

        Assert.AreSequenceEqual(["diff_drums_real"], keys.ToList());
    }

    private static EditorSong Song()
    {
        var chart = Chart(Guitar, Enumerable.Range(0, 64).Select(i => new EditableNote(i * 240L, 1, i % 4)));
        var song = new OpenedSong(
            new ChartPacker.Core.Scanning.SongEntry(ChartPacker.Core.Scanning.SongEntryKind.SongFolder, "C:\\Song", ".", "Song"),
            [KeyValuePair.Create("name", "Song"), KeyValuePair.Create("diff_guitar", "-1")],
            [],
            "notes.mid",
            new ChartLoadResult(chart, []),
            Options,
            TimeSpan.Zero,
            null);
        return EditorSong.Of(song, new HashSet<ChartInstrument> { Guitar });
    }

    private static string? Value(IReadOnlyList<KeyValuePair<string, string>> pairs, string key) => pairs.LastOrDefault(p => p.Key == key).Value;

    private static EditableChart Chart(ChartInstrument part, IEnumerable<EditableNote> notes, int bpm = 120)
    {
        var chart = EditableChart.CreateNew(new NewChartSettings("Song", 480, bpm, 4, 4, new HashSet<ChartInstrument> { part }, DrumsVariant.Pro));
        var list = notes.ToList();
        if (list.Count > 0)
            new AddNotes(part, Expert, list).Apply(chart);
        return chart;
    }
}
