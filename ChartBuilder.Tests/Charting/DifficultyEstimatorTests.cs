using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Difficulty;
using ChartShared.Core.Charting.Lint;
using ChartShared.Core.Charts;
using ChartShared.Core.Validation;
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
        Assert.IsNotNull(estimate.Rating);
        Assert.IsFalse(estimate.Rating.IsOverchart);
    }

    [TestMethod]
    public void FastHopoChordsAreRatedTheHardest()
    {
        // Sixteenth-note chords at 200 BPM, every one forced a HOPO.
        var chart = Chart(Guitar, Enumerable.Range(0, 256).SelectMany(i => new[] { new EditableNote(i * 120L, 1, i % 3), new EditableNote(i * 120L, 1, (i % 3) + 2) }), bpm: 200);
        new SetChordFlags(Guitar, Expert, Enumerable.Range(0, 256).Select(i => i * 120L), ChordFlags.ForceHopo, on: true).Apply(chart);

        var estimate = DifficultyEstimator.EstimatePart(chart, Guitar, Options)!;

        Assert.IsGreaterThanOrEqualTo(6, estimate.Tier);
        Assert.AreEqual(estimate.Tier, (int)Math.Floor(estimate.Rating!.Rank));
        Assert.AreEqual(1, estimate.Basis.ChordShare);
        Assert.AreEqual(1, estimate.Basis.HopoTapShare);
    }

    [TestMethod]
    public void AnOverchartIsRatedPastSix()
    {
        // Thirty-second-note chords at 220 BPM, strummed and jumping across the neck.
        var chart = Chart(Guitar, Enumerable.Range(0, 512).SelectMany(i => new[] { new EditableNote(i * 60L, 1, (i * 2) % 4), new EditableNote(i * 60L, 1, 4) }), bpm: 220);
        new SetChordFlags(Guitar, Expert, Enumerable.Range(0, 512).Select(i => i * 60L), ChordFlags.ForceStrum, on: true).Apply(chart);

        var estimate = DifficultyEstimator.EstimatePart(chart, Guitar, Options)!;

        Assert.IsGreaterThanOrEqualTo(RankCurve.FirstOverchart, estimate.Tier);
        Assert.IsTrue(estimate.Rating!.IsOverchart);
    }

    [TestMethod]
    public void NaturalHoposCount()
    {
        // Eighth notes moving between frets at 120 BPM are close enough to be HOPOs as the games read them.
        var chart = Chart(Guitar, Enumerable.Range(0, 64).Select(i => new EditableNote(i * 120L, 1, i % 2)));
        var estimate = DifficultyEstimator.EstimatePart(chart, Guitar, Options)!;
        new SetChordFlags(Guitar, Expert, Enumerable.Range(0, 64).Select(i => i * 120L), ChordFlags.ForceStrum, on: true).Apply(chart);
        var strummed = DifficultyEstimator.EstimatePart(chart, Guitar, Options)!;

        Assert.IsGreaterThan(0.5, estimate.Basis.HopoTapShare);
        Assert.IsGreaterThan(estimate.Rating!.Attributes.Strum, strummed.Rating!.Attributes.Strum);
    }

    [TestMethod]
    public void NotesAfterTheEndEventAreNotRated()
    {
        // Quarter notes, then a burst of thirty-seconds the [end] event cuts off.
        var notes = Enumerable.Range(0, 64).Select(i => new EditableNote(i * 480L, 1, i % 3))
            .Concat(Enumerable.Range(0, 256).Select(i => new EditableNote(40_000 + (i * 60L), 1, i % 5)));
        var chart = Chart(Guitar, notes);
        var whole = DifficultyEstimator.EstimatePart(chart, Guitar, Options)!;
        chart.Events.Add(TextEvent.Of(32_000, SongEvents.EndText));

        var ended = DifficultyEstimator.EstimatePart(chart, Guitar, Options)!;

        Assert.IsGreaterThan(ended.Tier, whole.Tier);
        Assert.AreEqual(64, ended.Basis.Chords);
    }

    [TestMethod]
    public void KicksBetweenTheHandsHitsAreHarderThanKicksWithThem()
    {
        // Snare eighths at 120 BPM, a kick with each, or a sixteenth after each.
        var snare = Enumerable.Range(0, 64).Select(i => new EditableNote(i * 240L, 1, 1));
        var together = Chart(Drums, snare.Concat(Enumerable.Range(0, 64).Select(i => new EditableNote(i * 240L, 1, 0))));
        var apart = Chart(Drums, snare.Concat(Enumerable.Range(0, 64).Select(i => new EditableNote((i * 240L) + 120, 1, 0))));

        var a = DifficultyEstimator.EstimatePart(together, Drums, Options)!;
        var b = DifficultyEstimator.EstimatePart(apart, Drums, Options)!;

        Assert.AreEqual(0.0, a.Basis.KickIndependence);
        Assert.AreEqual(1.0, b.Basis.KickIndependence);
        Assert.AreEqual(DifficultyInstrument.Drums, a.Rating.Instrument);
        Assert.IsGreaterThan(a.Rating.Attributes.Strum, b.Rating.Attributes.Strum);
    }

    [TestMethod]
    public void DrumsAndSixFretAreRatedByTheModel()
    {
        var drums = DifficultyEstimator.EstimatePart(Chart(Drums, Enumerable.Range(0, 128).Select(i => new EditableNote(i * 120L, 1, (i % 4) + 1))), Drums, Options)!;
        var ghl = DifficultyEstimator.EstimatePart(Chart(ChartInstrument.GhlGuitar, Enumerable.Range(0, 128).Select(i => new EditableNote(i * 120L, 1, i % 6))), ChartInstrument.GhlGuitar, Options)!;

        Assert.AreEqual(DifficultyInstrument.Drums, drums.Rating.Instrument);
        Assert.AreEqual(DifficultyInstrument.SixFret, ghl.Rating.Instrument);
        Assert.IsGreaterThan(0, drums.Rating.Attributes.Note);
        Assert.IsGreaterThan(0, ghl.Rating.Attributes.Note);
        Assert.AreEqual(drums.Tier, (int)Math.Floor(drums.Rating.Rank));
    }

    [TestMethod]
    public void ProDrumsAreRatedForEachKeyTheirCymbalsOnlyInDiffDrumsReal()
    {
        // Sixteenths around the snare and the yellow and blue cymbals: Pro drums' cymbals are a row of their own.
        var chart = Chart(Drums, Enumerable.Range(0, 128).Select(i => new EditableNote(i * 120L, 1, (i % 3) + 1)));

        var keyed = DifficultyEstimator.EstimateKeys(chart, Drums, Options);

        Assert.AreSequenceEqual(["diff_drums", "diff_drums_real"], keyed.Select(k => k.Key).ToList());
        Assert.AreSequenceEqual([DifficultyInstrument.Drums, DifficultyInstrument.ProDrums], keyed.Select(k => k.Estimate.Rating.Instrument).ToList());
        Assert.IsGreaterThan(keyed[0].Estimate.Rating.Attributes.Note, keyed[1].Estimate.Rating.Attributes.Note);
        Assert.AreEqual(keyed[0].Estimate, DifficultyEstimator.EstimatePart(chart, Drums, Options));
        Assert.AreEqual(keyed[1].Estimate, DifficultyEstimator.EstimatePart(chart, Drums, Options, DifficultyInstrument.ProDrums));
    }

    [TestMethod]
    public void ANotProDrumsChartIsRatedForDiffDrumsAlone()
    {
        var chart = Chart(Drums, Enumerable.Range(0, 64).Select(i => new EditableNote(i * 240L, 1, (i % 2) + 1)));
        chart.DrumsVariant = DrumsVariant.FourLane;

        Assert.AreSequenceEqual(["diff_drums"], DifficultyEstimator.EstimateKeys(chart, Drums, Options).Select(k => k.Key).ToList());
    }

    [TestMethod]
    public void AProChartTheGamesDontReadAsProIsntRatedAsProDrums()
    {
        // song.ini says five pads, or no pro drums: either way the game view has no cymbals to rate.
        var chart = Chart(Drums, Enumerable.Range(0, 128).Select(i => new EditableNote(i * 120L, 1, (i % 4) + 1)));

        foreach (var options in new[] { new ChartReadOptions(FiveLaneDrums: true), new ChartReadOptions(ProDrums: false) })
        {
            Assert.AreSequenceEqual(["diff_drums"], DifficultyEstimator.EstimateKeys(chart, Drums, options).Select(k => k.Key).ToList());
            Assert.IsNull(DifficultyEstimator.EstimatePart(chart, Drums, options, DifficultyInstrument.ProDrums));
            Assert.IsEmpty(DifficultyEstimator.EstimateDifficulties(chart, Drums, options, DifficultyInstrument.ProDrums));
            Assert.IsNotNull(DifficultyEstimator.EstimatePart(chart, Drums, options));
        }
    }

    [TestMethod]
    public void ExpertPlusKicksArentCountedInWhatTheRatingWasWorkedOutFrom()
    {
        // Snare and kick on the beat, and Expert+ kicks between them.
        var played = Enumerable.Range(0, 64).SelectMany(i => new[] { new EditableNote(i * 480L, 1, 1), new EditableNote(i * 480L, 1, 0) }).ToList();
        var plain = Chart(Drums, played);
        var plus = Chart(Drums, played.Concat(Enumerable.Range(0, 64).Select(i => new EditableNote((i * 480L) + 240, 1, 0, DoubleKick: true))));

        var a = DifficultyEstimator.EstimatePart(plain, Drums, Options)!;
        var b = DifficultyEstimator.EstimatePart(plus, Drums, Options)!;

        Assert.AreEqual(a.Basis, b.Basis);
        Assert.AreEqual(a.Rating, b.Rating);
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
        Assert.DoesNotContain(c => c.Key == "diff_guitar", song.PendingChanges());

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
        Assert.DoesNotContain(f => f.RuleId == ChrRules.Chr083, lint.Findings);
    }

    [TestMethod]
    public void OnlyTheKeysSongIniRatesOtherwiseAreSetEachToItsOwnTier()
    {
        var chart = Chart(Drums, Enumerable.Range(0, 64).Select(i => new EditableNote(i * 240L, 1, (i % 2) + 1)));
        var keyed = DifficultyEstimator.EstimateKeys(chart, Drums, Options);
        var basis = keyed[0].Estimate;
        KeyedEstimate[] estimates = [keyed[0] with { Estimate = basis with { Tier = 3 } }, keyed[1] with { Estimate = basis with { Tier = 4 } }];

        var ratings = PendingSongIniFixes.RatingsToSet(Drums, [KeyValuePair.Create("diff_drums", "3")], estimates);

        Assert.AreSequenceEqual([("diff_drums_real", 4)], ratings.ToList());
    }

    [TestMethod]
    public void TheUnratedFindingOfDiffDrumsRealIsFixedWithTheProDrumsRating()
    {
        var chart = Chart(Drums, Enumerable.Range(0, 128).Select(i => new EditableNote(i * 120L, 1, i % 2 == 0 ? 1 : 2)));
        var finding = new Finding(ChrRules.Chr083, Severity.Warning, "", Key: "diff_drums_real", Location: new ChartLocation(Drums, null, 0, 0));

        var rating = PendingSongIniFixes.SuggestedRating(finding, chart, Options);

        Assert.AreEqual(("diff_drums_real", DifficultyEstimator.EstimatePart(chart, Drums, Options, DifficultyInstrument.ProDrums)!.Tier), rating);
    }

    private static EditorSong Song()
    {
        var chart = Chart(Guitar, Enumerable.Range(0, 64).Select(i => new EditableNote(i * 240L, 1, i % 4)));
        var song = new OpenedSong(
            new ChartShared.Core.Scanning.SongEntry(ChartShared.Core.Scanning.SongEntryKind.SongFolder, "C:\\Song", ".", "Song"),
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
