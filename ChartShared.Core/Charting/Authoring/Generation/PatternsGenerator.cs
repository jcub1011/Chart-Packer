using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Authoring.Generation;

/// <summary>Options for one <see cref="PatternsGenerator"/> run (spec 3.4 density, 3.5 limits).</summary>
public sealed record PatternsOptions(
    string Style = "Any",
    double Density = 0,
    long? MinGapTicks = null);

/// <summary>Count-based generator (spec 3.2): table probabilities + Viterbi decoding,
/// voiced by Rules' logic (spec 3.4.5-6). Swappable with <see cref="RulesGenerator"/> and,
/// later, the neural generator: same input, same <see cref="ReductionResult"/> out.</summary>
public sealed class PatternsGenerator : IDifficultyGenerator
{
    private readonly PatternPack? _pack;
    private readonly PatternsOptions _options;
    private readonly PatternScorer? _scorer;

    public GeneratorKind Kind => GeneratorKind.Patterns;

    public string DisplayName => "Patterns";

    public PatternPack? Pack => _pack;

    public const int MaxCandidatesPerBeat = 14;

    public PatternsGenerator(PatternPack? pack, PatternsOptions? options = null)
    {
        _pack = pack;
        _options = options ?? new PatternsOptions();
        _scorer = pack?.Scorer;
    }

    public bool Supports(ChartInstrumentKind kind) => kind == ChartInstrumentKind.FiveFret;

