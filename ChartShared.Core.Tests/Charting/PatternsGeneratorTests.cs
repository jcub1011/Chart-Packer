using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charting.Authoring.Generation;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Tests.Charting;

/// <summary>Counter (Patterns) generator: beat pipeline, decoder limits, scoring, packs and fallback.</summary>
[TestClass]
public sealed class PatternsGeneratorTests
{
    private const ChartInstrument Guitar = ChartInstrument.Guitar;

    [TestMethod]
    public void BeatPatternsSplitByTimeSignature()
    {
        var chart = NewChart();
        Add(chart, [new EditableNote(0, 1, 0), new EditableNote(480, 1, 1), new EditableNote(960, 1, 2)]);
        var map = chart.TempoMap.ToTempoMap();

        var beats = BeatTable.Build([0, 480, 960], map);

        Assert.IsTrue(beats.Count >= 3);
        Assert.AreEqual(0, beats[0].StartTick);
        Assert.AreEqual(0, beats[0].MeasurePlace);
        Assert.AreEqual(1, beats[1].MeasurePlace);
        Assert.AreEqual(new BeatPattern(1UL << 0), beats[0].Source);
    }

    [TestMethod]
    public void EmptyBeatsAreKeptAsContext()
    {
        var chart = NewChart();
        var map = chart.TempoMap.ToTempoMap();

        var beats = BeatTable.Build([0, 1920], map);

        // Beats 1..3 have no onsets but are still present.
        Assert.IsTrue(beats.Count >= 4);
        Assert.IsTrue(beats[1].Source.IsEmpty);
        Assert.AreEqual(1, beats[0].Source.OnsetCount);
    }

    [TestMethod]
    public void TripletsAndSixteenthsShareThe48Grid()
    {
        // 480-tick beat: sixteenth at 120, triplet at 160.
        Assert.AreEqual(BeatTable.Offset48(120, 480), 12);
        Assert.AreEqual(BeatTable.Offset48(160, 480), 16);

        var chart = NewChart();
        var map = chart.TempoMap.ToTempoMap();
        var beats = BeatTable.Build([120, 160], map);

        Assert.AreEqual(2, beats[0].Ticks.Count);
        Assert.AreSequenceEqual(new List<int> { 12, 16 }, beats[0].Offsets48.ToList());
    }

    [TestMethod]
    public void DecoderFindsBestSequenceOnSmallCaseByBruteForce()
    {
        var rnd = new Random(11);
        for (int trial = 0; trial < 25; trial++)
        {
            int n = 4;
            var cands = new List<IReadOnlyList<BeatPattern>>();
            for (int i = 0; i < n; i++)
                cands.Add([new BeatPattern(0), new BeatPattern(1UL << 0), new BeatPattern((1UL << 0) | (1UL << 24))]);
            double Score(int i, BeatPattern c, BeatPattern p1, BeatPattern p2, BeatPattern p3) =>
                (c.Mask * 7919 + (ulong)i * 104729 + p1.Mask * 13 + p2.Mask * 7 + p3.Mask) % 97 / 10.0;
            var request = new SequenceDecoder.DecodeRequest(cands, Score, (_, _) => [], 1);
            var decoded = SequenceDecoder.Decode(request);

            double best = double.NegativeInfinity;
            foreach (var combo in Combos(n, cands))
            {
                double s = 0;
                for (int i = 0; i < n; i++)
                {
                    var p1 = i >= 1 ? combo[i - 1] : new BeatPattern(0);
                    var p2 = i >= 2 ? combo[i - 2] : new BeatPattern(0);
                    var p3 = i >= 3 ? combo[i - 3] : new BeatPattern(0);
                    s += Score(i, combo[i], p1, p2, p3);
                }

                if (s > best)
                    best = s;
            }

            double got = 0;
            for (int i = 0; i < n; i++)
            {
                var p1 = i >= 1 ? decoded[i - 1] : new BeatPattern(0);
                var p2 = i >= 2 ? decoded[i - 2] : new BeatPattern(0);
                var p3 = i >= 3 ? decoded[i - 3] : new BeatPattern(0);
                got += Score(i, decoded[i], p1, p2, p3);
            }

            Assert.AreEqual(best, got, 1e-9, $"Trial {trial}");
        }
    }

