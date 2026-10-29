using System;
using System.Globalization;
using Avalonia.Threading;
using ChartBuilder.Resources;
using ChartBuilder.Services;
using ChartShared.UI.Keybinds;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChartBuilder.ViewModels
{
    // The 3, 2, 1 before record mode or a playtest starts the song.
    public sealed partial class ChartEditorViewModel
    {
        private PlaybackCountdownRun? _countdown;
        private DispatcherTimer? _countdownTimer;

        /// <summary>The number counted down to, "3", "2" or "1", for the Editor to show; null when it's not counting down.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsCountingDown))]
        public partial string? CountdownText { get; private set; }

        /// <summary>Whether it's counting down to the song starting.</summary>
        public bool IsCountingDown => CountdownText is not null;

        /// <summary>
        /// Asked before the song starts from the play button or key: counts down first, with record mode or playtest on and
        /// counting down for it in Settings, and returns true as the start is left till then. Asked again during the count,
        /// it ends it, as pressing play again does.
        /// </summary>
        /// <remarks>
        /// The song starts, and a pass of record mode takes its range, from where the playhead and selection are when the
        /// count ends, so scrubbing during it moves where it starts. It ends without starting if what it's for is turned off
        /// by then, or the editor is closed.
        /// </remarks>
        private bool DeferPlayStart(Action start)
        {
            if (_countdown is not null)
            {
                CancelCountdown();
                return true;
            }

            if (_playbackBorrows > 0 || !CountdownWanted())
                return false;

            _countdown = new PlaybackCountdownRun(
                _settings.Settings.CountdownSeconds,
                CountdownWanted,
                number => ShowCountdown(number),
                () => StartAfterCountdown(start));
            _countdownTimer ??= CreateCountdownTimer();
            _countdownTimer.Start();
            ShowCountdown(_countdown.Remaining);
            return true;
        }

        // Whether record mode or playtest is still on, and counting down for it in Settings.
        private bool CountdownWanted() =>
            PlaybackCountdown.ShouldRun(_settings.Settings.PlaybackCountdown, IsRecordArmed, Playtest.IsArmed);

        private DispatcherTimer CreateCountdownTimer()
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (_, _) => OnCountdownTick();
            return timer;
        }

        private void OnCountdownTick()
        {
            if (_countdown is not { } run)
                return;

            run.Tick();

            // Ended without starting the song, as what it was for was turned off.
            if (ReferenceEquals(_countdown, run) && !run.IsActive)
                EndCountdown();
        }

        private void StartAfterCountdown(Action start)
        {
            // Cleared first, so the number is gone as the song starts.
            EndCountdown();
            start();

            // A pass of record mode says it's recording as it starts; a playtest has nothing to say, which would leave the count up.
            if (Playtest.IsArmed)
                AnnouncePlaytestOn();
        }

        private void ShowCountdown(int number)
        {
            var text = number.ToString(CultureInfo.CurrentCulture);
            CountdownText = text;
            Messages.Info(string.Format(
                CultureInfo.CurrentCulture,
                Strings.CountdownMessageFormat,
                text,
                AppKeys.Map.Display(SharedKeys.PlayPause),
                AppKeys.Map.Display(BuilderKeys.Cancel)),
                RecordMessageKey);
        }

        /// <summary>Stops the count without starting the song: it was cancelled, or record mode or playtest was turned off.</summary>
        /// <param name="announce">Whether to say so on the message bar.</param>
        public void CancelCountdown(bool announce = true)
        {
            if (_countdown is null)
                return;

            EndCountdown();
            if (announce)
                Messages.Info(Strings.CountdownCancelled, RecordMessageKey);
        }

        private void EndCountdown()
        {
            StopCountdown();
            CountdownText = null;
        }

        // The count and its clock, without telling the view, as closing the editor needs.
        private void StopCountdown()
        {
            _countdownTimer?.Stop();
            _countdown?.Cancel();
            _countdown = null;
        }
    }
}
