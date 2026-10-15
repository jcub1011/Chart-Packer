namespace ChartPacker.Audio.Analysis;

/// <summary>
/// Computes <see cref="Spectrogram"/>s on <see cref="AnalysisWorkers"/>: a 2048-sample Hann window every 256 samples,
/// two frames per FFT, mapped to <see cref="SpectrogramRows"/> (charter-spec C13).
/// </summary>
/// <remarks>
/// Columns are computed a chunk at a time, the chunk nearest the focus first, so the view fills in from where it is
/// outwards. Frames are paired by their column numbers, so a chunk's columns are the same whichever thread computes them
/// and however the song is cut into chunks.
/// </remarks>
public static class SpectrogramBuilder
{
    /// <summary>How many columns are computed together: about 1.5 s at 44.1 kHz.</summary>
    public const int ChunkColumns = 256;

    /// <summary>Starts computing <paramref name="input"/>'s spectrogram, from <paramref name="focusSeconds"/> outwards.</summary>
    public static Spectrogram Start(AnalysisInput input, AnalysisWorkers workers, double focusSeconds) =>
        Start(input, workers, focusSeconds, ChunkColumns);

    internal static Spectrogram Start(AnalysisInput input, AnalysisWorkers workers, double focusSeconds, int chunkColumns)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(workers);

        var spectrogram = new Spectrogram(input.SampleRate, input.LengthFrames, chunkColumns);
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

    /// <summary>How many columns are computed between publishing them and checking for cancellation.</summary>
    private const int PublishEvery = 32;

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
        const int window = Spectrogram.WindowSize;
        const int hop = Spectrogram.Hop;

        // Every column's window, from half a window before the first column's centre.
        int length = ((count - 1) * hop) + window;
        var samples = scratch.Samples(length);
        WindowRead.Read(reader, ((long)first * hop) - (window / 2), samples.AsSpan(0, length));

        var fft = scratch.Fft(window);
        var map = SpectrogramRowMap.For(_spectrogram.SampleRate);
        double offset = 20 * Math.Log10(2 / fft.WindowSum);
        Span<float> powerA = stackalloc float[(window / 2) + 1];
        Span<float> powerB = stackalloc float[(window / 2) + 1];

        // Chunks start on even columns, so columns pair the same however the song is cut into chunks.
        for (int c = 0; c < count; c += 2)
        {
            bool pair = c + 1 < count;
            var a = samples.AsSpan(c * hop, window);
            var b = pair ? samples.AsSpan((c + 1) * hop, window) : [];
            fft.Power(a, b, powerA, powerB);
            map.Map(powerA, block.AsSpan(c * Spectrogram.Rows, Spectrogram.Rows), offset);
            if (pair)
                map.Map(powerB, block.AsSpan((c + 1) * Spectrogram.Rows, Spectrogram.Rows), offset);

            int done = Math.Min(count, c + 2);
            if (done % PublishEvery == 0 || done == count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _spectrogram.Publish(chunk, done);
            }
        }
    }

    protected override void Complete(string? error) => _spectrogram.Finish(error);

    protected override void Failed(string error) => _spectrogram.Finish(error);

    protected override void Cancelled() => _spectrogram.Finish(_spectrogram.Error);
}
