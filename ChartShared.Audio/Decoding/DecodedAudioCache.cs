using System.Diagnostics.CodeAnalysis;
using ChartShared.Audio.Caching;
using ChartShared.Audio.Waveforms;

namespace ChartShared.Audio.Decoding;

/// <summary>
/// The stems of an open chart decoded once (<see cref="StemDecoder"/>), for the waveforms, spectrograms and onsets to read
/// rather than each decoding them again: their channels averaged, at their own rates, within a memory budget, the least
/// recently used released first. Nothing is written to disk. Thread-safe.
/// </summary>
/// <remarks>
/// <para>A stem is known by the file it was read from, with that file's length and last write time (<see cref="StemPeaksCache.KeyOf"/>),
/// so what's kept is never used for a file that has been saved or replaced since.</para>
/// <para>A stem counts at its full size from the moment its decode begins, and isn't released while it's decoded. One that's
/// released is still read by whatever has it already, and decoded again by what asks for it next.</para>
/// <para>The budget may be shared with what comes first (<see cref="SharedBytes"/>): the stems keep within what that leaves.</para>
/// <para>One cache belongs to one open chart; clearing it when the chart's audio changes, or closes, releases everything.</para>
/// </remarks>
public sealed class DecodedAudioCache : IDisposable
{
    /// <summary>The budget unless another is given: the memory budget setting's default, 512 MB.</summary>
    public const long DefaultBudgetBytes = 512L * 1024 * 1024;

    private readonly Lock _lock = new();
    private readonly SizedLru<StemPeaksKey, DecodedStem> _stems = new();
    private long _budget;
    private bool _disposed;

    public DecodedAudioCache(long budgetBytes = DefaultBudgetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budgetBytes);
        _budget = budgetBytes;
    }

    /// <summary>
    /// How much memory the stems may take, less what <see cref="SharedBytes"/> takes of it. Lowering it releases the
    /// least recently used at once.
    /// </summary>
    public long BudgetBytes
    {
        get
        {
            lock (_lock)
                return _budget;
        }

        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            lock (_lock)
                _budget = value;
            Trim();
        }
    }

    /// <summary>
    /// How much of the budget something else takes, first: the spectrograms, which are drawn, where the stems only save
    /// decoding again. Called from any thread, and never under a lock of the cache's own; <see cref="Trim"/> once it grows.
    /// </summary>
    public Func<long>? SharedBytes { get; set; }

    /// <summary>How much memory the stems kept and being decoded take.</summary>
    public long SizeBytes
    {
        get
        {
            lock (_lock)
                return _stems.Size;
        }
    }

    /// <summary>The stem kept under <paramref name="key"/>, decoded or being decoded, counted as just used.</summary>
    public bool TryGet(StemPeaksKey key, [MaybeNullWhen(false)] out DecodedStem stem)
    {
        lock (_lock)
        {
            if (!_disposed && _stems.TryGet(key, out stem))
                return true;
        }

        stem = null;
        return false;
    }

    /// <summary>Releases the least recently used stems decoded until they fit what the budget leaves them.</summary>
    public void Trim()
    {
        long available = Available();
        lock (_lock)
            _stems.MakeRoom(0, available, IsFinished);
    }

    /// <summary>Releases the stems decoded of files other than <paramref name="keys"/>: after the chart's tracks change.</summary>
    public void Retain(IReadOnlyCollection<StemPeaksKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        lock (_lock)
        {
            foreach (var stem in _stems.Values.Where(s => IsFinished(s) && !keys.Contains(s.Key)).ToList())
                _stems.Remove(stem.Key, out _);
        }
    }

    /// <summary>
    /// Releases every stem decoded. Those being decoded go when their decodes stop, and count until then: their memory is
    /// taken until they do.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            foreach (var stem in _stems.Values.Where(IsFinished).ToList())
                _stems.Remove(stem.Key, out _);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _stems.Clear();
        }
    }

    /// <summary>
    /// A stem of <paramref name="frames"/> at <paramref name="sampleRate"/> to be decoded under <paramref name="key"/>, made
    /// room for by releasing the least recently used decoded.
    /// </summary>
    /// <param name="filling">Stops the decode that will fill it.</param>
    /// <returns>
    /// The stem; or <see langword="null"/> when there's one under the key already, or it has no frames, or it doesn't fit
    /// what the budget leaves even with every stem decoded released, when none is.
    /// </returns>
    internal DecodedStem? TryBegin(StemPeaksKey key, long frames, int sampleRate, long chunkFrames, CancellationToken filling)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        if (frames <= 0)
            return null;

        long needed = frames * sizeof(float);
        long available = Available();
        lock (_lock)
        {
            if (_disposed || needed > available || _stems.TryPeek(key, out _) || !_stems.CanFit(needed, available, IsFinished))
                return null;

            _stems.MakeRoom(needed, available, IsFinished);
            var stem = new DecodedStem(key, sampleRate, frames, chunkFrames, filling);
            _stems.Add(key, stem, needed);
            return stem;
        }
    }

    /// <summary>
    /// Takes up a stem whose decode has finished (<see cref="DecodedStem.Finish"/>): one decoded whole counts at its size as
    /// decoded, and the others make room for it first; one that isn't is released.
    /// </summary>
    internal void Finished(DecodedStem stem)
    {
        long available = Available();
        lock (_lock)
        {
            if (!_stems.TryPeek(stem.Key, out var kept) || !ReferenceEquals(kept, stem))
                return;

            if (!stem.IsComplete)
            {
                _stems.Remove(stem.Key, out _);
                return;
            }

            // Longer than the decoder said, or the budget lowered meanwhile: the others go first. Whatever has it reads it
            // either way.
            _stems.Resize(stem.Key, stem.SizeBytes);
            _stems.MakeRoom(0, available, s => IsFinished(s) && !ReferenceEquals(s, stem));
            _stems.MakeRoom(0, available, IsFinished);
        }
    }

    private static bool IsFinished(DecodedStem stem) => stem.Completion.IsCompleted;

    /// <summary>What the budget leaves the stems, asked outside the lock.</summary>
    private long Available()
    {
        long shared = SharedBytes?.Invoke() ?? 0;
        lock (_lock)
            return Math.Max(0, _budget - shared);
    }
}
