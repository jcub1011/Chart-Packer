using ChartShared.Core.Generation.Neural;
using TorchSharp;
using TorchSharp.Modules;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace DifficultyTrainer;

/// <summary>
/// Trains a BiGRU+CRF pack with TorchSharp (spec §6.5): the .NET bindings to the PyTorch engine.
/// Runs on CUDA when present, else CPU. Reproducible from a seed; early-stops on the held-out
/// score. The apps never run this: trained weights are exported into the pack format and read by
/// the managed C# inference (§3.3), with parity checked by <c>selftest</c>.
/// </summary>
internal sealed class NeuralTrainer : IDisposable
{
    private readonly int _vocab;
    private readonly int _hidden;
    private readonly torch.Device _device;
    private readonly GruCrfModel _model;
    private readonly torch.optim.Optimizer _adam;
    private bool _disposed;

    public string Device => _model.DeviceType;

    public NeuralTrainer(int vocab, int styleCount, int pairCount, int instrumentCount, int hidden, int embed, int seed, double learningRate)
    {
        _vocab = vocab;
        _hidden = hidden;

        torch.manual_seed(seed);
        _device = torch.cuda.is_available() ? torch.CUDA : torch.CPU;
        _model = new GruCrfModel(vocab, Math.Max(1, styleCount), Math.Max(1, pairCount), Math.Max(1, instrumentCount), hidden, embed);
        _model.to(_device);
        _adam = torch.optim.Adam(_model.parameters(), learningRate);
    }

    /// <summary>One Adam step on a batch; returns the mean NLL per beat. Bucketed by length upstream.</summary>
    public double TrainBatch(IReadOnlyList<BatchItem> batch)
    {
        if (batch.Count == 0)
            return 0;
        _model.train();
        _adam.zero_grad();
        using var loss = Forward(batch);
        loss.backward();
        torch.nn.utils.clip_grad_norm_(_model.parameters(), 5.0);
        _adam.step();
        return loss.item<float>();
    }

    /// <summary>Mean held-out NLL per beat over the batch (no updates).</summary>
    public double EvalBatch(IReadOnlyList<BatchItem> batch)
    {
        if (batch.Count == 0)
            return 0;
        _model.eval();
        using var _ = torch.no_grad();
        using var loss = Forward(batch);
        return loss.item<float>();
    }

    /// <summary>
    /// Emission scores for each sequence of the batch, on CPU, padded and packed as training runs them. Test hook for
    /// the parity check: each must match the app's inference over that sequence alone.
    /// </summary>
    public List<float[,]> ComputeEmissions(IReadOnlyList<BatchItem> batch)
    {
        _model.eval();
        using var _ = torch.no_grad();
        var (src, features, style, pair, instrument, lengths, tMax) = Inputs(batch);
        using var emissions = _model.ForwardEmissions(src, style, pair, instrument, features, lengths);
        var cpu = emissions.to(torch.CPU).contiguous().data<float>().ToArray();
        var result = new List<float[,]>();
        for (int i = 0; i < batch.Count; i++)
        {
            var one = new float[batch[i].Length, _vocab];
            for (int t = 0; t < batch[i].Length; t++)
            {
                for (int v = 0; v < _vocab; v++)
                    one[t, v] = cpu[(((i * tMax) + t) * _vocab) + v];
            }

            result.Add(one);
        }

        return result;
    }

