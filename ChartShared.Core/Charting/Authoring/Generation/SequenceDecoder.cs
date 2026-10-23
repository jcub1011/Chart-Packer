namespace ChartShared.Core.Charting.Authoring.Generation;

/// <summary>Finds the highest-scoring pattern sequence inside the hard limits (spec 3.4.4, 3.5).
/// Viterbi over the last three chosen patterns; minimum gap enforced on transitions.
/// Deterministic: ties break toward the smaller pattern mask.</summary>
public static class SequenceDecoder
{
    public const int MaxBeamWidth = 48;

    public sealed record DecodeRequest(
        IReadOnlyList<IReadOnlyList<BeatPattern>> Candidates,
        Func<int, BeatPattern, BeatPattern, BeatPattern, BeatPattern, double> LogScore,
        Func<int, BeatPattern, IReadOnlyList<long>> KeptTicksOf,
        long MinGapTicks);

    public static IReadOnlyList<BeatPattern> Decode(DecodeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        int n = request.Candidates.Count;
        if (n == 0)
            return [];
        var layers = new List<Dictionary<(ulong, ulong, ulong), Entry>>(n);
        Dictionary<(ulong, ulong, ulong), Entry> prev = new()
        {
            [(0, 0, 0)] = new Entry(0, (0, 0, 0), BeatPattern.Empty, -1, true),
        };

        for (int i = 0; i < n; i++)
        {
            var candidates = request.Candidates[i].Count == 0 ? [BeatPattern.Empty] : request.Candidates[i];
            IEnumerable<KeyValuePair<(ulong, ulong, ulong), Entry>> activeStates = prev;
            if (prev.Count > MaxBeamWidth)
            {
                activeStates = prev
                    .OrderByDescending(static kv => kv.Value.Score)
                    .ThenBy(static kv => kv.Key.Item1)
                    .ThenBy(static kv => kv.Key.Item2)
                    .ThenBy(static kv => kv.Key.Item3)
                    .Take(MaxBeamWidth);
            }

            var next = new Dictionary<(ulong, ulong, ulong), Entry>(Math.Min(prev.Count * candidates.Count, MaxBeamWidth * 4));
            if (candidates.Count == 1 && candidates[0].IsEmpty)
            {
                foreach (var (state, entry) in activeStates)
                {
                    var key = (0UL, state.Item1, state.Item2);
                    if (next.TryGetValue(key, out var have) && entry.Score <= have.Score + 1e-12)
                        continue;
                    next[key] = new Entry(entry.Score, state, BeatPattern.Empty, entry.LastKeptTick, false);
                }
            }
            else
            {
                foreach (var (state, entry) in activeStates)
                {
                    var p1 = new BeatPattern(state.Item1);
                    var p2 = new BeatPattern(state.Item2);
                    var p3 = new BeatPattern(state.Item3);
                    foreach (var candidate in candidates)
                    {
                        var ticks = request.KeptTicksOf(i, candidate);
                        if (!GapKeeps(ticks, entry.LastKeptTick, request.MinGapTicks))
                            continue;
                        double score = entry.Score + request.LogScore(i, candidate, p1, p2, p3);
                        var key = (candidate.Mask, state.Item1, state.Item2);
                        if (next.TryGetValue(key, out var have) && score <= have.Score + 1e-12)
                            continue;
                        next[key] = new Entry(score, state, candidate, ticks.Count == 0 ? entry.LastKeptTick : ticks[^1], false);
                    }
                }
            }

            if (next.Count == 0)
            {
                // Nothing kept the gap: rest this beat from the best previous path, with a penalty.
                var best = prev.OrderByDescending(static kv => kv.Value.Score)
                    .ThenBy(static kv => kv.Key.Item1)
                    .ThenBy(static kv => kv.Key.Item2)
                    .ThenBy(static kv => kv.Key.Item3)
                    .First();
                var key = (BeatPattern.Empty.Mask, best.Key.Item1, best.Key.Item2);
                next[key] = new Entry(best.Value.Score - 1e6, best.Key, BeatPattern.Empty, best.Value.LastKeptTick, false);
            }

            layers.Add(next);
            prev = next;
        }

        var bestFinal = prev.OrderByDescending(static kv => kv.Value.Score)
            .ThenBy(static kv => kv.Key.Item1).ThenBy(static kv => kv.Key.Item2).ThenBy(static kv => kv.Key.Item3).First();
        var choices = new BeatPattern[n];
        var stateKey = bestFinal.Key;
        for (int i = n - 1; i >= 0; i--)
        {
            var entry = layers[i][stateKey];
            choices[i] = entry.IsSeed ? new BeatPattern(stateKey.Item1) : entry.Choice;
            if (!entry.IsSeed)
                stateKey = entry.Prev;
        }

        return choices;
    }

    private sealed record Entry(double Score, (ulong, ulong, ulong) Prev, BeatPattern Choice, long LastKeptTick, bool IsSeed);

    internal static bool GapKeeps(IReadOnlyList<long> ticks, long lastKept, long minGap)
    {
        if (ticks.Count == 0 || minGap <= 1)
            return true;
        for (int k = 1; k < ticks.Count; k++)
        {
            if (ticks[k] - ticks[k - 1] < minGap)
                return false;
        }

        return lastKept < 0 || ticks[0] - lastKept >= minGap;
    }
}
