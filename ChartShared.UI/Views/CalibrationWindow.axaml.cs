using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ChartShared.UI.ViewModels;

namespace ChartShared.UI.Views
{
    /// <summary>
    /// The calibration tool's window (charter-spec C11), which Chart Packer and Chart Builder both open. The arrow keys
    /// move the latency, by 5 with Shift; holding the hold button or Space draws the notes with no calibration until
    /// it's let go. Closing it any way but Save leaves the stored latency as it was.
    /// </summary>
    public partial class CalibrationWindow : Window
    {
        // Whether Space, or the hold button, is held down.
        private bool _isSpaceHeld;
        private bool _isButtonHeld;

        public CalibrationWindow()
        {
            InitializeComponent();
            WindowChrome.ShowIcon(this, WindowTitleBar);
            Opened += (_, _) =>
            {
                WindowChrome.UseDarkTitleBar(this);
                if (ViewModel is { } vm)
                {
                    Highway.Clock = vm.Now;
                    vm.CloseRequested += (_, _) => Close();
                    vm.Start();
                }
            };
            Closed += (_, _) => ViewModel?.Dispose();

            // A key let go in another window never reaches this one, so calibration comes back on as it loses focus.
            Deactivated += (_, _) =>
            {
                _isSpaceHeld = false;
                ShowHeld();
            };

            HoldButton.PropertyChanged += OnHoldButtonPropertyChanged;
            AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
            AddHandler(KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);
        }

        private CalibrationViewModel? ViewModel => DataContext as CalibrationViewModel;

        private void OnCancel(object? sender, RoutedEventArgs e) => Close();

        private void OnHoldButtonPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property != Button.IsPressedProperty)
                return;

            _isButtonHeld = HoldButton.IsPressed;
            ShowHeld();
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            // Not while a number is being typed.
            if (ViewModel is not { } vm || e.Source is TextBox)
                return;

            // Handled before any button has it, so Space never presses the button with the focus, Save included.
            if (e.Key == Key.Space)
            {
                _isSpaceHeld = true;
                ShowHeld();
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

        private void OnKeyUp(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space || !_isSpaceHeld)
                return;

            _isSpaceHeld = false;
            ShowHeld();
            e.Handled = true;
        }

        private void ShowHeld()
        {
            if (ViewModel is { } vm)
                vm.IsCalibrationOff = _isSpaceHeld || _isButtonHeld;
        }
    }
}
