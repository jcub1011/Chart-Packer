using ChartShared.Core.Charts;

namespace ChartShared.Core.Generation.Neural;

/// <summary>
/// Managed BiGRU + linear-chain CRF inference (spec §3.3, proposals §2.4): no native dependency,
/// AOT-compatible, deterministic. Reads the part as a beat sequence in both directions; the CRF
/// layer scores whole pattern sequences, decoded by <see cref="ViterbiDecoder"/>.
/// </summary>
public sealed class BiGruCrf
{
    public const string ArchitectureName = "bigru-crf-v1";

    /// <summary>Default hidden size per direction; stays under ~500k parameters (§3.3).</summary>
    public const int DefaultHiddenSize = 64;

    /// <summary>Default embedding size for source pattern, style and target ids.</summary>
    public const int DefaultEmbeddingSize = 16;

    private readonly NeuralPack _pack;
    private readonly int _vocab;
    private readonly int _hidden;
    private readonly int _embed;
    private readonly int _features;

    public BiGruCrf(NeuralPack pack)
    {
        _pack = pack ?? throw new ArgumentNullException(nameof(pack));
        _vocab = pack.Manifest.Vocabulary.Count;
        _hidden = pack.Manifest.Inputs.HiddenSize;
        _embed = pack.Manifest.Inputs.EmbeddingSize;
        _features = FeatureSize(pack.Manifest);
    }

    /// <summary>Per-beat input width: source + style + target embeddings + scalar features.</summary>
    public static int FeatureSize(PackManifest manifest) => manifest.Inputs.EmbeddingSize * 3 + ScalarFeatureCount;

    /// <summary>Scalar per-beat features after the embeddings (§3.3 inputs).</summary>
    public const int ScalarFeatureCount = 9;

    /// <summary>
    /// Emission scores for every beat × vocabulary pattern. Masked out (NaN-safe −inf) where the
    /// candidate is not in the beat's candidate set — pass null masks to score all.
    /// </summary>
    public float[,] Emissions(
        IReadOnlyList<BeatFrame> beats, RunConditioning conditioning, IReadOnlyList<HashSet<int>>? candidateSets = null)
    {
        ArgumentNullException.ThrowIfNull(beats);
        ArgumentNullException.ThrowIfNull(conditioning);
        int n = beats.Count;
        var emission = new float[n, _vocab];
        if (n == 0)
            return emission;

        var inputs = new float[n * _features];
        for (int i = 0; i < n; i++)
            WriteFeatures(beats[i], conditioning, inputs.AsSpan(i * _features, _features));

        var forward = RunGru(_pack.Weights.Forward, inputs, n, _features, _hidden, forward: true);
        var backward = RunGru(_pack.Weights.Backward, inputs, n, _features, _hidden, forward: false);

        var w = _pack.Weights.OutputWeight;
        var b = _pack.Weights.OutputBias;
        int outSize = _hidden * 2;
        var context = new float[outSize];
        for (int i = 0; i < n; i++)
        {
            Array.Copy(forward, i * _hidden, context, 0, _hidden);
            Array.Copy(backward, i * _hidden, context, _hidden, _hidden);
            for (int v = 0; v < _vocab; v++)
            {
                float sum = b[v];
                int row = v * outSize;
                for (int k = 0; k < outSize; k++)
                    sum += w[row + k] * context[k];
                emission[i, v] = sum;
            }
        }

        if (candidateSets is not null)
        {
            for (int i = 0; i < n; i++)
            {
                for (int v = 0; v < _vocab; v++)
                {
                    if (!candidateSets[i].Contains(v))
                        emission[i, v] = float.NegativeInfinity;
                }
            }
        }

        return emission;
    }

    private void WriteFeatures(BeatFrame beat, RunConditioning conditioning, Span<float> dest)
    {
        var weights = _pack.Weights;
        int vocab = _vocab;
        int p = 0;

        // Source pattern embedding.
        int src = Math.Clamp(beat.SourcePatternId, 0, vocab - 1) * _embed;
        for (int k = 0; k < _embed; k++)
            dest[p++] = weights.SourceEmbedding[src + k];

        // Style embedding (Any = 0 when the pack offers it, §3.3).
        int styles = Math.Max(1, _pack.Manifest.Styles.Count);
        int style = Math.Clamp(conditioning.StyleId, 0, styles - 1) * _embed;
        for (int k = 0; k < _embed; k++)
            dest[p++] = weights.StyleEmbedding[style + k];

        // Target embedding.
        int targets = Math.Max(1, _pack.Manifest.Inputs.Targets.Length);
        int targetIndex = TargetIndex(conditioning.Target, targets);
        for (int k = 0; k < _embed; k++)
            dest[p++] = weights.TargetEmbedding[targetIndex * _embed + k];

        // Scalar features, each roughly in [-1, 1].
        dest[p++] = beat.BeatInMeasure / 4f;
        dest[p++] = beat.GapBucket / 7f * 2f - 1f;
        dest[p++] = beat.PhraseStart ? 1f : -1f;
        dest[p++] = beat.SectionStart ? 1f : -1f;
        dest[p++] = beat.HasSustain ? 1f : -1f;
        dest[p++] = beat.StarPower ? 1f : -1f;
        dest[p++] = beat.Solo ? 1f : -1f;
        dest[p++] = Math.Clamp(beat.RiffRepeat, 0, 7) / 7f * 2f - 1f;
        // Tier: 0–6 as a value, 7 (unknown) as its own point; packs without tier read 0.
        dest[p++] = !_pack.Manifest.Inputs.UseTier ? 0f
            : conditioning.TierBucket >= 7 ? 1f
            : conditioning.TierBucket / 7f * 2f - 1f;
    }

