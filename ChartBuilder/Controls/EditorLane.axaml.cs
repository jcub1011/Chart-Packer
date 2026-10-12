using Avalonia.Controls;

namespace ChartBuilder.Controls
{
    /// <summary>One of the Editor's lanes, for an <see cref="ViewModels.EditorLaneViewModel"/>. Its look is in EditorLane.axaml.</summary>
    public partial class EditorLane : UserControl
    {
        // How much wider than the letters the header's room has to grow, in pixels, before they come back in place of the
        // drop-down, so a width on the edge doesn't flip between them.
        private const double LettersHysteresis = 8;

        // How wide the difficulty letters are, as last measured while they were shown. Starts at roughly four
        // buttons, so a narrow lane starts on the drop-down instead of flashing the letters first.
        private double _lettersWidth = 90;

        public EditorLane()
        {
            InitializeComponent();
            HeaderGrid.SizeChanged += (_, _) => FitDifficulties();
            PartButton.SizeChanged += (_, _) => FitDifficulties();
            LaneTools.SizeChanged += (_, _) => FitDifficulties();
            DifficultyLetters.SizeChanged += (_, _) => FitDifficulties();
        }

        /// <summary>The header, which <see cref="LaneDockHost"/> drags the lane by.</summary>
        public Control DragHandle => Header;

        /// <summary>
        /// Shows the difficulty letters where the header has room for them beside the part and the tools, and a drop-down
        /// in their place where it hasn't.
        /// </summary>
        private void FitDifficulties()
        {
            if (DifficultyLetters.IsVisible && DifficultyLetters.Bounds.Width > 0)
                _lettersWidth = DifficultyLetters.Bounds.Width;
            var area = DifficultyLetters.Parent as Control;
            double room = HeaderGrid.Bounds.Width - PartButton.Bounds.Width - LaneTools.Bounds.Width - (area?.Margin.Left ?? 0);
            bool narrow = DifficultyLetters.IsVisible ? room < _lettersWidth : room < _lettersWidth + LettersHysteresis;
            DifficultyLetters.IsVisible = !narrow;
            DifficultyPicker.IsVisible = narrow && DifficultyLetters.ItemCount > 0;
        }
    }
}
