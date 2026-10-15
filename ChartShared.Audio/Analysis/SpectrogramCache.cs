namespace ChartShared.Audio.Analysis;

/// <summary>What <see cref="SpectrogramCache.GetOrStart"/> gave.</summary>
/// <param name="Spectrogram">
/// The spectrogram, or <see langword="null"/> when it alone needs more than the whole budget, or when making room for it
/// would release one still in use (<paramref name="IsCrowded"/>).
/// </param>
/// <param name="NeededBytes">How much memory it takes.</param>
/// <param name="IsCrowded">It would fit, but only by releasing a spectrogram used within <see cref="SpectrogramCache.KeepInUse"/>.</param>
public readonly record struct SpectrogramResult(Spectrogram? Spectrogram, long NeededBytes, bool IsCrowded = false)
{
    public bool IsOverBudget => Spectrogram is null;
}

/// <summary>
/// The spectrograms of an open chart's audio, kept in memory within a budget (charter-spec C13 item 3): every stem's
/// together, the least recently used released first. Nothing is ever written to disk.
/// </summary>
/// <remarks>
/// A spectrogram counts at its full size from the moment it's started, so the budget holds while it computes. One
/// cache belongs to one open chart; clearing it when the chart closes releases everything. Thread-safe.
/// </remarks>
public sealed class SpectrogramCache : IDisposable
{
    /// <summary>The <b>Spectrogram memory budget</b> setting's default: 256 MB.</summary>
    public const long DefaultBudgetBytes = 256L * 1024 * 1024;

    private readonly AnalysisWorkers _workers;
    private readonly Lock _lock = new();
    private readonly Dictionary<AnalysisKey, LinkedListNode<(AnalysisKey Key, Spectrogram Spectrogram)>> _entries = [];
    private readonly LinkedList<(AnalysisKey Key, Spectrogram Spectrogram)> _recency = new();
    private readonly Dictionary<Spectrogram, long> _used = [];
    private long _budget;
    private long _size;

    public SpectrogramCache(AnalysisWorkers workers, long budgetBytes = DefaultBudgetBytes)
    {
        ArgumentNullException.ThrowIfNull(workers);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budgetBytes);
        _workers = workers;
        _budget = budgetBytes;
    }

    /// <summary>How much memory every spectrogram may take together. Lowering it releases the least recently used at once.</summary>
    public long BudgetBytes
    {
        get
        {
            lock (_lock)
                return _budget;
        }

        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            List<Spectrogram> released;
            lock (_lock)
            {
                _budget = value;
                released = MakeRoom(0);
            }

            Release(released);
        }
    }

    /// <summary>
    /// How long a spectrogram stays safe from being released to make room for another after it's used: those on screen
    /// are used every frame, so two lanes that don't both fit don't take turns releasing each other. Zero by default.
    /// Lowering the budget releases them regardless.
    /// </summary>
    public TimeSpan KeepInUse { get; set; }

    /// <summary>How much memory the spectrograms kept take.</summary>
    public long SizeBytes
    {
        get
        {
            lock (_lock)
                return _size;
        }
    }

    /// <summary>
    /// The spectrogram kept under <paramref name="key"/>, focused on <paramref name="focusSeconds"/>, or a new one of
    /// <paramref name="input"/>, made room for by releasing the least recently used.
    /// </summary>
    /// <param name="input">Made only when there's no spectrogram under <paramref name="key"/>.</param>
    public SpectrogramResult GetOrStart(AnalysisKey key, Func<AnalysisInput> input, double focusSeconds)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (TryGet(key, out var kept))
        {
            kept.Focus(focusSeconds);
            return new SpectrogramResult(kept, kept.SizeBytes);
        }

        var audio = input();
        long needed = ((long)(Math.Max(0, audio.LengthFrames) / Spectrogram.Hop) + 1) * Spectrogram.Rows;
        List<Spectrogram> released;
        Spectrogram started;
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var raced))
            {
                Touch(raced);
                raced.Value.Spectrogram.Focus(focusSeconds);
                return new SpectrogramResult(raced.Value.Spectrogram, raced.Value.Spectrogram.SizeBytes);
            }

            if (needed > _budget)
                return new SpectrogramResult(null, needed);
            if (!HasRoom(needed))
                return new SpectrogramResult(null, needed, IsCrowded: true);

            released = MakeRoom(needed);
            started = SpectrogramBuilder.Start(audio, _workers, focusSeconds);
            _entries[key] = _recency.AddFirst((key, started));
            _used[started] = Environment.TickCount64;
            _size += started.SizeBytes;
        }

        Release(released);
        return new SpectrogramResult(started, needed);
    }

    /// <summary>The spectrogram kept under <paramref name="key"/>, counted as just used.</summary>
    public bool TryGet(AnalysisKey key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out Spectrogram spectrogram)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var node))
            {
                Touch(node);
                spectrogram = node.Value.Spectrogram;
                return true;
            }
        }

        spectrogram = null;
        return false;
    }

    /// <summary>Releases the spectrogram kept under <paramref name="key"/>, if there is one.</summary>
    public void Release(AnalysisKey key)
    {
        Spectrogram? released = null;
        lock (_lock)
        {
            if (_entries.Remove(key, out var node))
            {
                _recency.Remove(node);
                _used.Remove(node.Value.Spectrogram);
                _size -= node.Value.Spectrogram.SizeBytes;
                released = node.Value.Spectrogram;
            }
        }

        released?.Dispose();
    }

    /// <summary>Releases every spectrogram.</summary>
    public void Clear()
    {
        List<Spectrogram> released;
        lock (_lock)
        {
            released = [.. _recency.Select(e => e.Spectrogram)];
            _entries.Clear();
            _recency.Clear();
            _used.Clear();
            _size = 0;
        }

        Release(released);
    }

    public void Dispose() => Clear();

    private static void Release(List<Spectrogram> spectrograms)
    {
        foreach (var spectrogram in spectrograms)
            spectrogram.Dispose();
    }

    private void Touch(LinkedListNode<(AnalysisKey Key, Spectrogram Spectrogram)> node)
    {
        _recency.Remove(node);
        _recency.AddFirst(node);
        _used[node.Value.Spectrogram] = Environment.TickCount64;
    }

    /// <summary>Whether <paramref name="needed"/> more fits once the spectrograms not used within <see cref="KeepInUse"/> are released.</summary>
    private bool HasRoom(long needed)
    {
        long size = _size;
        long since = Environment.TickCount64 - (long)KeepInUse.TotalMilliseconds;
        for (var node = _recency.Last; node is not null && size + needed > _budget; node = node.Previous)
        {
            if (KeepInUse > TimeSpan.Zero && _used.GetValueOrDefault(node.Value.Spectrogram) >= since)
                return false;
            size -= node.Value.Spectrogram.SizeBytes;
        }

        return size + needed <= _budget;
    }

    /// <summary>Drops the least recently used until <paramref name="needed"/> more fits; returns them to release outside the lock.</summary>
    private List<Spectrogram> MakeRoom(long needed)
    {
        var released = new List<Spectrogram>();
        while (_size + needed > _budget && _recency.Last is { } oldest)
        {
            _recency.RemoveLast();
            _entries.Remove(oldest.Value.Key);
            _used.Remove(oldest.Value.Spectrogram);
            _size -= oldest.Value.Spectrogram.SizeBytes;
            released.Add(oldest.Value.Spectrogram);
        }

        return released;
    }
}
