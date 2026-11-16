using ChartShared.Audio.Dsp;

namespace ChartShared.Audio.Analysis;

/// <summary>
/// Computes <see cref="Spectrogram"/>s on <see cref="AnalysisWorkers"/>: a column every 256 samples, from Hann windows of
/// three lengths (<see cref="SpectrogramTiers"/>), two frames per FFT, mapped to <see cref="SpectrogramRows"/>
/// (charter-spec C13).
/// </summary>
/// <remarks>
/// Columns are computed a chunk at a time, the chunk nearest the focus first, so the view fills in from where it is
/// outwards. Frames are paired by their column numbers and samples are counted from the audio's start, so a chunk's
/// columns are the same whichever thread computes them and however the song is cut into chunks. A sharpened chunk
/// analyses a frame at every other column (<see cref="SpectrogramJob.SharpStride"/>), which is enough when each
/// frame's power is put where it really is, and a few columns either side of its own too, for the power they put in
/// its columns.
/// <para>Sharpened, each bin's power goes where the bin points (<see cref="ReassignedPair"/>): spread over the rows
/// between there and halfway to where the bins either side of it point, and over the columns between there and halfway
/// to where the same bin points in the frames before and after. A steady tone's bins all point at one frequency, so
/// it's a row or two; a click's frames all point at one time, so it's a column or two; and noise, whose bins point all
/// over, still leaves hardly a cell empty, where power put only at the points would leave it speckled with them.
/// What cells that does leave empty, where the bins around all point away, are filled by a little of each bin's power
/// (<see cref="Bed"/> of it) put where the bin itself is, as unsharpened: a faint bed under the sharp picture.</para>
/// </remarks>
public static class SpectrogramBuilder
{
    /// <summary>How many columns are computed together: about 1.5 s at 44.1 kHz.</summary>
    public const int ChunkColumns = 256;

    /// <summary>Starts computing <paramref name="input"/>'s spectrogram, from <paramref name="focusSeconds"/> outwards.</summary>
    /// <param name="sharpen">Whether to put each bin's energy where it really is (<see cref="Spectrogram.IsSharpened"/>).</param>
    public static Spectrogram Start(AnalysisInput input, AnalysisWorkers workers, double focusSeconds, bool sharpen = true) =>
        Start(input, workers, focusSeconds, ChunkColumns, sharpen);

    internal static Spectrogram Start(AnalysisInput input, AnalysisWorkers workers, double focusSeconds, int chunkColumns, bool sharpen = true)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(workers);

        var spectrogram = new Spectrogram(input.SampleRate, input.LengthFrames, chunkColumns, sharpen);
        var job = new SpectrogramJob(input, spectrogram, workers);
        spectrogram.Job = job;
        job.Focus(spectrogram.ColumnAt(focusSeconds) / chunkColumns);
        workers.Submit(job);
        return spectrogram;
    }
}

/// <summary>A spectrogram's columns, as chunks for <see cref="AnalysisWorkers"/>.</summary>
internal sealed class SpectrogramJob : AnalysisJob
{
    /// <summary>Chunks this near the focus come before other work.</summary>
    private const int FocusedChunks = 24;

    /// <summary>How many columns are analysed between checking for cancellation.</summary>
    private const int CheckEvery = 64;

    // WorkerScratch buffers: the audio at each halving of its rate take the first few.
    private const int SumsBuffer = SpectrogramTiers.MostHalvings + 1;
    private const int LevelsBuffer = SumsBuffer + 1;
    private const int PowerBufferA = SumsBuffer + 2;
    private const int PowerBufferB = SumsBuffer + 3;
    private const int PointBuffers = SumsBuffer + 4;

    private const int Rows = Spectrogram.Rows;

    /// <summary>How many columns apart the frames analysed are when sharpened.</summary>
    internal const int SharpStride = 2;

