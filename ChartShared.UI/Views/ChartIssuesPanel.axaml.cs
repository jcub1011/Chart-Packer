using Avalonia;
using Avalonia.Controls;

namespace ChartShared.UI.Views
{
    /// <summary>
    /// A chart's findings, searched, filtered and in sections by how serious they are, with their counts at the foot: Chart
    /// Packer's chart issues window and Chart Builder's Findings tab alike.
    /// </summary>
    public partial class ChartIssuesPanel : UserControl
    {
        public static readonly StyledProperty<object?> FooterActionsProperty =
            AvaloniaProperty.Register<ChartIssuesPanel, object?>(nameof(FooterActions));

        public ChartIssuesPanel() => InitializeComponent();

        /// <summary>What the host puts at the right of the counts, such as a window's Close button.</summary>
        public object? FooterActions
        {
            get => GetValue(FooterActionsProperty);
            set => SetValue(FooterActionsProperty, value);
        }
    }
}
