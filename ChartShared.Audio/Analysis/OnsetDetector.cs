using ChartShared.Core.Charting.Analysis;

namespace ChartShared.Audio.Analysis;

/// <summary>
/// Finds onsets, where sounds start (charter-spec C14 item 1), by spectral flux: how much louder each part of the
/// spectrum gets from one moment to the next. One flux per band: the whole spectrum, and for drums the kick, snare and
/// cymbal bands, so each onset can sit near the pad it most likely belongs to.
/// </summary>
/// <remarks>
/// <para>A 1024-sample Hann window every 128 samples (about 3 ms) is taken to twelve bands an octave, log-compressed,
/// and compared with the frame two before, each band against the loudest of it and its neighbours there (so vibrato
/// isn't an onset). Peaks are picked once, each with how far it stands out from the flux around it; the sensitivity is a
/// threshold on that, so changing it needs nothing recomputed.</para>
/// <para>Each onset is then timed off the audio itself: the 32-sample block near the peak where the level rises most,
/// which places a sharp hit to within a millisecond whatever its level.</para>
/// </remarks>
public static class OnsetDetector
{
    public const int WindowSize = 1024;
    public const int Hop = 128;

    /// <summary>How many frames back each frame is compared with.</summary>
    public const int Lag = 2;

    /// <summary>The samples in each block of the level envelope that times onsets.</summary>
    public const int FineBlock = 32;

    /// <summary>The sensitivity the editor starts with.</summary>
    public const double DefaultSensitivity = 0.5;

    /// <summary>How many frames are computed together: about 12 s at 44.1 kHz.</summary>
    public const int ChunkFrames = 4096;

    /// <summary>How far an onset must stand out to count at <paramref name="sensitivity"/>, from 0 (fewest) to 1 (most).</summary>
    public static double ThresholdFor(double sensitivity) => 0.5 * Math.Pow(10, -1.5 * Math.Clamp(sensitivity, 0, 1));

    /// <summary>Finds <paramref name="input"/>'s onsets on <paramref name="workers"/>.</summary>
    /// <param name="drumBands">Find them in the kick, snare and cymbal bands too.</param>
    /// <param name="requested">The author is waiting on it, so it comes before other background work.</param>
    /// <returns>The onsets; cancelled by <paramref name="cancellationToken"/>; faulted with <see cref="IOException"/> when the audio can't be read.</returns>
    public static Task<OnsetAnalysis> Start(AnalysisInput input, bool drumBands, AnalysisWorkers workers, bool requested = false, CancellationToken cancellationToken = default) =>
        Start(input, drumBands, workers, requested, ChunkFrames, cancellationToken);

    internal static Task<OnsetAnalysis> Start(AnalysisInput input, bool drumBands, AnalysisWorkers workers, bool requested, int chunkFrames, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(workers);

        var job = new OnsetJob(input, drumBands, requested ? AnalysisPriority.Requested : AnalysisPriority.Background, chunkFrames);
        if (cancellationToken.CanBeCanceled)
            cancellationToken.Register(job.Cancel);
        workers.Submit(job);
        return job.Result;
    }

    /// <summary>Finds <paramref name="input"/>'s onsets on this thread.</summary>
    /// <exception cref="IOException">The audio can't be read.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static OnsetAnalysis Analyze(AnalysisInput input, bool drumBands, CancellationToken cancellationToken = default) =>
        Analyze(input, drumBands, ChunkFrames, cancellationToken);

