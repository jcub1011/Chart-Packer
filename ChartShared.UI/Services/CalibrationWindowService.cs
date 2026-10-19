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
    /// window. Only one is open at a time; opening it again brings that one to the front.
    /// </summary>
    /// <param name="openOutput">Opens the audio output at a rate, for the tool's clicks.</param>
    /// <param name="latency">The app's latency, which is loaded again when the tool saves.</param>
    public sealed class CalibrationWindowService(Func<int, IAudioOutput> openOutput, AudioLatencyService latency)
    {
        // The tool, while it's open.
        private CalibrationWindow? _open;

        /// <summary>Whether the tool is open.</summary>
        public bool IsOpen => _open is not null;

        /// <summary>Opens the tool over <paramref name="owner"/>, or on its own without one, and waits for it to close.</summary>
        public async Task ShowAsync(Window? owner)
        {
            if (_open is { } open)
            {
                open.Activate();
                return;
            }

            var window = new CalibrationWindow { DataContext = new CalibrationViewModel(openOutput, latency) };
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
        }
    }
}
