using ChartPacker.Audio.Playback;
using ChartPacker.Audio.Spectrum;
using ChartPacker.Audio.Waveforms;
using ChartPacker.Core.Merging;
using ChartPacker.Core.Scanning;

namespace ChartPacker.Audio.Alignment;

/// <summary>
/// A song's audio, read for lining up: how sharply its mix gets louder, <see cref="AudioAligner.FramesPerSecond"/> times a
/// second, so that its beats and hits stand out and how loud it is overall doesn't.
/// </summary>
public sealed class AlignmentAudio
{
    private AlignmentAudio(float[] onsets, double energy)
    {
        Onsets = onsets;
        Energy = energy;
    }

    /// <summary>How long it is, in seconds.</summary>
    public double Seconds => (double)Onsets.Length / AudioAligner.FramesPerSecond;

    // How sharply the mix gets louder in each frame, less its average, so that a quiet stretch counts against a match.
    internal float[] Onsets { get; }

    // The sum of the onsets' squares, for telling how alike two are.
    internal double Energy { get; }

    /// <summary>
    /// The audio of <paramref name="stems"/> mixed, each at full level, as <see cref="WaveformPeaks.Mix"/> mixes them: from the
    /// samples each is read at for mixing. <see langword="null"/> when there's nothing to mix, or it's all silence.
    /// </summary>
    public static AlignmentAudio? Of(IReadOnlyList<StemPeaks> stems)
    {
        ArgumentNullException.ThrowIfNull(stems);
        float[] mix = Mix(stems);
        if (mix.Length == 0)
            return null;

        float[] onsets = OnsetsOf(mix);
        double mean = onsets.Length > 0 ? onsets.Average(o => (double)o) : 0;
        double energy = 0;
        for (int i = 0; i < onsets.Length; i++)
        {
            onsets[i] -= (float)mean;
            energy += (double)onsets[i] * onsets[i];
        }

        return energy > 0 ? new AlignmentAudio(onsets, energy) : null;
    }

    /// <summary>The stems summed, at <see cref="AudioAligner.MixRate"/>: each stem's samples for mixing averaged down to it.</summary>
    private static float[] Mix(IReadOnlyList<StemPeaks> stems)
    {
        int length = stems.Select(s => (int)((long)s.ReadTicks * AudioAligner.MixRate / s.Resolution.SignalRate)).DefaultIfEmpty(0).Max();
        var mix = new float[length];
        foreach (var stem in stems)
        {
            int rate = stem.Resolution.SignalRate;
            Half[] signal = stem.Signal;
            for (int n = 0; n < length; n++)
            {
                long first = (long)n * rate / AudioAligner.MixRate;
                long end = Math.Min((long)(n + 1) * rate / AudioAligner.MixRate, stem.ReadTicks);
                if (first >= end)
                    break;

                float sum = 0;
                for (long k = first; k < end; k++)
                    sum += (float)signal[k];
                mix[n] += sum / (end - first);
            }
        }

        return mix;
    }

    /// <summary>
    /// How sharply <paramref name="mix"/> gets louder in each frame: the rise in its loudness, on a log scale, over
    /// <see cref="RiseFrames"/> frames, less the rise around it, and never below nothing.
    /// </summary>
    private static float[] OnsetsOf(float[] mix)
    {
        int hop = AudioAligner.MixRate / AudioAligner.FramesPerSecond;
        int frames = Math.Max(0, ((mix.Length - WindowSamples) / hop) + 1);

        var loudness = new float[frames];
        for (int m = 0; m < frames; m++)
        {
            double squares = 0;
            int start = m * hop;
            for (int i = start; i < start + WindowSamples; i++)
                squares += (double)mix[i] * mix[i];
            loudness[m] = (float)Math.Log(1 + (Compression * Math.Sqrt(squares / WindowSamples)));
        }

        var rise = new float[frames];
        for (int m = RiseFrames; m < frames; m++)
            rise[m] = Math.Max(0, loudness[m] - loudness[m - RiseFrames]);

        // Less the rise around it, so a hit stands out from a stretch that's busy all through.
        var onsets = new float[frames];
        double sum = 0;
        int lo = 0, hi = 0;
        for (int m = 0; m < frames; m++)
        {
            while (hi < frames && hi <= m + AroundFrames)
                sum += rise[hi++];
            while (lo < m - AroundFrames)
                sum -= rise[lo++];
            onsets[m] = (float)Math.Max(0, rise[m] - (sum / (hi - lo)));
        }

        return onsets;
    }