    public (ReductionResult Result, GenerationFallback? Fallback) Reduce(
        EditableChart chart,
        ChartInstrument part,
        ChartDifficulty from,
        ChartDifficulty to,
        FretReductionRules voiceRules,
        (long From, long To)? range = null,
        ChartReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(voiceRules);
        var kind = ChartInstruments.KindOf(part);
        if (kind != ChartInstrumentKind.FiveFret)
            return FallbackToRules(chart, part, from, to, voiceRules, range, options, $"{DisplayName} supports 5-fret parts only.");
        if (_pack is null || _scorer is null)
            return FallbackToRules(chart, part, from, to, voiceRules, range, options, "No pattern pack is installed.");
        if (!PairSupported(_pack, from, to, out string? pairNote))
            return FallbackToRules(chart, part, from, to, voiceRules, range, options, pairNote!);

        var whole = chart.PartOf(part) ?? throw new InvalidOperationException($"The chart has no {part} part.");
        var sourceTrack = whole[from];
        var targetTrack = whole[to];
        if (to >= from)
            throw new ArgumentException("A difficulty is reduced to a lower one.", nameof(to));
        if (sourceTrack.Notes.Count == 0)
            return FallbackToRules(chart, part, from, to, voiceRules, range, options, "There are no notes to reduce.");

        var map = chart.TempoMap.ToTempoMap();
        var beats = BeatTable.Build([.. sourceTrack.Notes], map).ToList();
        var inRange = range is { } r
            ? beats.Where(b => b.StartTick + b.LengthTicks > r.From && b.StartTick <= r.To).ToList()
            : beats;
        if (inRange.Count == 0)
        {
            var empty = Reducer.Reduce(chart, part, from, to, voiceRules, range, options);
            return (empty, null);
        }

        int startIndex = beats.IndexOf(inRange[0]);
        var seed = SeedHistory(targetTrack, range, map);
        long minGap = _options.MinGapTicks ?? PackGap(_pack, to, chart.Resolution) ?? BurstFloor(voiceRules, chart.Resolution);
        double density = Math.Clamp(_options.Density, -1.5, 1.5);
        string style = _options.Style;
        string toStr = to.ToString();
        string fromStr = from.ToString();

        var candidatesPerBeat = new List<IReadOnlyList<BeatPattern>>(inRange.Count);
        var keptTicksPerCandidate = new List<Dictionary<ulong, List<long>>>(inRange.Count);
        var candIndexByMask = new List<Dictionary<ulong, int>>(inRange.Count);
        var salienceByBeat = new List<double[]>(inRange.Count);
        var beatScorers = new PatternScorer.BeatScorer?[inRange.Count];

        for (int k = 0; k < inRange.Count; k++)
        {
            var beat = inRange[k];
            if (beat.Source.IsEmpty)
            {
                IReadOnlyList<BeatPattern> emptyOnly = [BeatPattern.Empty];
                candidatesPerBeat.Add(emptyOnly);
                keptTicksPerCandidate.Add(new Dictionary<ulong, List<long>> { [0UL] = [] });
                candIndexByMask.Add(new Dictionary<ulong, int> { [0UL] = 0 });
                salienceByBeat.Add([0.0]);
                continue;
            }

            var nextSource = k + 1 < inRange.Count ? inRange[k + 1].Source : BeatPattern.Empty;
            var rawCandidates = FilterByGap(_pack.CandidatesFor(beat.Source), beat, minGap);
            var rawSalience = ComputeCandidateSalience(beat, rawCandidates, to);
            var initialScorer = _scorer.PrepareBeat(beat.Source, beat.Shape, nextSource, beat.MeasurePlace, style, toStr, fromStr, rawCandidates);

            IReadOnlyList<BeatPattern> candidates = rawCandidates;
            double[] salScores = rawSalience;
            PatternScorer.BeatScorer beatScorer = initialScorer;

            if (rawCandidates.Count > MaxCandidatesPerBeat)
            {
                var baseLog = initialScorer.LogScoresForState(BeatPattern.Empty, BeatPattern.Empty, BeatPattern.Empty);
                var keepIndices = Enumerable.Range(0, rawCandidates.Count)
                    .OrderByDescending(idx => rawCandidates[idx].IsEmpty || rawCandidates[idx].Mask == beat.Source.Mask ? double.PositiveInfinity : baseLog[idx] + rawSalience[idx])
                    .Take(MaxCandidatesPerBeat)
                    .Order()
                    .ToArray();
                var pruned = new List<BeatPattern>(keepIndices.Length);
                var prunedSal = new double[keepIndices.Length];
                for (int idx = 0; idx < keepIndices.Length; idx++)
                {
                    pruned.Add(rawCandidates[keepIndices[idx]]);
                    prunedSal[idx] = rawSalience[keepIndices[idx]];
                }

                candidates = pruned;
                salScores = prunedSal;
                beatScorer = _scorer.PrepareBeat(beat.Source, beat.Shape, nextSource, beat.MeasurePlace, style, toStr, fromStr, candidates);
            }

            var idxMap = new Dictionary<ulong, int>(candidates.Count);
            for (int c = 0; c < candidates.Count; c++)
                idxMap[candidates[c].Mask] = c;

            candidatesPerBeat.Add(candidates);
            keptTicksPerCandidate.Add(TicksByCandidate(beat, candidates));
            candIndexByMask.Add(idxMap);
            salienceByBeat.Add(salScores);
            beatScorers[k] = beatScorer;
        }

        var request = new SequenceDecoder.DecodeRequest(
            candidatesPerBeat,
            (i, cand, p1, p2, p3) =>
            {
                var scorerForBeat = beatScorers[i];
                if (scorerForBeat is null)
                    return 0;
                if (i == 0 && startIndex > 0 && p1.IsEmpty && p2.IsEmpty && p3.IsEmpty)
                {
                    p1 = seed.Prev1;
                    p2 = seed.Prev2;
                    p3 = seed.Prev3;
                }

                int cIdx = candIndexByMask[i][cand.Mask];
                double logProb = scorerForBeat.LogScoresForState(p1, p2, p3)[cIdx];
                double salBonus = salienceByBeat[i][cIdx];
                double tiePenalty = 0;

                if (i > 0 && (cand.Mask & 1UL) != 0)
                {
                    var prevBeat = inRange[i - 1];
                    var curBeat = inRange[i];
                    if (prevBeat.LastOffbeatOffset48 >= 12
                        && prevBeat.LastOffbeatFretMask != 0
                        && curBeat.DownbeatFretMask == prevBeat.LastOffbeatFretMask
                        && (prevBeat.Shape & BeatShape.OffbeatChangesFret) != 0
                        && (p1.Mask & (1UL << prevBeat.LastOffbeatOffset48)) != 0)
                    {
                        // The previous beat already struck this exact chord/fret as a syncopated anticipation.
                        tiePenalty = to == ChartDifficulty.Hard
                            ? (cand.OnsetCount == 1 ? -0.45 : 0.0)
                            : -1.40;
                    }
                }

                return logProb + salBonus + tiePenalty + density * cand.OnsetCount;
            },
            (i, cand) => keptTicksPerCandidate[i].TryGetValue(cand.Mask, out var ticks) ? ticks : [],
            minGap);

        var decoded = SequenceDecoder.Decode(request);
        var keptTicks = new SortedSet<long>();
        for (int k = 0; k < inRange.Count; k++)
        {
            foreach (long tick in keptTicksPerCandidate[k].TryGetValue(decoded[k].Mask, out var ticks) ? ticks : [])
                keptTicks.Add(tick);
        }

        EnforceSpSolo(whole, sourceTrack, keptTicks, minGap);
        ConformRiffs(chart, sourceTrack, beats, inRange, decoded, keptTicks, keptTicksPerCandidate, minGap);

        // Map kept ticks back to chord indexes for voicing.
        var allChords = Reducer.ChordsOf([.. whole[from].Notes]);
        var chordIndexByTick = new Dictionary<long, int>();
        for (int i = 0; i < allChords.Count; i++)
            chordIndexByTick.TryAdd(allChords[i].Tick, i);
        List<int> kept;
        if (range is null)
        {
            kept = [.. keptTicks.Where(chordIndexByTick.ContainsKey).Select(t => chordIndexByTick[t]).Order()];
        }
        else
        {
            // Voice only the range's chords; VoiceKeptFrets splices the target outside back in.
            var rangeChords = Reducer.ChordsOf([.. whole[from].Notes.Between(range.Value.From, range.Value.To)]);
            var rangeIndex = rangeChords.Select(c => c.Tick).ToHashSet();
            var keptInRange = keptTicks.Where(rangeIndex.Contains).ToList();
            var indexByTick = new Dictionary<long, int>();
            for (int i = 0; i < rangeChords.Count; i++)
                indexByTick.TryAdd(rangeChords[i].Tick, i);
            var phrases = Reducer.Phrases(rangeChords, Reducer.SectionTicks(chart), chart.Resolution);
            var sliceKept = keptInRange.Where(indexByTick.ContainsKey).Select(t => indexByTick[t]).Order().ToList();
            int Column(EditableNote n) => n.Lane;
            return (Reducer.VoiceKeptFrets(chart, sourceTrack, targetTrack, rangeChords, phrases, sliceKept, voiceRules, false, Column, range, options, rangeChords.Count), null);
        }

        var phrasesAll = Reducer.Phrases(allChords, Reducer.SectionTicks(chart), chart.Resolution);
        bool ghl = false;
        int Col(EditableNote n) => n.Lane;
        var result = Reducer.VoiceKeptFrets(chart, sourceTrack, targetTrack, allChords, phrasesAll, kept, voiceRules, ghl, Col, range, options, allChords.Count);
        return (result, null);
    }

