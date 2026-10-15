using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace ChartBuilder.Controls
{
    /// <summary>One of the Editor's lanes, for an <see cref="ViewModels.EditorLaneViewModel"/>. Its look is in EditorLane.axaml.</summary>
    public partial class EditorLane : UserControl
    {
        // How much wider than a layout needs the header's room has to grow, in pixels, before it comes back in place of
        // a narrower one, so a width on the edge doesn't flip between them.
        private const double LettersHysteresis = 8;

        // How wide the difficulty letters are, as last measured while they were shown. Starts at roughly four
        // buttons, so a narrow lane starts on the drop-down instead of flashing the letters first.
        private double _lettersWidth = 90;

        // How wide the difficulty drop-down is, as last measured while it was shown; its style fixes it at 60.
        private double _pickerWidth = 60;

        // How wide the tools that fold into the "more" menu are, as last measured while they were shown. Starts at
        // roughly four buttons, as the letters do.
        private double _toolsWidth = 112;

        public EditorLane()
        {
            InitializeComponent();
            HeaderGrid.SizeChanged += (_, _) => FitHeader();
            PartButton.SizeChanged += (_, _) => FitHeader();
            LaneTools.SizeChanged += (_, _) => FitHeader();
            DifficultyLetters.SizeChanged += (_, _) => FitHeader();
            DifficultyPicker.SizeChanged += (_, _) => FitHeader();
            OverflowTools.SizeChanged += (_, _) => FitHeader();
            MoreHost.SizeChanged += (_, _) => FitHeader();
        }

        /// <summary>The header, which <see cref="LaneDockHost"/> drags the lane by.</summary>
        public Control DragHandle => Header;

        /// <summary>
        /// Fits the header to the lane's width, widest first: the difficulty letters and every tool; a drop-down in the
        /// letters' place; then the audio, edit, ghost and view tools folded into the "more" menu as well.
        /// </summary>
        private void FitHeader()
        {
            if (DifficultyLetters.IsVisible && DifficultyLetters.Bounds.Width > 0)
                _lettersWidth = DifficultyLetters.Bounds.Width;
            if (DifficultyPicker.IsVisible && DifficultyPicker.Bounds.Width > 0)
                _pickerWidth = DifficultyPicker.Bounds.Width;
            if (OverflowTools.IsVisible && OverflowTools.Bounds.Width > 0)
                _toolsWidth = OverflowTools.Bounds.Width;

            // The room left for the difficulties and the folding tools, beside the part and the tools that stay.
            var folding = OverflowTools.IsVisible ? OverflowTools : (Control)MoreHost;
            var area = DifficultyLetters.Parent as Control;
            double room = HeaderGrid.Bounds.Width - PartButton.Bounds.Width - (LaneTools.Bounds.Width - folding.Bounds.Width)
                - (area?.Margin.Left ?? 0);

            int current = DifficultyLetters.IsVisible ? 0 : OverflowTools.IsVisible ? 1 : 2;
            int fit = Fits(0, _lettersWidth + _toolsWidth) ? 0 : Fits(1, _pickerWidth + _toolsWidth) ? 1 : 2;

            DifficultyLetters.IsVisible = fit == 0;
            DifficultyPicker.IsVisible = fit > 0 && DifficultyLetters.ItemCount > 0;
            OverflowTools.IsVisible = fit < 2;
            MoreHost.IsVisible = fit == 2;

            // A layout no wider than the current one needs only its room; a wider one needs a little more.
            bool Fits(int layout, double needs) => room >= needs + (layout < current ? LettersHysteresis : 0);
        }

        /// <summary>Opens the lane's audio settings from the "more" menu, once the menu has closed.</summary>
        private void OnAudioMenuClick(object? sender, RoutedEventArgs e)
        {
            if (Resources["AudioFlyout"] is Flyout flyout)
                Dispatcher.UIThread.Post(() => flyout.ShowAt(MoreButton));
        }
    }
}
