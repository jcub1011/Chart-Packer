using ChartShared.Audio.Decoding;
using ChartShared.Audio.Mixing;

namespace ChartShared.Audio.Analysis;

/// <summary>Audio for analysis: one channel of samples, read in order and sought, from one stem or several mixed.</summary>
/// <remarks>Not thread-safe: each analysis thread opens its own.</remarks>
internal interface IMonoReader : IDisposable
{
    int SampleRate { get; }

    /// <summary>How many frames there are.</summary>
    long LengthFrames { get; }

    /// <summary>Why some of the audio couldn't be read (it's then silent), or <see langword="null"/>.</summary>
    string? Error { get; }

    /// <summary>The frame the next read starts at, or -1 when that isn't known (the next read must seek first).</summary>
    long Position { get; }

    void Seek(long frame);

    /// <summary>Reads the next frames into <paramref name="mono"/>.</summary>
    /// <returns>How many were read: all of them, except at the end.</returns>
    int Read(Span<float> mono);
}

/// <summary>One stem, its channels averaged. Damaged audio ends it, with <see cref="Error"/> saying why.</summary>
internal sealed class MonoStemReader(IPcmSource source, long lengthFrames) : IMonoReader
{
    private float[] _buffer = [];
    private bool _failed;

    public int SampleRate => source.SampleRate;

    public long LengthFrames => lengthFrames;

    public string? Error { get; private set; }

    public long Position { get; private set; }

    public void Seek(long frame)
    {
        Position = frame;
        if (_failed)
            return;
        try
        {
            source.SeekToFrame(frame);
        }
        catch (InvalidDataException ex)
        {
            Fail(ex);
        }
    }

    public int Read(Span<float> mono)
    {
        if (_failed || mono.IsEmpty)
            return 0;

        int channels = source.Channels;
        if (_buffer.Length < mono.Length * channels)
            _buffer = new float[mono.Length * channels];

        int frames;
        try
        {
            frames = source.ReadFrames(_buffer.AsSpan(0, mono.Length * channels));
        }
        catch (InvalidDataException ex)
        {
            Fail(ex);
            return 0;
        }

        Position += frames;
        if (channels == 1)
        {
            _buffer.AsSpan(0, frames).CopyTo(mono);
            return frames;
        }

        float scale = 1f / channels;
        for (int n = 0; n < frames; n++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
                sum += _buffer[(n * channels) + c];
            mono[n] = sum * scale;
        }

        return frames;
    }

    public void Dispose() => source.Dispose();

    private void Fail(InvalidDataException ex)
    {
        _failed = true;
        Position = -1;
        Error = ex.Message;
    }
}

/// <summary>
/// Several stems mixed as playback hears them: each read at <see cref="SampleRate"/> through <see cref="StemReader"/>,
/// its sides averaged, and the stems summed without clipping. A damaged stem goes quiet and the others go on.
/// </summary>
internal sealed class MonoMixReader : IMonoReader
{
    private readonly IReadOnlyList<IPcmSource> _sources;
    private readonly StemReader[] _readers;
    private readonly bool[] _failed;
    private float[] _stereo = [];

    public MonoMixReader(IReadOnlyList<IPcmSource> sources, int sampleRate, long lengthFrames)
    {
        _sources = sources;
        _readers = [.. sources.Select(s => new StemReader(s, sampleRate))];
        _failed = new bool[sources.Count];
        SampleRate = sampleRate;
        LengthFrames = lengthFrames;
    }

    public int SampleRate { get; }

    public long LengthFrames { get; }

    public string? Error { get; private set; }

    public long Position { get; private set; }

    public void Seek(long frame)
    {
        Position = frame;
        for (int i = 0; i < _readers.Length; i++)
        {
            if (_failed[i])
                continue;
            try
            {
                _readers[i].Seek(frame);
            }
            catch (InvalidDataException ex)
            {
                Fail(i, ex);
            }
        }
    }

    public int Read(Span<float> mono)
    {
        int frames = (int)Math.Clamp(LengthFrames - Position, 0, mono.Length);
        if (frames == 0)
            return 0;

        if (_stereo.Length < frames * 2)
            _stereo = new float[frames * 2];

        var output = mono[..frames];
        output.Clear();
        var stereo = _stereo.AsSpan(0, frames * 2);
        for (int i = 0; i < _readers.Length; i++)
        {
            if (_failed[i])
                continue;
            try
            {
                _readers[i].Read(stereo);
            }
            catch (InvalidDataException ex)
            {
                Fail(i, ex);
                continue;
            }

            for (int n = 0; n < frames; n++)
                output[n] += (stereo[n * 2] + stereo[(n * 2) + 1]) * 0.5f;
        }

        Position += frames;
        return frames;
    }

    public void Dispose()
    {
        foreach (var source in _sources)
            source.Dispose();
    }

    private void Fail(int index, InvalidDataException ex)
    {
        _failed[index] = true;
        Error ??= ex.Message;
    }
}

/// <summary>Reads a window of audio that may start before the audio does or run past its end, where it's silent.</summary>
internal static class WindowRead
{
    /// <summary>
    /// Fills <paramref name="dest"/> with the frames from <paramref name="startFrame"/> on, silence outside the audio. A read
    /// straight on from the last doesn't seek.
    /// </summary>
    public static void Read(IMonoReader reader, long startFrame, Span<float> dest)
    {
        dest.Clear();
        long from = Math.Max(0, startFrame);
        long to = Math.Min(reader.LengthFrames, startFrame + dest.Length);
        if (to <= from)
            return;

        if (reader.Position != from)
            reader.Seek(from);

        var into = dest.Slice((int)(from - startFrame), (int)(to - from));
        while (!into.IsEmpty)
        {
            int read = reader.Read(into);
            if (read <= 0)
                return; // Ended early, or damaged: the rest stays silent.
            into = into[read..];
        }
    }
}