    private static int TargetIndex(ChartDifficulty target, int count)
    {
        int index = target switch
        {
            ChartDifficulty.Easy => 0,
            ChartDifficulty.Medium => 1,
            ChartDifficulty.Hard => 2,
            _ => 0,
        };
        return count == 0 ? 0 : index % count;
    }

    /// <summary>Runs one GRU layer over the sequence; returns n × hidden states.</summary>
    internal static float[] RunGru(GruWeights weights, float[] inputs, int n, int inputSize, int hidden, bool forward) =>
        GruForward(weights, inputs, n, inputSize, hidden, forward).States;

    /// <summary>
    /// The cached activations of one GRU forward pass. Used by the trainer's backward pass;
    /// inference should use <see cref="Emissions"/> or <see cref="RunGru"/>.
    /// </summary>
    public sealed class GruForwardCache
    {
        /// <summary> n × hidden output states, in sequence order.</summary>
        public required float[] States { get; init; }

        /// <summary>n × hidden update gates, in sequence order.</summary>
        public required float[] Z { get; init; }

        /// <summary>n × hidden reset gates, in sequence order.</summary>
        public required float[] R { get; init; }

        /// <summary>n × hidden candidates, in sequence order.</summary>
        public required float[] HTilde { get; init; }
    }

    /// <summary>
    /// Runs one GRU layer over the sequence with cached activations. The single forward
    /// implementation shared by app inference and trainer training (parity, spec §3.3).
    /// </summary>
    public static GruForwardCache GruForward(GruWeights weights, float[] inputs, int n, int inputSize, int hidden, bool forward)
    {
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentNullException.ThrowIfNull(inputs);
        var states = new float[n * hidden];
        var z = new float[n * hidden];
        var r = new float[n * hidden];
        var ht = new float[n * hidden];
        var hPrev = new float[hidden];
        for (int step = 0; step < n; step++)
        {
            int i = forward ? step : n - 1 - step;
            var x = inputs.AsSpan(i * inputSize, inputSize);
            var zs = z.AsSpan(i * hidden, hidden);
            var rs = r.AsSpan(i * hidden, hidden);
            for (int hh = 0; hh < hidden; hh++)
            {
                // PyTorch gate order: W_ih·x + b_ih + W_hh·h + b_hh.
                float sz = weights.BzIh[hh], sr = weights.BrIh[hh];
                int row = hh * inputSize;
                int urow = hh * hidden;
                for (int k = 0; k < inputSize; k++)
                {
                    sz += weights.Wz[row + k] * x[k];
                    sr += weights.Wr[row + k] * x[k];
                }

                for (int k = 0; k < hidden; k++)
                {
                    sz += weights.Uz[urow + k] * hPrev[k];
                    sr += weights.Ur[urow + k] * hPrev[k];
                }

                zs[hh] = Sigmoid(sz + weights.BzHh[hh]);
                rs[hh] = Sigmoid(sr + weights.BrHh[hh]);
            }

            // PyTorch: n = tanh(W_in·x + b_in + r·(W_hn·h + b_hn)) — the unit's own reset
            // gates the recurrent term and its bias together.
            for (int hh = 0; hh < hidden; hh++)
            {
                float sh = weights.BnIh[hh];
                int row = hh * inputSize;
                int urow = hh * hidden;
                for (int k = 0; k < inputSize; k++)
                    sh += weights.Wh[row + k] * x[k];
                float gated = weights.BnHh[hh];
                for (int k = 0; k < hidden; k++)
                    gated += weights.Uh[urow + k] * hPrev[k];
                sh += r[i * hidden + hh] * gated;
                float htv = MathF.Tanh(sh);
                ht[i * hidden + hh] = htv;
                // PyTorch GRU convention (matches the TorchSharp trainer): the update gate
                // weights the old state, one minus it the candidate.
                states[i * hidden + hh] = z[i * hidden + hh] * hPrev[hh] + (1 - z[i * hidden + hh]) * htv;
            }

            Array.Copy(states, i * hidden, hPrev, 0, hidden);
        }

        return new GruForwardCache { States = states, Z = z, R = r, HTilde = ht };
    }

    private static float Sigmoid(float x) => 1f / (1f + MathF.Exp(-x));

    /// <summary>Counts parameters for a default-shaped pack (spec §3.3 size requirement).</summary>
    public static int EstimateParameters(int vocab, int styleCount, int targetCount, int hidden, int embed)
    {
        int features = embed * 3 + ScalarFeatureCount;
        int gru = 3 * (hidden * features + hidden * hidden + hidden);
        return vocab * embed + styleCount * embed + targetCount * embed
            + 2 * gru + (hidden * 2 * vocab) + vocab + vocab * vocab + 2 * vocab;
    }
}
