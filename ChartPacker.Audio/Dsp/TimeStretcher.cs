namespace ChartPacker.Audio.Dsp;

/// <summary>
/// Plays stereo audio faster or slower without moving its pitch, by WSOLA (waveform-similarity overlap-add): the output
/// is built from overlapping grains of the input, each taken from about where the speed says the input has got to, and
/// nudged to wherever it lines up best with the grain before so the waveform carries on smoothly.
/// </summary>
/// <remarks>
/// Grains are 40 ms long under a Hann window, one every 20 ms of output, each looked for within 12 ms of where it
/// belongs. The search compares the channels' sum, and both channels take the grain from the same place, so the stereo
/// image holds. Pulls its input from a reader as it needs it; used from one thread at a time.
/// </remarks>
public sealed class TimeStretcher
{
    /// <summary>The slowest <see cref="Ratio"/>.</summary>
    public const double MinRatio = 0.01;

    /// <summary>The fastest <see cref="Ratio"/>.</summary>
    public const double MaxRatio = 100;

    // The coarse search looks at every this many places, comparing every this many frames; the fine one at each frame near the best.
    private const int CoarseStep = 4;

    private readonly ReadStereo _read;

    // Output frames per grain step, half a grain; and how far either side of where a grain belongs it may be taken from.
    private readonly int _hop;
    private readonly int _tolerance;
    private readonly float[] _window;

    // The input held, interleaved: frame _inStart + n at _in[2n]. Frames before _inStart have been let go.
    private float[] _in;
    private long _inStart;
    private int _inCount;
    private bool _inputEnded;

    // The channels' sum over where grains are looked for, and over where the last grain carries on to.
    private readonly float[] _mono;
    private readonly float[] _natural;

    // The second half of the last grain, windowed, waiting to be added to the first half of the next.
    private readonly float[] _overlap;

    // The output of the last step, of which _readyOffset frames have been read.
    private readonly float[] _ready;
    private int _readyOffset;
    private int _readyCount;

    private bool _primed;
    private bool _finished;
    private long _lastGrain;
    private double _analysisPosition;
    private double _ratio = 1;

    /// <summary>Reads the next frames of input into an interleaved stereo span.</summary>
    /// <returns>How many frames were read; 0 once the input has ended.</returns>
    public delegate int ReadStereo(Span<float> stereo);

    /// <param name="sampleRate">The input's rate, which the output has too.</param>
    /// <param name="read">Where the input comes from, from the frame given to <see cref="Reset"/> on.</param>
    public TimeStretcher(int sampleRate, ReadStereo read)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentNullException.ThrowIfNull(read);

        _read = read;
        _hop = Math.Max(16, (int)Math.Round(sampleRate * 0.020));
        _tolerance = Math.Max(CoarseStep, (int)Math.Round(sampleRate * 0.012));

        // A periodic Hann window: two of them half a window apart add up to 1 everywhere.
        _window = new float[2 * _hop];
        for (int n = 0; n < _window.Length; n++)
            _window[n] = (float)(0.5 - (0.5 * Math.Cos(2 * Math.PI * n / _window.Length)));

