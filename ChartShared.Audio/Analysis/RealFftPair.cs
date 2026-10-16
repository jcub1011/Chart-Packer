using System.Collections.Concurrent;
using ChartShared.Audio.Dsp;
using ChartShared.Audio.Spectrum;

namespace ChartShared.Audio.Analysis;

/// <summary>
/// The power spectra of two real frames at once, Hann-windowed, through one complex FFT: one frame as the real part and
/// the other as the imaginary, pulled apart by the spectrum's symmetry. That halves the work of an STFT.
/// </summary>
/// <remarks>Not thread-safe: it has buffers of its own. The FFT's tables are shared between instances of a size.</remarks>
internal sealed class RealFftPair
{
    private static readonly ConcurrentDictionary<int, Fft> Ffts = new();

    private readonly Fft _fft;
    private readonly float[] _window;
    private readonly float[] _re;
    private readonly float[] _im;

    public RealFftPair(int size)
    {
        _fft = Ffts.GetOrAdd(size, s => new Fft(s));
        _window = Windowing.PeriodicHann(size);
        _re = new float[size];
        _im = new float[size];
        float sum = 0;
        foreach (float w in _window)
            sum += w;
        WindowSum = sum;
    }

    public int Size => _fft.Size;

    /// <summary>The window's samples added up: a full-scale sine's bin has magnitude <c>WindowSum / 2</c>.</summary>
    public float WindowSum { get; }

    /// <summary>
    /// The power (squared magnitude) of bins 0 to <c>Size / 2</c> of <paramref name="a"/> and of <paramref name="b"/>,
    /// each windowed. Pass an empty <paramref name="b"/> for silence.
    /// </summary>
    public void Power(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> powerA, Span<float> powerB)
    {
        int size = Size;
        for (int n = 0; n < size; n++)
        {
            _re[n] = a[n] * _window[n];
            _im[n] = b.IsEmpty ? 0 : b[n] * _window[n];
        }

        _fft.Forward(_re, _im);

        // X = A + iB, with A and B the spectra of two real frames: A_k = (X_k + conj X_(N-k)) / 2, B_k = (X_k - conj X_(N-k)) / 2i.
        for (int k = 0; k <= size / 2; k++)
        {
            int m = (size - k) & (size - 1);
            float ar = _re[k] + _re[m];
            float ai = _im[k] - _im[m];
            float br = _im[k] + _im[m];
            float bi = _re[k] - _re[m];
            powerA[k] = ((ar * ar) + (ai * ai)) * 0.25f;
            powerB[k] = ((br * br) + (bi * bi)) * 0.25f;
        }
    }
}
