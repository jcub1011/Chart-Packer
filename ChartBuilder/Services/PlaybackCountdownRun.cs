using System;

namespace ChartBuilder.Services
{
    /// <summary>
    /// A <see cref="PlaybackCountdown"/> being counted, a second at a time by whoever owns the clock: it shows each number,
    /// then starts the song, unless it was cancelled or is no longer wanted by the time a second goes by.
    /// </summary>
    public sealed class PlaybackCountdownRun
    {
        private readonly PlaybackCountdown _countdown;
        private readonly Func<bool> _isWanted;
        private readonly Action<int> _shown;
        private readonly Action _start;

        /// <param name="seconds">The number to count down from, kept within what Settings allows.</param>
        /// <param name="isWanted">Asked each second: whether what the count is for, record mode or playtest being on, still is.</param>
        /// <param name="shown">Called with each number after the first, which the caller shows itself, as it counts down to 1.</param>
        /// <param name="start">Called once, after the last second, to start the song.</param>
        public PlaybackCountdownRun(int seconds, Func<bool> isWanted, Action<int> shown, Action start)
        {
            ArgumentNullException.ThrowIfNull(isWanted);
            ArgumentNullException.ThrowIfNull(shown);
            ArgumentNullException.ThrowIfNull(start);
            _countdown = new PlaybackCountdown(seconds);
            _isWanted = isWanted;
            _shown = shown;
            _start = start;
            IsActive = true;
        }

        /// <summary>The number being shown.</summary>
        public int Remaining => _countdown.Remaining;

        /// <summary>Whether it's still counting: false once it started the song, was cancelled, or was no longer wanted.</summary>
        public bool IsActive { get; private set; }

        /// <summary>A second goes by. Does nothing once it's over.</summary>
        public void Tick()
        {
            if (!IsActive)
                return;

            if (!_isWanted())
            {
                IsActive = false;
                return;
            }

            if (!_countdown.Tick())
            {
                _shown(_countdown.Remaining);
                return;
            }

            IsActive = false;
            _start();
        }

        /// <summary>Ends the count without starting the song.</summary>
        public void Cancel() => IsActive = false;
    }
}
