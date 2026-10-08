namespace ChartPacker.Audio.Analysis;

/// <summary>How soon an analysis job's next chunk is wanted.</summary>
internal enum AnalysisPriority
{
    /// <summary>Spectrogram near what's on screen.</summary>
    Focused,

    /// <summary>Work the author asked for and is waiting on.</summary>
    Requested,

    /// <summary>Everything else.</summary>
    Background,
}

/// <summary>
/// The threads that analyse audio for an open chart (charter-spec §4.4): spectrograms, onsets. They run below normal
/// priority, so the interface and playback come first, and take the most wanted chunk of any job next.
/// </summary>
/// <remarks>
/// Each thread keeps a reader per job it has worked on, so chunks read one after another need no new decoder. Disposing
/// stops the threads, cancels every job, and disposes the readers; dispose this before the audio the jobs read.
/// </remarks>
public sealed class AnalysisWorkers : IDisposable
{
    private readonly object _gate = new();
    private readonly List<AnalysisJob> _jobs = [];
    private readonly Thread[] _threads;
    private bool _stopping;
    private long _submitted;

    /// <param name="count">How many threads; 0 for one fewer than the machine's cores, leaving one for the interface.</param>
    public AnalysisWorkers(int count = 0, ThreadPriority priority = ThreadPriority.BelowNormal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0)
            count = DefaultCount;

        _threads = new Thread[count];
        for (int i = 0; i < count; i++)
        {
            _threads[i] = new Thread(Work) { IsBackground = true, Priority = priority, Name = $"Audio analysis {i + 1}" };
            _threads[i].Start();
        }
    }

    public static int DefaultCount { get; } = Math.Max(1, Environment.ProcessorCount - 1);

    public int Count => _threads.Length;

    public void Dispose()
    {
        List<AnalysisJob> jobs;
        lock (_gate)
        {
            if (_stopping)
                return;
            _stopping = true;
            jobs = [.. _jobs];
            _jobs.Clear();
            Monitor.PulseAll(_gate);
        }

        foreach (var job in jobs)
            job.Cancel();
        foreach (var thread in _threads)
            thread.Join(TimeSpan.FromSeconds(5));
    }

    internal void Submit(AnalysisJob job)
    {
        lock (_gate)
        {
            if (_stopping)
            {
                job.Cancel();
                return;
            }

            job.Order = _submitted++;
            _jobs.Add(job);
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Tells the threads a job's priority or work changed.</summary>
    internal void Wake()
    {
        lock (_gate)
            Monitor.PulseAll(_gate);
    }

    private void Work()
    {
        var scratch = new WorkerScratch();
        var readers = new Dictionary<AnalysisJob, IMonoReader>();
        try
        {
            while (Next(readers) is var (job, chunk))
            {
                if (!readers.TryGetValue(job, out var reader))
                {
                    try
                    {
                        reader = job.Input.Open();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ObjectDisposedException)
                    {
                        job.Fail(ex.Message);
                        continue;
                    }

                    readers[job] = reader;
                }

                job.Run(chunk, reader, scratch);
            }
        }
        finally
        {
            foreach (var reader in readers.Values)
                reader.Dispose();
        }
    }

    /// <summary>The next chunk to work on, waiting for one; <see langword="null"/> once stopping.</summary>
    private (AnalysisJob Job, int Chunk)? Next(Dictionary<AnalysisJob, IMonoReader> readers)
    {
        lock (_gate)
        {
            while (true)
            {
                if (_stopping)
                    return null;

                _jobs.RemoveAll(j => j.IsFinished);
                foreach (var job in readers.Keys.Where(j => j.IsFinished).ToList())
                {
                    readers[job].Dispose();
                    readers.Remove(job);
                }

                foreach (var job in _jobs.OrderBy(j => j.Priority).ThenByDescending(j => j.FocusStamp).ThenBy(j => j.Order))
                {
                    int chunk = job.ClaimNext();
                    if (chunk >= 0)
                        return (job, chunk);
                }

                Monitor.Wait(_gate, TimeSpan.FromSeconds(1));
            }
        }
    }
}

/// <summary>Buffers a worker thread reuses from chunk to chunk.</summary>
internal sealed class WorkerScratch
{
    private readonly Dictionary<int, RealFftPair> _ffts = [];
    private float[] _samples = [];

    /// <summary>A buffer of at least <paramref name="length"/> samples; its contents are left over.</summary>
    public float[] Samples(int length)
    {
        if (_samples.Length < length)
            _samples = new float[length];
        return _samples;
    }

    public RealFftPair Fft(int size)
    {
        if (!_ffts.TryGetValue(size, out var fft))
            _ffts[size] = fft = new RealFftPair(size);
        return fft;
    }
}

/// <summary>
/// Work for <see cref="AnalysisWorkers"/>, cut into chunks any thread can do in any order. The last chunk done finishes
/// the job, once.
/// </summary>
internal abstract class AnalysisJob
{
    private readonly CancellationTokenSource _cancel = new();
    private int _remaining = -1;
    private int _finished;
    private string? _error;

    protected AnalysisJob(AnalysisInput input) => Input = input;

    public AnalysisInput Input { get; }

    /// <summary>Set by the workers: the order jobs were handed over in.</summary>
    public long Order { get; set; }

    /// <summary>The latest of these is preferred, among jobs of one priority.</summary>
    public long FocusStamp { get; set; }

    public abstract AnalysisPriority Priority { get; }

    /// <summary>It's done, failed or cancelled: no more chunks will be worked on.</summary>
    public bool IsFinished => Volatile.Read(ref _finished) != 0;

    protected CancellationToken Token => _cancel.Token;

    /// <summary>Claims a chunk to work on; -1 when there's none left to claim.</summary>
    public abstract int ClaimNext();

    /// <summary>Sets how many chunks must be done before <see cref="Complete"/>; call it once, before handing the job over.</summary>
    protected void Expect(int chunks) => _remaining = chunks;

    /// <summary>Works on <paramref name="chunk"/>, then finishes the job if it was the last.</summary>
    public void Run(int chunk, IMonoReader reader, WorkerScratch scratch)
    {
        if (IsFinished)
            return;
        try
        {
            RunChunk(chunk, reader, scratch, Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ObjectDisposedException)
        {
            Fail(ex.Message);
            return;
        }

        if (reader.Error is { } error)
            Interlocked.CompareExchange(ref _error, error, null);
        if (Interlocked.Decrement(ref _remaining) == 0 && Interlocked.Exchange(ref _finished, 1) == 0)
            Complete(Volatile.Read(ref _error));
    }

    public void Fail(string error)
    {
        if (Interlocked.Exchange(ref _finished, 1) == 0)
            Failed(error);
    }

    /// <summary>Stops the job: chunks being worked on stop at their next check, and no more are started.</summary>
    public void Cancel()
    {
        _cancel.Cancel();
        if (Interlocked.Exchange(ref _finished, 1) == 0)
            Cancelled();
    }

    protected abstract void RunChunk(int chunk, IMonoReader reader, WorkerScratch scratch, CancellationToken cancellationToken);

    /// <summary>Every chunk is done. <paramref name="error"/> says why some audio was silent, if it was.</summary>
    protected abstract void Complete(string? error);

    protected abstract void Failed(string error);

    protected abstract void Cancelled();
}