    /// <summary>How much of each bin's power is put where the bin itself is when sharpened: 23 dB under the rest.</summary>
    private const float Bed = 0.005f;

    private static long _focusClock;

    private readonly Spectrogram _spectrogram;
    private readonly AnalysisWorkers _workers;
    private readonly bool[] _claimed;
    private readonly Lock _lock = new();
    private int _focus;

    // How many of _claimed are still false. Under _lock.
    private int _unclaimed;

    public SpectrogramJob(AnalysisInput input, Spectrogram spectrogram, AnalysisWorkers workers)
        : base(input)
    {
        _spectrogram = spectrogram;
        _workers = workers;
        _claimed = new bool[spectrogram.ChunkCount];
        _unclaimed = _claimed.Length;
        Expect(spectrogram.ChunkCount);
    }

    public override AnalysisPriority Priority
    {
        get
        {
            lock (_lock)
            {
                int from = Math.Max(0, _focus - FocusedChunks);
                int to = Math.Min(_claimed.Length - 1, _focus + FocusedChunks);
                for (int i = from; i <= to; i++)
                {
                    if (!_claimed[i])
                        return AnalysisPriority.Focused;
                }

                return AnalysisPriority.Background;
            }
        }
    }

    /// <remarks>
    /// Asked every frame a spectrogram is drawn, so the workers are woken only when there's somewhere new to start from
    /// and something left to compute: once every chunk is claimed, waking them would only have them look for work again.
    /// </remarks>
    public void Focus(int chunk)
    {
        bool wake;
        lock (_lock)
        {
            int focus = Math.Clamp(chunk, 0, Math.Max(0, _claimed.Length - 1));
            wake = focus != _focus && _unclaimed > 0;
            _focus = focus;
        }

        FocusStamp = Interlocked.Increment(ref _focusClock);
        if (wake)
            _workers.Wake();
    }

    public override int ClaimNext()
    {
        lock (_lock)
        {
            // The unclaimed chunk nearest the focus, the later of two as near (the view looks ahead).
            for (int distance = 0; distance < _claimed.Length; distance++)
            {
                foreach (int chunk in (ReadOnlySpan<int>)[_focus + distance, _focus - distance])
                {
                    if (chunk >= 0 && chunk < _claimed.Length && !_claimed[chunk])
                    {
                        _claimed[chunk] = true;
                        _unclaimed--;
                        return chunk;
                    }
                }
            }

            return -1;
        }
    }