    [TestMethod]
    public void DecoderKeepsMinimumGap()
    {
        var cands = new List<IReadOnlyList<BeatPattern>>
        {
            new List<BeatPattern> { new BeatPattern((1UL << 0) | (1UL << 1)) },
            new List<BeatPattern> { new BeatPattern(1UL << 0) },
        };
        var request = new SequenceDecoder.DecodeRequest(
            cands,
            (_, cand, _, _, _) => cand.IsEmpty ? 0 : 10,
            (i, cand) => cand.IsEmpty ? new List<long>() : (i == 0 ? new List<long> { 0L, 10L } : new List<long> { 100L }),
            480);
        var decoded = SequenceDecoder.Decode(request);

        // Beat 0's pair is closer than the floor, so one beat must rest.
        Assert.IsTrue(decoded[0].IsEmpty || decoded[1].IsEmpty);
    }

    [TestMethod]
    public void DensityCenteredChangesNothingAndEndsMoveCount()
    {
        var chart = ChartWithEighths(8);
        var pack = FakePack();
        var calm = new PatternsGenerator(pack, new PatternsOptions(Style: "Any", Density: 0));
        var sparse = new PatternsGenerator(pack, new PatternsOptions(Style: "Any", Density: -1.5));
        var dense = new PatternsGenerator(pack, new PatternsOptions(Style: "Any", Density: 1.5));
        var rules = FretReductionRules.Default(ChartInstrumentKind.FiveFret, ChartDifficulty.Easy);

        var calmNotes = calm.Reduce(chart, Guitar, ChartDifficulty.Expert, ChartDifficulty.Easy, rules).Result.Notes.Count;
        var sparseNotes = sparse.Reduce(chart, Guitar, ChartDifficulty.Expert, ChartDifficulty.Easy, rules).Result.Notes.Count;
        var denseNotes = dense.Reduce(chart, Guitar, ChartDifficulty.Expert, ChartDifficulty.Easy, rules).Result.Notes.Count;

        Assert.IsTrue(sparseNotes <= calmNotes, $"sparse {sparseNotes} <= calm {calmNotes}");
        Assert.IsTrue(denseNotes >= calmNotes, $"dense {denseNotes} >= calm {calmNotes}");
    }

    [TestMethod]
    public void UnseenPatternsStillGetNonZeroProbability()
    {
        var pack = FakePack();
        var scorer = new PatternScorer(pack);
        var ctx = new PatternContext(BeatPattern.Parse("1 & a"), BeatPattern.Empty, BeatPattern.Empty, BeatPattern.Empty, BeatPattern.Empty, 0, "Any", "Easy", "Expert", "Guitar");

        double score = scorer.Score(ctx, BeatPattern.Parse("e"));
        Assert.IsFalse(double.IsNegativeInfinity(score));
        Assert.IsTrue(score < 0);
    }

    [TestMethod]
    public void SameInputGivesSameDraft()
    {
        var chart = ChartWithEighths(16);
        var pack = FakePack();
        var rules = FretReductionRules.Default(ChartInstrumentKind.FiveFret, ChartDifficulty.Easy);

        var a = new PatternsGenerator(pack).Reduce(chart, Guitar, ChartDifficulty.Expert, ChartDifficulty.Easy, rules).Result;
        var b = new PatternsGenerator(pack).Reduce(chart, Guitar, ChartDifficulty.Expert, ChartDifficulty.Easy, rules).Result;

        Assert.AreSequenceEqual(a.Notes.Select(n => n.Tick).ToList(), b.Notes.Select(n => n.Tick).ToList());
    }

