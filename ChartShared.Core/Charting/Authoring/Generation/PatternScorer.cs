namespace ChartShared.Core.Charting.Authoring.Generation;

/// <summary>The scorer context for one beat (spec 3.2). Prev patterns are the decoder's own
/// earlier choices (or Empty at the start); missing history is Empty, never null.</summary>
public sealed record PatternContext(
    BeatPattern Source,
    BeatPattern Prev1,
    BeatPattern Prev2,
    BeatPattern Prev3,
    BeatPattern NextSource,
    int MeasurePlace,
    string Style,
    string Target,
    string SourceDifficulty,
    string Instrument,
    BeatShape Shape = BeatShape.None);

/// <summary>Count-based scorer (spec 3.2): table probabilities with recursive Witten–Bell backoff
/// across all context levels from shortest to longest. Deterministic; unseen patterns get a small non-zero probability.</summary>
public sealed class PatternScorer
{
    public const double Epsilon = 1e-9;

    internal readonly record struct RowStats(long Total, double Lambda, IReadOnlyDictionary<ulong, int> Counts);

    private readonly PatternPack _pack;
    private readonly Dictionary<string, RowStats> _rows;
    private readonly long _globalTotal;
    private readonly Dictionary<ulong, long> _globalHits;
    private readonly bool _hasSrcDiffLevels;

    public PatternScorer(PatternPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        _pack = pack;
        _rows = new Dictionary<string, RowStats>(pack.Counts.Count, StringComparer.Ordinal);
        long globalTotal = 0;
        var hits = new Dictionary<ulong, long>();
        bool hasSrcDiff = false;

        foreach (var (key, row) in _pack.Counts)
        {
            if (row.Count == 0)
                continue;
            long rowTotal = 0;
            foreach (var (mask, count) in row)
            {
                rowTotal += count;
                if (key.StartsWith("src=", StringComparison.Ordinal) && !key.Contains('|'))
                {
                    globalTotal += count;
                    hits[mask] = hits.TryGetValue(mask, out long h) ? h + count : count;
                }
            }

            if (rowTotal <= 0)
                continue;
            if (!hasSrcDiff && key.Contains("|srcD=", StringComparison.Ordinal))
                hasSrcDiff = true;
            double lambda = (double)rowTotal / (rowTotal + 2.0 * row.Count);
            _rows[key] = new RowStats(rowTotal, lambda, row);
        }

        if (globalTotal == 0 && _rows.TryGetValue("global", out var globalRow))
        {
            globalTotal = globalRow.Total;
            foreach (var (mask, count) in globalRow.Counts)
                hits[mask] = count;
        }

        _globalTotal = globalTotal;
        _globalHits = hits;
        _hasSrcDiffLevels = hasSrcDiff;
    }

    /// <summary>Log-probability of <paramref name="candidate"/> in <paramref name="context"/>,
    /// recursively interpolated across every matching backoff level from general to specific.</summary>
    public double Score(PatternContext context, BeatPattern candidate)
    {
        double p = BackedOff(candidate);
        var keys = ContextKeys(context);
        for (int i = 0; i < keys.Count; i++)
        {
            if (_rows.TryGetValue(keys[i], out var row))
            {
                row.Counts.TryGetValue(candidate.Mask, out int count);
                double mle = (double)count / row.Total;
                p = row.Lambda * mle + (1.0 - row.Lambda) * p;
            }
        }

        return Math.Log(Math.Max(p, Epsilon));
    }