        _in = new float[(4 * _hop + 2 * _tolerance) * 2];
        _mono = new float[(2 * _tolerance) + _hop + 1];
        _natural = new float[_hop];
        _overlap = new float[_hop * 2];
        _ready = new float[_hop * 2];
    }

    /// <summary>
    /// How fast the input plays, from <see cref="MinRatio"/> to <see cref="MaxRatio"/>: 2 plays it in half the time,
    /// at the same pitch. Takes effect at the next grain.
    /// </summary>
    public double Ratio
    {
        get => _ratio;
        set => _ratio = double.IsFinite(value) ? Math.Clamp(value, MinRatio, MaxRatio) : 1;
    }

    /// <summary>The input frame the next output frame stands for: where it's heard to have got to in the input.</summary>
    public double SourcePosition { get; private set; }

    /// <summary>
    /// Forgets everything held, for input that starts again at <paramref name="sourceFrame"/>, as after a seek. The
    /// output carries on from exactly there: its first frames are the input's own.
    /// </summary>
    public void Reset(long sourceFrame)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sourceFrame);
        _inStart = sourceFrame;
        _inCount = 0;
        _inputEnded = false;
        _readyOffset = _readyCount = 0;
        _primed = false;
        _finished = false;
        _analysisPosition = sourceFrame;
        SourcePosition = sourceFrame;
    }

    /// <summary>Stretches the next frames of input into <paramref name="stereo"/> (interleaved left, right).</summary>
    /// <returns>How many frames were written: all of them, unless the input runs out first; 0 once it has.</returns>
    public int Read(Span<float> stereo)
    {
        int frames = stereo.Length / 2;
        int done = 0;
        while (done < frames && !_finished)
        {
            if (_readyOffset == _readyCount)
            {
                Step();
                _readyOffset = 0;
                _readyCount = _hop;
            }

            int count = Math.Min(frames - done, _readyCount - _readyOffset);

            // Stops where the input ends, rather than at the end of the last grain.
            if (_inputEnded)
            {
                double left = _inStart + _inCount - SourcePosition;
                count = left <= 0 ? 0 : (int)Math.Min(count, Math.Ceiling(left / _ratio));
                if (count == 0)
                {
                    _finished = true;
                    break;
                }
            }

            _ready.AsSpan(_readyOffset * 2, count * 2).CopyTo(stereo[(done * 2)..]);
            done += count;
            _readyOffset += count;
            SourcePosition += count * _ratio;
        }

        return done;
    }

    /// <summary>Adds the next grain, putting the next <see cref="_hop"/> frames of output in <see cref="_ready"/>.</summary>
    private void Step()
    {
        int hop = _hop;
        if (!_primed)
        {
            // The first grain is the input itself, so the output starts exactly where it was asked to and doesn't fade in.
            long start = _inStart;
            Fill(start + (2 * hop));
            for (int n = 0; n < hop; n++)
            {
                _ready[2 * n] = Sample(start + n, 0);
                _ready[(2 * n) + 1] = Sample(start + n, 1);
                _overlap[2 * n] = _window[hop + n] * Sample(start + hop + n, 0);
                _overlap[(2 * n) + 1] = _window[hop + n] * Sample(start + hop + n, 1);
            }

            _lastGrain = start;
            _primed = true;
            _analysisPosition += hop * _ratio;
            return;
        }

        long target = (long)Math.Round(_analysisPosition);
        long natural = _lastGrain + hop;
        Discard(Math.Min(target - _tolerance, natural));

        long low = Math.Max(target - _tolerance, _inStart);
        long high = Math.Max(low, target + _tolerance);

        // Read as far as this grain's search, and as far as the output it makes stands for, so the input's end is
        // known before the output passes it.
        long nextAnalysis = (long)Math.Ceiling(_analysisPosition + (hop * _ratio));
        Fill(Math.Max(Math.Max(high + (2 * hop), natural + hop), nextAnalysis));

        long grain = BestGrain(low, high, natural, target);
        for (int n = 0; n < hop; n++)
        {
            for (int c = 0; c < 2; c++)
            {
                int i = (2 * n) + c;
                _ready[i] = _overlap[i] + (_window[n] * Sample(grain + n, c));
                _overlap[i] = _window[hop + n] * Sample(grain + hop + n, c);
            }
        }

        _lastGrain = grain;
        _analysisPosition += hop * _ratio;
    }

    /// <summary>
    /// Where from <paramref name="low"/> to <paramref name="high"/> the next grain starts: where the input looks most like
    /// the last grain carrying on from <paramref name="natural"/>, nearest <paramref name="target"/> on a tie.
    /// </summary>
    private long BestGrain(long low, long high, long natural, long target)
    {
        int hop = _hop;
        for (int n = 0; n < hop; n++)
            _natural[n] = Sample(natural + n, 0) + Sample(natural + n, 1);

        int span = (int)(high - low) + hop;
        for (int n = 0; n < span; n++)
            _mono[n] = Sample(low + n, 0) + Sample(low + n, 1);

        // Coarsely first, comparing every few frames at every few places; then finely around the best of those.
        int best = (int)(Math.Clamp(target, low, high) - low);
        double bestScore = Similarity(best, CoarseStep);
        for (int offset = 0; offset <= high - low; offset += CoarseStep)
        {
            double score = Similarity(offset, CoarseStep);
            if (score > bestScore)
            {
                bestScore = score;
                best = offset;
            }
        }

        int coarse = best;
        bestScore = Similarity(coarse, 1);
        for (int offset = Math.Max(0, coarse - CoarseStep + 1); offset <= Math.Min(high - low, coarse + CoarseStep - 1); offset++)
        {
            double score = Similarity(offset, 1);
            if (score > bestScore)
            {
                bestScore = score;
                best = offset;
            }
        }

        return low + best;
    }

    /// <summary>How alike the input at <paramref name="offset"/> into <see cref="_mono"/> is to <see cref="_natural"/>: their correlation, scaled by the candidate's level.</summary>
    private double Similarity(int offset, int step)
    {
        double dot = 0, energy = 0;
        for (int n = 0; n < _hop; n += step)
        {
            float candidate = _mono[offset + n];
            dot += _natural[n] * candidate;
            energy += candidate * candidate;
        }

        return dot / Math.Sqrt(energy + 1e-9);
    }

    /// <summary>Frame <paramref name="frame"/>'s sample on <paramref name="channel"/>; silence past the input's end.</summary>
    private float Sample(long frame, int channel)
    {
        long index = frame - _inStart;
        return index >= 0 && index < _inCount ? _in[(2 * index) + channel] : 0f;
    }

    /// <summary>Lets go of the input before <paramref name="frame"/>, reading past it if it hasn't been read yet.</summary>
    private void Discard(long frame)
    {
        long end = _inStart + _inCount;
        if (frame <= _inStart)
            return;

        if (frame < end)
        {
            int dropped = (int)(frame - _inStart);
            _in.AsSpan(dropped * 2, (_inCount - dropped) * 2).CopyTo(_in);
            _inCount -= dropped;
            _inStart = frame;
            return;
        }

        // A fast ratio skips input the grains never use: it's read, to keep the reader in step, and let go at once.
        _inStart = end;
        _inCount = 0;
        while (_inStart < frame && !_inputEnded)
        {
            int read = _read(_in.AsSpan(0, (int)Math.Min(_in.Length / 2, frame - _inStart) * 2));
            if (read <= 0)
                _inputEnded = true;
            else
                _inStart += read;
        }
    }

    /// <summary>Reads input until it's held up to <paramref name="end"/>, or has ended.</summary>
    private void Fill(long end)
    {
        long needed = end - _inStart;
        if (needed * 2 > _in.Length)
            Array.Resize(ref _in, (int)Math.Max(needed * 2, _in.Length * 2L));

        while (_inStart + _inCount < end && !_inputEnded)
        {
            int read = _read(_in.AsSpan(_inCount * 2, (int)(end - _inStart - _inCount) * 2));
            if (read <= 0)
                _inputEnded = true;
            else
                _inCount += read;
        }
    }
}
