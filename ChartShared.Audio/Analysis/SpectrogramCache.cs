using ChartShared.Audio.Caching;

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
    /// <summary>The <b>Spectrogram memory budget</b> setting's default: 512 MB.</summary>
    public const long DefaultBudgetBytes = 512L * 1024 * 1024;

    private readonly AnalysisWorkers _workers;
    private readonly Lock _lock = new();
    private readonly SizedLru<AnalysisKey, Spectrogram> _entries = new();
    private readonly Dictionary<Spectrogram, long> _used = [];
    private long _budget;
    private bool _sharpen;

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

    /// <summary>
    /// Whether the spectrograms started are sharpened (<see cref="Spectrogram.IsSharpened"/>); they aren't at first. Changing
    /// it releases every spectrogram, which were made the other way.
    /// </summary>
    public bool Sharpen
    {
        get
        {
            lock (_lock)
                return _sharpen;
        }

        set
        {
            lock (_lock)
            {
                if (_sharpen == value)
                    return;
                _sharpen = value;
            }

            Clear();
        }
    }

    /// <summary>How much memory the spectrograms kept take.</summary>
    public long SizeBytes
    {
        get
        {
            lock (_lock)
                return _entries.Size;
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
            if (_entries.TryGet(key, out var raced))
            {
                _used[raced] = Environment.TickCount64;
                raced.Focus(focusSeconds);
                return new SpectrogramResult(raced, raced.SizeBytes);
            }

            if (needed > _budget)
                return new SpectrogramResult(null, needed);
            if (!HasRoom(needed))
                return new SpectrogramResult(null, needed, IsCrowded: true);

            released = MakeRoom(needed);
            started = SpectrogramBuilder.Start(audio, _workers, focusSeconds, _sharpen);
            _entries.Add(key, started, started.SizeBytes);
            _used[started] = Environment.TickCount64;
        }

        Release(released);
        return new SpectrogramResult(started, needed);
    }

    /// <summary>The spectrogram kept under <paramref name="key"/>, counted as just used.</summary>
    public bool TryGet(AnalysisKey key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out Spectrogram spectrogram)
    {
        lock (_lock)
        {
            if (!_entries.TryGet(key, out spectrogram))
                return false;
            _used[spectrogram] = Environment.TickCount64;
            return true;
        }
    }

    /// <summary>Releases the spectrogram kept under <paramref name="key"/>, if there is one.</summary>
    public void Release(AnalysisKey key)
    {
        Spectrogram? released;
        lock (_lock)
        {
            if (_entries.Remove(key, out released))
                _used.Remove(released);
        }

        released?.Dispose();
    }

    /// <summary>Releases every spectrogram.</summary>
    public void Clear()
    {
        List<Spectrogram> released;
        lock (_lock)
        {
            released = _entries.Clear();
            _used.Clear();
        }

        Release(released);
    }

    public void Dispose() => Clear();

    private static void Release(List<Spectrogram> spectrograms)
    {
        foreach (var spectrogram in spectrograms)
            spectrogram.Dispose();
    }

    /// <summary>Whether <paramref name="needed"/> more fits once the spectrograms not used within <see cref="KeepInUse"/> are released.</summary>
    /// <remarks>
    /// Those used within it are the most recently used, so passing over them stops short of every one used later too, as
    /// releasing in order would.
    /// </remarks>
    private bool HasRoom(long needed)
    {
        if (KeepInUse <= TimeSpan.Zero)
            return _entries.CanFit(needed, _budget);

        long since = Environment.TickCount64 - (long)KeepInUse.TotalMilliseconds;
        return _entries.CanFit(needed, _budget, s => _used.GetValueOrDefault(s) < since);
    }

    /// <summary>Drops the least recently used until <paramref name="needed"/> more fits; returns them to release outside the lock.</summary>
    private List<Spectrogram> MakeRoom(long needed)
    {
        var released = _entries.MakeRoom(needed, _budget);
        foreach (var spectrogram in released)
            _used.Remove(spectrogram);
        return released;
    }
}