    // Each frame's loudness is of 10 ms of the mix; a rise is over 10 ms; and the rise around a frame is of 100 ms either side.
    private const int WindowSamples = 20;
    private const int RiseFrames = 4;
    private const int AroundFrames = 40;

    // How far loudness is scaled before its log is taken: enough that a quiet passage's hits still count.
    private const double Compression = 100;
}

/// <summary>
/// Lines songs up by their audio: each song's tracks mixed down, and the offset at which the two mixes' beats and hits
/// line up best, as cross-correlation finds it.
/// </summary>
/// <remarks>
/// <para>The mixes are read from the samples each track's waveform is read at for mixing, which <see cref="StemPeaksCache"/>
/// may already have. How sharply each gets louder, 400 times a second, is correlated with the other's through an FFT, within
/// <see cref="SourceAligner.MaxShiftMs"/>; the highest peak, refined between frames, is the offset. How alike the two are
/// there, from 0 to 1, says whether they're the same music at all.</para>
/// <para>As music repeats, another peak a beat or a bar away can come close: then the result is
/// <see cref="AlignmentEstimate.IsAmbiguous"/>. The first and last thirds of the song are then lined up apart, near the
/// offset, to tell a song a different length or speed.</para>
/// </remarks>
public static class AudioAligner
{
    /// <summary>How many frames a second the audio is lined up by: 2.5 ms each.</summary>
    public const int FramesPerSecond = 400;

    /// <summary>How many samples a second the tracks are mixed at.</summary>
    internal const int MixRate = 2000;

    // A second peak at least this far from the first that comes within this share of it makes the result ambiguous.
    private const int SecondPeakFrames = 20;
    private const double AmbiguousShare = 0.85;

    // How far either side of the offset the start and the end of the song are looked for, in frames, for drift.
    private const int DriftSearchFrames = 20;

    private static readonly int ReadParallelism = Math.Max(1, Environment.ProcessorCount - 1);

    /// <summary>How far to move <paramref name="to"/>'s song, as its audio puts it, to line it up with <paramref name="from"/>'s.</summary>
    public static AlignmentEstimate Align(AlignmentAudio from, AlignmentAudio to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        int maxShift = SourceAligner.MaxShiftMs * FramesPerSecond / 1000;
        float[] a = from.Onsets, b = to.Onsets;
        int size = Fft.SizeFor(a.Length + b.Length);
        var fft = new Fft(size);

        var ar = new float[size];
        var ai = new float[size];
        var br = new float[size];
        var bi = new float[size];
        a.CopyTo(ar, 0);
        b.CopyTo(br, 0);
        fft.Forward(ar, ai);
        fft.Forward(br, bi);

        // A times B's conjugate: at lag k, the sum of a[n + k] b[n], at index k, or size + k when it's negative.
        for (int i = 0; i < size; i++)
        {
            float re = (ar[i] * br[i]) + (ai[i] * bi[i]);
            float im = (ai[i] * br[i]) - (ar[i] * bi[i]);
            ar[i] = re;
            ai[i] = im;
        }

        fft.Inverse(ar, ai);

        int low = Math.Max(-maxShift, -(b.Length - 1));
        int high = Math.Min(maxShift, a.Length - 1);
        double scale = Math.Sqrt(from.Energy * to.Energy);
        var r = new double[high - low + 1];
        for (int lag = low; lag <= high; lag++)
            r[lag - low] = ar[lag >= 0 ? lag : size + lag] / scale;

        int peak = 0;
        for (int i = 1; i < r.Length; i++)
        {
            if (r[i] > r[peak])
                peak = i;
        }

        // The next highest peak of its own, not the first's shoulder.
        double second = double.NegativeInfinity;
        for (int i = 0; i < r.Length; i++)
        {
            bool isPeak = (i == 0 || r[i] >= r[i - 1]) && (i == r.Length - 1 || r[i] >= r[i + 1]);
            if (isPeak && Math.Abs(i - peak) >= SecondPeakFrames && r[i] > second)
                second = r[i];
        }

        double lagFrames = peak + low + Refine(r, peak);
        double match = Math.Clamp(r[peak], 0, 1);
        bool ambiguous = r[peak] > 0 && second >= r[peak] * AmbiguousShare;
        int offset = Milliseconds(lagFrames);
        return new AlignmentEstimate(AlignmentMethod.Audio, offset, Match: match, IsAmbiguous: ambiguous, DriftMs: DriftOf(a, b, (int)Math.Round(lagFrames)));
    }

