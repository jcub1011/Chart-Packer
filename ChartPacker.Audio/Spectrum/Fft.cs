namespace ChartPacker.Audio.Spectrum;

/// <summary>
/// A radix-2 FFT of one size (Cooley–Tukey, decimation in time), done in place, with its tables worked out once.
/// Nothing is allocated per transform. The tables are only read, so one can transform on several threads at once.
/// </summary>
internal sealed class Fft
{
    private readonly int[] _reversed;
    private readonly float[] _cos;
    private readonly float[] _sin;

    /// <param name="size">How many points it transforms: a power of two.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="size"/> isn't a power of two of at least 2.</exception>
    public Fft(int size)
    {
        if (size < 2 || (size & (size - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(size), size, "Must be a power of two of at least 2.");

        Size = size;
        int bits = (int)Math.Log2(size);
        _reversed = new int[size];
        for (int i = 0; i < size; i++)
        {
            int reversed = 0;
            for (int b = 0; b < bits; b++)
                reversed |= ((i >> b) & 1) << (bits - 1 - b);
            _reversed[i] = reversed;
        }

        _cos = new float[size / 2];
        _sin = new float[size / 2];
        for (int i = 0; i < size / 2; i++)
        {
            _cos[i] = (float)Math.Cos(2 * Math.PI * i / size);
            _sin[i] = (float)Math.Sin(2 * Math.PI * i / size);
        }
    }

    /// <summary>How many points it transforms.</summary>
    public int Size { get; }

    /// <summary>The smallest power of two that's at least <paramref name="length"/>, and at least 2.</summary>
    public static int SizeFor(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, 1 << 30);
        int size = 2;
        while (size < length)
            size *= 2;
        return size;
    }

    /// <summary>Transforms <paramref name="re"/> and <paramref name="im"/>, each <see cref="Size"/> long, to their spectrum, in place.</summary>
    public void Forward(Span<float> re, Span<float> im) => Transform(re, im, -1);

    /// <summary>Transforms a spectrum back, in place: <see cref="Forward"/> undone, scaled back down by <see cref="Size"/>.</summary>
    public void Inverse(Span<float> re, Span<float> im)
    {
        Transform(re, im, 1);
        float scale = 1f / Size;
        for (int i = 0; i < Size; i++)
        {
            re[i] *= scale;
            im[i] *= scale;
        }
    }

    // The twiddle factors are e^(sign·2πik/size).
    private void Transform(Span<float> re, Span<float> im, int sign)
    {
        if (re.Length != Size || im.Length != Size)
            throw new ArgumentException($"There must be {Size} points.", re.Length != Size ? nameof(re) : nameof(im));

        for (int i = 0; i < Size; i++)
        {
            int j = _reversed[i];
            if (j > i)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int size = 2; size <= Size; size *= 2)
        {
            int half = size / 2;
            int step = Size / size;
            for (int start = 0; start < Size; start += size)
            {
                for (int k = 0; k < half; k++)
                {
                    float wr = _cos[k * step];
                    float wi = sign * _sin[k * step];
                    int a = start + k;
                    int b = a + half;
                    float xr = (re[b] * wr) - (im[b] * wi);
                    float xi = (re[b] * wi) + (im[b] * wr);
                    re[b] = re[a] - xr;
                    im[b] = im[a] - xi;
                    re[a] += xr;
                    im[a] += xi;
                }
            }
        }
    }
}