    public PackWeights Export()
    {
        _model.eval();
        using var _ = torch.no_grad();
        // NOTE: state_dict values alias the module's live parameters: copy out, never dispose.
        var state = _model.state_dict();
        float[] F(string key)
        {
            using var t = state[key].to(torch.CPU).contiguous();
            return t.data<float>().ToArray();
        }

        // PyTorch GRU rows are [reset; update; new]; the pack stores [update; reset; new].
        static (float[] Z, float[] R, float[] N) SplitRows(float[] stacked, int hidden, int cols)
        {
            var z = new float[hidden * cols];
            var r = new float[hidden * cols];
            var n = new float[hidden * cols];
            Array.Copy(stacked, 0, r, 0, r.Length);
            Array.Copy(stacked, r.Length, z, 0, z.Length);
            Array.Copy(stacked, r.Length + z.Length, n, 0, n.Length);
            return (z, r, n);
        }

        int h = _hidden;
        GruWeights Direction(string suffix)
        {
            var (wz, wr, wn) = SplitRows(F("_gru.weight_ih_l0" + suffix), h, _model.FeatureSize);
            var (uz, ur, un) = SplitRows(F("_gru.weight_hh_l0" + suffix), h, h);
            var (bzIh, brIh, bnIh) = SplitRows(F("_gru.bias_ih_l0" + suffix), h, 1);
            var (bzHh, brHh, bnHh) = SplitRows(F("_gru.bias_hh_l0" + suffix), h, 1);
            return new GruWeights
            {
                Wz = wz, Wr = wr, Wh = wn, Uz = uz, Ur = ur, Uh = un,
                BzIh = bzIh, BzHh = bzHh, BrIh = brIh, BrHh = brHh,
                BnIh = bnIh, BnHh = bnHh,
            };
        }

        return new PackWeights
        {
            SourceEmbedding = F("_source.weight"),
            StyleEmbedding = F("_style.weight"),
            PairEmbedding = F("_pair.weight"),
            InstrumentEmbedding = F("_instrument.weight"),
            Forward = Direction(""),
            Backward = Direction("_reverse"),
            OutputWeight = F("_out.weight"),
            OutputBias = F("_out.bias"),
            Transition = F("_trans"),
            StartTransition = F("_start"),
            EndTransition = F("_end"),
        };
    }

    // The batch as tensors, padded to its longest sequence; lengths stay on the CPU, as packing wants them.
    private (Tensor Src, Tensor Features, Tensor Style, Tensor Pair, Tensor Instrument, long[] Lengths, int TMax) Inputs(IReadOnlyList<BatchItem> batch)
    {
        int b = batch.Count;
        int tMax = batch.Max(static x => x.Length);
        int f = BeatFeatures.Count;

        // Bulk host-to-device copies: per-element indexed sets each synchronize, so the
        // batch is packed into flat managed arrays first and copied once per tensor.
        var srcFlat = new long[b * tMax];
        var featuresFlat = new float[b * tMax * f];
        for (int i = 0; i < b; i++)
        {
            var item = batch[i];
            for (int t = 0; t < item.Length; t++)
                srcFlat[(i * tMax) + t] = item.Source[t];
            Array.Copy(item.Features, 0, featuresFlat, i * tMax * f, item.Length * f);
        }

        return (
            torch.tensor(srcFlat, dtype: ScalarType.Int64, device: _device).view(b, tMax),
            torch.tensor(featuresFlat, dtype: ScalarType.Float32, device: _device).view(b, tMax, f),
            torch.tensor(batch.Select(static x => (long)x.StyleId).ToArray(), dtype: ScalarType.Int64, device: _device),
            torch.tensor(batch.Select(static x => (long)x.PairIndex).ToArray(), dtype: ScalarType.Int64, device: _device),
            torch.tensor(batch.Select(static x => (long)x.InstrumentIndex).ToArray(), dtype: ScalarType.Int64, device: _device),
            [.. batch.Select(static x => (long)x.Length)],
            tMax);
    }