    private static double[] ComputeCandidateSalience(SourceBeat beat, IReadOnlyList<BeatPattern> candidates, ChartDifficulty target)
    {
        var bonuses = new double[candidates.Count];
        if (beat.OnsetSalience is not { Count: > 0 } sal || beat.Offsets48.Count != sal.Count)
            return bonuses;

        int m = sal.Count;
        if (m == 1)
        {
            double adj = Math.Clamp((sal[0] - 0.5) * 0.25, -0.35, 0.45);
            for (int c = 0; c < candidates.Count; c++)
            {
                if (!candidates[c].IsEmpty)
                    bonuses[c] = adj;
            }

            return bonuses;
        }

        double mean = 0;
        double minSal = double.PositiveInfinity;
        for (int k = 0; k < m; k++)
        {
            mean += sal[k];
            if (sal[k] < minSal)
                minSal = sal[k];
        }

        mean /= m;
        Span<double> deltaByOffset = stackalloc double[BeatPattern.Divisions];
        for (int k = 0; k < m; k++)
            deltaByOffset[beat.Offsets48[k]] = sal[k] - mean;

        double multiBonus = minSal >= 0.45 && (beat.Shape & BeatShape.OffbeatChangesFret) != 0
            ? (target == ChartDifficulty.Hard ? 0.35 : target == ChartDifficulty.Medium ? 0.25 : 0.05)
            : 0.0;

        for (int c = 0; c < candidates.Count; c++)
        {
            var cand = candidates[c];
            if (cand.IsEmpty)
                continue;
            if (cand.Mask == beat.Source.Mask)
            {
                bonuses[c] = multiBonus;
                continue;
            }

            double sum = 0;
            ulong mask = cand.Mask;
            while (mask != 0)
            {
                int p = System.Numerics.BitOperations.TrailingZeroCount(mask);
                mask &= mask - 1;
                if (p < BeatPattern.Divisions)
                    sum += deltaByOffset[p];
            }

            bonuses[c] = sum;
        }

        return bonuses;
    }

