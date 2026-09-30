using System;
using Avalonia;
using Avalonia.Media;
using ChartPacker.Audio.Waveforms;

namespace ChartPacker.Controls
{
    /// <summary>
    /// Draws a waveform behind a lane's notes: along the lane's time axis, centred across it, as
    /// <see cref="WaveformView"/> draws a column, its peaks faintly behind how loud it is.
    /// </summary>
    internal static class LaneWaveformPainter
    {
        // Each bar is at least this long along the lane, so a slow lane isn't drawn as thousands of slivers.
        private const double MinBarLength = 2;

        // A track whose loudest peak is below this (-60 dBFS) is silent: scaling it up would only draw its noise.
        private const double SilentPeak = 0.001;

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
                // The bar at the edge of what's been read ends there, rather than reaching into what hasn't.
                int last = Math.Min(bucket + buckets, waveform.ReadCount);
                var (low, high, rms) = WaveformLevels.Of(waveform, bucket, last);
                (low, high, rms) = (low * gain, high * gain, rms * gain);
                double a = position((double)bucket / WaveformPeaks.BucketsPerSecond);
                double b = position((double)last / WaveformPeaks.BucketsPerSecond);
                double near = Math.Min(a, b), length = Math.Abs(b - a);

                double loud = WaveformLevels.Loudness(low, high, rms);
                Bar(context, peakBrush, isVertical, near, length, centre, half, -low, high);
                Bar(context, loudBrush, isVertical, near, length, centre, half, loud, loud);
            }
        }

        /// <summary>
        /// The gain that brings the loudest peak read so far of <paramref name="waveform"/> to full scale, so a quiet
        /// track's beats stand out as clearly as a loud one's; 1 for a silent track.
        /// </summary>
        /// <remarks>While a track is read the gain can drop, as a louder part is read, until the whole track is.</remarks>
        public static double FullScaleGain(Waveform waveform) => waveform.Peak > SilentPeak ? 1 / waveform.Peak : 1;

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