    internal static OnsetAnalysis Analyze(AnalysisInput input, bool drumBands, int chunkFrames, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var job = new OnsetJob(input, drumBands, AnalysisPriority.Requested, chunkFrames);
        using (cancellationToken.Register(job.Cancel))
        {
            IMonoReader reader;
            try
            {
                reader = input.Open();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidDataException or NotSupportedException)
            {
                throw new IOException(ex.Message, ex);
            }

            using (reader)
            {
                var scratch = new WorkerScratch();
                int chunk;
                while ((chunk = job.ClaimNext()) >= 0)
                    job.Run(chunk, reader, scratch);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return job.Result.GetAwaiter().GetResult();
    }
}

/// <summary>The bands of one sample rate's 1024-point spectrum: twelve to the octave from 30 Hz to 16 kHz.</summary>
internal sealed class OnsetFilterbank
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, OnsetFilterbank> Banks = new();

    private OnsetFilterbank(int sampleRate)
    {
        double binHz = (double)sampleRate / OnsetDetector.WindowSize;
        var groups = new List<(int First, int Last)>();
        int current = int.MinValue;
        for (int k = 1; k <= OnsetDetector.WindowSize / 2; k++)
        {
            double hz = k * binHz;
            if (hz < 30 || hz > 16000)
                continue;
            int band = (int)Math.Floor(12 * Math.Log2(hz / 30));
            if (band != current)
            {
                groups.Add((k, k));
                current = band;
            }
            else
            {
                groups[^1] = (groups[^1].First, k);
            }
        }

        First = [.. groups.Select(g => g.First)];
        Last = [.. groups.Select(g => g.Last)];
        CentreHz = [.. groups.Select(g => (g.First + g.Last) / 2.0 * binHz)];
    }

    public int[] First { get; }

    public int[] Last { get; }

    public double[] CentreHz { get; }

    public int Count => First.Length;

    public static OnsetFilterbank For(int sampleRate) => Banks.GetOrAdd(sampleRate, rate => new OnsetFilterbank(rate));

    /// <summary>Whether band <paramref name="group"/> belongs to <paramref name="band"/> (charter-spec C14's drum bands).</summary>
    public bool IsIn(int group, OnsetBand band)
    {
        double hz = CentreHz[group];
        return band switch
        {
            OnsetBand.Full => true,
            OnsetBand.Kick => hz <= 120,
            OnsetBand.Snare => (hz >= 150 && hz <= 300) || (hz >= 2000 && hz <= 5000),
            OnsetBand.Cymbals => hz >= 6000,
            _ => false,
        };
    }
}

/// <summary>An analysis's onset fluxes and level envelope, as chunks for <see cref="AnalysisWorkers"/>.</summary>
internal sealed class OnsetJob : AnalysisJob
{
    /// <summary>The whole spectrum's bands are taken as <c>log(1 + this × magnitude)</c>, a full-scale sine's magnitude being 1.</summary>
    private const float FullCompression = 1000;

    /// <summary>The drum bands' compression, nearly linear: a hit's leakage into another band stays a few percent of it.</summary>
    private const float DrumCompression = 10;

    private readonly AnalysisPriority _priority;
    private readonly int _chunkFrames;
    private readonly int _chunkCount;
    private readonly OnsetBand[] _bands;
    private readonly bool[][] _membership;
    private readonly float[][] _flux;
    private readonly float[] _fine;
    private readonly OnsetFilterbank _bank;
    private readonly TaskCompletionSource<OnsetAnalysis> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _next = -1;