    private static (BeatPattern Prev1, BeatPattern Prev2, BeatPattern Prev3) SeedHistory(EditableTrack target, (long From, long To)? range, TempoMap map)
    {
        if (range is not { } r)
            return (BeatPattern.Empty, BeatPattern.Empty, BeatPattern.Empty);
        var before = target.Notes.Where(n => n.Tick < r.From).Select(n => n.Tick).Order().ToList();
        if (before.Count == 0)
            return (BeatPattern.Empty, BeatPattern.Empty, BeatPattern.Empty);
        var beats = BeatTable.Build(before, map);
        var last = beats.TakeLast(3).Select(b => b.Source).ToList();
        while (last.Count < 3)
            last.Insert(0, BeatPattern.Empty);
        return (last[2], last[1], last[0]);
    }

    private static long BurstFloor(FretReductionRules voiceRules, int resolution)
    {
        long pace = Reducer.MinGap(voiceRules.ThinningDivision, resolution);
        if (voiceRules.BurstDivision is { } burst)
            return Math.Min(pace, Reducer.MinGap(burst, resolution));
        return pace;
    }

    private static long? PackGap(PatternPack pack, ChartDifficulty target, int resolution)
    {
        if (pack.Manifest.MinGaps.TryGetValue(target.ToString(), out long gap) && gap > 0)
            return Math.Max(1L, gap * resolution / 480L);
        return null;
    }

    private static bool PairSupported(PatternPack pack, ChartDifficulty from, ChartDifficulty to, out string? note)
    {
        note = null;
        if (pack.Manifest.Pairs.Count == 0)
            return true;
        string want = $"{from}->{to}";
        if (pack.Manifest.Pairs.Contains(want))
            return true;
        // Nearest fallback: any pair for the same target (spec 3.6).
        if (pack.Manifest.Pairs.Any(p => p.EndsWith($"->{to}", StringComparison.Ordinal)))
            return true;
        note = $"Pack has no {want} pair.";
        return false;
    }

    private (ReductionResult Result, GenerationFallback? Fallback) FallbackToRules(
        EditableChart chart, ChartInstrument part, ChartDifficulty from, ChartDifficulty to,
        FretReductionRules voiceRules, (long From, long To)? range, ChartReadOptions? options, string reason)
    {
        var result = Reducer.Reduce(chart, part, from, to, voiceRules, range, options);
        return (result, new GenerationFallback(reason));
    }

