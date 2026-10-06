using System;
using System.ComponentModel;
using Avalonia.Controls;
using ChartBuilder.Services;
using ChartBuilder.ViewModels;
using ChartPacker.Views;

namespace ChartBuilder.Views
{
    public partial class EditorWindow : Window
    {
        private const double SplitterSize = 4;

        private ColumnDefinition LeftColumn => PanelsGrid.ColumnDefinitions[0];

        private ColumnDefinition CenterColumn => PanelsGrid.ColumnDefinitions[2];

        private ColumnDefinition RightColumn => PanelsGrid.ColumnDefinitions[4];

        private RowDefinition PanelsRow => RootGrid.RowDefinitions[1];

        private RowDefinition TimelineRow => RootGrid.RowDefinitions[3];

        private EditorWindowViewModel? _viewModel;

        public EditorWindow()
        {
            InitializeComponent();

            // The panel sizes are view state, so they're applied and captured here rather than bound.
            Opened += (_, _) =>
            {
                WindowChrome.UseDarkTitleBar(this);
                RestoreLayout();
            };
            Closing += (_, _) => CaptureLayout();
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);

            if (_viewModel is not null)
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = DataContext as EditorWindowViewModel;
            if (_viewModel is not null)
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_viewModel is not { } vm)
                return;

            // The panel's width is kept while it's collapsed, so expanding it brings it back as it was.
            switch (e.PropertyName)
            {
                case nameof(EditorWindowViewModel.IsLeftPanelCollapsed):
                    if (vm.IsLeftPanelCollapsed)
                        vm.LeftPanelWidth = LeftColumn.ActualWidth;
                    SizeSidePanel(LeftColumn, vm.IsLeftPanelCollapsed, vm.LeftPanelWidth, otherWidth: SidePanelWidth(RightColumn));
                    break;
                case nameof(EditorWindowViewModel.IsRightPanelCollapsed):
                    if (vm.IsRightPanelCollapsed)
                        vm.RightPanelWidth = RightColumn.ActualWidth;
                    SizeSidePanel(RightColumn, vm.IsRightPanelCollapsed, vm.RightPanelWidth, otherWidth: SidePanelWidth(LeftColumn));
                    break;
            }
        }

        private void RestoreLayout()
        {
            if (_viewModel is not { } vm)
                return;

            // Keep the chart and the panels row at least their minimum sizes if the window opens smaller than last time.
            // The left panel is sized first, so it's the right one that gives way when both don't fit.
            SizeSidePanel(LeftColumn, vm.IsLeftPanelCollapsed, vm.LeftPanelWidth, otherWidth: vm.IsRightPanelCollapsed ? 0 : RightColumn.MinWidth);
            SizeSidePanel(RightColumn, vm.IsRightPanelCollapsed, vm.RightPanelWidth, otherWidth: SidePanelWidth(LeftColumn));

            double maxHeight = Math.Max(TimelineRow.MinHeight, ClientSize.Height - RootGrid.RowDefinitions[0].ActualHeight - PanelsRow.MinHeight - SplitterSize);
            TimelineRow.Height = new GridLength(Math.Clamp(vm.TimelineHeight, TimelineRow.MinHeight, maxHeight), GridUnitType.Pixel);
        }

        /// <summary>
        /// Collapses <paramref name="column"/> to its strip, or gives it <paramref name="width"/>, as much of it as
        /// leaves the chart its minimum width beside the other side's <paramref name="otherWidth"/>.
        /// </summary>
        private void SizeSidePanel(ColumnDefinition column, bool collapsed, double width, double otherWidth)
        {
            if (collapsed)
            {
                column.MinWidth = 0;
                column.Width = GridLength.Auto;
                return;
            }

            column.MinWidth = BuilderSettings.MinEditorPanelWidth;
            double max = Math.Max(column.MinWidth, ClientSize.Width - CenterColumn.MinWidth - otherWidth - 2 * SplitterSize);
            column.Width = new GridLength(Math.Clamp(width, column.MinWidth, max), GridUnitType.Pixel);
        }

        /// <summary>How wide a side panel is now: its pixel width, or its strip's while collapsed.</summary>
        private static double SidePanelWidth(ColumnDefinition column) =>
            column.Width.IsAbsolute ? column.Width.Value : column.ActualWidth;

        private void CaptureLayout()
        {
            if (_viewModel is not { } vm)
                return;

            // A collapsed panel's width was kept as it collapsed; its column is only the strip now.
            if (!vm.IsLeftPanelCollapsed)
                vm.LeftPanelWidth = LeftColumn.ActualWidth;
            if (!vm.IsRightPanelCollapsed)
                vm.RightPanelWidth = RightColumn.ActualWidth;
            vm.TimelineHeight = TimelineRow.ActualHeight;
            vm.SaveLayout();
        }
    }
}
