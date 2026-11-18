using System.Diagnostics;
using System.Runtime.CompilerServices;
using ChartShared.Audio.Playback;
using ChartShared.Audio.Waveforms;
using ChartShared.Core.Scanning;

namespace ChartShared.Audio.Decoding;

/// <summary>
/// Decodes an open chart's stems once, up front, into a <see cref="DecodedAudioCache"/>, for the waveforms, spectrograms
/// and onsets to read from there rather than each decoding them again. Every chunk of every stem is decoded side by side,
/// each from a source of its own, the stems' first chunks first.
/// </summary>
/// <remarks>
/// <para>A stem is handed to what asks for it (<see cref="GetAsync"/>) as soon as its decode has begun, so a reader can follow
/// it as it fills (<see cref="DecodedStem.WhenReadable"/>), or wait for all of it (<see cref="DecodedStem.Completion"/>).</para>
/// <para>Only stems of one or two channels whose length is known are decoded, as many as fit the budget; any other is null,
/// and is read from its file as it would be without this. A stem released from the cache to make room is decoded again the
/// next time it's asked for.</para>
/// </remarks>
public sealed class StemDecoder : IDisposable
{
    /// <summary>How many frames a chunk reads from its source at a time.</summary>
    private const int ReadFrames = 4096;

    /// <summary>
    /// How long a chunk decodes before its start, and throws away, beyond the decoder's own pre-roll: Opus only settles
    /// back onto what a straight decode gives some time after a seek (within 1e-3 at once, exactly after 0.2 to 0.4 s, on
    /// the fixtures). So the seams between chunks don't show in what the analysis reads.
    /// </summary>
    private const int OverlapMilliseconds = 500;

    private readonly SongEntry? _entry;
    private readonly IReadOnlyList<SongTrack> _tracks;
    private readonly CancellationTokenSource _cancel = new();

    // Under the lock: the tracks a decode is under way for, each given its stem once its decode has begun, or null.
    private readonly Lock _lock = new();
    private readonly Dictionary<SongTrack, TaskCompletionSource<DecodedStem?>> _starting = [];
    private bool _disposed;

