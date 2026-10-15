using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ChartBuilder.ViewModels;
using ChartShared.UI.Views;

namespace ChartBuilder.Views
{
    /// <summary>
    /// The calibration tool's window (charter-spec C11). The arrow keys move the latency, by 5 with Shift; a click on
    /// the light is a tap in the tap test. Closing it any way but Save leaves the stored latency as it was.
    /// </summary>
    /// <remarks>
    /// Taps are clicks only. Key events carry no timestamp in Avalonia 12, and the raw input events that do
    /// (<c>InputManager</c>, <c>RawKeyEventArgs</c>) aren't public, so a key's time would be when the UI thread got
    /// to it, which the tap test can't use.
    /// </remarks>
    public partial class CalibrationWindow : Window
    {
        // An input event this much older than when it's handled is taken to be on another clock than the one assumed.
        private const long MaxWaitMilliseconds = 1000;

        public CalibrationWindow()
        {
            InitializeComponent();
            Opened += (_, _) =>
            {
                WindowChrome.UseDarkTitleBar(this);
                if (ViewModel is { } vm)
                {
                    View.Clock = vm.Now;
                    vm.CloseRequested += (_, _) => Close();
                    vm.Start();
                }
            };
            Closed += (_, _) => ViewModel?.Dispose();

            View.PointerPressed += OnViewPressed;
            AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        }

        private CalibrationViewModel? ViewModel => DataContext as CalibrationViewModel;

        private void OnCancel(object? sender, RoutedEventArgs e) => Close();

        private void OnViewPressed(object? sender, PointerPressedEventArgs e)
        {
            if (ViewModel is not { IsTapTesting: true } vm)
                return;

            vm.Tap(TapTime(vm, e.Timestamp));
            e.Handled = true;
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            // The arrow keys move the latency, but not while a number is being typed.
            if (ViewModel is not { IsManual: true } vm || e.Source is TextBox)
                return;

            int step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 5 : 1;
            switch (e.Key)
            {
                case Key.Left or Key.Down:
                    vm.Value -= step;
                    e.Handled = true;
                    break;
                case Key.Right or Key.Up:
                    vm.Value += step;
                    e.Handled = true;
                    break;
            }
        }

        /// <summary>
        /// When an input event happened, on the clock the beats are on: now, less how long the event waited to be
        /// handled. Its timestamp is on the OS's input clock, which on Windows is the milliseconds since boot that
        /// <c>Environment.TickCount</c> counts too. Elsewhere the wait isn't known, and is taken as none.
        /// </summary>
        private static double TapTime(CalibrationViewModel vm, ulong timestamp)
        {
            double now = vm.Now();
            if (!OperatingSystem.IsWindows())
                return now;

            long waited = unchecked((uint)Environment.TickCount - (uint)timestamp);
            return waited < MaxWaitMilliseconds ? now - (waited / 1000.0) : now;
        }
    }
}
