using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using ChartShared.Audio.Playback;
using ChartShared.UI.ViewModels;
using ChartShared.UI.Views;

namespace ChartShared.UI.Services
{
    /// <summary>
    /// Opens the calibration tool (charter-spec C11), which Chart Packer and Chart Builder share, as each app's own
    /// window: the audio/visual test, or an input device's. Only one is open at a time; opening it again, for either test,
    /// brings the one open to the front, and says so.
    /// </summary>
    /// <param name="openOutput">Opens the audio output at a rate, for the audio/visual test's clicks.</param>
    /// <param name="latency">The app's latency, which is loaded again when the audio/visual test saves.</param>
    /// <param name="opening">
    /// Run as the tool opens, before its test is made, never when it's open already: to pause the app's playback, so the
    /// test's notes are the only ones moving.
    /// </param>
    public sealed class CalibrationWindowService(Func<int, IAudioOutput> openOutput, AudioLatencyService latency, Action? opening = null)
    {
        // The tool, while it's open.
        private CalibrationWindow? _open;

        /// <summary>Whether the tool is open.</summary>
        public bool IsOpen => _open is not null;

        /// <summary>Opens the audio/visual test over <paramref name="owner"/>, or on its own without one, and waits for it to close.</summary>
        /// <returns>Whether it ran: false when the tool was open already, which is brought to the front instead.</returns>
        public Task<bool> ShowAsync(Window? owner) => ShowAsync(owner, () => new CalibrationViewModel(openOutput, latency));

        /// <summary>Opens <paramref name="input"/>'s test over <paramref name="owner"/>, or on its own without one, and waits for it to close.</summary>
        /// <returns>Whether it ran: false when the tool was open already, which is brought to the front instead.</returns>
        public Task<bool> ShowDeviceAsync(Window? owner, ICalibrationInput input) => ShowAsync(owner, () => new DeviceCalibrationViewModel(input));

        private async Task<bool> ShowAsync(Window? owner, Func<CalibrationViewModelBase> test)
        {
            if (_open is { } open)
            {
                open.Activate();
                return false;
            }

            opening?.Invoke();
            var window = new CalibrationWindow { DataContext = test() };
            _open = window;
            try
            {
                if (owner is null)
                {
                    var closed = new TaskCompletionSource();
                    window.Closed += (_, _) => closed.TrySetResult();
                    window.Show();
                    await closed.Task;
                }
                else
                {
                    WindowChrome.UseOwnerIcon(window, owner);
                    await window.ShowDialog(owner);
                }
            }
            finally
            {
                _open = null;
            }

            return true;
        }
    }
}
