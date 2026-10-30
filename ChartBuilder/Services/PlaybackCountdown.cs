namespace ChartBuilder.Services
{
    /// <summary>
    /// The count before record mode or a playtest starts the song, so there's time to get ready: which starts count down,
    /// and the count itself, a second at a time.
    /// </summary>
    public sealed class PlaybackCountdown
    {
        /// <summary>Starts a count from <paramref name="seconds"/>, kept within what Settings allows.</summary>
        /// <param name="seconds">The number to count down from, to 1.</param>
        public PlaybackCountdown(int seconds = BuilderSettings.DefaultCountdownSeconds) =>
            Remaining = BuilderSettings.ClampCountdownSeconds(seconds);

        /// <summary>The number being shown, from where it started down to 1, and 0 once it's over.</summary>
        public int Remaining { get; private set; }

        /// <summary>Whether it's counted down to the end, when the song starts.</summary>
        public bool IsDone => Remaining == 0;

        /// <summary>Whether starting the song counts down first, for the mode in Settings and what's armed.</summary>
        /// <param name="mode">Which starts count down.</param>
        /// <param name="recordArmed">Whether record mode is on.</param>
        /// <param name="playtestArmed">Whether playtest is on.</param>
        public static bool ShouldRun(PlaybackCountdownMode mode, bool recordArmed, bool playtestArmed) =>
            (recordArmed && (mode is PlaybackCountdownMode.Record or PlaybackCountdownMode.Both))
            || (playtestArmed && (mode is PlaybackCountdownMode.Play or PlaybackCountdownMode.Both));

        /// <summary>A second goes by: counts down one, and returns whether that was the last.</summary>
        public bool Tick()
        {
            if (Remaining > 0)
                Remaining--;
            return IsDone;
        }
    }
}