    private Tensor Forward(IReadOnlyList<BatchItem> batch)
    {
        int b = batch.Count;
        int v = _vocab;
        var (src, features, style, pair, instrument, lengths, tMax) = Inputs(batch);

        var labelsFlat = new long[b * tMax];
        var validFlat = new bool[b * tMax];
        var allowedFlat = new bool[b * tMax * v];
        for (int i = 0; i < b; i++)
        {
            var item = batch[i];
            for (int t = 0; t < item.Length; t++)
            {
                labelsFlat[(i * tMax) + t] = item.Labels[t];
                validFlat[(i * tMax) + t] = true;
                foreach (int c in item.Candidates[t])
                    allowedFlat[(((i * tMax) + t) * v) + c] = true;
            }
        }

        var labels = torch.tensor(labelsFlat, dtype: ScalarType.Int64, device: _device).view(b, tMax);
        var valid = torch.tensor(validFlat, dtype: ScalarType.Bool, device: _device).view(b, tMax);
        var allowed = torch.tensor(allowedFlat, dtype: ScalarType.Bool, device: _device).view(b, tMax, v);

        var emissions = _model.ForwardEmissions(src, style, pair, instrument, features, lengths);
        // Disallowed candidates score very low, not -inf: logsumexp's backward is 0/0 over
        // all -inf rows, which NaNs every gradient. Padded steps are masked out of the CRF below.
        // Nothing here is disposed: every tensor feeds autograd's backward pass.
        var negInf = torch.full(emissions.shape, -1e9f, emissions.dtype, _device);
        emissions = torch.where(allowed, emissions, negInf);

        var logZ = CrfLogZ(emissions, valid);
        var gold = CrfGold(emissions, labels, valid);
        // Per beat, so a long song weighs as much per beat as a short one and the step size doesn't scale with length.
        return (logZ - gold).sum() / lengths.Sum();
    }

    private Tensor CrfLogZ(Tensor emissions, Tensor valid)
    {
        long tMax = emissions.shape[1];
        var trans = _model.Trans; // [V,V]
        var start = _model.Start; // [V]
        var end = _model.End;

        var alpha = emissions.select(1, 0) + start.unsqueeze(0); // [B,V]
        for (long t = 1; t < tMax; t++)
        {
            // score[b,p,v] = alpha[b,p] + trans[p,v] + emit[b,v].
            // (Nothing is disposed here: every intermediate feeds autograd's backward pass.)
            var emit = emissions.select(1, t); // [B,V]
            var score = alpha.unsqueeze(2) + trans.unsqueeze(0) + emit.unsqueeze(1);
            var next = torch.logsumexp(score, 2); // [B,V]
            var mask = valid.select(1, t).unsqueeze(1); // [B,1]
            alpha = torch.where(mask, next, alpha);
        }

        return torch.logsumexp(alpha + end.unsqueeze(0), 1); // [B]
    }

    private Tensor CrfGold(Tensor emissions, Tensor labels, Tensor valid)
    {
        long b = labels.shape[0];
        int v = _vocab;
        long tMax = labels.shape[1];
        var trans = _model.Trans;
        var start = _model.Start;
        var end = _model.End;

        var l0 = labels.select(1, 0);
        var gold = start.unsqueeze(0).expand([b, v]).gather(1, l0.unsqueeze(1)).squeeze(1)
            + emissions.select(1, 0).gather(1, l0.unsqueeze(1)).squeeze(1);
        for (long t = 1; t < tMax; t++)
        {
            // (Intermediates are intentionally not disposed: autograd needs them for backward.)
            var pv = labels.select(1, t - 1);
            var cv = labels.select(1, t);
            // trans[pv, cv] per batch element via two gathers.
            var perPrev = trans.unsqueeze(0).expand([b, v, v])
                .gather(2, cv.view([b, 1, 1]).expand([b, v, 1])).squeeze(2); // [B,V]
            var tr = perPrev.gather(1, pv.view([b, 1])).squeeze(1); // [B]
            var em = emissions.select(1, t).gather(1, cv.view([b, 1])).squeeze(1); // [B]
            var m = valid.select(1, t).to(em.dtype);
            gold = gold + ((tr + em) * m);
        }

        // End transition at each sequence's last valid step: end[label[last]].
        var lastIdx = valid.to(ScalarType.Int64).sum([1]) - 1; // [B]
        var lastLabel = labels.gather(1, lastIdx.unsqueeze(1)).squeeze(1); // [B]
        var last = end.unsqueeze(0).expand([b, v]).gather(1, lastLabel.unsqueeze(1)).squeeze(1); // [B]
        return gold + last;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _adam.Dispose();
        _model.Dispose();
    }
}

