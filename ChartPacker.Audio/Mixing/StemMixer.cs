using ChartPacker.Audio.Decoding;

namespace ChartPacker.Audio.Mixing;

/// <summary>One stem in a <see cref="StemMixer"/>: its switches, which can be flipped from any thread.</summary>
public sealed class MixerStem
{
    private volatile bool _isMuted;
    private volatile bool _isSolo;
    private volatile string? _error;
    private volatile float _volume = 1f;
    private readonly Action _switched;
    private readonly Action _turned;

    internal MixerStem(string name, StemReader reader, Action switched, Action turned)
    {
        Name = name;
        Reader = reader;
        _switched = switched;
        _turned = turned;
    }

    public string Name { get; }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (value == _isMuted)
                return;
            _isMuted = value;
            _switched();
        }
    }

    /// <summary>While any stem is soloed, only soloed stems are heard, muted or not.</summary>
    public bool IsSolo
    {
        get => _isSolo;
        set
        {
            if (value == _isSolo)
                return;
            _isSolo = value;
            _switched();
        }
    }

    /// <summary>How loud the stem is in the mix, from 0 (silent) up; 1 leaves it as recorded.</summary>
    public float Volume
    {
        get => _volume;
        set
        {
            float volume = float.IsFinite(value) ? Math.Max(0f, value) : 1f;
            if (volume == _volume)
                return;
            _volume = volume;
            _turned();
        }
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
    private long _minimumLengthFrames;
    private int _switchVersion;
    private int _volumeVersion;

    // Every stem was found to have ended by the last mix, while the stems' length isn't known: only silence is left.
    private bool _stemsEnded;

    /// <param name="sources">The stems, by name. The mixer reads them but doesn't own them.</param>
    /// <param name="outputRate">The rate to mix at; by default, the rate most of the stems have, so that as few as possible are resampled.</param>
    public StemMixer(IReadOnlyList<KeyValuePair<string, IPcmSource>> sources, int? outputRate = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        OutputRate = outputRate ?? CommonRate(sources.Select(s => s.Value.SampleRate));
        foreach (var (name, source) in sources)
        {
            _stems.Add(new MixerStem(
                name,
                new StemReader(source, OutputRate),
                () => Interlocked.Increment(ref _switchVersion),
                () => Interlocked.Increment(ref _volumeVersion)));
        }

        var lengths = _stems.Select(s => s.Reader.LengthFrames).ToList();
        LengthFrames = lengths.Count == 0 ? 0 : lengths.Any(l => l < 0) ? -1 : lengths.Max();
        KnownLengthFrames = lengths.Where(l => l >= 0).DefaultIfEmpty(0).Max();
    }

    public int OutputRate { get; }

    public IReadOnlyList<MixerStem> Stems => _stems;

    /// <summary>
    /// Counts changes to the stems' switches (<see cref="MixerStem.IsMuted"/> and <see cref="MixerStem.IsSolo"/>),
    /// so whoever has mixed ahead can tell when to mix again.
    /// </summary>
    public int SwitchVersion => Volatile.Read(ref _switchVersion);

    /// <summary>
    /// Counts changes to the stems' <see cref="MixerStem.Volume"/>, apart from <see cref="SwitchVersion"/>: a slider
    /// dragged changes it many times a second, so whoever has mixed ahead can mix again less often for it.
    /// </summary>
    public int VolumeVersion => Volatile.Read(ref _volumeVersion);

    /// <summary>The longest stem's length, or -1 when a stem's length isn't known.</summary>
    public long LengthFrames { get; }

    /// <summary>The longest of the lengths that are known, for display when <see cref="LengthFrames"/> isn't.</summary>
    public long KnownLengthFrames { get; }

    /// <summary>
    /// How long the song plays for at least, in frames: past the end of its stems it plays on in silence until here,
    /// as a game plays a chart whose notes or video run on past its audio. Shorter than the stems, it changes nothing.
    /// Can be set from any thread; it takes effect at the next <see cref="Mix"/> or <see cref="Seek"/>.
    /// </summary>
    public long MinimumLengthFrames
    {
        get => Volatile.Read(ref _minimumLengthFrames);
        set => Volatile.Write(ref _minimumLengthFrames, Math.Max(0, value));
    }

    /// <summary>The frame the next <see cref="Mix"/> starts at.</summary>
    public long PositionFrames { get; private set; }

    /// <summary>Every stem has ended, and so has <see cref="MinimumLengthFrames"/>.</summary>
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

        // Past the stems' end, silence plays on to the minimum length.
        long minimum = MinimumLengthFrames;
        if (LengthFrames >= 0)
            frames = (int)Math.Min(frames, Math.Max(LengthFrames, minimum) - PositionFrames);
        else if (_stemsEnded)
            frames = (int)Math.Min(frames, minimum - PositionFrames);
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
                float volume = stem.Volume;
                for (int i = 0; i < output.Length; i++)
                    output[i] += scratch[i] * volume;
            }
        }

        foreach (ref float sample in output)
            sample = Math.Clamp(sample, -1f, 1f);

        PositionFrames += frames;
        if (LengthFrames < 0 && allEnded)
        {
            _stemsEnded = true;
            if (PositionFrames >= minimum)
                IsEnded = true;
        }

        return frames;
    }

    /// <summary>Moves every stem to <paramref name="frame"/>; past a stem's end, it plays silence from there.</summary>
    public void Seek(long frame)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        if (LengthFrames >= 0)
            frame = Math.Min(frame, Math.Max(LengthFrames, MinimumLengthFrames));

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
        _stemsEnded = false;
    }
}