    /// <summary>Precomputed per-beat scorer state so <see cref="SequenceDecoder"/> resolves base probabilities once per beat
    /// and history rows once per state instead of formatting strings per candidate.</summary>
    internal BeatScorer PrepareBeat(
        BeatPattern source,
        BeatShape shape,
        BeatPattern nextSource,
        int measurePlace,
        string style,
        string target,
        string sourceDifficulty,
        IReadOnlyList<BeatPattern> candidates)
    {
        string normStyle = NormalizeStyle(style);
        string srcStr = source.ToDisplay();
        string nextStr = nextSource.ToDisplay();
        int n = candidates.Count;
        var baseProbs = new double[n];
        for (int c = 0; c < n; c++)
            baseProbs[c] = BackedOff(candidates[c]);

        string kGlobal = "global";
        string kSrc = $"src={srcStr}";
        string kRun = $"src={srcStr}|tgt={target}";
        string kSrcD = $"src={srcStr}|tgt={target}|srcD={sourceDifficulty}";
        string kShape = $"src={srcStr}|tgt={target}|srcD={sourceDifficulty}|sh={(int)shape}";

        ApplyRow(kGlobal, candidates, baseProbs);
        ApplyRow(kSrc, candidates, baseProbs);
        ApplyRow(kRun, candidates, baseProbs);
        if (_hasSrcDiffLevels)
        {
            ApplyRow(kSrcD, candidates, baseProbs);
            ApplyRow(kShape, candidates, baseProbs);
        }

        string historyPrefix = _hasSrcDiffLevels ? kShape : kRun;
        string? styleSuffix = string.IsNullOrEmpty(normStyle) || normStyle.Equals("Any", StringComparison.OrdinalIgnoreCase)
            ? null
            : $"|style={normStyle}";
        return new BeatScorer(this, historyPrefix, $"|next={nextStr}", $"|pos={measurePlace}", styleSuffix, candidates, baseProbs);
    }

    private void ApplyRow(string key, IReadOnlyList<BeatPattern> candidates, double[] probs)
    {
        if (!_rows.TryGetValue(key, out var row))
            return;
        double invTotal = 1.0 / row.Total;
        double lam = row.Lambda;
        double oneMinus = 1.0 - lam;
        for (int c = 0; c < candidates.Count; c++)
        {
            row.Counts.TryGetValue(candidates[c].Mask, out int count);
            probs[c] = lam * (count * invTotal) + oneMinus * probs[c];
        }
    }

    internal sealed class BeatScorer(
        PatternScorer owner,
        string historyPrefix,
        string nextPiece,
        string posPiece,
        string? stylePiece,
        IReadOnlyList<BeatPattern> candidates,
        double[] baseProbs)
    {
        private readonly Dictionary<(ulong, ulong, ulong), double[]> _stateLogScores = new();

        public double[] LogScoresForState(BeatPattern p1, BeatPattern p2, BeatPattern p3)
        {
            var stateKey = (p1.Mask, p2.Mask, p3.Mask);
            if (_stateLogScores.TryGetValue(stateKey, out var cached))
                return cached;

            int n = candidates.Count;
            var scores = new double[n];
            Span<RowStats> chain = stackalloc RowStats[6];
            int depth = 0;

            string k1 = $"{historyPrefix}|p1={p1.ToDisplay()}";
            if (owner._rows.TryGetValue(k1, out var r1))
            {
                chain[depth++] = r1;
                string k2 = $"{k1}|p2={p2.ToDisplay()}";
                if (owner._rows.TryGetValue(k2, out var r2))
                {
                    chain[depth++] = r2;
                    string k3 = $"{k2}|p3={p3.ToDisplay()}";
                    if (owner._rows.TryGetValue(k3, out var r3))
                    {
                        chain[depth++] = r3;
                        string kNext = k3 + nextPiece;
                        if (owner._rows.TryGetValue(kNext, out var rNext))
                        {
                            chain[depth++] = rNext;
                            string kPos = kNext + posPiece;
                            if (owner._rows.TryGetValue(kPos, out var rPos))
                            {
                                chain[depth++] = rPos;
                                if (stylePiece is not null && owner._rows.TryGetValue(kPos + stylePiece, out var rStyle))
                                    chain[depth++] = rStyle;
                            }
                        }
                    }
                }
            }

            for (int c = 0; c < n; c++)
            {
                ulong mask = candidates[c].Mask;
                double p = baseProbs[c];
                for (int d = 0; d < depth; d++)
                {
                    ref readonly var row = ref chain[d];
                    row.Counts.TryGetValue(mask, out int count);
                    p = row.Lambda * ((double)count / row.Total) + (1.0 - row.Lambda) * p;
                }

                scores[c] = Math.Log(Math.Max(p, Epsilon));
            }

            _stateLogScores[stateKey] = scores;
            return scores;
        }
    }

