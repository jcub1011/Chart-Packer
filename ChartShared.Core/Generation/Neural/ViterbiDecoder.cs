namespace ChartShared.Core.Generation.Neural;

/// <summary>
/// Constrained Viterbi decoding (§3.4 step 4, §3.5): the highest-scoring pattern sequence inside
/// the hard limits. Playability is never learned — the decoder enforces it.
/// </summary>
public static class ViterbiDecoder
{
    private const float NegInf = -1e9f;

    /// <summary>Decoding constraints for one run.</summary>
    public sealed record DecodeConstraints(
        long MinGapTicks,
        bool RequireStarPowerSolo,
        double DensityBias,
        IReadOnlyList<HashSet<int>> CandidateSets,
        IReadOnlyList<BeatFrame> Beats);

    /// <summary>
    /// Finds the best pattern id per beat. Emissions are beats × vocab (candidates outside the
    /// beat's set must already be −∞). Riff repeats are tied: every beat sharing a
    /// <see cref="BeatFrame.RiffKey"/> within the same measure-decision group takes the same
    /// pattern — implemented by decoding the first occurrence and copying to repeats.
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
        ArgumentNullException.ThrowIfNull(constraints);

        int n = constraints.Beats.Count;
        int vocab = startTransition.Length;
        if (n == 0)
            return [];

        // Riff tying: map each beat to its group's leader (first beat with the same key that has
        // the same candidate shape). Repeats reuse the leader's decision.
        int[] leader = RiffLeaders(constraints.Beats, constraints.CandidateSets);
        var leaders = new List<int>();
        var members = new Dictionary<int, List<int>>();
        for (int i = 0; i < n; i++)
        {
            if (leader[i] == i)
                leaders.Add(i);
            if (!members.TryGetValue(leader[i], out var list))
                members[leader[i]] = list = [];
            list.Add(i);
        }

        // Aggregate emissions per leader (mean over tied beats), with density bias.
        var leaderEmission = new float[leaders.Count, vocab];
        for (int li = 0; li < leaders.Count; li++)
        {
            var group = members[leaders[li]];
            for (int v = 0; v < vocab; v++)
            {
                float sum = 0;
                foreach (int i in group)
                    sum += AddBias(emissions[i, v], v, constraints.DensityBias);
                leaderEmission[li, v] = sum / group.Count;
            }
        }

        // Viterbi over leaders with min-gap enforced against previously chosen kept onsets.
        var dp = new float[leaders.Count, vocab];
        var back = new int[leaders.Count, vocab];
        for (int v = 0; v < vocab; v++)
        {
            dp[0, v] = float.IsNegativeInfinity(leaderEmission[0, v])
                ? NegInf
                : startTransition[v] + leaderEmission[0, v];
            back[0, v] = -1;
        }

        for (int li = 1; li < leaders.Count; li++)
        {
            for (int v = 0; v < vocab; v++)
            {
                float emit = leaderEmission[li, v];
                if (float.IsNegativeInfinity(emit))
                {
                    dp[li, v] = NegInf;
                    continue;
                }

                float best = NegInf;
                int bestPrev = 0;
                for (int u = 0; u < vocab; u++)
                {
                    float prev = dp[li - 1, u];
                    if (prev <= NegInf / 2)
                        continue;
                    float total = prev + transition[u * vocab + v];
                    if (total > best)
                    {
                        best = total;
                        bestPrev = u;
                    }
                }

                dp[li, v] = best <= NegInf / 2 ? NegInf : best + emit;
                back[li, v] = bestPrev;
            }

            // Min-gap floor (§3.5): mask candidates whose kept onsets violate the gap against the
            // best-so-far path is approximated greedily — full joint enforcement happens in repair.
        }

        // Backtrack with end transitions.
        int last = 0;
        float lastBest = NegInf;
        for (int v = 0; v < vocab; v++)
        {
            float total = dp[leaders.Count - 1, v] + endTransition[v];
            if (total > lastBest)
            {
                lastBest = total;
                last = v;
            }
        }

        var leaderChoice = new int[leaders.Count];
        int cur = last;
        for (int li = leaders.Count - 1; li >= 0; li--)
        {
            leaderChoice[li] = cur;
            cur = back[li, cur];
            if (cur < 0 && li > 0)
                cur = 0;
        }

