using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using ChartBuilder.Controls;
using ChartBuilder.Services;
using ChartBuilder.ViewModels;
using ChartShared.UI.Views;

namespace ChartBuilder.Views
{
    public partial class EditorWindow : Window
    {
        private const double SplitterSize = 4;

        // How much of the window a flown-out panel leaves uncovered, at the least.
        private const double FlyoutGap = 80;

        private ColumnDefinition LeftColumn => PanelsGrid.ColumnDefinitions[0];

        private ColumnDefinition CenterColumn => PanelsGrid.ColumnDefinitions[2];

        private ColumnDefinition RightColumn => PanelsGrid.ColumnDefinitions[4];

        private RowDefinition PanelsRow => RootGrid.RowDefinitions[1];

        private RowDefinition TimelineRow => RootGrid.RowDefinitions[3];

        private EditorWindowViewModel? _viewModel;

        private bool _isOpen;

        // Space played or paused and hasn't been let go yet, so its auto-repeat doesn't toggle again.
        private bool _isSpaceHeld;

        private bool _isApplying;

        // The sizes last given to the side columns and the timeline row: NaN for an auto-hiding side, and before the
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

            // Keys held in record mode or playtest are let go when the window loses focus, as their key-ups won't come here.
            Deactivated += (_, _) =>
            {
                _isSpaceHeld = false;
                _viewModel?.Editor.RecordReleaseAll();
                _viewModel?.Editor.Playtest.ReleaseKeys();
            };
            TopBand.SizeChanged += (_, _) => ApplyLayout();
            TitleBand.SizeChanged += (_, e) => LoadingOverlay.Margin = new Thickness(0, e.NewSize.Height, 0, 0);
            Closing += (_, _) => CaptureLayout();

            // While a song opens, keys don't reach the Editor being left: its edits would go with it, unsaved. First, so
            // nothing below sees them. Key-ups still go through, so a key held then is let go of.
            AddHandler(KeyDownEvent, OnOpeningInput, RoutingStrategies.Tunnel);
            AddHandler(TextInputEvent, OnOpeningInput, RoutingStrategies.Tunnel);

            // Audio files dropped anywhere on the window start a new chart with them as its stems (charter-spec C1). A
            // song folder or .sng opens it (C2).
            DragDrop.SetAllowDrop(this, true);
            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            AddHandler(DragDrop.DropEvent, OnDrop);

            // Space plays or pauses, tunnelled so a focused button takes it as playback rather than as a click.
            AddHandler(KeyDownEvent, OnSpaceKeyDown, RoutingStrategies.Tunnel);
            AddHandler(KeyUpEvent, OnSpaceKeyUp, RoutingStrategies.Tunnel);

            // Playtest's keys, tunnelled, so they play wherever focus is: on the play button just clicked, say.
            AddHandler(KeyDownEvent, OnPlaytestKeyDown, RoutingStrategies.Tunnel);
            AddHandler(KeyUpEvent, OnPlaytestKeyUp, RoutingStrategies.Tunnel);
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);

            if (_viewModel is not null)
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = DataContext as EditorWindowViewModel;
            if (_viewModel is not null)
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;