    public OnsetJob(AnalysisInput input, bool drumBands, AnalysisPriority priority, int chunkFrames)
        : base(input)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkFrames);
        _priority = priority;
        // Chunks start on even frames, so frames pair the same however the song is cut.
        _chunkFrames = chunkFrames + (chunkFrames % 2);
        FrameCount = (int)(Math.Max(0, input.LengthFrames) / OnsetDetector.Hop) + 1;
        _chunkCount = (FrameCount + _chunkFrames - 1) / _chunkFrames;
        _bands = drumBands ? [OnsetBand.Full, OnsetBand.Kick, OnsetBand.Snare, OnsetBand.Cymbals] : [OnsetBand.Full];
        _bank = OnsetFilterbank.For(input.SampleRate);
        _membership = [.. _bands.Select(b => Enumerable.Range(0, _bank.Count).Select(g => _bank.IsIn(g, b)).ToArray())];
        _flux = [.. _bands.Select(_ => new float[FrameCount])];
        _fine = new float[(int)((Math.Max(0, input.LengthFrames) + OnsetDetector.FineBlock - 1) / OnsetDetector.FineBlock)];
        Expect(_chunkCount);
    }

    public int FrameCount { get; }

    public Task<OnsetAnalysis> Result => _result.Task;

    public override AnalysisPriority Priority => _priority;

    public override int ClaimNext()
    {
        int chunk = Interlocked.Increment(ref _next);
        return chunk < _chunkCount ? chunk : -1;
    }

    protected override void RunChunk(int chunk, IMonoReader reader, WorkerScratch scratch, CancellationToken cancellationToken)
    {
        const int window = OnsetDetector.WindowSize;
        const int hop = OnsetDetector.Hop;
        const int lag = OnsetDetector.Lag;
        int first = chunk * _chunkFrames;
        int end = Math.Min(FrameCount, first + _chunkFrames);

        // Frames from `lag` before the first, for the comparison; each window from half a window before its centre.
        int from = first - lag;
        int frames = end - from;
        int length = ((frames - 1) * hop) + window;
        var samples = scratch.Samples(length);
        long start = ((long)from * hop) - (window / 2);
        WindowRead.Read(reader, start, samples.AsSpan(0, length));

        FillLevels(samples, start, first, end);

        var fft = scratch.Fft(window);
        int groups = _bank.Count;
        float norm = 2 / fft.WindowSum;
        Span<float> powerA = stackalloc float[(window / 2) + 1];
        Span<float> powerB = stackalloc float[(window / 2) + 1];

        // The compressed bands of the last lag + 1 frames, by frame number modulo lag + 1: compressed hard for the whole
        // spectrum, so soft onsets in a loud mix count, and gently for the drum bands, so a band's leakage from a loud hit
        // in another doesn't.
        var full = Ring(groups);
        var drums = _bands.Length > 1 ? Ring(groups) : null;

        for (int f = 0; f < frames; f += 2)
        {
            if ((f & 63) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            bool pair = f + 1 < frames;
            fft.Power(samples.AsSpan(f * hop, window), pair ? samples.AsSpan((f + 1) * hop, window) : [], powerA, powerB);
            Frame(from + f, powerA);
            if (pair)
                Frame(from + f + 1, powerB);
        }

        void Frame(int frame, ReadOnlySpan<float> power)
        {
            var nowFull = full[Mod(frame)];
            var nowDrums = drums?[Mod(frame)];
            for (int g = 0; g < groups; g++)
            {
                float sum = 0;
                for (int k = _bank.First[g]; k <= _bank.Last[g]; k++)
                    sum += MathF.Sqrt(power[k]);
                float magnitude = sum / (_bank.Last[g] - _bank.First[g] + 1) * norm;
                nowFull[g] = MathF.Log(1 + (FullCompression * magnitude));
                if (nowDrums is not null)
                    nowDrums[g] = MathF.Log(1 + (DrumCompression * magnitude));
            }

            if (frame < first)
                return;

            for (int b = 0; b < _bands.Length; b++)
            {
                var rings = _bands[b] == OnsetBand.Full ? full : drums!;
                _flux[b][frame] = Flux(rings[Mod(frame)], rings[Mod(frame - lag)], _membership[b]);
            }
        }
    }

    /// <summary>How much louder <paramref name="now"/> is than <paramref name="before"/>, band by band, each against the loudest of it and its neighbours.</summary>
    private static float Flux(float[] now, float[] before, bool[] member)
    {
        float flux = 0;
        for (int g = 0; g < now.Length; g++)
        {
            if (!member[g])
                continue;
            float reference = before[g];
            if (g > 0)
                reference = Math.Max(reference, before[g - 1]);
            if (g + 1 < now.Length)
                reference = Math.Max(reference, before[g + 1]);
            flux += Math.Max(0, now[g] - reference);
        }

        return flux;
    }

    private static float[][] Ring(int groups)
    {
        var ring = new float[OnsetDetector.Lag + 1][];
        for (int i = 0; i < ring.Length; i++)
            ring[i] = new float[groups];
        return ring;
    }

    protected override void Complete(string? error)
    {
        try
        {
            _result.TrySetResult(new OnsetAnalysis(Input.SampleRate, FrameCount, _bands, _flux, _fine, error));
        }
        catch (Exception ex)
        {
            _result.TrySetException(ex);
        }
    }

    protected override void Failed(string error) => _result.TrySetException(new IOException(error));

    protected override void Cancelled() => _result.TrySetCanceled();

    private static int Mod(int frame) => ((frame % (OnsetDetector.Lag + 1)) + OnsetDetector.Lag + 1) % (OnsetDetector.Lag + 1);

    /// <summary>The level (energy) of each 32-sample block whose samples are this chunk's frames' own.</summary>
    private void FillLevels(float[] samples, long start, int first, int end)
    {
        const int block = OnsetDetector.FineBlock;
        int fromBlock = first * (OnsetDetector.Hop / block);
        int toBlock = Math.Min(_fine.Length, end * (OnsetDetector.Hop / block));
        for (int j = fromBlock; j < toBlock; j++)
        {
            int offset = (int)(((long)j * block) - start);
            float energy = 0;
            for (int n = 0; n < block; n++)
            {
                float s = samples[offset + n];
                energy += s * s;
            }

            _fine[j] = energy;
        }
    }
}
