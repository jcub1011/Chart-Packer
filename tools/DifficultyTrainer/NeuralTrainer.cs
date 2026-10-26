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
    private readonly int _embed;
    private readonly int _styleCount;
    private readonly int _targetCount;
    private readonly torch.Device _device;
    private readonly GruCrfModel _model;
    private readonly torch.optim.Optimizer _adam;
    private bool _disposed;

    public double LearningRate { get; init; } = 3e-4;
    public double AnyStyleShare { get; init; } = 0.2;

    public string Device => _model.DeviceType;

    public NeuralTrainer(int vocab, int styleCount, int targetCount, int hidden, int embed, int seed)
    {
        _vocab = vocab;
        _styleCount = Math.Max(1, styleCount);
        _targetCount = Math.Max(1, targetCount);
        _hidden = hidden;
        _embed = embed;

        torch.manual_seed(seed);
        _device = torch.cuda.is_available() ? torch.CUDA : torch.CPU;
        _model = new GruCrfModel(vocab, _styleCount, _targetCount, hidden, embed, useTier: true);
        _model.to(_device);
        _adam = torch.optim.Adam(_model.parameters(), (double)LearningRate);
    }

    /// <summary>One Adam step on a batch; returns the mean NLL. Bucketed by length upstream.</summary>
    public double TrainBatch(IReadOnlyList<BatchItem> batch)
    {
        if (batch.Count == 0)
            return 0;
        _model.train();
        _adam.zero_grad();
        using var loss = Forward(batch, train: true);
        loss.backward();
        torch.nn.utils.clip_grad_norm_(_model.parameters(), 5.0);
        _adam.step();
        return loss.item<float>();
    }

    /// <summary>Mean held-out NLL over the batch (no updates).</summary>
    public double EvalBatch(IReadOnlyList<BatchItem> batch)
    {
        if (batch.Count == 0)
            return 0;
        _model.eval();
        using var _ = torch.no_grad();
        using var loss = Forward(batch, train: false);
        return loss.item<float>();
    }

    /// <summary>Emission scores for one sequence, on CPU. Test hook for the parity check.</summary>
    public float[,] ComputeEmissions(
        int[] source, sbyte[] scalars, int styleId, int targetIndex, int tierBucket, bool useTier)
    {
        _model.eval();
        using var _ = torch.no_grad();
        int tMax = source.Length;
        var src = torch.zeros([1, tMax], dtype: ScalarType.Int64, device: _device);
        var sc = torch.zeros([1, tMax, 8], dtype: ScalarType.Float32, device: _device);
        var sty = torch.tensor([styleId], dtype: ScalarType.Int64, device: _device);
        var tgt = torch.tensor([targetIndex], dtype: ScalarType.Int64, device: _device);
        var tier = torch.tensor([tierBucket], dtype: ScalarType.Int64, device: _device);
        for (int t = 0; t < tMax; t++)
        {
            src[0, t] = source[t];
            for (int k = 0; k < 8; k++)
                sc[0, t, k] = scalars[t * 8 + k];
        }

        using var emissions = _model.ForwardEmissions(src, sty, tgt, tier, sc, useTier);
        var cpu = emissions.to(torch.CPU).contiguous().data<float>().ToArray();
        var result = new float[tMax, _vocab];
        for (int t = 0; t < tMax; t++)
            for (int v = 0; v < _vocab; v++)
                result[t, v] = cpu[t * _vocab + v];
        src.Dispose(); sc.Dispose(); sty.Dispose(); tgt.Dispose(); tier.Dispose();
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
            var fih = F("_gru.weight_ih_l0");
            var fhh = F("_gru.weight_hh_l0");
            var fib = F("_gru.bias_ih_l0");
            var fhb = F("_gru.bias_hh_l0");
            var bih = F("_gru.weight_ih_l0_reverse");
            var bhh = F("_gru.weight_hh_l0_reverse");
            var bib = F("_gru.bias_ih_l0_reverse");
            var bhb = F("_gru.bias_hh_l0_reverse");

            var (fz, fr, fn) = SplitRows(fih, h, _model.FeatureSize);
            var (fuz, fur, fuh) = SplitRows(fhh, h, h);
            var (fbzIh, fbrIh, fbnIh) = SplitRows(fib, h, 1);
            var (fbzHh, fbrHh, fbnHh) = SplitRows(fhb, h, 1);
            var (bz, br, bh) = SplitRows(bih, h, _model.FeatureSize);
            var (buz, bur, buh) = SplitRows(bhh, h, h);
            var (bbzIh, bbrIh, bbnIh) = SplitRows(bib, h, 1);
            var (bbzHh, bbrHh, bbnHh) = SplitRows(bhb, h, 1);

            return new PackWeights
            {
                SourceEmbedding = F("_source.weight"),
                StyleEmbedding = F("_style.weight"),
                TargetEmbedding = F("_target.weight"),
                Forward = new GruWeights
                {
                    Wz = fz, Wr = fr, Wh = fn, Uz = fuz, Ur = fur, Uh = fuh,
                    BzIh = fbzIh, BzHh = fbzHh, BrIh = fbrIh, BrHh = fbrHh,
                    BnIh = fbnIh, BnHh = fbnHh,
                },
                Backward = new GruWeights
                {
                    Wz = bz, Wr = br, Wh = bh, Uz = buz, Ur = bur, Uh = buh,
                    BzIh = bbzIh, BzHh = bbzHh, BrIh = bbrIh, BrHh = bbrHh,
                    BnIh = bbnIh, BnHh = bbnHh,
                },
                OutputWeight = F("_out.weight"),
                OutputBias = F("_out.bias"),
                Transition = F("_trans"),
                StartTransition = F("_start"),
                EndTransition = F("_end"),
            };
    }

    private Tensor Forward(IReadOnlyList<BatchItem> batch, bool train)
    {
        int b = batch.Count;
        int tMax = batch.Max(static x => x.Length);
        int v = _vocab;

        // Bulk host-to-device copies: per-element indexed sets each synchronize, so the
        // batch is packed into flat managed arrays first and copied once per tensor.
        var srcFlat = new long[b * tMax];
        var labelsFlat = new long[b * tMax];
        var validFlat = new bool[b * tMax];
        var allowedFlat = new bool[b * tMax * v];
        var scalarsFlat = new float[b * tMax * 8];
        var styleFlat = new long[b];
        var targetFlat = new long[b];
        var tierFlat = new long[b];

        for (int i = 0; i < b; i++)
        {
            var item = batch[i];
            for (int t = 0; t < item.Length; t++)
            {
                srcFlat[i * tMax + t] = item.Source[t];
                labelsFlat[i * tMax + t] = item.Labels[t];
                validFlat[i * tMax + t] = true;
                foreach (int c in item.Candidates[t])
                    allowedFlat[(i * tMax + t) * v + c] = true;
                for (int k = 0; k < 8; k++)
                    scalarsFlat[(i * tMax + t) * 8 + k] = item.Scalars[t * 8 + k];
            }

            styleFlat[i] = item.StyleId;
            targetFlat[i] = item.TargetIndex;
            tierFlat[i] = item.TierBucket;
        }

        var src = torch.tensor(srcFlat, dtype: ScalarType.Int64, device: _device).view(b, tMax);
        var labels = torch.tensor(labelsFlat, dtype: ScalarType.Int64, device: _device).view(b, tMax);
        var valid = torch.tensor(validFlat, dtype: ScalarType.Bool, device: _device).view(b, tMax);
        var allowed = torch.tensor(allowedFlat, dtype: ScalarType.Bool, device: _device).view(b, tMax, v);
        var scalars = torch.tensor(scalarsFlat, dtype: ScalarType.Float32, device: _device).view(b, tMax, 8);
        var style = torch.tensor(styleFlat, dtype: ScalarType.Int64, device: _device);
        var target = torch.tensor(targetFlat, dtype: ScalarType.Int64, device: _device);
        var tier = torch.tensor(tierFlat, dtype: ScalarType.Int64, device: _device);

        var emissions = _model.ForwardEmissions(src, style, target, tier, scalars, _model.UseTier);
        // Disallowed candidates score very low, not -inf: logsumexp's backward is 0/0 over
        // all -inf rows, which NaNs every gradient. Padded steps are masked out of the CRF below.
        // Nothing here is disposed: every tensor feeds autograd's backward pass.
        var negInf = torch.full(emissions.shape, -1e9f, emissions.dtype, _device);
        emissions = torch.where(allowed, emissions, negInf);

        var logZ = CrfLogZ(emissions, valid);
        var gold = CrfGold(emissions, labels, valid);
        return (logZ - gold).mean();
    }

    private Tensor CrfLogZ(Tensor emissions, Tensor valid)
    {
        // emissions [B,T,V] with -inf outside candidates; valid [B,T].
        int b = (int)emissions.shape[0];
        int v = _vocab;
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

        var total = torch.logsumexp(alpha + end.unsqueeze(0), 1); // [B]
        return total;
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
            gold = gold + (tr + em) * m;
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
internal sealed record BatchItem(
    int[] Source,
    int[][] Candidates,
    sbyte[] Scalars,
    int[] Labels,
    int StyleId,
    int TargetIndex,
    int TierBucket)
{
    public int Length => Source.Length;
}

/// <summary>BiGRU + linear-chain CRF over beat patterns (spec §3.3).</summary>
internal sealed class GruCrfModel : nn.Module<Tensor, Tensor>
{
    private readonly Embedding _source;
    private readonly Embedding _style;
    private readonly Embedding _target;
    private readonly GRU _gru;
    private readonly Linear _out;
    private readonly Parameter _trans;
    private readonly Parameter _start;
    private readonly Parameter _end;

    public int FeatureSize { get; }
    public bool UseTier { get; set; }
    public Tensor Trans => _trans;
    public Tensor Start => _start;
    public Tensor End => _end;
    public string DeviceType => _trans.device.type.ToString();

    public GruCrfModel(int vocab, int styles, int targets, int hidden, int embed, bool useTier)
        : base(nameof(GruCrfModel))
    {
        FeatureSize = embed * 3 + BiGruCrf.ScalarFeatureCount;
        UseTier = useTier;
        _source = nn.Embedding(vocab, embed);
        _style = nn.Embedding(styles, embed);
        _target = nn.Embedding(targets, embed);
        _gru = nn.GRU(FeatureSize, hidden, bidirectional: true, batchFirst: true);
        _out = nn.Linear(hidden * 2, vocab);
        _trans = nn.Parameter(torch.zeros(vocab, vocab));
        _start = nn.Parameter(torch.zeros(vocab));
        _end = nn.Parameter(torch.zeros(vocab));
        RegisterComponents();
    }

    /// <summary>Emission scores [B,T,V] for the batch.</summary>
    public Tensor ForwardEmissions(Tensor src, Tensor style, Tensor target, Tensor tier, Tensor scalars, bool useTier)
    {
        // Nothing is disposed here: every intermediate feeds autograd's backward pass.
        var se = _source.forward(src); // [B,T,E]
        var b = se.shape[0];
        var t = se.shape[1];
        var ye = _style.forward(style).unsqueeze(1).expand(b, t, -1);
        var te = _target.forward(target).unsqueeze(1).expand(b, t, -1);
        var tierFeat = useTier
            ? (tier.to(ScalarType.Float32).unsqueeze(1).unsqueeze(2).expand(b, t, 1) / 7 * 2 - 1)
            : torch.zeros([b, t, 1], dtype: ScalarType.Float32, device: se.device);
        var scaled = ScaleScalars(scalars); // [B,T,8]
        var inputs = torch.cat([se, ye, te, scaled, tierFeat], dim: 2); // [B,T,F]
        var (y, _) = _gru.forward(inputs); // [B,T,2H]
        return _out.forward(y); // [B,T,V]
    }

    private static Tensor ScaleScalars(Tensor s)
    {
        // s [B,T,8] bytes matching Prepare's packing; reproduces BiGruCrf's feature scaling.
        var parts = new Tensor[8];
        parts[0] = s[.., .., 0] / 4;
        parts[1] = s[.., .., 1] / 7 * 2 - 1;
        for (int k = 2; k <= 6; k++)
            parts[k] = torch.where(s[.., .., k] != 0, torch.ones_like(s[.., .., k]), -torch.ones_like(s[.., .., k]));
        parts[7] = torch.clamp(s[.., .., 7], 0, 7) / 7 * 2 - 1;
        return torch.stack(parts, dim: 2);
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