            // A song or a new chart opened in place of another: the sizes given were the last one's, not moves of the
            // splitters, and the new one's sizes show straight away.
            _leftApplied = double.NaN;
            _rightApplied = double.NaN;
            _timelineApplied = double.NaN;
            _isSpaceHeld = false;
            ApplyLayout();
        }

        /// <summary>
        /// Esc ends a run of Generate fills or Generate star power (charter-spec C6 item 5, C8 item 1) when nothing focused
        /// took it first: a lane takes back its drag before it ends the run (EditorKeys), and a field or a list keeps its own
        /// Esc. Not a key binding, which would run before the focused control saw the key.
        /// </summary>
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None || _viewModel?.Editor.Session.Proposals is not { } run)
                return;
            run.Cancel();
            e.Handled = true;
        }

        /// <summary>
        /// Space anywhere in the window plays or pauses, and Shift+Space plays the selection, once a press however long
        /// it's held. Tunnelled, so a focused button or list plays rather than taking it. Not while a text box has focus:
        /// it types a space there. Not a key binding, which would run alongside the focused control's own Space.
        /// </summary>
        private void OnSpaceKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space)
                return;

            bool playSelection = e.KeyModifiers == KeyModifiers.Shift;
            if (!playSelection && e.KeyModifiers != KeyModifiers.None)
                return;

            if (e.Source is TextBox || FocusManager?.GetFocusedElement() is TextBox)
                return;

            e.Handled = true;
            if (_isSpaceHeld)
                return;

            _isSpaceHeld = true;
            if (_viewModel is not { } vm)
                return;

            if (playSelection)
                vm.Editor.PlaySelection();
            else
                vm.Editor.PlayPause();
        }

        /// <summary>
        /// While the song plays a playtest, a key bound to the lane's instrument plays rather than editing, wherever focus is
        /// (charter-spec C19 item 4). Not while a text box has focus, which types it.
        /// </summary>
        private void OnPlaytestKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Handled || _viewModel?.Editor.Playtest is not { IsActive: true } playtest || IsTyping(e))
                return;
            e.Handled = playtest.HandleKey(e.PhysicalKey, e.KeyModifiers, down: true);
        }

        private void OnPlaytestKeyUp(object? sender, KeyEventArgs e)
        {
            if (e.Handled || _viewModel?.Editor.Playtest is not { IsActive: true } playtest)
                return;
            e.Handled = playtest.HandleKey(e.PhysicalKey, e.KeyModifiers, down: false);
        }

        private void OnOpeningInput(object? sender, RoutedEventArgs e)
        {
            if (_viewModel is { IsOpening: true })
                e.Handled = true;
        }

        private bool IsTyping(KeyEventArgs e) => e.Source is TextBox || FocusManager?.GetFocusedElement() is TextBox;

        /// <summary>Letting go of a Space that played or paused, so the focused control doesn't see half a press.</summary>
        private void OnSpaceKeyUp(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space || !_isSpaceHeld)
                return;

            _isSpaceHeld = false;
            e.Handled = true;
        }

        /// <summary>Generate fills' options close once the fills are proposed, so the ghosts can be seen. Posted, as closing the flyout clears the button's bound command before it runs.</summary>
        private void OnGenerateFillsClick(object? sender, RoutedEventArgs e) => Dispatcher.UIThread.Post(() => GenerateFillsButton.Flyout?.Hide());

        private void OnGenerateStarPowerClick(object? sender, RoutedEventArgs e) => Dispatcher.UIThread.Post(() => GenerateStarPowerButton.Flyout?.Hide());

        private void OnDragOver(object? sender, DragEventArgs e) =>
            e.DragEffects = DroppedFiles.SongOf(e) is not null || DroppedFiles.AudioOf(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;

        private void OnDrop(object? sender, DragEventArgs e)
        {
            if (_viewModel is not { } vm)
                return;

            // Posted, since it swaps what the window edits and the drop is still being handled.
            if (DroppedFiles.SongOf(e) is { } song)
            {
                Dispatcher.UIThread.Post(() => _ = vm.OpenSongAsync(song));
                return;
            }

            var files = DroppedFiles.AudioOf(e);
            if (files.Count > 0)
                Dispatcher.UIThread.Post(() => _ = vm.StartNewChartAsync(files));
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // The panel's width is kept while it auto-hides, so pinning it brings it back as it was; flown out, it's as wide.
            if (e.PropertyName is nameof(EditorWindowViewModel.IsLeftPanelAutoHide) or nameof(EditorWindowViewModel.IsRightPanelAutoHide)
                or nameof(EditorWindowViewModel.IsLeftPanelOpen) or nameof(EditorWindowViewModel.IsRightPanelOpen)
                or nameof(EditorWindowViewModel.LeftPanelWidth) or nameof(EditorWindowViewModel.RightPanelWidth))
                ApplyLayout();
        }

        /// <summary>
        /// Gives the side panels and the timeline the sizes the user chose, as much of each as leaves the chart and
        /// the panels row their minimum sizes.
        /// </summary>
        private void ApplyLayout()
        {
            // Taking a splitter's move sets the view model's width, which comes back here.
            if (!_isOpen || _viewModel is not { } vm || _isApplying)
                return;

            _isApplying = true;
            try
            {
                // A splitter moved since the last fit is a size the user chose. Taken before refitting, which would undo it.
                AbsorbSplitterChanges(vm);

                // The left panel is fitted first, leaving the right one at least its minimum, so it's the right one that
                // gives way when both don't fit.
                double room = ClientSize.Width - CenterColumn.MinWidth;
                _leftApplied = SizeSidePanel(LeftPanel, LeftColumn, vm.IsLeftPanelAutoHide, vm.LeftPanelWidth, room - LeastWidth(vm.IsRightPanelAutoHide));
                _rightApplied = SizeSidePanel(RightPanel, RightColumn, vm.IsRightPanelAutoHide, vm.RightPanelWidth, room - WidthTaken(_leftApplied));

                // A flown-out panel stops short of the other side, which is sized by now.
                FitFlyout(LeftPanel, vm.IsLeftPanelAutoHide, vm.IsLeftPanelOpen, vm.LeftPanelWidth, WidthTaken(_rightApplied));
                FitFlyout(RightPanel, vm.IsRightPanelAutoHide, vm.IsRightPanelOpen, vm.RightPanelWidth, WidthTaken(_leftApplied));

                double maxHeight = Math.Floor(ClientSize.Height - TopBand.Bounds.Height - MessageBar.Bounds.Height - PanelsRow.MinHeight - SplitterSize);
                _timelineApplied = Math.Clamp(vm.TimelineHeight, TimelineRow.MinHeight, Math.Max(TimelineRow.MinHeight, maxHeight));
                TimelineRow.Height = new GridLength(_timelineApplied, GridUnitType.Pixel);
            }
            finally
            {
                _isApplying = false;
            }
        }

        /// <summary>
        /// Pins <paramref name="panel"/> in <paramref name="column"/> with <paramref name="preferred"/>, as much of it as
        /// fits with its splitter in <paramref name="room"/>; or, auto-hiding, leaves the column its strip and lays the panel
        /// over the chart beside it, its width left to <see cref="FitFlyout"/>.
        /// </summary>
        /// <returns>The width given the column, or NaN when the panel auto-hides.</returns>
        private double SizeSidePanel(EditorSidePanel panel, ColumnDefinition column, bool autoHide, double preferred, double room)
        {
            bool right = panel == RightPanel;
            double min = BuilderSettings.MinEditorPanelWidth;
            if (autoHide)
            {
                column.MinWidth = 0;
                column.Width = new GridLength(EditorSidePanel.StripWidth, GridUnitType.Pixel);

                // Over the chart's column too, from its own edge, and above it.
                Grid.SetColumn(panel, right ? 2 : 0);
                Grid.SetColumnSpan(panel, 3);
                panel.HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                panel.ZIndex = 1;
                return double.NaN;
            }

            Grid.SetColumn(panel, right ? 4 : 0);
            Grid.SetColumnSpan(panel, 1);
            panel.HorizontalAlignment = HorizontalAlignment.Stretch;
            panel.ZIndex = 0;
            panel.Width = double.NaN;

            double width = Math.Clamp(preferred, min, Math.Max(min, Math.Floor(room - SplitterSize)));
            column.MinWidth = min;
            column.Width = new GridLength(width, GridUnitType.Pixel);
            return width;
        }

        /// <summary>
        /// Makes an auto-hiding <paramref name="panel"/> as wide as its strip, or, flown out, that and <paramref name="preferred"/>,
        /// as much of it as leaves the chart <see cref="FlyoutGap"/> beside <paramref name="otherTaken"/>, what the other side
        /// takes: its strip, or the panel pinned there and its splitter.
        /// </summary>
        private void FitFlyout(EditorSidePanel panel, bool autoHide, bool open, double preferred, double otherTaken)
        {
            if (!autoHide)
                return;

            double min = BuilderSettings.MinEditorPanelWidth;
            double most = Math.Max(min, Math.Floor(ClientSize.Width - EditorSidePanel.StripWidth - otherTaken - FlyoutGap));
            panel.Width = open ? EditorSidePanel.StripWidth + Math.Clamp(preferred, min, most) : EditorSidePanel.StripWidth;
        }

        /// <summary>The least a side panel takes: its strip while it auto-hides, or its minimum and its splitter.</summary>
        private static double LeastWidth(bool autoHide) =>
            autoHide ? EditorSidePanel.StripWidth : BuilderSettings.MinEditorPanelWidth + SplitterSize;

        /// <summary>What a side panel given <paramref name="applied"/> takes: its strip while it auto-hides (NaN), or that and its splitter.</summary>
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

        /// <summary>
        /// Saves the panels' and the lanes' sizes, as the window closes, and before a song or a new chart opens in place
        /// of the one open, which starts from the sizes saved.
        /// </summary>
        public void CaptureLayout()
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
