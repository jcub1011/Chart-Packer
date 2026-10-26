namespace ChartShared.Core.Generation.Neural;

/// <summary>
/// Constrained Viterbi decoding (§3.4 step 4, §3.5): the highest-scoring pattern sequence inside
/// the hard limits. Playability is never learned — the decoder enforces it.
/// </summary>
public static class ViterbiDecoder
{
    private const float NegInf = -1e9f;

    /// <summary>Decoding constraints for one run.</summary>
    /// <param name="MinGapTicks">The fewest ticks between two kept chords; 0 for none.</param>
    /// <param name="RequireStarPowerSolo">Whether every star power phrase and solo with source onsets keeps one.</param>
    /// <param name="DensityBias">Added to each candidate's score per chord it keeps (§3.4 density control).</param>
    /// <param name="CandidateSets">Each beat's candidates.</param>
    /// <param name="Beats">The part's beats; the emissions have a row for each.</param>
    /// <param name="Range">The ticks decoded, both ends in; null for the whole part. Beats outside it are left empty and
    /// onsets outside it are never kept (§3.6).</param>
    /// <param name="FixedTicks">Chords kept already outside <paramref name="Range"/>: the target's, which the gap holds
    /// against at both ends (§3.6).</param>
    public sealed record DecodeConstraints(
        long MinGapTicks,
        bool RequireStarPowerSolo,
        double DensityBias,
        IReadOnlyList<HashSet<int>> CandidateSets,
        IReadOnlyList<BeatFrame> Beats,
        (long From, long To)? Range = null,
        IReadOnlyCollection<long>? FixedTicks = null);

    /// <summary>
    /// Finds the best pattern id per beat. Emissions are beats × vocab (candidates outside the beat's set must already be
    /// −∞). Riff repeats are one decision (§3.5): the beats sharing a <see cref="BeatFrame.RiffKey"/> and candidates
    /// score as their mean, the chain is decoded beat by beat, and then each such group takes the choice most of it has.
    /// The min gap and star power floors are then met by <see cref="Repair"/>.
    /// </summary>
    public static int[] Decode(
        float[,] emissions,
        float[] transition,
        float[] startTransition,
        float[] endTransition,
        DecodeConstraints constraints)
    {
        ArgumentNullException.ThrowIfNull(emissions);
        ArgumentNullException.ThrowIfNull(transition);
        ArgumentNullException.ThrowIfNull(startTransition);
        ArgumentNullException.ThrowIfNull(endTransition);
        ArgumentNullException.ThrowIfNull(constraints);

        int n = constraints.Beats.Count;
        int vocab = startTransition.Length;
        var choice = new int[n];
        if (n == 0)
            return choice;
        var (lo, hi) = WindowOf(constraints.Beats, constraints.Range);
        if (lo > hi)
            return choice;

        var groups = RiffGroups(constraints.Beats, constraints.CandidateSets);
        var scores = Scores(emissions, groups, vocab, constraints.DensityBias);

        // Viterbi over the adjacent beats of the window.
        int m = hi - lo + 1;
        var dp = new float[m, vocab];
        var back = new int[m, vocab];
        for (int v = 0; v < vocab; v++)
        {
            float emit = scores[lo, v];
            dp[0, v] = float.IsNegativeInfinity(emit) ? NegInf : startTransition[v] + emit;
        }

        for (int k = 1; k < m; k++)
        {
            for (int v = 0; v < vocab; v++)
            {
                float emit = scores[lo + k, v];
                if (float.IsNegativeInfinity(emit))
                {
                    dp[k, v] = NegInf;
                    continue;
                }

                float best = NegInf;
                int bestPrev = BeatPattern.EmptyId;
                for (int u = 0; u < vocab; u++)
                {
                    float prev = dp[k - 1, u];
                    if (prev <= NegInf / 2)
                        continue;
                    float total = prev + transition[(u * vocab) + v];
                    if (total > best)
                    {
                        best = total;
                        bestPrev = u;
                    }
                }

                dp[k, v] = best <= NegInf / 2 ? NegInf : best + emit;
                back[k, v] = bestPrev;
            }
        }

        int last = BeatPattern.EmptyId;
        float lastBest = float.NegativeInfinity;
        for (int v = 0; v < vocab; v++)
        {
            float total = dp[m - 1, v] + endTransition[v];
            if (total > lastBest)
            {
                lastBest = total;
                last = v;
            }
        }

        for (int k = m - 1; k >= 0; k--)
        {
            choice[lo + k] = last;
            last = back[k, last];
        }

        Conform(choice, groups, lo, hi);
        Repair(choice, constraints, scores);
        return choice;
    }