    protected override void RunChunk(int chunk, IMonoReader reader, WorkerScratch scratch, CancellationToken cancellationToken)
    {
        if (_spectrogram.BlockFor(chunk) is not { } block)
            return;

        int first = chunk * _spectrogram.ChunkColumns;
        int count = _spectrogram.ColumnsIn(chunk);
        var tiers = SpectrogramTiers.For(_spectrogram.SampleRate);
        bool sharpen = _spectrogram.IsSharpened;

        // The columns each band analyses: the chunk's, those either side that can put energy in them, and out to a
        // whole pair at each end. Then the samples those take, at each halving of the rate.
        Span<int> from = stackalloc int[tiers.All.Count];
        Span<int> to = stackalloc int[tiers.All.Count];
        Span<long> start = stackalloc long[SpectrogramTiers.MostHalvings + 1];
        Span<long> end = stackalloc long[SpectrogramTiers.MostHalvings + 1];
        for (int t = 0; t < tiers.All.Count; t++)
        {
            var tier = tiers.All[t];
            if (sharpen)
            {
                // The frames that can put power in the chunk's columns, and one more either side: where a frame's goes
                // depends on the frames before and after. Pairs start on every fourth column.
                int reach = tier.MarginColumns + (2 * SharpStride);
                from[t] = (first - reach) & ~((2 * SharpStride) - 1);
                to[t] = ((first + count - 1 + reach - from[t]) | ((2 * SharpStride) - 1)) - (SharpStride - 1) + from[t];
            }
            else
            {
                from[t] = first & ~1;
                to[t] = (first + count - 1) | 1;
            }

            if (tier.FirstRow >= 0)
                Need(start, end, tier.Halvings, ((long)from[t] * tier.Hop) - (tier.Window / 2), ((long)to[t] * tier.Hop) + (tier.Window / 2));
        }

        for (int h = SpectrogramTiers.MostHalvings; h > 0; h--)
        {
            if (end[h] > start[h])
            {
                var (inputStart, inputCount) = HalfBandDecimator.InputFor(start[h], (int)(end[h] - start[h]));
                Need(start, end, h - 1, inputStart, inputStart + inputCount);
            }
        }

        var audio = new float[SpectrogramTiers.MostHalvings + 1][];
        audio[0] = scratch.Floats(0, (int)(end[0] - start[0]));
        WindowRead.Read(reader, start[0], audio[0].AsSpan(0, (int)(end[0] - start[0])));
        for (int h = 1; h <= SpectrogramTiers.MostHalvings; h++)
        {
            int length = (int)(end[h] - start[h]);
            audio[h] = scratch.Floats(h, length);
            if (length > 0)
                HalfBandDecimator.Decimate(audio[h - 1].AsSpan(0, (int)(end[h - 1] - start[h - 1])), start[h - 1], audio[h].AsSpan(0, length), start[h]);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Sharpened, each cell adds up power; otherwise, each band's level times how much of the row is the band's.
        var sums = scratch.Floats(SumsBuffer, count * Rows);
        Array.Clear(sums, 0, count * Rows);
        for (int t = 0; t < tiers.All.Count; t++)
        {
            var tier = tiers.All[t];
            if (tier.FirstRow < 0)
                continue;

            var samples = audio[tier.Halvings];
            long origin = start[tier.Halvings] + (tier.Window / 2);
            if (sharpen)
            {
                var pair = scratch.Reassigned(tier.Window);
                int firstBin = Math.Max(1, tier.FirstBin - 1), lastBin = Math.Min(tier.Window / 2, tier.LastBin + 1);
                float scale = (float)(1 / pair.TonePower);

                // The frame before the one whose power is being put, it, and the one after.
                var points = new Points[3];
                for (int i = 0; i < points.Length; i++)
                    points[i] = new Points(scratch, PointBuffers + (3 * i), (tier.Window / 2) + 1);
                for (int c = from[t]; c <= to[t]; c += 2 * SharpStride)
                {
                    pair.Analyse(Frame(samples, origin, tier, c), Frame(samples, origin, tier, c + SharpStride), firstBin, lastBin);
                    for (int i = 0; i < 2; i++)
                    {
                        (points[0], points[1], points[2]) = (points[1], points[2], points[0]);
                        points[2].Take(tier, i == 0 ? pair.A : pair.B, firstBin, lastBin);
                        int middle = c + ((i - 1) * SharpStride);
                        if (middle > from[t])
                            Sharpened(tier, points[0], points[1], points[2], middle - first, count, scale, sums);
                    }

                    if (((c - from[t]) & (CheckEvery - 1)) == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                }
            }
            else
            {
                var fft = scratch.Fft(tier.Window);
                double offset = 20 * Math.Log10(2 / fft.WindowSum);
                var powerA = scratch.Floats(PowerBufferA, (tier.Window / 2) + 1);
                var powerB = scratch.Floats(PowerBufferB, (tier.Window / 2) + 1);
                var levels = scratch.Floats(LevelsBuffer, Rows);
                for (int c = from[t]; c <= to[t]; c += 2)
                {
                    fft.Power(Frame(samples, origin, tier, c), Frame(samples, origin, tier, c + 1), powerA, powerB);
                    Plain(tier, powerA, c - first, count, offset, levels, sums);
                    Plain(tier, powerB, c + 1 - first, count, offset, levels, sums);
                    if (((c - from[t]) & (CheckEvery - 1)) == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }

        int silentFrom = tiers.FirstSilentRow;
        for (int c = 0; c < count; c++)
        {
            var cells = sums.AsSpan(c * Rows, Rows);
            var column = block.AsSpan(c * Rows, Rows);
            for (int row = 0; row < silentFrom; row++)
            {
                if (sharpen)
                    column[row] = cells[row] > 1e-12f ? Spectrogram.LevelOf(10 * Math.Log10(cells[row])) : (byte)0;
                else
                    column[row] = Spectrogram.LevelOf(cells[row]);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        _spectrogram.Publish(chunk, count);
    }

    /// <summary>Widens the samples needed at <paramref name="halvings"/> to take in <paramref name="from"/> up to <paramref name="to"/>.</summary>
    private static void Need(Span<long> start, Span<long> end, int halvings, long from, long to)
    {
        if (end[halvings] <= start[halvings])
        {
            start[halvings] = from;
            end[halvings] = to;
            return;
        }

        start[halvings] = Math.Min(start[halvings], from);
        end[halvings] = Math.Max(end[halvings], to);
    }

    /// <summary>Column <paramref name="column"/>'s window of <paramref name="samples"/>, whose first is half a window before sample <paramref name="origin"/>.</summary>
    private static ReadOnlySpan<float> Frame(float[] samples, long origin, SpectrogramTier tier, int column) =>
        samples.AsSpan((int)(((long)column * tier.Hop) - origin), tier.Window);

    /// <summary>Adds a frame's levels to its column, each row's times how much of the row is the band's.</summary>
    private static void Plain(SpectrogramTier tier, float[] power, int column, int count, double offset, float[] levels, float[] sums)
    {
        if ((uint)column >= (uint)count)
            return;

        tier.Map.Map(power, levels, tier.FirstRow, tier.LastRow, offset);
        var cells = sums.AsSpan(column * Rows, Rows);
        var weight = tier.RowWeight;
        for (int row = tier.FirstRow; row <= tier.LastRow; row++)
            cells[row] += weight[row] * levels[row];
    }

    /// <summary>
    /// Adds a frame's power to the cells it's really in: each bin's over the rows and columns around where it points
    /// (see the class's remarks).
    /// </summary>
    /// <param name="before">The frame <see cref="SharpStride"/> columns before, and <paramref name="after"/> the one as many after.</param>
    /// <param name="column">The frame's column, counted from the chunk's first; it may be outside the chunk.</param>
    /// <param name="scale">What a bin's power is multiplied by for a full-scale sine's to add up to 1.</param>
    private static void Sharpened(SpectrogramTier tier, Points before, Points frame, Points after, int column, int count, float scale, float[] sums)
    {
        int half = tier.Window / 2;
        float top = tier.RowOfBinOne + ((float)SpectrogramRows.RowsPerLog * MathF.Log(half));
        var weights = tier.RowWeight;
        float[] power = frame.Power, rows = frame.Rows, when = frame.When, whenBefore = before.When, whenAfter = after.When;

        // A frame stands for as many columns as there are between frames. A column is from half a column before its
        // time to half after, and times are counted from the frame's own column, so a cell's sum is the same to the
        // last bit whichever chunk's column numbers it's worked out in.
        scale *= SharpStride;
        const float OwnEarly = 0.5f - (SharpStride / 2f), OwnLate = 0.5f + (SharpStride / 2f);
        for (int k = tier.FirstBin; k <= tier.LastBin; k++)
        {
            if (power[k] <= 0)
                continue;

            // The bed: a little of it where the bin itself is, over the frame's own columns.
            if (tier.BinWeight[k] > 0)
                Put(sums, column, count, OwnEarly, OwnLate, tier.BinLow[k], tier.BinHigh[k], power[k] * scale * tier.BinWeight[k] * Bed);

            // Up the rows: between halfway to where the bin below points and halfway to where the bin above does, and
            // at least a row.
            float at = rows[k];
            float below = k > 1 ? (rows[k - 1] + at) / 2 : at;
            float above = k < half ? (rows[k + 1] + at) / 2 : top;
            float low = Math.Min(at, Math.Min(below, above));
            float high = Math.Max(at, Math.Max(below, above));
            if (high - low < 1)
            {
                low = at - 0.5f;
                high = at + 0.5f;
            }

            if (high <= 0 || low >= Rows)
                continue;
            float weight = weights[Math.Clamp((int)MathF.Floor(at), 0, Rows - 1)];
            if (weight <= 0)
                continue;

            // Along the columns, likewise, with the frames before and after, and at least a column.
            float time = when[k];
            float sooner = (whenBefore[k] - SharpStride + time) / 2;
            float later = (whenAfter[k] + SharpStride + time) / 2;
            float early = Math.Min(time, Math.Min(sooner, later)) + 0.5f;
            float late = Math.Max(time, Math.Max(sooner, later)) + 0.5f;
            if (late - early < 1)
            {
                early = time;
                late = time + 1;
            }

            Put(sums, column, count, early, late, low, high, power[k] * scale * weight);
        }
    }

    /// <summary>
    /// Adds <paramref name="power"/> evenly over the rows from <paramref name="low"/> to <paramref name="high"/> and the
    /// columns from <paramref name="early"/> to <paramref name="late"/>, counted from <paramref name="column"/>'s start.
    /// </summary>
    private static void Put(float[] sums, int column, int count, float early, float late, float low, float high, float power)
    {
        int firstColumn = (int)MathF.Floor(early), lastColumn = (int)MathF.Ceiling(late) - 1;
        if (column + lastColumn < 0 || column + firstColumn >= count)
            return;

        float density = power / ((high - low) * (late - early));
        int firstRow = Math.Max(0, (int)MathF.Floor(low));
        int lastRow = Math.Min(Rows - 1, (int)MathF.Ceiling(high) - 1);
        for (int j = firstColumn; j <= lastColumn; j++)
        {
            int into = column + j;
            float share = (Math.Min(late, j + 1) - Math.Max(early, j)) * density;
            if ((uint)into >= (uint)count || share <= 0)
                continue;

            var cells = sums.AsSpan(into * Rows, Rows);
            for (int row = firstRow; row <= lastRow; row++)
                cells[row] += share * (Math.Min(high, row + 1) - Math.Max(low, row));
        }
    }

    /// <summary>A frame's bins as points on the picture: each one's power, and where it is up the rows and along the columns.</summary>
    private readonly struct Points(WorkerScratch scratch, int buffer, int bins)
    {
        public float[] Power { get; } = scratch.Floats(buffer, bins);

        /// <summary>Where each bin points up the rows (<see cref="SpectrogramRows.EdgeOf"/>).</summary>
        public float[] Rows { get; } = scratch.Floats(buffer + 1, bins);

        /// <summary>When each bin points to, in columns after the frame's own.</summary>
        public float[] When { get; } = scratch.Floats(buffer + 2, bins);

        public void Take(SpectrogramTier tier, ReassignedPair.Frame frame, int firstBin, int lastBin)
        {
            float perLog = (float)SpectrogramRows.RowsPerLog;
            float perHop = 1f / tier.Hop;
            for (int k = firstBin; k <= lastBin; k++)
            {
                Power[k] = frame.Power[k];
                Rows[k] = tier.RowOfBinOne + (perLog * MathF.Log(Math.Max(0.5f, k + frame.BinShift[k])));
                When[k] = frame.TimeShift[k] * perHop;
            }
        }
    }

    protected override void Complete(string? error) => _spectrogram.Finish(error);

    protected override void Failed(string error) => _spectrogram.Finish(error);

    protected override void Cancelled() => _spectrogram.Finish(_spectrogram.Error);
}
