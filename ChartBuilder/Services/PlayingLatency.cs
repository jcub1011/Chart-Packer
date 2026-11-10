using System.Runtime;

namespace ChartBuilder.Services
{
    /// <summary>
    /// While a song plays, asks the garbage collector to put off its long, blocking collections of the oldest objects, which hold every
    /// thread, the drawing's too, for as long as they take: a hitch in the highways moving. It goes back to how it was once nothing plays.
    /// </summary>
    /// <remarks>Counted, as more than one player can play at once; on the UI thread or not, so locked.</remarks>
    internal static class PlayingLatency
    {
        private static readonly object Gate = new();
        private static int s_playing;
        private static GCLatencyMode s_before;

        /// <summary>A song began to play.</summary>
        public static void Hold()
        {
            lock (Gate)
            {
                if (s_playing++ == 0)
                {
                    s_before = GCSettings.LatencyMode;
                    GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
                }
            }
        }

        /// <summary>A song that began to play stopped, or its player went.</summary>
        public static void Release()
        {
            lock (Gate)
            {
                if (s_playing > 0 && --s_playing == 0)
                    GCSettings.LatencyMode = s_before;
            }
        }
    }
}
