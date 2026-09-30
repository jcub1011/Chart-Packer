using System;
using ChartPacker.Audio.Waveforms;

namespace ChartPacker.Controls
{
    /// <summary>
    /// A waveform's levels over a stretch of time, and how loud to draw them, for each control that draws one.
    /// Only what's been read counts: the buckets past <see cref="Waveform.ReadCount"/> are silent for now, not quiet.
    /// </summary>
    internal static class WaveformLevels
    {
        /// <summary>How loud a stretch must be to fill the height: a full-scale sine, at -3 dBFS.</summary>
        public const double RmsGain = 1.41;

        /// <summary>The lowest and highest level between two times, and how loud it is across them.</summary>
        public static (double Low, double High, double Rms) Of(Waveform waveform, double fromSeconds, double toSeconds) =>
            Of(waveform, (int)Math.Floor(fromSeconds * WaveformPeaks.BucketsPerSecond), (int)Math.Ceiling(toSeconds * WaveformPeaks.BucketsPerSecond));

        /// <summary>The lowest and highest level of the read buckets from <paramref name="first"/> up to <paramref name="last"/>, and how loud they are together.</summary>
        public static (double Low, double High, double Rms) Of(Waveform waveform, int first, int last)
        {
            first = Math.Max(0, first);
            last = Math.Min(waveform.ReadCount, last);

            double low = 0, high = 0, squares = 0;
            for (int i = first; i < last; i++)
            {
                low = Math.Min(low, waveform.Min[i]);
                high = Math.Max(high, waveform.Max[i]);
                squares += (double)waveform.Rms[i] * waveform.Rms[i];
            }

            // The loudness of the buckets together, rather than of the loudest, so a stretch isn't only as quiet as its loudest moment.
            double rms = last > first ? Math.Sqrt(squares / (last - first)) : 0;
            return (low, high, rms);
        }

        /// <summary>How far to draw a stretch's loudness from the centre: its root mean square at <see cref="RmsGain"/>, never past its peaks.</summary>
        /// <remarks>A square wave is as loud as its peaks, and no louder.</remarks>
        public static double Loudness(double low, double high, double rms) => Math.Min(rms * RmsGain, Math.Max(-low, high));
    }
}
