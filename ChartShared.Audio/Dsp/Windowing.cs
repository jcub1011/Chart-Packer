namespace ChartShared.Audio.Dsp;

/// <summary>Windows for taking a stretch of audio at a time.</summary>
internal static class Windowing
{
    /// <summary>
    /// A periodic Hann window of <paramref name="length"/> samples: rising from 0 to 1 at the middle and back, and two of
    /// them half a window apart add up to 1 everywhere.
    /// </summary>
    public static float[] PeriodicHann(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);

        var window = new float[length];
        for (int n = 0; n < length; n++)
            window[n] = (float)(0.5 * (1 - Math.Cos(2 * Math.PI * n / length)));
        return window;
    }
}