/// <summary>One training example as the batcher feeds it.</summary>
/// <param name="Features">Each beat's scalar features (<see cref="BeatFeatures"/>), <see cref="BeatFeatures.Count"/> a beat.</param>
internal sealed record BatchItem(
    int[] Source,
    int[][] Candidates,
    float[] Features,
    int[] Labels,
    int StyleId,
    int PairIndex,
    int InstrumentIndex)
{
    public int Length => Source.Length;
}

/// <summary>BiGRU + linear-chain CRF over beat patterns (spec §3.3).</summary>
internal sealed class GruCrfModel : nn.Module<Tensor, Tensor>
{
    private readonly Embedding _source;
    private readonly Embedding _style;
    private readonly Embedding _pair;
    private readonly Embedding _instrument;
    private readonly GRU _gru;
    private readonly Linear _out;
    private readonly Parameter _trans;
    private readonly Parameter _start;
    private readonly Parameter _end;

    public int FeatureSize { get; }
    public Tensor Trans => _trans;
    public Tensor Start => _start;
    public Tensor End => _end;
    public string DeviceType => _trans.device.type.ToString();

    public GruCrfModel(int vocab, int styles, int pairs, int instruments, int hidden, int embed)
        : base(nameof(GruCrfModel))
    {
        FeatureSize = (embed * BiGruCrf.EmbeddingCount) + BeatFeatures.Count;
        _source = nn.Embedding(vocab, embed);
        _style = nn.Embedding(styles, embed);
        _pair = nn.Embedding(pairs, embed);
        _instrument = nn.Embedding(instruments, embed);
        _gru = nn.GRU(FeatureSize, hidden, bidirectional: true, batchFirst: true);
        _out = nn.Linear(hidden * 2, vocab);
        _trans = nn.Parameter(torch.zeros(vocab, vocab));
        _start = nn.Parameter(torch.zeros(vocab));
        _end = nn.Parameter(torch.zeros(vocab));
        RegisterComponents();
    }

    /// <summary>
    /// Emission scores [B,T,V] for the batch. Sequences shorter than the batch's longest are packed, so the backward
    /// direction starts at each one's own last beat, as the apps' inference does, never on padding.
    /// </summary>
    public Tensor ForwardEmissions(Tensor src, Tensor style, Tensor pair, Tensor instrument, Tensor features, long[] lengths)
    {
        // Nothing is disposed here: every intermediate feeds autograd's backward pass.
        var se = _source.forward(src); // [B,T,E]
        var b = se.shape[0];
        var t = se.shape[1];
        var ye = _style.forward(style).unsqueeze(1).expand(b, t, -1);
        var pe = _pair.forward(pair).unsqueeze(1).expand(b, t, -1);
        var ie = _instrument.forward(instrument).unsqueeze(1).expand(b, t, -1);
        var inputs = torch.cat([se, ye, pe, ie, features], dim: 2); // [B,T,F]

        Tensor y;
        if (lengths.All(l => l == t))
        {
            (y, _) = _gru.forward(inputs, null); // [B,T,2H]
        }
        else
        {
            var lengthTensor = torch.tensor(lengths, dtype: ScalarType.Int64);
            var packed = torch.nn.utils.rnn.pack_padded_sequence(inputs, lengthTensor, true, false);
            var (output, _) = _gru.call(packed, null);
            (y, _) = torch.nn.utils.rnn.pad_packed_sequence(output, true, 0, t);
        }

        return _out.forward(y); // [B,T,V]
    }

    public override Tensor forward(Tensor x) =>
        throw new NotSupportedException("Use ForwardEmissions.");
}

/// <summary>Buckets parts by length for padded batching.</summary>
internal static class Batcher
{
    public static List<List<BatchItem>> Bucket(IReadOnlyList<BatchItem> items, int batchSize, Random random)
    {
        var order = items.OrderBy(_ => random.Next()).ToList();
        order.Sort(static (a, b) => a.Length.CompareTo(b.Length));
        var batches = new List<List<BatchItem>>();
        for (int i = 0; i < order.Count; i += batchSize)
            batches.Add(order.GetRange(i, Math.Min(batchSize, order.Count - i)));
        // Shuffle the buckets so lengths don't march in order.
        return [.. batches.OrderBy(_ => random.Next())];
    }
}