    [TestMethod]
    public void NoPackFallsBackToRules()
    {
        var chart = ChartWithEighths(8);
        var rules = FretReductionRules.Default(ChartInstrumentKind.FiveFret, ChartDifficulty.Easy);
        var expected = Reducer.Reduce(chart, Guitar, ChartDifficulty.Expert, ChartDifficulty.Easy, rules);

        var (result, fallback) = new PatternsGenerator(null).Reduce(chart, Guitar, ChartDifficulty.Expert, ChartDifficulty.Easy, rules);

        Assert.IsNotNull(fallback);
        Assert.AreSequenceEqual(expected.Notes.Select(n => n.Tick).ToList(), result.Notes.Select(n => n.Tick).ToList());
    }

    [TestMethod]
    public void DrumsAreUnsupportedAndFallBack()
    {
        var chart = EditableChart.CreateNew(new NewChartSettings("Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.Drums }, DrumsVariant.FourLane));
        new AddNotes(ChartInstrument.Drums, ChartDifficulty.Expert, [new EditableNote(0, 1, 0)]).Apply(chart);
        var gen = new PatternsGenerator(FakePack());

        Assert.IsFalse(gen.Supports(ChartInstrumentKind.Drums));
    }

    [TestMethod]
    public void DamagedPackIsRefused()
    {
        Assert.Throws<PackValidationException>(() => PatternPackLoader.LoadJson("not json", "bad"));
        Assert.Throws<PackValidationException>(() => PatternPackLoader.LoadJson("""{"FormatVersion":999,"BeatSchemaVersion":1,"Vocabulary":["-"]}""", "future"));
        Assert.Throws<PackValidationException>(() => PatternPackLoader.LoadJson("""{"FormatVersion":1,"BeatSchemaVersion":999,"Vocabulary":["-"]}""", "schema"));
        Assert.Throws<PackValidationException>(() => PatternPackLoader.LoadJson("""{"FormatVersion":1,"BeatSchemaVersion":1,"Vocabulary":["bogus token !!!"]}""", "vocab"));
    }

    [TestMethod]
    public void ChecksumMismatchIsRefused()
    {
        var pack = FakePack();
        string json = FakePackJson(checksum: "00");
        Assert.Throws<PackValidationException>(() => PatternPackLoader.LoadJson(json, "tampered"));
        Assert.IsNotNull(pack);
    }

    [TestMethod]
    public void EndToEndDraftPassesCheck()
    {
        var chart = ChartWithEighths(32);
        // Star power phrase must keep a note.
        new AddPhrase(Guitar, new Phrase(PhraseKind.StarPower, 0, 480)).Apply(chart);
        var pack = FakePack();
        var rules = FretReductionRules.Default(ChartInstrumentKind.FiveFret, ChartDifficulty.Easy) with { ThinningDivision = 0 };

        var (result, _) = new PatternsGenerator(pack).Reduce(chart, Guitar, ChartDifficulty.Expert, ChartDifficulty.Easy, rules);
        var ticks = result.Notes.Select(n => n.Tick).Distinct().Order().ToList();

        Assert.IsNotEmpty(ticks);
        Assert.IsTrue(result.Notes.All(n => n.Lane is >= 0 and <= 2), "Voiced to Easy lane range.");
        for (int i = 1; i < ticks.Count; i++)
            Assert.IsGreaterThanOrEqualTo(1L, ticks[i] - ticks[i - 1]);
        Assert.IsTrue(ticks.Any(t => t >= 0 && t < 480), "Star power phrase is not left empty.");
    }

    [TestMethod]
    public void TrainerCountsAndPackRoundTrips()
    {
        var tempo = new TempoMap(480, [new TempoChange(0, 500_000)], [new TimeSignature(0, 4, 4)]);
        var tracks = new Dictionary<ChartTrackKey, NoteTrack>
        {
            [new ChartTrackKey(Guitar, ChartDifficulty.Expert)] = new NoteTrack(
                [new ChartNote(0, 1, 0, NoteFlags.None), new ChartNote(480, 1, 1, NoteFlags.None)], []),
            [new ChartTrackKey(Guitar, ChartDifficulty.Hard)] = new NoteTrack(
                [new ChartNote(0, 1, 0, NoteFlags.None)], []),
        };
        var doc = new ChartDocument(tempo, tracks, null, false);
        var result = PatternPackTrainer.Train([(doc, "Harmonix", "song1")]);

        Assert.IsTrue(result.Parts >= 1);
        Assert.IsTrue(result.Beats >= 1);
        string json = PatternPackWriter.ToJson(result.Pack);
        var reloaded = PatternPackLoader.LoadJson(json, "roundtrip");
        Assert.AreEqual(result.Pack.Vocabulary.Count, reloaded.Vocabulary.Count);
        // Deterministic: same input, same counts.
        var again = PatternPackTrainer.Train([(doc, "Harmonix", "song1")]);
        Assert.AreEqual(
            result.Pack.Counts.Values.Sum(r => r.Values.Sum()),
            again.Pack.Counts.Values.Sum(r => r.Values.Sum()));
    }

    [TestMethod]
    public void TargetsDecodeDifferently()
    {
        // Same source, target-aware pack: Hard keeps the off-beat with the downbeat, Easy rests.
        var counts = new Dictionary<string, Dictionary<string, int>>
        {
            ["global"] = new() { ["1"] = 10, ["-"] = 10 },
            ["src=1 &|tgt=Hard"] = new() { ["1 &"] = 90, ["1"] = 10 },
            ["src=1 &|tgt=Easy"] = new() { ["-"] = 60, ["1"] = 30, ["1 &"] = 10 },
        };
        var pack = PatternPackLoader.LoadJson(TargetPackJson(counts), "targets");
        var gen = new PatternsGenerator(pack);
        var chart = ChartWithOffbeats(8);
        var hardRules = FretReductionRules.Default(ChartInstrumentKind.FiveFret, ChartDifficulty.Hard) with { ThinningDivision = 0 };
        var easyRules = FretReductionRules.Default(ChartInstrumentKind.FiveFret, ChartDifficulty.Easy) with { ThinningDivision = 0 };

        var hard = gen.Reduce(chart, Guitar, ChartDifficulty.Expert, ChartDifficulty.Hard, hardRules).Result;
        var easy = gen.Reduce(chart, Guitar, ChartDifficulty.Expert, ChartDifficulty.Easy, easyRules).Result;

        var hardTicks = hard.Notes.Select(static n => n.Tick).Distinct().Count();
        var easyTicks = easy.Notes.Select(static n => n.Tick).Distinct().Count();
        Assert.IsTrue(hardTicks > easyTicks, $"Hard keeps more than Easy on the same source: hard {hardTicks}, easy {easyTicks}.");
        Assert.IsNotEmpty(hard.Notes.Where(static n => n.Tick % 480 == 240).ToList(), "Hard keeps off-beats.");
    }

    [TestMethod]
    public void TrainingEmitsBackoffPrefixLevels()
    {
        var tempo = new TempoMap(480, [new TempoChange(0, 500_000)], [new TimeSignature(0, 4, 4)]);
        var tracks = new Dictionary<ChartTrackKey, NoteTrack>
        {
            [new ChartTrackKey(Guitar, ChartDifficulty.Expert)] = new NoteTrack(
                [new ChartNote(0, 1, 0, NoteFlags.None), new ChartNote(240, 1, 1, NoteFlags.None),
                 new ChartNote(480, 1, 0, NoteFlags.None), new ChartNote(720, 1, 1, NoteFlags.None)], []),
            [new ChartTrackKey(Guitar, ChartDifficulty.Hard)] = new NoteTrack(
                [new ChartNote(0, 1, 0, NoteFlags.None), new ChartNote(240, 1, 1, NoteFlags.None),
                 new ChartNote(480, 1, 0, NoteFlags.None), new ChartNote(720, 1, 1, NoteFlags.None)], []),
            [new ChartTrackKey(Guitar, ChartDifficulty.Easy)] = new NoteTrack(
                [new ChartNote(0, 1, 0, NoteFlags.None), new ChartNote(480, 1, 0, NoteFlags.None)], []),
        };
        var doc = new ChartDocument(tempo, tracks, null, false);
        var result = PatternPackTrainer.Train([(doc, "Harmonix", "song1")]);

        // Every backoff level is counted (spec 3.2), not just full contexts: the run-off,
        // the style-less prefixes Any queries back off to, and the bare source level.
        Assert.IsTrue(result.Pack.Counts.ContainsKey("src=1 &|tgt=Hard"), "Run level counted.");
        Assert.IsTrue(result.Pack.Counts.ContainsKey("src=1 &"), "Bare source level counted.");
        Assert.IsTrue(result.Pack.Counts.Keys.Any(static k => k.Contains("|tgt=Easy") && !k.Contains("style=")), "Style-less Easy prefix counted.");
    }

    [TestMethod]
    public void TrainedPackDifferentiatesAnyStyleQuery()
    {
        // The reported failure: style "Any" (unknown charter) fell through to the
        // target-blind source level because no style-less rows existed.
        var tempo = new TempoMap(480, [new TempoChange(0, 500_000)], [new TimeSignature(0, 4, 4)]);
        NoteTrack Expert() => new(
            [new ChartNote(0, 1, 0, NoteFlags.None), new ChartNote(240, 1, 1, NoteFlags.None),
             new ChartNote(480, 1, 0, NoteFlags.None), new ChartNote(720, 1, 1, NoteFlags.None)], []);
        var hardDoc = new ChartDocument(tempo, new Dictionary<ChartTrackKey, NoteTrack>
        {
            [new(Guitar, ChartDifficulty.Expert)] = Expert(),
            [new(Guitar, ChartDifficulty.Hard)] = Expert(),
        }, null, false);
        var easyDoc = new ChartDocument(tempo, new Dictionary<ChartTrackKey, NoteTrack>
        {
            [new(Guitar, ChartDifficulty.Expert)] = Expert(),
            [new(Guitar, ChartDifficulty.Easy)] = new NoteTrack(
                [new ChartNote(0, 1, 0, NoteFlags.None), new ChartNote(480, 1, 0, NoteFlags.None)], []),
        }, null, false);
        var pack = PatternPackTrainer.Train([(hardDoc, "Harmonix", "s1"), (easyDoc, "Harmonix", "s2")]).Pack;

        // Same "1 &" source beats as training, queried with an unstyled (Any) style.
        var chart = ChartWithOffbeats(8);
        var gen = new PatternsGenerator(pack, new PatternsOptions(Style: "Any", Density: 0));
        var kind = ChartInstrumentKind.FiveFret;
        var hard = gen.Reduce(chart, Guitar, ChartDifficulty.Expert, ChartDifficulty.Hard, FretReductionRules.Default(kind, ChartDifficulty.Hard) with { ThinningDivision = 0 }).Result;
        var easy = gen.Reduce(chart, Guitar, ChartDifficulty.Expert, ChartDifficulty.Easy, FretReductionRules.Default(kind, ChartDifficulty.Easy) with { ThinningDivision = 0 }).Result;
        var hardTicks = hard.Notes.Select(static n => n.Tick).Distinct().Order().ToList();
        var easyTicks = easy.Notes.Select(static n => n.Tick).Distinct().Order().ToList();
        Assert.IsTrue(hardTicks.Count > easyTicks.Count, $"Hard ({hardTicks.Count}) keeps more than Easy ({easyTicks.Count}) for an Any-style query.");
        Assert.IsFalse(hardTicks.SequenceEqual(easyTicks), "Hard and Easy drafts differ.");
    }

    [TestMethod]
    public void TrainingEmitsTargetSpecificKeys()
    {
        var tempo = new TempoMap(480, [new TempoChange(0, 500_000)], [new TimeSignature(0, 4, 4)]);
        // Same source beat, different official outcomes per target.
        var tracks = new Dictionary<ChartTrackKey, NoteTrack>
        {
            [new ChartTrackKey(Guitar, ChartDifficulty.Expert)] = new NoteTrack(
                [new ChartNote(0, 1, 0, NoteFlags.None), new ChartNote(240, 1, 1, NoteFlags.None),
                 new ChartNote(480, 1, 0, NoteFlags.None), new ChartNote(720, 1, 1, NoteFlags.None)], []),
            [new ChartTrackKey(Guitar, ChartDifficulty.Hard)] = new NoteTrack(
                [new ChartNote(0, 1, 0, NoteFlags.None), new ChartNote(240, 1, 1, NoteFlags.None),
                 new ChartNote(480, 1, 0, NoteFlags.None), new ChartNote(720, 1, 1, NoteFlags.None)], []),
            [new ChartTrackKey(Guitar, ChartDifficulty.Easy)] = new NoteTrack(
                [new ChartNote(0, 1, 0, NoteFlags.None), new ChartNote(480, 1, 0, NoteFlags.None)], []),
        };
        var doc = new ChartDocument(tempo, tracks, null, false);
        var result = PatternPackTrainer.Train([(doc, "Harmonix", "song1")]);

        Assert.IsTrue(result.Pack.Counts.Keys.Any(static k => k.Contains("|tgt=Hard")), "Hard contexts recorded.");
        Assert.IsTrue(result.Pack.Counts.Keys.Any(static k => k.Contains("|tgt=Easy")), "Easy contexts recorded.");
    }

    private static string TargetPackJson(Dictionary<string, Dictionary<string, int>> counts)
    {
        var vocab = """["-", "1", "&", "1 &"]""";
        var countsJson = "{" + string.Join(",", counts.Select(kv => $"\"{kv.Key}\":{{" + string.Join(",", kv.Value.Select(p => $"\"{p.Key}\":{p.Value}")) + "}")) + "}";
        string sum = PatternPackLoader.ChecksumOf(
            new List<string> { "-", "1", "&", "1 &" }.Select(BeatPattern.Parse).ToList(),
            counts.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<ulong, int>)kv.Value.ToDictionary(p => BeatPattern.Parse(p.Key).Mask, p => p.Value)));
        return $"{{\"FormatVersion\":1,\"BeatSchemaVersion\":1,\"Name\":\"targets\",\"Vocabulary\":{vocab},\"Counts\":{countsJson},\"Checksum\":\"{sum}\"}}";
    }

