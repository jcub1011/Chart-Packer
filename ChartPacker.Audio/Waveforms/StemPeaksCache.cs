using System.Diagnostics.CodeAnalysis;
using ChartPacker.Core.Scanning;

namespace ChartPacker.Audio.Waveforms;

/// <summary>Which file a track's peaks were read from, as it was then: see <see cref="StemPeaksCache.KeyOf"/>.</summary>
public readonly record struct StemPeaksKey(string FilePath, string StoredName, long Length, DateTime LastWriteTimeUtc);

/// <summary>
/// Tracks' peaks, kept after their song is no longer shown so that showing it again needn't read its audio.
/// The least recently used are dropped once they take more than the capacity. Thread-safe.
/// </summary>
/// <remarks>
/// A track is known by the file its audio is read from, with that file's length and last write time, so
/// peaks are never used for a file that has been saved or replaced since.
/// </remarks>
public sealed class StemPeaksCache
{
    /// <summary>The cache the app shares: about the last ten songs of several stems, read at <see cref="WaveformResolution.High"/>.</summary>
    public static StemPeaksCache Shared { get; } = new(128L * 1024 * 1024);

    private readonly Lock _lock = new();
    private readonly Dictionary<StemPeaksKey, LinkedListNode<(StemPeaksKey Key, StemPeaks Peaks)>> _entries = [];

    // Most recently used first.
    private readonly LinkedList<(StemPeaksKey Key, StemPeaks Peaks)> _order = [];
    private long _size;

    public StemPeaksCache(long capacityBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacityBytes);
        CapacityBytes = capacityBytes;
    }

    public long CapacityBytes { get; }

    /// <summary>How much the peaks held take.</summary>
    public long SizeBytes
    {
        get
        {
            lock (_lock)
                return _size;
        }
    }

    /// <summary>
    /// The key for <paramref name="track"/> of <paramref name="entry"/> as its file is now, or <see langword="null"/>
    /// when the file can't be found. Take it before reading the track, so what's read is at least as new.
    /// </summary>
    public static StemPeaksKey? KeyOf(SongEntry? entry, SongTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);

        string? path = track.StagedSourcePath
            ?? (entry is null ? null : entry.Kind == SongEntryKind.SngFile ? entry.FullPath : Path.Combine(entry.FullPath, track.StoredName));
        if (path is null)
            return null;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
                return null;
            return new StemPeaksKey(file.FullName, track.StoredName, file.Length, file.LastWriteTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public bool TryGet(StemPeaksKey key, [MaybeNullWhen(false)] out StemPeaks peaks)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                peaks = node.Value.Peaks;
                return true;
            }
        }

        peaks = null;
        return false;
    }

    /// <summary>
    /// The peaks kept for <paramref name="key"/>, if they were read at <paramref name="resolution"/>: those read at another
    /// aren't used, and are replaced once the track is read again.
    /// </summary>
    public bool TryGet(StemPeaksKey key, WaveformResolution resolution, [MaybeNullWhen(false)] out StemPeaks peaks)
    {
        if (TryGet(key, out peaks) && peaks.Resolution == resolution)
            return true;

        peaks = null;
        return false;
    }

    /// <summary>
    /// The peaks kept for <paramref name="key"/>, if they were read at <paramref name="resolution"/>, and by channel when
    /// <paramref name="needChannels"/> (<see cref="StemPeaks.HasChannels"/>).
    /// </summary>
    public bool TryGet(StemPeaksKey key, WaveformResolution resolution, bool needChannels, [MaybeNullWhen(false)] out StemPeaks peaks)
    {
        if (TryGet(key, resolution, out peaks) && (!needChannels || peaks.HasChannels))
            return true;

        peaks = null;
        return false;
    }

    /// <summary>Keeps <paramref name="peaks"/> for <paramref name="key"/>, in place of any it had. Peaks larger than the whole cache aren't kept.</summary>
    public void Add(StemPeaksKey key, StemPeaks peaks)
    {
        ArgumentNullException.ThrowIfNull(peaks);

        lock (_lock)
        {
            if (_entries.Remove(key, out var old))
            {
                _order.Remove(old);
                _size -= old.Value.Peaks.SizeBytes;
            }

            if (peaks.SizeBytes > CapacityBytes)
                return;

            _entries[key] = _order.AddFirst((key, peaks));
            _size += peaks.SizeBytes;

            while (_size > CapacityBytes && _order.Last is { } last)
            {
                _order.RemoveLast();
                _entries.Remove(last.Value.Key);
                _size -= last.Value.Peaks.SizeBytes;
            }
        }
    }
}