    private double BackedOff(BeatPattern candidate)
    {
        if (_globalTotal > 0)
        {
            _globalHits.TryGetValue(candidate.Mask, out long hit);
            return (hit + 1.0) / (_globalTotal + _pack.Vocabulary.Count + 1.0);
        }

        return 1.0 / Math.Max(1, _pack.Vocabulary.Count + 1);
    }

    /// <summary>Backoff chain, shortest first: global, source alone, source+target run, then each
    /// backoff feature added most-important first.</summary>
    internal List<string> ContextKeys(PatternContext context)
    {
        string style = NormalizeStyle(context.Style);
        var normalized = context with { Style = style };
        var order = _pack.Manifest.BackoffOrder is { Count: > 0 } b ? b : PatternPackLoader.DefaultBackoffOrder;
        var keys = new List<string>(12) { "global", $"src={context.Source}" };
        keys.AddRange(ChainKeys(order, normalized));
        return keys;
    }

    /// <summary>Counted levels for one example, longest last: source+target run, every backoff prefix,
    /// then the full key. Training increments each so every backoff level has data (spec 3.2).</summary>
    internal static List<string> ChainKeys(IReadOnlyList<string> backoffOrder, PatternContext context)
    {
        string run = $"src={context.Source}|tgt={context.Target}";
        var keys = new List<string>(backoffOrder.Count + 2) { run };
        string current = run;
        for (int i = backoffOrder.Count - 1; i >= 0; i--)
        {
            string? piece = FeaturePiece(backoffOrder[i], context, context.Style);
            if (piece is null)
                continue;
            current += "|" + piece;
            keys.Add(current);
        }

        return keys;
    }

    /// <summary>Canonical full context key. Training must use this so counts line up with queries.</summary>
    public static string FullKey(PatternContext context) =>
        $"src={context.Source}|tgt={context.Target}|srcD={context.SourceDifficulty}|sh={(int)context.Shape}|p1={context.Prev1}|p2={context.Prev2}|p3={context.Prev3}|next={context.NextSource}|pos={context.MeasurePlace}|style={context.Style}";

    private static string? FeaturePiece(string feature, PatternContext context, string style) => feature switch
    {
        "SourceDifficulty" => string.IsNullOrEmpty(context.SourceDifficulty) ? null : $"srcD={context.SourceDifficulty}",
        "Shape" => $"sh={(int)context.Shape}",
        "Prev1" => $"p1={context.Prev1}",
        "Prev2" => $"p2={context.Prev2}",
        "Prev3" => $"p3={context.Prev3}",
        "NextSource" => $"next={context.NextSource}",
        "MeasurePlace" => $"pos={context.MeasurePlace}",
        "Style" => string.IsNullOrEmpty(style) || style.Equals("Any", StringComparison.OrdinalIgnoreCase) ? null : $"style={style}",
        _ => null,
    };

    private string NormalizeStyle(string style)
    {
        if (string.IsNullOrWhiteSpace(style))
            return "Any";
        if (_pack.Manifest.SupportsAnyStyle && style.Equals("Any", StringComparison.OrdinalIgnoreCase))
            return "Any";
        foreach (var s in _pack.Manifest.Styles)
        {
            if (s.Id.Equals(style, StringComparison.OrdinalIgnoreCase))
                return s.Id;
        }

        return "Any";
    }
}