    private static EditableChart ChartWithOffbeats(int beats)
    {
        var chart = NewChart();
        var notes = new List<EditableNote>();
        for (int i = 0; i < beats; i++)
        {
            notes.Add(new EditableNote(i * 480L, 1, 0));
            notes.Add(new EditableNote(i * 480L + 240, 1, 1));
        }

        Add(chart, [.. notes]);
        return chart;
    }

    [TestMethod]
    public void RiffsConformOnlyNoteIdenticalMeasures()
    {
        var chart = NewChart();
        // Two measures, same rhythm, different lanes.
        Add(chart, new EditableNote(0, 1, 0), new EditableNote(240, 1, 1),
            new EditableNote(1920, 1, 2), new EditableNote(2160, 1, 3));
        var source = chart.PartOf(Guitar)![ChartDifficulty.Expert];
        var map = chart.TempoMap.ToTempoMap();
        var beats = BeatTable.Build([0, 240, 1920, 2160], map).ToList();
        var keptTicksPerCandidate = beats.Select(b => BeatTicks(b)).ToList();

        // Decoded differently per measure: downbeat only vs downbeat and off-beat.
        var decoded = beats.Select((b, i) => b.Source.IsEmpty ? BeatPattern.Empty
            : i == 0 ? BeatPattern.Parse("1") : BeatPattern.Parse("1 &")).ToList();
        var kept = new SortedSet<long> { 0, 1920, 2160 };
        PatternsGenerator.ConformRiffs(chart, source, beats, beats, decoded, kept, keptTicksPerCandidate, 1);

        Assert.IsFalse(kept.Contains(240), "Same rhythm but different notes is no riff: nothing added.");
        Assert.IsTrue(kept.SetEquals(new SortedSet<long> { 0, 1920, 2160 }), "Nothing removed either.");

        static Dictionary<ulong, List<long>> BeatTicks(SourceBeat beat)
        {
            var byOffset = new Dictionary<int, long>();
            for (int i = 0; i < beat.Ticks.Count; i++)
                byOffset.TryAdd(beat.Offsets48[i], beat.Ticks[i]);
            return new Dictionary<ulong, List<long>>
            {
                [BeatPattern.Empty.Mask] = [],
                [BeatPattern.Parse("1").Mask] = byOffset.TryGetValue(0, out long down) ? [down] : [],
                [BeatPattern.Parse("1 &").Mask] = [.. byOffset.OrderBy(kv => kv.Key).Select(kv => kv.Value)],
            };
        }
    }

