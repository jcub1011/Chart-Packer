using System;
using ChartPacker.Audio.Waveforms;

namespace ChartPacker.UI.Controls
{
    /// <summary>
    /// A waveform's levels over a stretch of time, and how loud to draw them, for each control that draws one.
    /// Only what's been read counts: the buckets past <see cref="Waveform.ReadCount"/> are silent for now, not quiet.
    /// </summary>
    public static class WaveformLevels
    {
        /// <summary>How loud a stretch must be to fill the height: a full-scale sine, at -3 dBFS.</summary>
        public const double RmsGain = 1.41;

        /// <summary>The lowest and highest level between two times, and how loud it is across them.</summary>
        public static (double Low, double High, double Rms) Of(Waveform waveform, double fromSeconds, double toSeconds) =>
            waveform.LevelsOf((int)Math.Floor(fromSeconds * waveform.BucketsPerSecond), (int)Math.Ceiling(toSeconds * waveform.BucketsPerSecond));

        /// <summary>How far to draw a stretch's loudness from the centre: its root mean square at <see cref="RmsGain"/>, never past its peaks.</summary>
        /// <remarks>A square wave is as loud as its peaks, and no louder.</remarks>
        public static double Loudness(double low, double high, double rms) => Math.Min(rms * RmsGain, Math.Max(-low, high));
    }
}
