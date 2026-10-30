using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using ChartBuilder.Controls;
using ChartBuilder.Controls.Editing;
using ChartBuilder.Services;
using ChartBuilder.ViewModels;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Keybinds;
using ChartShared.UI.Keybinds;
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

        // The editor and lane a lane action (1 to 6, 0, 2× kick) was pressed on, by action, so its release goes to them even if
        // focus has moved on by then.
        private readonly Dictionary<string, LanePress> _pressedLanes = [];

        private bool _isApplying;

        // The sizes last given to the side columns and the timeline row: NaN for an auto-hiding side, and before the
        // window opens. A definition that no longer matches was moved by its splitter, i.e. by the user.
        private double _leftApplied = double.NaN;
        private double _rightApplied = double.NaN;
        private double _timelineApplied = double.NaN;

        public EditorWindow()
        {
            // While a song opens, keys don't reach the Editor being left: its edits would go with it, unsaved. First, so
            // nothing below sees them. Key-ups still go through, so a key held then is let go of.
            AddHandler(KeyDownEvent, OnOpeningInput, RoutingStrategies.Tunnel);
            AddHandler(TextInputEvent, OnOpeningInput, RoutingStrategies.Tunnel);

            // Playtest's keys, tunnelled, so they play wherever focus is: on the play button just clicked, say. Before the key
            // dispatcher's, so a key bound to an instrument plays rather than editing.
            AddHandler(KeyDownEvent, OnPlaytestKeyDown, RoutingStrategies.Tunnel);
            AddHandler(KeyUpEvent, OnPlaytestKeyUp, RoutingStrategies.Tunnel);

            // The modifier keys held, wherever focus is, even when something took the key: the place tool's options show
            // what Ctrl and Alt swap while they're held.
            AddHandler(KeyDownEvent, OnModifierKeys, RoutingStrategies.Tunnel, handledEventsToo: true);
            AddHandler(KeyUpEvent, OnModifierKeys, RoutingStrategies.Tunnel, handledEventsToo: true);

            // Before the panels are built, so what registers its keys as it attaches finds the dispatcher.
            RegisterKeys();
            InitializeComponent();
            WindowMemory.Remember(this, position: true);

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

            // Coming back from Chart Packer, the keys it may have rebound are read again.
            Activated += (_, _) => AppKeys.ReloadIfChanged();
            TopBand.SizeChanged += (_, _) => ApplyLayout();
            TitleBand.SizeChanged += (_, e) => LoadingOverlay.Margin = new Thickness(0, e.NewSize.Height, 0, 0);
            Closing += (_, _) => CaptureLayout();

            // Audio files dropped anywhere on the window start a new chart with them as its stems (charter-spec C1). A
            // song folder or .sng opens it (C2).
            DragDrop.SetAllowDrop(this, true);
            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            AddHandler(DragDrop.DropEvent, OnDrop);
        }

        /// <summary>
        /// The window's keys are the app's <see cref="KeyMap"/>, not bindings of its own, so rebinding one moves it here too. They
        /// act on <see cref="_viewModel"/> as it is when pressed, as the Editor is swapped in place for each song opened. A command
        /// that can't run now leaves its key to whatever else would take it.
        /// </summary>
        private void RegisterKeys()
        {
            // The canvas's keys are live only while a lane canvas has focus, so a focused combo box or button never takes them.
            var keys = new KeyDispatcher(AppKeys.Map, BuilderKeys.Tools)
            {
                ScopeProvider = () => FocusManager?.GetFocusedElement() is EditorCanvas ? BuilderKeys.Canvas : BuilderKeys.Tools,
                IsBlocked = () => _viewModel is { IsOpening: true },
            };
            // Keys held in record mode or playtest are let go when the window loses focus, as their key-ups won't come here. Through
            // the dispatcher, which does this before it lets go of the keys it holds: the Loop tool is no longer held when it lets
            // go of L, which is then the end of a hold, not a tap, which turns repeating the loop region on or off.
            keys.OnDeactivated = () =>
            {
                _viewModel?.Editor.RecordReleaseAll();
                _viewModel?.Editor.Playtest.ReleaseKeys();
                _viewModel?.Editor.HoldModifiers(EditorModifiers.None);
                _viewModel?.Editor.HoldLoopTool(false);
            };
            keys.Attach(this);

            keys.On(SharedKeys.Save, () => Run(vm => vm.SaveCommand));
            keys.On(SharedKeys.Settings, () => Run(vm => vm.OpenProgramSettingsCommand));
            keys.On(SharedKeys.Undo, () => Run(vm => vm.UndoCommand));
            keys.On(SharedKeys.Redo, () => Run(vm => vm.RedoCommand));
            keys.On(BuilderKeys.NewChart, () => Run(vm => vm.NewChartCommand));
            keys.On(BuilderKeys.OpenChart, () => Run(vm => vm.OpenChartCommand));
            keys.On(BuilderKeys.GoToMeasure, () => Run(vm => vm.GoToMeasureCommand));
            keys.On(BuilderKeys.Find, () => Run(vm => vm.FindMatchesCommand));
            keys.On(BuilderKeys.FindNext, () => Run(vm => vm.FindNextCommand));
            keys.On(BuilderKeys.FindPrevious, () => Run(vm => vm.FindPreviousCommand));

            // Space plays or pauses, once a press however long it's held. Tunnelled, so a focused button or list plays rather
            // than taking it. Not while a text box has focus: it types a space there.
            keys.On(SharedKeys.PlayPause, () =>
            {
                if (_viewModel is not { } vm)
                    return false;
                vm.Editor.PlayPause();
                return true;
            });

            // Alt and 1 to 6 pick the Select, Place, Erase, Phrase, Tempo and Sync tools, in the toolbar's order, wherever focus
            // is: on a lane, on a button, in a panel. Picking a tool is what its button does (a gesture under way is cancelled),
            // so it's allowed wherever the buttons are, a playtest or record pass included.
            foreach (string id in BuilderKeys.ToolActions)
            {
                var tool = EditorKeys.ToolOf(id)!.Value;
                keys.On(id, () =>
                {
                    if (_viewModel is not { } vm)
                        return false;
                    vm.Editor.Session.Tool = tool;
                    return true;
                });
            }

            // Holding L holds the Loop tool on, wherever focus is, as Ctrl+Shift holds the Sync tool, so the loop region can be
            // drawn, moved or resized without picking the tool, and the toolbar's button is lit meanwhile; let go, the picked tool
            // is in charge again. A short press that touched nothing is a tap, which turns repeating the region on or off; held
            // longer, it's only the tool. While a playtest goes on it's the playtest's, as Space, Esc, P and N are, so a lane's key
            // bound to L doesn't turn repeating off mid-pass.
            keys.OnHold(
                BuilderKeys.LoopTool,
                () =>
                {
                    if (_viewModel is not { } vm || vm.Editor.Playtest is { IsActive: true })
                        return false;
                    vm.Editor.HoldLoopTool(true);
                    return true;
                },
                () => _viewModel?.Editor.ReleaseLoopTool());

            foreach (string id in BuilderKeys.CanvasActions(AppKeys.Map.Catalog))
            {
                if (EditorKeys.LaneNumberOf(id) is { } number)
                    keys.OnHold(id, () => PressLane(id, number), () => ReleaseLane(id, number));
                else
                    keys.On(id, () => RunCanvas(id));
            }
        }

        private bool Run(Func<EditorWindowViewModel, ICommand> command)
        {
            if (_viewModel is not { } vm || command(vm) is not { } run || !run.CanExecute(null))
                return false;

            run.Execute(null);
            return true;
        }

        /// <summary>The lane canvas with focus and what it shows, when one has and it shows a lane.</summary>
        private bool TryGetCanvas([NotNullWhen(true)] out ChartEditorViewModel? editor, [NotNullWhen(true)] out EditorLaneViewModel? lane, [NotNullWhen(true)] out EditorCanvas? canvas)
        {
            editor = null;
            lane = null;
            canvas = FocusManager?.GetFocusedElement() as EditorCanvas;
            return canvas is not null && canvas.TryGetKeyContext(out editor, out lane);
        }

        private bool RunCanvas(string id) =>
            TryGetCanvas(out var editor, out var lane, out var canvas) && EditorKeys.Run(id, editor, lane, canvas);

        private bool PressLane(string id, int number)
        {
            // A second key of an action already held (NumPad1 over Digit1) joins the hold rather than pressing again.
            if (_pressedLanes.TryGetValue(id, out var held))
            {
                held.Keys++;
                return true;
            }

            if (!TryGetCanvas(out var editor, out var lane, out _))
                return false;

            if (!EditorKeys.PressLane(number, id == BuilderKeys.NoteDoubleKick, editor, lane))
                return false;
            _pressedLanes[id] = new LanePress(editor, lane);
            return true;
        }

        private void ReleaseLane(string id, int number)
        {
            if (!_pressedLanes.TryGetValue(id, out var pressed) || --pressed.Keys > 0)
                return;

            _pressedLanes.Remove(id);
            EditorKeys.ReleaseLane(number, id == BuilderKeys.NoteDoubleKick, pressed.Editor, pressed.Lane);
        }

        /// <summary>A lane action being held: where it was pressed, and how many of its keys are down.</summary>
        private sealed class LanePress(ChartEditorViewModel editor, EditorLaneViewModel lane)
        {
            public ChartEditorViewModel Editor { get; } = editor;

            public EditorLaneViewModel Lane { get; } = lane;

            public int Keys { get; set; } = 1;
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
            ApplyLayout();
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

        private void OnModifierKeys(object? sender, KeyEventArgs e) => _viewModel?.Editor.HoldModifiers(EditorCanvas.ModifiersOf(e.KeyModifiers));

        private void OnOpeningInput(object? sender, RoutedEventArgs e)
        {
            if (_viewModel is { IsOpening: true })
                e.Handled = true;
        }

        private bool IsTyping(KeyEventArgs e) => e.Source is TextBox || FocusManager?.GetFocusedElement() is TextBox;

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