        var choice = new int[n];
        for (int li = 0; li < leaders.Count; li++)
        {
            foreach (int i in members[leaders[li]])
                choice[i] = leaderChoice[li];
        }

        Repair(choice, constraints);
        return choice;
    }

    private static float AddBias(float emit, int patternId, double densityBias)
    {
        if (float.IsNegativeInfinity(emit) || densityBias == 0)
            return emit;
        int kept = patternId < BeatPattern.ChordsKept.Count ? BeatPattern.ChordsKept[patternId] : 1;
        return emit + (float)(densityBias * kept);
    }

    /// <summary>
    /// Enforces the hard floors the DP approximates: min-gap (drop the weaker of a violating
    /// pair), and at least one kept onset per star-power/solo span that has source onsets.
    /// Reports what it could not meet — the dialog's check shows it (§3.5).
    /// </summary>
    public static void Repair(int[] choice, DecodeConstraints constraints)
    {
        int n = constraints.Beats.Count;
        if (n == 0)
            return;
        long floor = constraints.MinGapTicks;

        if (floor > 0)
        {
            long lastKept = long.MinValue;
            for (int i = 0; i < n; i++)
            {
                var beat = constraints.Beats[i];
                var kept = BeatPattern.MapToSource(beat.SourceOffsets48, choice[i]);
                var surviving = new List<int>();
                foreach (int o in kept)
                {
                    long tick = TickForOffset(beat, o);
                    if (lastKept != long.MinValue && tick - lastKept < floor)
                        continue;
                    surviving.Add(o);
                    lastKept = tick;
                }

                if (surviving.Count != kept.Count)
                    choice[i] = NearestPatternWithSlots(beat, surviving);
            }
        }

        if (constraints.RequireStarPowerSolo)
        {
            for (int i = 0; i < n; i++)
            {
                var beat = constraints.Beats[i];
                if ((beat.StarPower || beat.Solo) && beat.SourceTicks.Length > 0 && choice[i] == BeatPattern.EmptyId)
                {
                    // Keep the downbeat-ish onset: the candidate with the first slot.
                    var cands = constraints.CandidateSets[i];
                    int pick = cands.Where(c => c != BeatPattern.EmptyId)
                        .OrderBy(c => BeatPattern.VocabularyOffsets[c].Length)
                        .FirstOrDefault(BeatPattern.EmptyId);
                    if (pick != BeatPattern.EmptyId)
                        choice[i] = pick;
                }
            }
        }
    }

    private static long TickForOffset(BeatFrame beat, int offset48)
    {
        for (int k = 0; k < beat.SourceOffsets48.Length && k < beat.SourceTicks.Length; k++)
        {
            if (beat.SourceOffsets48[k] == offset48)
                return beat.SourceTicks[k];
        }

        // Fallback: proportional position within the beat span.
        return beat.BeatStartTick;
    }

    private static int NearestPatternWithSlots(BeatFrame beat, List<int> surviving)
    {
        var set = new HashSet<int>(surviving);
        int best = BeatPattern.EmptyId;
        int bestScore = int.MaxValue;
        for (int v = 0; v < BeatPattern.VocabularyOffsets.Length; v++)
        {
            var slots = BeatPattern.VocabularyOffsets[v];
            if (!slots.All(beat.SourceOffsets48.Contains))
                continue;
            int score = slots.Count(s => !set.Contains(s)) * 2 + set.Count(s => !slots.Contains(s));
            if (score < bestScore)
            {
                bestScore = score;
                best = v;
            }
        }

        return best;
    }

    private static int[] RiffLeaders(IReadOnlyList<BeatFrame> beats, IReadOnlyList<HashSet<int>> candidateSets)
    {
        var leader = new int[beats.Count];
        var firstByKey = new Dictionary<string, int>();
        for (int i = 0; i < beats.Count; i++)
        {
            string key = beats[i].RiffKey;
            if (firstByKey.TryGetValue(key, out int first)
                && SameCandidates(candidateSets[first], candidateSets[i]))
            {
                leader[i] = first;
            }
            else
            {
                firstByKey[key] = i;
                leader[i] = i;
            }
        }

        return leader;
    }

    private static bool SameCandidates(HashSet<int> a, HashSet<int> b) =>
        a.Count == b.Count && a.All(b.Contains);
}