    /// <summary>
    /// Enforces the hard floors (§3.5): the min gap, earlier chords first, against each other and the fixed ticks; then
    /// at least one kept onset in every star power phrase and solo that has source onsets, the best-scoring single onset
    /// that keeps the gap, or the best one when none does (met as nearly as it can be). Onsets outside the range are never
    /// kept.
    /// </summary>
    /// <param name="scores">The decoder's per-beat scores (beats × vocab), for picking the onset a phrase keeps; null to
    /// prefer each phrase's earliest onset.</param>
    public static void Repair(int[] choice, DecodeConstraints constraints, float[,]? scores = null)
    {
        ArgumentNullException.ThrowIfNull(choice);
        ArgumentNullException.ThrowIfNull(constraints);
        var beats = constraints.Beats;
        int n = beats.Count;
        if (n == 0)
            return;
        var range = constraints.Range;
        bool InRange(long tick) => range is not { } r || (tick >= r.From && tick <= r.To);
        long floor = constraints.MinGapTicks;
        var fixedTicks = new SortedSet<long>(constraints.FixedTicks ?? []);

        // Outside the range nothing is kept: such beats are empty, and a beat across its edge keeps only what's inside.
        for (int i = 0; i < n; i++)
        {
            var kept = KeptTicks(beats[i], choice[i]);
            if (kept.Count > 0 && !kept.All(InRange))
                choice[i] = NearestPatternWithSlots(beats[i], SlotsOf(beats[i], kept.Where(InRange)));
        }

        var taken = new SortedSet<long>(fixedTicks);
        bool Clear(long tick) => floor <= 0 || taken.GetViewBetween(tick - floor + 1, tick + floor - 1).Count == 0;
        for (int i = 0; i < n; i++)
        {
            var kept = KeptTicks(beats[i], choice[i]);
            var surviving = new List<long>();
            foreach (long tick in kept.Order())
            {
                if (!Clear(tick))
                    continue;
                surviving.Add(tick);
                taken.Add(tick);
            }

            if (surviving.Count == kept.Count)
                continue;
            choice[i] = NearestPatternWithSlots(beats[i], SlotsOf(beats[i], surviving));

            // The shape kept may be a subset of what survived; only what it keeps holds the gap.
            var final = KeptTicks(beats[i], choice[i]);
            foreach (long tick in surviving.Where(t => !final.Contains(t)))
                taken.Remove(tick);
        }

        if (!constraints.RequireStarPowerSolo)
            return;
        RequireOne(choice, constraints, scores, taken, Clear, static b => b.StarPowerPhrase, InRange);
        RequireOne(choice, constraints, scores, taken, Clear, static b => b.SoloPhrase, InRange);
    }

    // Each phrase (by phraseOf, -1 for none) with source onsets in range keeps at least one: the best-scoring candidate
    // that keeps one onset and the gap, or the best-scoring one when none keeps the gap.
    private static void RequireOne(
        int[] choice,
        DecodeConstraints constraints,
        float[,]? scores,
        SortedSet<long> taken,
        Func<long, bool> clear,
        Func<BeatFrame, int> phraseOf,
        Func<long, bool> inRange)
    {
        var beats = constraints.Beats;
        foreach (var phrase in Enumerable.Range(0, beats.Count).Where(i => phraseOf(beats[i]) >= 0).GroupBy(i => phraseOf(beats[i])))
        {
            var members = phrase.ToList();
            if (!members.Any(i => beats[i].SourceTicks.Any(inRange)))
                continue;
            if (members.Any(i => KeptTicks(beats[i], choice[i]).Count > 0))
                continue;

            (int Beat, int Pattern, bool Clear, float Score)? best = null;
            foreach (int i in members)
            {
                foreach (int c in constraints.CandidateSets[i])
                {
                    if (c == BeatPattern.EmptyId || BeatPattern.ChordsKept[c] != 1)
                        continue;
                    var kept = KeptTicks(beats[i], c);
                    if (kept.Count != 1 || !inRange(kept.First()))
                        continue;
                    bool isClear = clear(kept.First());
                    float score = scores is null ? -i : scores[i, c];
                    if (best is not { } b || (isClear && !b.Clear) || (isClear == b.Clear && score > b.Score))
                        best = (i, c, isClear, score);
                }
            }

            if (best is { } pick)
            {
                choice[pick.Beat] = pick.Pattern;
                taken.Add(KeptTicks(beats[pick.Beat], pick.Pattern).First());
            }
        }
    }