    /// <param name="cache">Where the stems are kept; the owner disposes it, after this.</param>
    public StemDecoder(SongEntry? entry, IReadOnlyList<SongTrack> tracks, DecodedAudioCache cache)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(cache);
        _entry = entry;
        _tracks = tracks;
        Cache = cache;
    }

    public DecodedAudioCache Cache { get; }

    /// <summary>
    /// Starts decoding every track in the background, but those the cache has already, and releases what it has of
    /// other files.
    /// </summary>
    public void Start()
    {
        if (_tracks.Count == 0)
            Cache.Retain([]);
        else
            Decode(_tracks, retain: true);
    }

    /// <summary>
    /// <paramref name="track"/>'s stem: kept, being decoded, or decoded now, which this starts. It's handed over once its
    /// decode has begun, and may still be being filled.
    /// </summary>
    /// <returns>The stem; or <see langword="null"/> when it isn't decoded here, so it's read from its file.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<DecodedStem?> GetAsync(SongTrack track, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);

        // A stem whose decode was stopped elsewhere (as the tracks changed) is begun again, once it's let go of.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Task<DecodedStem?>? pending;
            lock (_lock)
            {
                if (_disposed)
                    return null;
                pending = _starting.TryGetValue(track, out var starting) ? starting.Task : null;
            }

            if (pending is null)
            {
                if (StemPeaksCache.KeyOf(_entry, track) is not { } key)
                    return null;
                if (Cache.TryGet(key, out var kept) && !kept.IsAbandoned)
                    return kept;
                pending = Decode([track], retain: false)[track];
            }

            var stem = await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (stem is null || !stem.IsAbandoned)
                return stem;
            await stem.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>Stops the decodes under way; the stems they were filling aren't kept.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        _cancel.Cancel();
    }

    /// <summary>Decodes <paramref name="tracks"/> in the background, joining the decodes of those under way already.</summary>
    /// <param name="retain">Release what the cache has of files other than these first.</param>
    /// <returns>For each track, its stem once its decode has begun, or null.</returns>
    private Dictionary<SongTrack, Task<DecodedStem?>> Decode(IReadOnlyList<SongTrack> tracks, bool retain)
    {
        var tasks = new Dictionary<SongTrack, Task<DecodedStem?>>();
        var mine = new Dictionary<SongTrack, TaskCompletionSource<DecodedStem?>>();
        lock (_lock)
        {
            foreach (var track in tracks)
            {
                if (_disposed)
                {
                    tasks[track] = Task.FromResult<DecodedStem?>(null);
                    continue;
                }

                if (!_starting.TryGetValue(track, out var starting))
                {
                    starting = new TaskCompletionSource<DecodedStem?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _starting[track] = starting;
                    mine[track] = starting;
                }

                tasks[track] = starting.Task;
            }
        }

        if (mine.Count > 0)
        {
            var token = _cancel.Token;
            _ = Task.Run(() => Run(mine, retain, token), CancellationToken.None);
        }

        return tasks;
    }

    private void Run(Dictionary<SongTrack, TaskCompletionSource<DecodedStem?>> mine, bool retain, CancellationToken token)
    {
        var decoding = new List<(OpenedTrack Opened, DecodedStem Stem)>();
        SongAudio? audio = null;
        try
        {
            // Each key is taken before its file is read, so what's kept under it is never older than the file it names.
            var keys = mine.Keys.ToDictionary(t => t, t => StemPeaksCache.KeyOf(_entry, t));
            if (retain)
                Cache.Retain([.. keys.Values.OfType<StemPeaksKey>()]);

            var open = new List<SongTrack>();
            foreach (var (track, begun) in mine)
            {
                if (keys[track] is not { } key)
                {
                    begun.TrySetResult(null);
                    continue;
                }

                if (Cache.TryGet(key, out var kept))
                {
                    if (!kept.IsAbandoned)
                    {
                        begun.TrySetResult(kept);
                        continue;
                    }

                    // Stopped as the tracks changed, it goes in a moment, and is begun again here.
                    kept.Completion.Wait(token);
                }

                open.Add(track);
            }

            if (open.Count == 0)
                return;

            audio = SongAudio.Open(_entry, open, token);
            foreach (var opened in audio.Tracks.OrderByDescending(t => t.Source?.LengthFrames ?? 0))
            {
                var begun = mine[opened.Track];
                if (opened.Source is not { Channels: <= 2, LengthFrames: > 0 } source || keys[opened.Track] is not { } key)
                {
                    begun.TrySetResult(null);
                    continue;
                }

                long chunkFrames = (long)DecodedStem.DefaultChunkSeconds * source.SampleRate;
                if (Cache.TryBegin(key, source.LengthFrames, source.SampleRate, chunkFrames, token) is { } stem)
                {
                    decoding.Add((opened, stem));
                    begun.TrySetResult(stem);
                }
                else
                {
                    // Too big for what the budget leaves; or begun meanwhile, by another decode.
                    begun.TrySetResult(Cache.TryGet(key, out var raced) && !raced.IsAbandoned ? raced : null);
                }
            }

            DecodeChunks(audio, decoding, token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // Stopped: the tracks changed, or the chart closed.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            // Nothing is decoded; the waveforms and analysis read the files themselves, and say why they can't.
            Trace.TraceWarning($"Decoding the stems failed: {ex.Message}");
        }
        finally
        {
            // Whatever wasn't decoded whole, stopped or failed, isn't kept.
            foreach (var (_, stem) in decoding)
            {
                if (stem.Finish(whole: false))
                    Cache.Finished(stem);
            }

            lock (_lock)
            {
                foreach (var (track, begun) in mine)
                {
                    begun.TrySetResult(null);
                    if (_starting.TryGetValue(track, out var starting) && starting == begun)
                        _starting.Remove(track);
                }
            }

            audio?.Dispose();
        }
    }

    /// <summary>
    /// Decodes every chunk of <paramref name="decoding"/>, side by side. A chunk that can't be read takes its whole stem
    /// with it, and the other stems carry on.
    /// </summary>
    private void DecodeChunks(SongAudio audio, List<(OpenedTrack Opened, DecodedStem Stem)> decoding, CancellationToken token)
    {
        var remaining = decoding.ToDictionary(d => d.Stem, d => new StrongBox<int>(d.Stem.ChunkCount));
        var chunks = decoding
            .SelectMany(d => Enumerable.Range(0, d.Stem.ChunkCount).Select(index => (d.Opened, d.Stem, Index: index)))
            .OrderBy(chunk => chunk.Index)
            .ToList();

        var options = new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = ChunkedPeaksRead.Parallelism };
        Parallel.ForEach(chunks, options, chunk =>
        {
            var stem = chunk.Stem;
            if (stem.Completion.IsCompleted)
                return;

            try
            {
                if (chunk.Index == 0)
                {
                    // The first chunk reads from the source opened for the track, which is at its start.
                    DecodeChunk(stem, 0, chunk.Opened.Source!, token);
                }
                else
                {
                    using var source = audio.OpenSource(chunk.Opened.Track);
                    DecodeChunk(stem, chunk.Index, source, token);
                }

                if (Interlocked.Decrement(ref remaining[stem].Value) == 0 && stem.Finish(whole: true))
                    Cache.Finished(stem);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                // Stopped; the stem isn't kept.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
            {
                Trace.TraceWarning($"Decoding {chunk.Opened.Track.StoredName} failed: {ex.Message}");
                if (stem.Finish(whole: false))
                    Cache.Finished(stem);
            }
        });
    }

    /// <summary>
    /// Decodes chunk <paramref name="index"/> of <paramref name="stem"/> from <paramref name="source"/>, which nothing else
    /// reads: sought to <see cref="OverlapMilliseconds"/> before the chunk's start, but for the first, which is read from
    /// where the source is. The last reads on to the source's end. Damaged audio ends the chunk: what it read before
    /// stands, and the rest of it is silent.
    /// </summary>
    internal static void DecodeChunk(DecodedStem stem, int index, IPcmSource source, CancellationToken token = default)
    {
        var (from, to) = stem.ChunkRange(index);
        long end = index == stem.ChunkCount - 1 ? long.MaxValue : to;
        int channels = source.Channels;
        var buffer = new float[ReadFrames * channels];
        var averaged = new float[ReadFrames];
        long frame = from;
        try
        {
            if (index > 0)
                SeekBefore(source, from, (long)stem.SampleRate * OverlapMilliseconds / 1000, buffer, token);

            while (frame < end)
            {
                token.ThrowIfCancellationRequested();
                int wanted = (int)Math.Min(ReadFrames, end - frame);
                int frames = source.ReadFrames(buffer.AsSpan(0, wanted * channels));
                if (frames <= 0)
                    break;

                // Averaged as the waveform averages them (StemPeaksReader), so its peaks are the same read from either.
                for (int n = 0; n < frames; n++)
                {
                    float sum = 0;
                    for (int c = 0; c < channels; c++)
                        sum += buffer[(n * channels) + c];
                    averaged[n] = sum / channels;
                }

                stem.Write(index, frame, averaged.AsSpan(0, frames));
                frame += frames;
            }
        }
        catch (InvalidDataException)
        {
            // Damaged audio: what was read stands.
        }

        stem.EndChunk(index, frame);
    }

    /// <summary>
    /// Moves <paramref name="source"/> to <paramref name="frame"/> by decoding the <paramref name="overlap"/> frames before
    /// it, and throwing them away. Damaged audio there, which is the chunk before's, has it seek straight to the frame.
    /// </summary>
    private static void SeekBefore(IPcmSource source, long frame, long overlap, float[] buffer, CancellationToken token)
    {
        long at = Math.Max(0, frame - overlap);
        try
        {
            source.SeekToFrame(at);
            while (at < frame)
            {
                token.ThrowIfCancellationRequested();
                int read = source.ReadFrames(buffer.AsSpan(0, (int)Math.Min(ReadFrames, frame - at) * source.Channels));
                if (read <= 0)
                    return;
                at += read;
            }
        }
        catch (InvalidDataException)
        {
            source.SeekToFrame(frame);
        }
    }
}
