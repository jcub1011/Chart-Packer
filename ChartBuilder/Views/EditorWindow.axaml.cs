using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ChartBuilder.Controls;
using ChartBuilder.Services;
using ChartBuilder.ViewModels;
using ChartPacker.UI.Views;

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

        private bool _isOpen;

        // The sizes last given to the side columns and the timeline row: NaN for a collapsed side, and before the
        // window opens. A definition that no longer matches was moved by its splitter, i.e. by the user.
        private double _leftApplied = double.NaN;
        private double _rightApplied = double.NaN;
        private double _timelineApplied = double.NaN;

        public EditorWindow()
        {
            InitializeComponent();

            // The panel sizes are view state, so they're applied and captured here rather than bound. The view model
            // holds the sizes the user chose; the grid shows as much of each as fits, and refits as the window, or the
            // toolbar above the panels, change size. Fitting never changes the sizes the user chose.
            Opened += (_, _) =>
            {
                WindowChrome.UseDarkTitleBar(this);
                _isOpen = true;
                ApplyLayout();
            };
            SizeChanged += (_, _) => ApplyLayout();
            TopBand.SizeChanged += (_, _) => ApplyLayout();
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

        /// <summary>
        /// Esc ends a run of Generate fills (charter-spec C6 item 5) when nothing focused took it first: a lane takes back
        /// its drag before it ends the run (EditorKeys), and a field or a list keeps its own Esc. Not a key binding, which
        /// would run before the focused control saw the key.
        /// </summary>
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None || _viewModel?.Editor.Session.Fills is not { IsActive: true } fills)
                return;
            fills.Cancel();
            e.Handled = true;
        }

        /// <summary>Generate fills' options close once the fills are proposed, so the ghosts can be seen.</summary>
        private void OnGenerateFillsClick(object? sender, RoutedEventArgs e) => GenerateFillsButton.Flyout?.Hide();

        private void OnProposeTempoMapClick(object? sender, RoutedEventArgs e) => ProposeTempoMapButton.Flyout?.Hide();

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // The panel's width is kept while it's collapsed, so expanding it brings it back as it was.
            if (e.PropertyName is nameof(EditorWindowViewModel.IsLeftPanelCollapsed) or nameof(EditorWindowViewModel.IsRightPanelCollapsed))
                ApplyLayout();
        }

        /// <summary>
        /// Gives the side panels and the timeline the sizes the user chose, as much of each as leaves the chart and
        /// the panels row their minimum sizes.
        /// </summary>
        private void ApplyLayout()
        {
            if (!_isOpen || _viewModel is not { } vm)
                return;

            // A splitter moved since the last fit is a size the user chose. Taken before refitting, which would undo it.
            AbsorbSplitterChanges(vm);

            // The left panel is fitted first, leaving the right one at least its minimum, so it's the right one that
            // gives way when both don't fit.
            double room = ClientSize.Width - CenterColumn.MinWidth;
            _leftApplied = SizeSidePanel(LeftColumn, vm.IsLeftPanelCollapsed, vm.LeftPanelWidth, room - LeastWidth(vm.IsRightPanelCollapsed));
            _rightApplied = SizeSidePanel(RightColumn, vm.IsRightPanelCollapsed, vm.RightPanelWidth, room - WidthTaken(_leftApplied));

            double maxHeight = Math.Floor(ClientSize.Height - TopBand.Bounds.Height - MessageBar.Bounds.Height - PanelsRow.MinHeight - SplitterSize);
            _timelineApplied = Math.Clamp(vm.TimelineHeight, TimelineRow.MinHeight, Math.Max(TimelineRow.MinHeight, maxHeight));
            TimelineRow.Height = new GridLength(_timelineApplied, GridUnitType.Pixel);
        }

        /// <summary>
        /// Collapses <paramref name="column"/> to its strip, or gives it <paramref name="preferred"/>, as much of it as
        /// fits with its splitter in <paramref name="room"/>.
        /// </summary>
        /// <returns>The width given, or NaN when collapsed.</returns>
        private static double SizeSidePanel(ColumnDefinition column, bool collapsed, double preferred, double room)
        {
            if (collapsed)
            {
                column.MinWidth = 0;
                column.Width = GridLength.Auto;
                return double.NaN;
            }

            double min = BuilderSettings.MinEditorPanelWidth;
            double width = Math.Clamp(preferred, min, Math.Max(min, Math.Floor(room - SplitterSize)));
            column.MinWidth = min;
            column.Width = new GridLength(width, GridUnitType.Pixel);
            return width;
        }

        /// <summary>The least a side panel takes: its strip while collapsed, or its minimum and its splitter.</summary>
        private static double LeastWidth(bool collapsed) =>
            collapsed ? EditorSidePanel.StripWidth : BuilderSettings.MinEditorPanelWidth + SplitterSize;

        /// <summary>What a side panel given <paramref name="applied"/> takes: its strip while collapsed (NaN), or that and its splitter.</summary>
        private static double WidthTaken(double applied) =>
            double.IsNaN(applied) ? EditorSidePanel.StripWidth : applied + SplitterSize;

        /// <summary>
        /// Takes the sizes the splitters were dragged (or moved with the keyboard) to as the ones the user wants. Only a
        /// splitter changes a definition from the size it was given: it writes the pixel one, beside the chart's star one.
        /// </summary>
        private void AbsorbSplitterChanges(EditorWindowViewModel vm)
        {
            vm.LeftPanelWidth = Absorb(LeftColumn.Width, _leftApplied, vm.LeftPanelWidth);
            vm.RightPanelWidth = Absorb(RightColumn.Width, _rightApplied, vm.RightPanelWidth);
            vm.TimelineHeight = Absorb(TimelineRow.Height, _timelineApplied, vm.TimelineHeight);
        }

        /// <summary>The definition's size, in whole pixels, if it was moved from <paramref name="applied"/>; otherwise <paramref name="preferred"/>.</summary>
        private static double Absorb(GridLength now, double applied, double preferred) =>
            !double.IsNaN(applied) && now.IsAbsolute && now.Value != applied ? Math.Round(now.Value) : preferred;

        private void CaptureLayout()
        {
            if (_viewModel is not { } vm)
                return;

            // Only what the user chose is saved, never a size the window was too small for.
            AbsorbSplitterChanges(vm);
            LaneHost.CaptureSizes();
            vm.SaveLayout();
        }
    }
}