    /// <summary>The source ticks <paramref name="patternId"/> keeps in <paramref name="beat"/>.</summary>
    public static HashSet<long> KeptTicks(BeatFrame beat, int patternId)
    {
        ArgumentNullException.ThrowIfNull(beat);
        var slots = new HashSet<int>(BeatPattern.VocabularyOffsets[patternId]);
        var keep = new HashSet<long>();
        for (int k = 0; k < beat.SourceOffsets48.Length && k < beat.SourceTicks.Length; k++)
        {
            if (slots.Contains(beat.SourceOffsets48[k]))
                keep.Add(beat.SourceTicks[k]);
        }

        return keep;
    }

    // The first and last beat the range covers; the whole part without one.
    private static (int Lo, int Hi) WindowOf(IReadOnlyList<BeatFrame> beats, (long From, long To)? range)
    {
        if (range is not { } r)
            return (0, beats.Count - 1);
        int lo = 0;
        while (lo + 1 < beats.Count && beats[lo + 1].BeatStartTick <= r.From)
            lo++;
        int hi = beats.Count - 1;
        while (hi > lo && beats[hi].BeatStartTick > r.To)
            hi--;
        return (lo, hi);
    }

    // Each beat's riff group: the beats with its riff key and candidates, in order (a beat alone is its own).
    private static List<List<int>> RiffGroups(IReadOnlyList<BeatFrame> beats, IReadOnlyList<HashSet<int>> candidateSets)
    {
        var byKey = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var groups = new List<List<int>>();
        for (int i = 0; i < beats.Count; i++)
        {
            string key = beats[i].RiffKey + "|" + string.Join(",", candidateSets[i].Order());
            if (!byKey.TryGetValue(key, out var group))
            {
                byKey[key] = group = [];
                groups.Add(group);
            }

            group.Add(i);
        }

        return groups;
    }

    // Each beat's score per pattern: its riff group's mean emission, plus the density bias.
    private static float[,] Scores(float[,] emissions, List<List<int>> groups, int vocab, double densityBias)
    {
        var scores = new float[emissions.GetLength(0), vocab];
        foreach (var group in groups)
        {
            for (int v = 0; v < vocab; v++)
            {
                float sum = 0;
                foreach (int i in group)
                    sum += emissions[i, v];
                float mean = float.IsNegativeInfinity(sum) ? float.NegativeInfinity : (sum / group.Count) + (float)(densityBias * Kept(v));
                foreach (int i in group)
                    scores[i, v] = mean;
            }
        }

        return scores;

        static int Kept(int v) => v < BeatPattern.ChordsKept.Count ? BeatPattern.ChordsKept[v] : 0;
    }

    // Each riff group's beats in the window take the choice most of them have, the earliest's on a tie.
    private static void Conform(int[] choice, List<List<int>> groups, int lo, int hi)
    {
        foreach (var group in groups)
        {
            var inside = group.Where(i => i >= lo && i <= hi).ToList();
            if (inside.Count < 2)
                continue;
            int most = inside.GroupBy(i => choice[i])
                .OrderByDescending(static g => g.Count())
                .ThenBy(static g => g.First())
                .First().Key;
            foreach (int i in inside)
                choice[i] = most;
        }
    }

    private static List<int> SlotsOf(BeatFrame beat, IEnumerable<long> ticks)
    {
        var wanted = new HashSet<long>(ticks);
        var slots = new List<int>();
        for (int k = 0; k < beat.SourceOffsets48.Length && k < beat.SourceTicks.Length; k++)
        {
            if (wanted.Contains(beat.SourceTicks[k]))
                slots.Add(beat.SourceOffsets48[k]);
        }

        return slots;
    }

    // The pattern keeping the most of surviving and nothing else (a missing slot costs more than a dropped one).
    private static int NearestPatternWithSlots(BeatFrame beat, List<int> surviving)
    {
        var set = new HashSet<int>(surviving);
        int best = BeatPattern.EmptyId;
        int bestScore = int.MaxValue;
        for (int v = 0; v < BeatPattern.VocabularyOffsets.Length; v++)
        {
            var slots = BeatPattern.VocabularyOffsets[v];
            if (!slots.All(set.Contains))
                continue;
            int score = set.Count(s => !slots.Contains(s));
            if (score < bestScore)
            {
                bestScore = score;
                best = v;
            }
        }

        return best;
    }
}
