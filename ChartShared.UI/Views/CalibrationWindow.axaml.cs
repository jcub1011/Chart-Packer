using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ChartShared.UI.ViewModels;

namespace ChartShared.UI.Views
{
    /// <summary>
    /// The calibration tool's window (charter-spec C11), which Chart Packer and Chart Builder both open, for the
    /// audio/visual test or an input device's. In the first, the arrow keys move the latency, by 5 with Shift, and holding
    /// Space draws the notes with no calibration until it's let go. In the second, with the keyboard as the device, its keys
    /// are the device's, all but Escape. Closing it any way but Save leaves what's stored as it was.
    /// </summary>
    public partial class CalibrationWindow : Window
    {
        // The keys the keyboard device has down, let go when the window loses focus, as their key-ups go elsewhere.
        private readonly HashSet<PhysicalKey> _keysDown = [];

        public CalibrationWindow()
        {
            InitializeComponent();
            WindowChrome.ShowIcon(this, WindowTitleBar);
            Opened += (_, _) =>
            {
                WindowChrome.UseDarkTitleBar(this);
                if (DataContext is CalibrationViewModelBase vm)
                {
                    Highway.Clock = vm.Now;
                    vm.CloseRequested += (_, _) => Close();
                    vm.Start();
                }
            };
            Closed += (_, _) => (DataContext as CalibrationViewModelBase)?.Dispose();
            Deactivated += (_, _) => LetGo();

            AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
            AddHandler(KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);
        }

        private void OnCancel(object? sender, RoutedEventArgs e) => Close();

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            switch (DataContext)
            {
                case DeviceCalibrationViewModel { IsKeyboard: true } device when e.Key != Key.Escape:
                    // Handled before any button has it, so a tap of Space or Enter never presses one.
                    _keysDown.Add(e.PhysicalKey);
                    device.Key(e.PhysicalKey, down: true);
                    e.Handled = true;
                    break;

                // Not while a number is being typed.
                case CalibrationViewModel audio when e.Source is not TextBox:
                    OnAudioKeyDown(audio, e);
                    break;
            }
        }

        private void OnKeyUp(object? sender, KeyEventArgs e)
        {
            switch (DataContext)
            {
                case DeviceCalibrationViewModel { IsKeyboard: true } device when _keysDown.Remove(e.PhysicalKey):
                    device.Key(e.PhysicalKey, down: false);
                    e.Handled = true;
                    break;
                case CalibrationViewModel { IsHeldBySpace: true } audio when e.Key == Key.Space:
                    audio.IsHeldBySpace = false;
                    e.Handled = true;
                    break;
            }
        }

        private static void OnAudioKeyDown(CalibrationViewModel vm, KeyEventArgs e)
        {
            // Handled before any button has it, so Space never presses the button with the focus, Save included.
            if (e.Key == Key.Space)
            {
                vm.IsHeldBySpace = true;
                e.Handled = true;
                return;
            }

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

        // A key let go in another window never reaches this one, so what's held here is let go as it loses focus.
        private void LetGo()
        {
            switch (DataContext)
            {
                case CalibrationViewModel audio:
                    audio.IsHeldBySpace = false;
                    break;
                case DeviceCalibrationViewModel device:
                    foreach (var key in _keysDown)
                        device.Key(key, down: false);
                    break;
            }

            _keysDown.Clear();
        }
    }
}
