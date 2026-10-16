using ChartShared.Audio.Alignment;
using ChartShared.Audio.Decoding;
using ChartShared.Audio.Mixing;
using ChartShared.Audio.Playback;
using ChartShared.Audio.Waveforms;
using ChartShared.Core.Scanning;

namespace ChartShared.Audio.Analysis;

/// <summary>Which audio an analysis was made of, as its files were then: a stem, or several mixed.</summary>
/// <param name="Id">The tracks' files, their sizes and times, joined; equal keys are the same audio.</param>
public readonly record struct AnalysisKey(string Id)
{
    /// <summary>
    /// The key for <paramref name="tracks"/> of <paramref name="entry"/> as their files are now, or <see langword="null"/>
    /// when one can't be found. Take it before analysing them, so what's kept under it is at least as new.
    /// </summary>
    public static AnalysisKey? Of(SongEntry? entry, IReadOnlyList<SongTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (tracks.Count == 0)
            return null;

        var parts = new List<string>(tracks.Count);
        foreach (var track in tracks.OrderBy(t => t.StoredName, StringComparer.OrdinalIgnoreCase))
        {
            if (StemPeaksCache.KeyOf(entry, track) is not { } key)
                return null;
            parts.Add($"{key.FilePath}|{key.StoredName}|{key.Length}|{key.LastWriteTimeUtc.Ticks}");
        }

        return new AnalysisKey(string.Join(";", parts));
    }
}

/// <summary>
/// Audio to analyse at its full rate: one stem, its channels averaged, or several stems mixed as playback mixes them
/// (charter-spec §4.4). Each analysis thread opens a reader of its own.
/// </summary>
/// <remarks>The <see cref="SongAudio"/> it was made from must stay open while it's read.</remarks>
public sealed class AnalysisInput
{
    private readonly Func<IMonoReader> _open;

    private AnalysisInput(int sampleRate, long lengthFrames, Func<IMonoReader> open)
    {
        SampleRate = sampleRate;
        LengthFrames = lengthFrames;
        _open = open;
    }

    public int SampleRate { get; }

    /// <summary>How many frames there are, at <see cref="SampleRate"/>.</summary>
    public long LengthFrames { get; }

    public double LengthSeconds => (double)LengthFrames / SampleRate;

    /// <summary>One opened track of <paramref name="audio"/>.</summary>
    /// <remarks>When the decoder doesn't know the track's length, the track is decoded once to find it.</remarks>
    /// <exception cref="ArgumentException">The track didn't open.</exception>
    public static AnalysisInput Stem(SongAudio audio, OpenedTrack track) => Mix(audio, [track]);

    /// <summary>
    /// Opened tracks of <paramref name="audio"/> mixed, at the rate most of them have (<see cref="StemMixer.CommonRate"/>).
    /// Tracks that didn't open are left out.
    /// </summary>
    /// <exception cref="ArgumentException">None of the tracks opened.</exception>
    public static AnalysisInput Mix(SongAudio audio, IReadOnlyList<OpenedTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(tracks);

        var opened = tracks.Where(t => t.Source is not null).ToList();
        if (opened.Count == 0)
            throw new ArgumentException("None of the tracks opened.", nameof(tracks));

        return FromSources([.. opened.Select(t => (Func<IPcmSource>)(() => audio.OpenSource(t.Track)))], [.. opened.Select(t => t.Source!)]);
    }

    /// <summary>The whole song as playback mixes it, less the preview and the crowd; <see langword="null"/> when nothing opened.</summary>
    public static AnalysisInput? SongMix(SongAudio audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        var tracks = audio.Tracks.Where(t => t.Source is not null && !AudioAligner.IsLeftOut(t.Track.Stem)).ToList();
        return tracks.Count == 0 ? null : Mix(audio, tracks);
    }

    /// <summary>
    /// Audio from sources opened by <paramref name="open"/>, one per stem, mixed when there are several. Each is opened
    /// once here, for its rate and length.
    /// </summary>
    internal static AnalysisInput Of(IReadOnlyList<Func<IPcmSource>> open)
    {
        var probes = open.Select(o => o()).ToList();
        try
        {
            return FromSources(open, probes);
        }
        finally
        {
            foreach (var probe in probes)
                probe.Dispose();
        }
    }

    internal static AnalysisInput Of(int sampleRate, long lengthFrames, Func<IMonoReader> open) => new(sampleRate, lengthFrames, open);

    /// <summary>A reader of its own, at the start; the caller disposes it.</summary>
    internal IMonoReader Open() => _open();

    /// <param name="open">Opens each stem's source.</param>
    /// <param name="probes">An opened source of each stem, read for its rate and length (and to its end when that isn't known).</param>
    private static AnalysisInput FromSources(IReadOnlyList<Func<IPcmSource>> open, IReadOnlyList<IPcmSource> probes)
    {
        if (open.Count == 1)
        {
            long length = LengthOf(probes[0], open[0]);
            return new AnalysisInput(probes[0].SampleRate, length, () => new MonoStemReader(open[0](), length));
        }

        int rate = StemMixer.CommonRate(probes.Select(p => p.SampleRate));
        long mixLength = 0;
        for (int i = 0; i < probes.Count; i++)
        {
            long frames = LengthOf(probes[i], open[i]);
            mixLength = Math.Max(mixLength, (long)Math.Ceiling(frames * (double)rate / probes[i].SampleRate));
        }

        return new AnalysisInput(rate, mixLength, () =>
        {
            var sources = new List<IPcmSource>(open.Count);
            try
            {
                foreach (var opener in open)
                    sources.Add(opener());
            }
            catch
            {
                foreach (var source in sources)
                    source.Dispose();
                throw;
            }

            return new MonoMixReader(sources, rate, mixLength);
        });
    }

    /// <summary>The source's length, decoding a source of its own to the end when it doesn't know it.</summary>
    private static long LengthOf(IPcmSource probe, Func<IPcmSource> open)
    {
        if (probe.LengthFrames >= 0)
            return probe.LengthFrames;

        using var source = open();
        var buffer = new float[4096 * Math.Max(1, source.Channels)];
        long frames = 0;
        try
        {
            int read;
            while ((read = source.ReadFrames(buffer)) > 0)
                frames += read;
        }
        catch (InvalidDataException)
        {
            // What was read is what there is.
        }

        return frames;
    }
}
