using ChartPacker.Audio.Decoding;

namespace ChartPacker.Audio.Mixing;

/// <summary>One stem in a <see cref="StemMixer"/>: its switches, which can be flipped from any thread.</summary>
public sealed class MixerStem
{
    private volatile bool _isMuted;
    private volatile bool _isSolo;
    private volatile string? _error;

    internal MixerStem(string name, StemReader reader)
    {
        Name = name;
        Reader = reader;
    }

    public string Name { get; }

    public bool IsMuted
    {
        get => _isMuted;
        set => _isMuted = value;
    }

    /// <summary>While any stem is soloed, only soloed stems are heard, muted or not.</summary>
    public bool IsSolo
    {
        get => _isSolo;
        set => _isSolo = value;
    }

    /// <summary>Why the stem stopped playing, when its audio turned out to be damaged; it's silent from then on.</summary>
    public string? Error
    {
        get => _error;
        internal set => _error = value;
    }

    internal StemReader Reader { get; }
}

/// <summary>
/// Mixes stems into one stereo stream at a single sample rate, keeping them in step: every read
/// takes the same number of frames from every stem, heard or not, so they stay sample-aligned by
/// construction. The sum is clipped to [-1, 1].
/// </summary>
/// <remarks>
/// <see cref="Mix"/> and <see cref="Seek"/> are called from one thread at a time; the stems'
/// switches can be flipped from any thread and take effect at the next <see cref="Mix"/>.
/// </remarks>
public sealed class StemMixer
{
    private readonly List<MixerStem> _stems = [];
    private float[] _scratch = [];

    /// <param name="sources">The stems, by name. The mixer reads them but doesn't own them.</param>
    /// <param name="outputRate">The rate to mix at; by default, the rate most of the stems have, so that as few as possible are resampled.</param>
    public StemMixer(IReadOnlyList<KeyValuePair<string, IPcmSource>> sources, int? outputRate = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        OutputRate = outputRate ?? CommonRate(sources.Select(s => s.Value.SampleRate));
        foreach (var (name, source) in sources)
            _stems.Add(new MixerStem(name, new StemReader(source, OutputRate)));

        var lengths = _stems.Select(s => s.Reader.LengthFrames).ToList();
        LengthFrames = lengths.Count == 0 ? 0 : lengths.Any(l => l < 0) ? -1 : lengths.Max();
        KnownLengthFrames = lengths.Where(l => l >= 0).DefaultIfEmpty(0).Max();
    }

    public int OutputRate { get; }

    public IReadOnlyList<MixerStem> Stems => _stems;

    /// <summary>The longest stem's length, or -1 when a stem's length isn't known.</summary>
    public long LengthFrames { get; }

    /// <summary>The longest of the lengths that are known, for display when <see cref="LengthFrames"/> isn't.</summary>
    public long KnownLengthFrames { get; }

    /// <summary>The frame the next <see cref="Mix"/> starts at.</summary>
    public long PositionFrames { get; private set; }

    /// <summary>Every stem has ended.</summary>
    public bool IsEnded { get; private set; }

    /// <summary>The rate most of <paramref name="rates"/> share, the higher on a tie; 48 kHz when there are none.</summary>
    public static int CommonRate(IEnumerable<int> rates) =>
        rates.GroupBy(r => r)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Key)
            .Select(g => g.Key)
            .DefaultIfEmpty(48000)
            .First();

    /// <summary>Mixes the next frames into <paramref name="stereo"/> (interleaved left, right).</summary>
    /// <returns>How many frames were mixed: all of them, unless the song ends first; 0 once it has.</returns>
    public int Mix(Span<float> stereo)
    {
        int frames = stereo.Length / 2;
        if (IsEnded)
            return 0;
        if (LengthFrames >= 0)
            frames = (int)Math.Min(frames, LengthFrames - PositionFrames);
        if (frames <= 0)
        {
            IsEnded = true;
            return 0;
        }

        var output = stereo[..(frames * 2)];
        output.Clear();
        if (_scratch.Length < output.Length)
            _scratch = new float[output.Length];
        var scratch = _scratch.AsSpan(0, output.Length);

        bool anySolo = _stems.Any(s => s.IsSolo);
        bool allEnded = true;
        foreach (var stem in _stems)
        {
            if (stem.Error is not null)
                continue;

            try
            {
                stem.Reader.Read(scratch);
            }
            catch (InvalidDataException ex)
            {
                // A damaged stem goes quiet; the rest play on, still in step.
                stem.Error = ex.Message;
                continue;
            }

            allEnded &= stem.Reader.IsEnded;
            if (anySolo ? stem.IsSolo : !stem.IsMuted)
            {
                for (int i = 0; i < output.Length; i++)
                    output[i] += scratch[i];
            }
        }

        foreach (ref float sample in output)
            sample = Math.Clamp(sample, -1f, 1f);

        PositionFrames += frames;
        if (LengthFrames < 0 && allEnded)
            IsEnded = true;
        return frames;
    }

    /// <summary>Moves every stem to <paramref name="frame"/>.</summary>
    public void Seek(long frame)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        if (LengthFrames >= 0)
            frame = Math.Min(frame, LengthFrames);

        foreach (var stem in _stems)
        {
            if (stem.Error is not null)
                continue;

            try
            {
                stem.Reader.Seek(frame);
            }
            catch (InvalidDataException ex)
            {
                stem.Error = ex.Message;
            }
        }

        PositionFrames = frame;
        IsEnded = false;
    }
}