    [TestMethod]
    public void RiffsConformNoteIdenticalMeasures()
    {
        static Dictionary<ulong, List<long>> BeatTicks(SourceBeat beat)
        {
            var byOffset = new Dictionary<int, long>();
            for (int i = 0; i < beat.Ticks.Count; i++)
                byOffset.TryAdd(beat.Offsets48[i], beat.Ticks[i]);
            return new Dictionary<ulong, List<long>>
            {
                [BeatPattern.Empty.Mask] = [],
                [BeatPattern.Parse("1").Mask] = byOffset.TryGetValue(0, out long down) ? [down] : [],
                [BeatPattern.Parse("1 &").Mask] = [.. byOffset.OrderBy(kv => kv.Key).Select(kv => kv.Value)],
            };
        }

        static (EditableChart Chart, EditableTrack Source, List<SourceBeat> Beats) Setup(params EditableNote[] notes)
        {
            var chart = NewChart();
            Add(chart, notes);
            var source = chart.PartOf(Guitar)![ChartDifficulty.Expert];
            var map = chart.TempoMap.ToTempoMap();
            var beats = BeatTable.Build([.. notes.Select(static n => n.Tick).Order()], map).ToList();
            return (chart, source, beats);
        }

        // The same two chords three times: a real riff.
        var riff = new EditableNote[]
        {
            new(0, 1, 0), new(240, 1, 1),
            new(1920, 1, 0), new(2160, 1, 1),
            new(3840, 1, 0), new(4080, 1, 1),
        };
        {
            var (chart, source, beats) = Setup(riff);
            var perBeat = beats.Select(BeatTicks).ToList();

            // Agreeing decodes stay put.
            var agreed = beats.Select(b => b.Source.IsEmpty ? BeatPattern.Empty : BeatPattern.Parse("1")).ToList();
            var keptAgreed = new SortedSet<long>(agreed.SelectMany((p, i) => perBeat[i].TryGetValue(p.Mask, out var t) ? t : []));
            PatternsGenerator.ConformRiffs(chart, source, beats, beats, agreed, keptAgreed, perBeat, 1);
            Assert.IsTrue(keptAgreed.SetEquals(new SortedSet<long> { 0, 1920, 3840 }), "Agreeing riff untouched.");
        }

        {
            var (chart, source, beats) = Setup(riff);
            var perBeat = beats.Select(BeatTicks).ToList();

            // Two of three keep the off-beat, one drops it: strict majority restores it.
            var split = beats.Select((b, i) =>
            {
                if (b.Source.IsEmpty)
                    return BeatPattern.Empty;
                return b.StartTick == 3840 ? BeatPattern.Parse("1") : BeatPattern.Parse("1 &");
            }).ToList();
            var keptSplit = new SortedSet<long> { 0, 240, 1920, 2160, 3840 };
            PatternsGenerator.ConformRiffs(chart, source, beats, beats, split, keptSplit, perBeat, 1);
            Assert.IsTrue(keptSplit.SetEquals(new SortedSet<long> { 0, 240, 1920, 2160, 3840, 4080 }), "Repeated identical measures conform to the majority.");
        }
    }

