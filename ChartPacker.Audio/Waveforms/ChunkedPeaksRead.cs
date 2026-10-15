using ChartPacker.Audio.Playback;

namespace ChartPacker.Audio.Waveforms;

/// <summary>
/// Reads the peaks of a song's opened tracks with <see cref="StemPeaksReader"/>: every chunk of every track side by side,
/// each from a source of its own, so a long song isn't decoded on one thread.
/// </summary>
/// <remarks>
/// The tracks' first chunks go first, the longest track's first of all, so each track fills in from its start at the same
/// pace. The first chunk is read from the source opened for the track; the rest open their own. A chunk that can't be read
/// takes its whole track with it, and the other tracks carry on.
/// </remarks>
public static class ChunkedPeaksRead
{
    /// <summary>How many chunks are read at once: a core is left for the interface and playback.</summary>
    public static int Parallelism { get; } = Math.Max(1, Environment.ProcessorCount - 1);

    /// <summary>Reads the peaks of each of <paramref name="audio"/>'s tracks that opened.</summary>
    /// <param name="audio">The song's tracks, opened; it must stay open until this returns.</param>
    /// <param name="resolution">How finely to read them.</param>
    /// <param name="completed">
    /// Handed a track's peaks once all of it is read, on a reading thread; never for a track that failed, nor after
    /// <paramref name="failed"/> for it.
    /// </param>
    /// <param name="failed">Told, once, on a reading thread, that a track can't be read; what was read of it is no good.</param>
    /// <param name="progress">Handed a track's peaks read so far, as <see cref="StemPeaksReader"/> hands them, until it fails.</param>
    /// <param name="progressInterval">How often <paramref name="progress"/> is called for each track at most.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <param name="channels">Read the left and right channels' peaks too (<see cref="StemPeaks.HasChannels"/>).</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static void Read(
        SongAudio audio,
        WaveformResolution resolution,
        Action<OpenedTrack, StemPeaks> completed,
        Action<OpenedTrack>? failed = null,
        Action<OpenedTrack, StemPeaks>? progress = null,
        TimeSpan progressInterval = default,
        CancellationToken cancellationToken = default,
        bool channels = false)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(completed);

        var reads = audio.Tracks
            .Where(t => t.Source is not null)
            .OrderByDescending(t => t.Source!.LengthFrames)
            .Select(opened => new TrackRead(opened, progress is null ? null : partial => progress(opened, partial), progressInterval, resolution, channels))
            .ToList();
        var chunks = reads
            .SelectMany(read => Enumerable.Range(0, read.Reader.ChunkCount).Select(index => (Read: read, Index: index)))
            .OrderBy(chunk => chunk.Index)
            .ToList();

        var options = new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Parallelism };
        Parallel.ForEach(chunks, options, chunk =>
        {
            var read = chunk.Read;
            try
            {
                if (!read.HasFailed)
                {
                    if (chunk.Index == 0)
                    {
                        read.Reader.ReadChunk(0, read.Opened.Source!, cancellationToken);
                    }
                    else
                    {
                        using var source = audio.OpenSource(read.Opened.Track);
                        read.Reader.ReadChunk(chunk.Index, source, cancellationToken);
                    }
                }

                read.EndChunk(() => completed(read.Opened, read.Reader.Finish()));
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                // The read was stopped; the other tracks carry on.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
            {
                read.Fail(() => failed?.Invoke(read.Opened));
            }
        });
    }

    /// <summary>
    /// One track's peaks, read in chunks on several threads: whether a chunk's read failed, which takes the
    /// whole track's peaks with it, and how many chunks are left before it's done.
    /// </summary>
    private sealed class TrackRead
    {
        // Held while the track's peaks are handed on or it's failed, so that none are handed on once it has.
        private readonly Lock _lock = new();
        private bool _hasFailed;
        private int _remaining;

        /// <param name="progress">Handed the peaks read so far, unless the read has failed.</param>
        public TrackRead(OpenedTrack opened, Action<StemPeaks>? progress, TimeSpan progressInterval, WaveformResolution resolution, bool channels)
        {
            Action<StemPeaks>? report = null;
            if (progress is { } handOn)
            {
                report = partial =>
                {
                    lock (_lock)
                    {
                        if (!_hasFailed)
                            handOn(partial);
                    }
                };
            }

            Opened = opened;
            Reader = new StemPeaksReader(opened.Track.StoredName, opened.Source!, report, progressInterval, resolution: resolution, channels: channels);
            _remaining = Reader.ChunkCount;
        }

        public OpenedTrack Opened { get; }

        public StemPeaksReader Reader { get; }

        public bool HasFailed
        {
            get
            {
                lock (_lock)
                    return _hasFailed;
            }
        }

        /// <summary>Marks the read failed, calling <paramref name="fail"/> the first time.</summary>
        public void Fail(Action fail)
        {
            lock (_lock)
            {
                if (_hasFailed)
                    return;
                _hasFailed = true;
                fail();
            }
        }

        /// <summary>Counts a chunk as done with, calling <paramref name="finish"/> once they all are, unless the read has failed.</summary>
        public void EndChunk(Action finish)
        {
            if (Interlocked.Decrement(ref _remaining) != 0)
                return;

            lock (_lock)
            {
                if (!_hasFailed)
                    finish();
            }
        }
    }
}