    private static IReadOnlyList<BeatPattern> FilterByGap(IReadOnlyList<BeatPattern> candidates, SourceBeat beat, long minGap)
    {
        if (minGap <= 1 || beat.Ticks.Count == 0)
            return candidates;
        var tickByOffset = new Dictionary<int, long>();
        for (int i = 0; i < beat.Ticks.Count; i++)
            tickByOffset.TryAdd(beat.Offsets48[i], beat.Ticks[i]);
        var kept = new List<BeatPattern>();
        foreach (var candidate in candidates)
        {
            var ticks = candidate.Offsets().Where(tickByOffset.ContainsKey).Select(o => tickByOffset[o]).Order().ToList();
            bool ok = true;
            for (int k = 1; k < ticks.Count; k++)
            {
                if (ticks[k] - ticks[k - 1] < minGap)
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
                kept.Add(candidate);
        }

        if (!kept.Contains(BeatPattern.Empty))
            kept.Add(BeatPattern.Empty);
        return kept;
    }

    private static Dictionary<ulong, List<long>> TicksByCandidate(SourceBeat beat, IReadOnlyList<BeatPattern> candidates)
    {
        var tickByOffset = new Dictionary<int, long>();
        for (int i = 0; i < beat.Ticks.Count; i++)
            tickByOffset.TryAdd(beat.Offsets48[i], beat.Ticks[i]);
        var map = new Dictionary<ulong, List<long>>();
        foreach (var candidate in candidates)
            map[candidate.Mask] = [.. candidate.Offsets().Where(tickByOffset.ContainsKey).Select(o => tickByOffset[o]).Order()];
        return map;
    }

    private static void EnforceSpSolo(EditablePart whole, EditableTrack source, SortedSet<long> keptTicks, long minGap)
    {
        foreach (var phrase in whole.Phrases.AsSpan().ToArray())
        {
            if (phrase.Kind is not (PhraseKind.StarPower or PhraseKind.Solo))
                continue;
            var inPhrase = source.Notes.Where(n => phrase.Covers(n.Tick)).Select(n => n.Tick).Distinct().Order().ToList();
            if (inPhrase.Count == 0 || inPhrase.Any(keptTicks.Contains))
                continue;
            // Keep the phrase's first onset that holds the gap, else its first onset.
            long pick = inPhrase.FirstOrDefault(t => keptTicks.All(k => Math.Abs(k - t) >= minGap), inPhrase[0]);
            keptTicks.Add(pick);
        }
    }

    internal static void ConformRiffs(
        EditableChart chart,
        EditableTrack source,
        IReadOnlyList<SourceBeat> beats,
        IReadOnlyList<SourceBeat> inRange,
        IReadOnlyList<BeatPattern> decoded,
        SortedSet<long> keptTicks,
        IReadOnlyList<Dictionary<ulong, List<long>>> keptTicksPerCandidate,
        long minGap)
    {
        // Measures charted the same get the same patterns (spec 3.5): a riff repeated note-for-note
        // is reduced the same each time, as Rules does. Rhythm alone is NOT enough: a chorus sharing
        // a verse's rhythm but not its notes must keep its own reduction.
        var map = chart.TempoMap.ToTempoMap();
        var chords = Reducer.ChordsOf([.. source.Notes]);
        var measureStarts = new List<long>();
        var measureKeys = new List<string>();
        var key = new System.Text.StringBuilder();
        int ci = 0;
        while (ci < chords.Count)
        {
            var (signature, next) = SignatureAt(chords[ci].Tick, map);
            long beat = Math.Max(1, map.Resolution * 4L / signature.Denominator);
            long measure = beat * Math.Max(1, signature.Numerator);
            long from = signature.Tick + (Math.Max(0, chords[ci].Tick - signature.Tick) / measure * measure);
            long to = Math.Min(from + measure, next ?? long.MaxValue);
            int start = ci;
            while (ci < chords.Count && chords[ci].Tick < to)
                ci++;
            measureStarts.Add(from);
            if (ci - start > 1)
            {
                key.Clear().Append(to - from);
                for (int i = start; i < ci; i++)
                {
                    key.Append(';').Append(chords[i].Tick - from).Append(chords[i].Notes.Any(n => n.Length >= chart.Resolution) ? 'S' : ':');
                    foreach (var note in chords[i].Notes.OrderBy(static n => n.Lane))
                        key.Append(note.Lane).Append(',').Append((int)note.Dynamics).Append(note.DoubleKick ? '+' : ',');
                }

                measureKeys.Add(key.ToString());
            }
            else
            {
                measureKeys.Add("\0" + measureStarts.Count); // Unique: never a riff.
            }
        }

        int MeasureIndexOf(long tick)
        {
            var (signature, _) = SignatureAt(tick, map);
            long beat = Math.Max(1, map.Resolution * 4L / signature.Denominator);
            long measure = beat * Math.Max(1, signature.Numerator);
            long from = signature.Tick + (Math.Max(0, tick - signature.Tick) / measure * measure);
            int index = measureStarts.IndexOf(from);
            return index;
        }

        // Group each measure's beats by measure key; only a key in more than one measure is a
        // riff. Beats of a single measure never conform each other.
        var measuresByKey = new Dictionary<string, List<List<int>>>();
        var currentKey = "";
        var currentBeats = new List<int>();
        int currentMeasure = -2;
        for (int k = 0; k < inRange.Count; k++)
        {
            int m = MeasureIndexOf(inRange[k].StartTick);
            if (m < 0 || m >= measureKeys.Count)
                continue;
            string mk = measureKeys[m];
            if (m != currentMeasure || mk != currentKey)
            {
                if (currentBeats.Count > 0)
                {
                    if (!measuresByKey.TryGetValue(currentKey, out var prior))
                        measuresByKey[currentKey] = prior = [];
                    prior.Add(currentBeats);
                }

                currentKey = mk;
                currentMeasure = m;
                currentBeats = [];
            }

            currentBeats.Add(k);
        }

        if (currentBeats.Count > 0)
        {
            if (!measuresByKey.TryGetValue(currentKey, out var prior))
                measuresByKey[currentKey] = prior = [];
            prior.Add(currentBeats);
        }

        foreach (var measures in measuresByKey.Values.Where(l => l.Count > 1))
        {
            // Every repeat of a note-identical measure gets the same pattern (spec 3.5).
            // Plurality wins; on a 50/50 tie (e.g. a 2-repeat riff), prefer the interior repeat
            // which had full in-riff history (p1..p3) rather than the entry boundary.
            int width = measures.Min(static mm => mm.Count);
            for (int j = 0; j < width; j++)
            {
                ulong interiorMask = decoded[measures[^1][j]].Mask;
                var masks = measures.Select(mm => decoded[mm[j]].Mask).ToList();
                ulong top = masks.GroupBy(static x => x)
                    .OrderByDescending(static g => g.Count())
                    .ThenByDescending(g => g.Key == interiorMask ? 1 : 0)
                    .ThenBy(static g => g.Key)
                    .First().Key;
                foreach (var mm in measures)
                {
                    int k = mm[j];
                    if (decoded[k].Mask == top)
                        continue;
                    foreach (long t in keptTicksPerCandidate[k].TryGetValue(decoded[k].Mask, out var old) ? old : [])
                        keptTicks.Remove(t);
                    if (keptTicksPerCandidate[k].TryGetValue(top, out var add))
                    {
                        foreach (long t in add)
                        {
                            if (keptTicks.All(x => Math.Abs(x - t) >= minGap || x == t))
                                keptTicks.Add(t);
                        }
                    }
                }
            }
        }

        static (TimeSignature Signature, long? Next) SignatureAt(long tick, TempoMap map)
        {
            var signatures = map.TimeSignatures;
            var signature = signatures[0];
            foreach (var s in signatures)
            {
                if (s.Tick > tick)
                    return (signature, s.Tick);
                signature = s;
            }

            return (signature, null);
        }
    }

}
