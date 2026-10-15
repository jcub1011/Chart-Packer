using System;
using System.Diagnostics;

namespace ChartPacker.UI.Controls
{
    /// <summary>
    /// How long the painters may spend drawing new pictures (of waveforms, of spectrograms) in a frame, so that zooming,
    /// which needs many at once, never holds up the UI thread for long (charter-spec AC C12.1). A painter that runs out
    /// says so, and its control draws again once the frame is out, to carry on.
    /// </summary>
    /// <remarks>For the UI thread only, as drawing is.</remarks>
    public static class PaintBudget
    {
        // In each frame's worth of time, at most this is spent drawing pictures.
        private static readonly long Window = Stopwatch.Frequency * 16 / 1000;
        private static readonly long Allowed = Stopwatch.Frequency * 6 / 1000;

        private static long s_windowStart;
        private static long s_spent;

        /// <summary>Whether there's time left in this frame to draw another picture.</summary>
        public static bool CanBuild()
        {
            long now = Stopwatch.GetTimestamp();
            if (now - s_windowStart > Window)
            {
                s_windowStart = now;
                s_spent = 0;
            }

            return s_spent < Allowed;
        }

        /// <summary>Counts the time since <paramref name="started"/> (a <see cref="Stopwatch"/> timestamp) as spent drawing.</summary>
        public static void Spend(long started) => s_spent += Math.Max(0, Stopwatch.GetTimestamp() - started);
    }
}
