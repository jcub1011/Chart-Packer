using Avalonia;
using Avalonia.Controls;

namespace ChartPacker.Views
{
    public partial class ToolbarView : UserControl
    {
        // How much wider than the labelled row needs the toolbar has to grow, in pixels, before the labels come back,
        // so a width on the edge doesn't flip between them.
        private const double LabelsHysteresis = 8;

        // How wide the toolbar is on one row with every label shown, as last measured while they were. It starts at
        // nothing, so the first layout shows the labels and measures them; the class it then sets is laid out again
        // before anything is drawn.
        private double _labelledWidth;

        public ToolbarView()
        {
            InitializeComponent();
        }

        /// <summary>Fits the toolbar once everything in it is arranged, so every part's width is current.</summary>
        protected override Size ArrangeOverride(Size finalSize)
        {
            var arranged = base.ArrangeOverride(finalSize);
            FitToolbar();
            return arranged;
        }

        /// <summary>
        /// Drops the labelled buttons' labels (the compact class) while the toolbar is too narrow for one labelled row.
        /// Narrower still, the groups wrap.
        /// </summary>
        private void FitToolbar()
        {
            bool compact = Classes.Contains("compact");
            if (!compact)
                _labelledWidth = LabelledWidth();

            double room = ToolbarGrid.Bounds.Width;
            if (room < _labelledWidth)
                Classes.Set("compact", true);
            else if (room >= _labelledWidth + LabelsHysteresis)
                Classes.Set("compact", false);
        }

        /// <summary>The groups side by side, as one unwrapped row, beside the trailing tools.</summary>
        private double LabelledWidth()
        {
            double width = 0;
            int shown = 0;
            foreach (var group in ToolsRow.Children)
            {
                if (!group.IsVisible)
                    continue;
                width += group.Bounds.Width + group.Margin.Left + group.Margin.Right;
                shown++;
            }
            if (shown > 1)
                width += ToolsRow.ItemSpacing * (shown - 1);
            return width + ToolbarGrid.ColumnSpacing + TrailingTools.Bounds.Width;
        }
    }
}