    private static IEnumerable<BeatPattern[]> Combos(int n, List<IReadOnlyList<BeatPattern>> cands)
    {
        int[] idx = new int[n];
        while (true)
        {
            yield return idx.Select((v, i) => cands[i][v]).ToArray();
            int k = n - 1;
            while (k >= 0 && ++idx[k] >= cands[k].Count)
            {
                idx[k] = 0;
                k--;
            }

            if (k < 0)
                yield break;
        }
    }

    private static EditableChart NewChart() =>
        EditableChart.CreateNew(new NewChartSettings("Song", 480, 120, 4, 4, new HashSet<ChartInstrument> { Guitar }, DrumsVariant.FourLane));

    private static void Add(EditableChart chart, params EditableNote[] notes) =>
        new AddNotes(Guitar, ChartDifficulty.Expert, notes).Apply(chart);

    private static EditableChart ChartWithEighths(int beats)
    {
        var chart = NewChart();
        var notes = new List<EditableNote>();
        for (int i = 0; i < beats * 2; i++)
            notes.Add(new EditableNote(i * 240L, 1, i % 3));
        Add(chart, [.. notes]);
        return chart;
    }

    private static PatternPack FakePack()
    {
        // Vocabulary covers downbeats, off-beats and rests; counts prefer keeping downbeats,
        // resting every other beat, and the Any style.
        var counts = new Dictionary<string, Dictionary<string, int>>
        {
            ["global"] = new() { ["1"] = 50, ["-"] = 30, ["&"] = 10, ["1 &"] = 10 },
            ["src=1"] = new() { ["1"] = 60, ["-"] = 20, ["&"] = 10, ["1 &"] = 10 },
            ["src=1 &"] = new() { ["1"] = 57, ["-"] = 28, ["&"] = 11 },
            ["src=&"] = new() { ["&"] = 50, ["-"] = 30, ["1"] = 20 },
            ["src=-"] = new() { ["-"] = 80, ["1"] = 20 },
        };
        return PatternPackLoader.LoadJson(FakePackJson(counts: counts), "fake");
    }

    private static string FakePackJson(Dictionary<string, Dictionary<string, int>>? counts = null, string? checksum = null)
    {
        counts ??= new Dictionary<string, Dictionary<string, int>> { ["global"] = new() { ["1"] = 1 } };
        var vocab = """["-", "1", "&", "1 &", "e"]""";
        var countsJson = "{" + string.Join(",", counts.Select(kv => $"\"{kv.Key}\":{{" + string.Join(",", kv.Value.Select(p => $"\"{p.Key}\":{p.Value}")) + "}")) + "}";
        string sum = checksum ?? PatternPackLoader.ChecksumOf(
            new List<string> { "-", "1", "&", "1 &", "e" }.Select(BeatPattern.Parse).ToList(),
            counts.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<ulong, int>)kv.Value.ToDictionary(p => BeatPattern.Parse(p.Key).Mask, p => p.Value)));
        return $"{{\"FormatVersion\":1,\"BeatSchemaVersion\":1,\"Name\":\"fake\",\"Vocabulary\":{vocab},\"Counts\":{countsJson},\"Checksum\":\"{sum}\"}}";
    }
}