    /// <summary>
    /// A link by audio between every two of <paramref name="audio"/> (by source; <see langword="null"/> for a song without
    /// any), each way round.
    /// </summary>
    /// <param name="cancellationToken">Stops working them out.</param>
    /// <exception cref="OperationCanceledException">It was stopped.</exception>
    public static IReadOnlyList<AlignmentLink> Links(IReadOnlyList<AlignmentAudio?> audio, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        var pairs = new List<(int From, int To)>();
        for (int i = 0; i < audio.Count; i++)
        {
            for (int j = i + 1; j < audio.Count; j++)
            {
                if (audio[i] is not null && audio[j] is not null)
                    pairs.Add((i, j));
            }
        }

        var found = new AlignmentLink[pairs.Count];
        Parallel.For(0, pairs.Count, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = ReadParallelism }, p =>
        {
            var (i, j) = pairs[p];
            found[p] = AlignmentLink.ByAudio(i, j, Align(audio[i]!, audio[j]!));
        });

        return [.. found.SelectMany(link => new[] { link, link.Reversed() })];
    }

    /// <summary>
    /// Reads <paramref name="storedNames"/>' audio tracks of <paramref name="entry"/> for lining up, the preview left out: from
    /// <paramref name="cache"/> where it has a track's peaks, read at any resolution, and otherwise from its audio, at
    /// <paramref name="resolution"/>, kept in the cache afterwards. Each track is read in chunks on several threads at once.
    /// </summary>
    /// <returns>The song's audio; <see langword="null"/> when it has no track that could be read, or it's all silence.</returns>
    /// <exception cref="IOException">A <c>.sng</c> can't be read.</exception>
    /// <exception cref="UnauthorizedAccessException">A <c>.sng</c> can't be read.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static AlignmentAudio? Read(SongEntry entry, IReadOnlyList<string> storedNames, WaveformResolution resolution, StemPeaksCache? cache = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(storedNames);

        var tracks = SongTracks.Resolve(storedNames, null).Where(t => !t.Stem.Equals(AudioStems.Preview, StringComparison.OrdinalIgnoreCase)).ToList();
        var peaks = new List<StemPeaks>();
        var keys = new Dictionary<SongTrack, StemPeaksKey?>();
        foreach (var track in tracks)
        {
            // Each key is taken before its file is read, so what's kept under it is never older than the file it names.
            var key = StemPeaksCache.KeyOf(entry, track);
            if (key is { } found && cache?.TryGet(found, out var cached) == true)
                peaks.Add(cached);
            else
                keys[track] = key;
        }

        if (keys.Count > 0)
        {
            using var audio = SongAudio.Open(entry, [.. keys.Keys], cancellationToken);
            var reads = audio.Tracks
                .Where(t => t.Source is not null)
                .Select(t => (Opened: t, Reader: new StemPeaksReader(t.Track.StoredName, t.Source!, resolution: resolution), Failed: new FailedFlag()))
                .ToList();
            var chunks = reads.SelectMany(read => Enumerable.Range(0, read.Reader.ChunkCount).Select(index => (Read: read, Index: index))).ToList();

            Parallel.ForEach(chunks, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = ReadParallelism }, chunk =>
            {
                var (opened, reader, failed) = chunk.Read;
                if (failed.Value)
                    return;
                try
                {
                    // The first chunk is read from the source opened for the track; the rest open their own.
                    if (chunk.Index == 0)
                    {
                        reader.ReadChunk(0, opened.Source!, cancellationToken);
                    }
                    else
                    {
                        using var source = audio.OpenSource(opened.Track);
                        reader.ReadChunk(chunk.Index, source, cancellationToken);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    // A track that can't be read is left out of the mix; the others carry on.
                    failed.Value = true;
                }
            });

            foreach (var (opened, reader, failed) in reads)
            {
                if (failed.Value)
                    continue;
                var read = reader.Finish();
                peaks.Add(read);
                if (keys[opened.Track] is { } key)
                    cache?.Add(key, read);
            }
        }

        return AlignmentAudio.Of(peaks);
    }

    /// <summary>
    /// How far <paramref name="b"/>'s last third lines up from its first, in milliseconds, each looked for near
    /// <paramref name="lag"/>; <see langword="null"/> when either isn't alike enough to tell.
    /// </summary>
    private static int? DriftOf(float[] a, float[] b, int lag)
    {
        int third = b.Length / 3;
        if (third < FramesPerSecond)
            return null;

        var start = LagNear(a, b, 0, third, lag);
        var end = LagNear(a, b, b.Length - third, b.Length, lag);
        return start is { } s && end is { } e ? Milliseconds(e - s) : null;
    }

    /// <summary>
    /// The lag near <paramref name="lag"/>, refined between frames, at which <paramref name="b"/> from <paramref name="first"/>
    /// up to <paramref name="end"/> is most like <paramref name="a"/>; <see langword="null"/> when it isn't at least
    /// <see cref="SourceAligner.MinAudioMatch"/> alike there.
    /// </summary>
    private static double? LagNear(float[] a, float[] b, int first, int end, int lag)
    {
        // Each of its own average: the whole song's is taken off already, but a quiet stretch is below it all through,
        // which would be alike at every lag.
        var r = new double[(DriftSearchFrames * 2) + 1];
        for (int i = 0; i < r.Length; i++)
        {
            int k = lag - DriftSearchFrames + i;
            double sx = 0, sy = 0, sxy = 0, sxx = 0, syy = 0;
            int count = 0;
            for (int n = Math.Max(first, -k); n < Math.Min(end, a.Length - k); n++)
            {
                double x = a[n + k], y = b[n];
                sx += x;
                sy += y;
                sxy += x * y;
                sxx += x * x;
                syy += y * y;
                count++;
            }

            double xy = sxy - (sx * sy / Math.Max(1, count));
            double xx = sxx - (sx * sx / Math.Max(1, count));
            double yy = syy - (sy * sy / Math.Max(1, count));
            r[i] = xx > 0 && yy > 0 ? xy / Math.Sqrt(xx * yy) : 0;
        }

        int peak = 0;
        for (int i = 1; i < r.Length; i++)
        {
            if (r[i] > r[peak])
                peak = i;
        }

        // A peak at the edge of the search is no peak.
        if (r[peak] < SourceAligner.MinAudioMatch || peak == 0 || peak == r.Length - 1)
            return null;
        return lag - DriftSearchFrames + peak + Refine(r, peak);
    }

    /// <summary>How far between frames the peak of <paramref name="r"/> at <paramref name="peak"/> is: the top of a parabola through it and its neighbours.</summary>
    private static double Refine(double[] r, int peak)
    {
        if (peak == 0 || peak == r.Length - 1)
            return 0;
        double below = r[peak - 1], at = r[peak], above = r[peak + 1];
        double curve = below - (2 * at) + above;
        return curve < 0 ? Math.Clamp(0.5 * (below - above) / curve, -0.5, 0.5) : 0;
    }

    private static int Milliseconds(double frames) => (int)Math.Round(frames * 1000 / FramesPerSecond, MidpointRounding.AwayFromZero);

    /// <summary>Whether a track's read has failed, set by whichever thread reads the chunk that fails.</summary>
    private sealed class FailedFlag
    {
        public volatile bool Value;
    }
}
