using Avalonia;
using Avalonia.Controls;
using ChartShared.UI.ViewModels;

namespace ChartShared.UI.Views
{
    /// <summary>The audio/visual test's controls in <see cref="CalibrationWindow"/>. Holding the hold button holds calibration off.</summary>
    public partial class AudioCalibrationPanel : UserControl
    {
        public AudioCalibrationPanel()
        {
            InitializeComponent();
            HoldButton.PropertyChanged += OnHoldButtonPropertyChanged;
        }

        private void OnHoldButtonPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Button.IsPressedProperty && DataContext is CalibrationViewModel vm)
                vm.IsHeldByButton = HoldButton.IsPressed;
        }
    }
}
