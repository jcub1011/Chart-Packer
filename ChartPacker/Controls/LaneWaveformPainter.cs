using System;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media;
using ChartPacker.Audio.Waveforms;

namespace ChartPacker.Controls
{
    /// <summary>
    /// Works out a waveform's levels over a stretch of time, and draws one behind a lane's notes: along the lane's
    /// time axis, centred across it, as <see cref="WaveformView"/> draws a column, its peaks faintly behind how loud it is.
    /// </summary>
    internal static class LaneWaveformPainter
    {
        /// <summary>How loud a stretch must be to fill the height: a full-scale sine, at -3 dBFS.</summary>
        public const double RmsGain = 1.41;

        // Each bar is at least this long along the lane, so a slow lane isn't drawn as thousands of slivers.
        private const double MinBarLength = 2;

        // A track whose loudest peak is below this (-60 dBFS) is silent: scaling it up would only draw its noise.
        private const double SilentPeak = 0.001;

        // Each waveform's full-scale gain, worked out once: a waveform never changes, and one being read is a new one each time.
        private static readonly ConditionalWeakTable<Waveform, StrongBox<double>> FullScaleGains = new();

        /// <summary>
        /// Draws what's been read of <paramref name="waveform"/> between <paramref name="from"/> and <paramref name="to"/>
        /// seconds, across <paramref name="bounds"/>.
        /// </summary>
        /// <param name="isVertical">Whether time runs up or down the lane, rather than across it.</param>
        /// <param name="position">Where along the lane a time is drawn, in the lane's coordinates; linear.</param>
        /// <param name="gain">How much louder than it is to draw it, e.g. <see cref="FullScaleGain"/>.</param>
        public static void Draw(DrawingContext context, Waveform waveform, Rect bounds, bool isVertical, double from, double to, Func<double, double> position, IBrush peakBrush, IBrush loudBrush, double gain = 1)
        {
            double perBucket = Math.Abs(position(1) - position(0)) / WaveformPeaks.BucketsPerSecond;
            if (waveform.ReadCount == 0 || perBucket <= 0 || double.IsNaN(perBucket) || double.IsInfinity(perBucket))
                return;

            // Whole buckets to a bar, on a grid from the song's start, so the bars keep their places as the lane moves.
            int buckets = Math.Max(1, (int)Math.Ceiling(MinBarLength / perBucket));
            int first = Math.Max(0, (int)Math.Floor(from * WaveformPeaks.BucketsPerSecond / buckets) * buckets);
            int end = Math.Min(waveform.ReadCount, (int)Math.Ceiling(to * WaveformPeaks.BucketsPerSecond));

            double centre = isVertical ? bounds.Center.X : bounds.Center.Y;
            double half = (isVertical ? bounds.Width : bounds.Height) / 2;
            for (int bucket = first; bucket < end; bucket += buckets)
            {
                var (low, high, rms) = Levels(waveform, bucket, bucket + buckets);
                (low, high, rms) = (low * gain, high * gain, rms * gain);
                double a = position((double)bucket / WaveformPeaks.BucketsPerSecond);
                double b = position((double)(bucket + buckets) / WaveformPeaks.BucketsPerSecond);
                double near = Math.Min(a, b), length = Math.Abs(b - a);

                // Never past the peaks: a square wave is as loud as its peaks, and no louder.
                double loud = Math.Min(rms * RmsGain, Math.Max(-low, high));
                Bar(context, peakBrush, isVertical, near, length, centre, half, -low, high);
                Bar(context, loudBrush, isVertical, near, length, centre, half, loud, loud);
            }
        }

        /// <summary>
        /// The gain that brings the loudest peak read so far of <paramref name="waveform"/> to full scale, so a quiet
        /// track's beats stand out as clearly as a loud one's; 1 for a silent track.
        /// </summary>
        /// <remarks>While a track is read the gain can drop, as a louder part is read, until the whole track is.</remarks>
        public static double FullScaleGain(Waveform waveform) =>
            FullScaleGains.GetValue(waveform, w =>
            {
                double peak = 0;
                for (int i = 0; i < w.ReadCount; i++)
                    peak = Math.Max(peak, Math.Max(-w.Min[i], w.Max[i]));
                return new StrongBox<double>(peak > SilentPeak ? 1 / peak : 1);
            }).Value;

        /// <summary>The lowest and highest level between two times, and how loud it is across them.</summary>
        public static (double Low, double High, double Rms) Levels(Waveform waveform, double fromSeconds, double toSeconds) =>
            Levels(waveform, (int)Math.Floor(fromSeconds * WaveformPeaks.BucketsPerSecond), (int)Math.Ceiling(toSeconds * WaveformPeaks.BucketsPerSecond));

        /// <summary>The lowest and highest level of buckets <paramref name="first"/> up to <paramref name="last"/>, and how loud they are together.</summary>
        public static (double Low, double High, double Rms) Levels(Waveform waveform, int first, int last)
        {
            first = Math.Max(0, first);
            last = Math.Min(waveform.Count, last);

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

        /// <summary>A bar from <paramref name="near"/> along the lane, reaching <paramref name="before"/> and <paramref name="after"/> of the way from the centre to each side.</summary>
        private static void Bar(DrawingContext context, IBrush brush, bool isVertical, double near, double length, double centre, double half, double before, double after)
        {
            double start = centre - (Math.Clamp(isVertical ? before : after, 0, 1) * half);
            double stop = centre + (Math.Clamp(isVertical ? after : before, 0, 1) * half);
            if (stop - start < 1)
                return;

            context.FillRectangle(brush, isVertical ? new Rect(start, near, stop - start, length) : new Rect(near, start, length, stop - start));
        }
    }
}
