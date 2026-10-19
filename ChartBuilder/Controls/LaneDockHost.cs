using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using ChartBuilder.ViewModels;
using ChartShared.Core.Charting.Layout;

namespace ChartBuilder.Controls
{
    /// <summary>
    /// Shows the Editor's lanes as their <see cref="LaneDockLayout"/> arranges them: a grid per split, with splitters
    /// between its children. Dragging a lane by its header shows guides, at the middle of the lane under the pointer and
    /// at the middle of each of this control's edges; dropping it on one docks it against that edge, like Visual Studio's
    /// tool windows. Escape, or dropping anywhere else, leaves it where it was.
    /// </summary>
    /// <remarks>
    /// The grids are rebuilt whenever the layout changes; each lane's <see cref="EditorLane"/> is kept and moved into the
    /// new ones. Splitters set the sizes, which are taken back into the layout as weights when one is let go, so the
    /// lanes keep their shares as the window changes size and the sizes can be saved.
    /// </remarks>
    public sealed class LaneDockHost : Panel
    {
        /// <summary>The narrowest a lane can be dragged.</summary>
        public const double MinLaneWidth = 120;

        /// <summary>The shortest a lane can be dragged: its header and a little of its body.</summary>
        public const double MinLaneHeight = 96;

        private const double SplitterSize = 4;
        private const double DragThreshold = 4;
        private const double GuideSize = 32;
        private const double GuideGap = 4;

        public static readonly StyledProperty<LaneDockViewModel?> LanesProperty =
            AvaloniaProperty.Register<LaneDockHost, LaneDockViewModel?>(nameof(Lanes));

        public static readonly StyledProperty<IBrush?> SplitterBrushProperty =
            AvaloniaProperty.Register<LaneDockHost, IBrush?>(nameof(SplitterBrush));

        public static readonly StyledProperty<IBrush?> GuideBrushProperty =
            AvaloniaProperty.Register<LaneDockHost, IBrush?>(nameof(GuideBrush));

        public static readonly StyledProperty<IBrush?> GuideBorderBrushProperty =
            AvaloniaProperty.Register<LaneDockHost, IBrush?>(nameof(GuideBorderBrush));

        public static readonly StyledProperty<IBrush?> AccentBrushProperty =
            AvaloniaProperty.Register<LaneDockHost, IBrush?>(nameof(AccentBrush));

        public static readonly StyledProperty<IBrush?> PreviewBrushProperty =
            AvaloniaProperty.Register<LaneDockHost, IBrush?>(nameof(PreviewBrush));

        private readonly Dictionary<int, EditorLane> _views = [];
        private readonly List<(Grid Grid, DockSplit Split)> _grids = [];
        private readonly DropOverlay _overlay;
        private Control? _content;
        private LaneDockViewModel? _subscribed;

        // The lane pressed by its header, and where; once it's moved far enough, it's being dragged, over _drop.
        private EditorLane? _pressed;
        private IPointer? _pointer;
        private Point _pressPoint;
        private bool _isDragging;
        private Guide? _drop;
        private TopLevel? _keyHost;

        static LaneDockHost()
        {
            ClipToBoundsProperty.OverrideDefaultValue<LaneDockHost>(true);
        }

        public LaneDockHost()
        {
            _overlay = new DropOverlay(this) { IsHitTestVisible = false, IsVisible = false };
            Children.Add(_overlay);
        }

        public LaneDockViewModel? Lanes
        {
            get => GetValue(LanesProperty);
            set => SetValue(LanesProperty, value);
        }

        /// <summary>The splitters between lanes.</summary>
        public IBrush? SplitterBrush
        {
            get => GetValue(SplitterBrushProperty);
            set => SetValue(SplitterBrushProperty, value);
        }

        /// <summary>The drop guides' background.</summary>
        public IBrush? GuideBrush
        {
            get => GetValue(GuideBrushProperty);
            set => SetValue(GuideBrushProperty, value);
        }

        public IBrush? GuideBorderBrush
        {
            get => GetValue(GuideBorderBrushProperty);
            set => SetValue(GuideBorderBrushProperty, value);
        }

        /// <summary>The drop guides' pictures, and the outline of where a dropped lane will go.</summary>
        public IBrush? AccentBrush
        {
            get => GetValue(AccentBrushProperty);
            set => SetValue(AccentBrushProperty, value);
        }

        /// <summary>Fills where a dropped lane will go, and the guide the pointer is over.</summary>
        public IBrush? PreviewBrush
        {
            get => GetValue(PreviewBrushProperty);
            set => SetValue(PreviewBrushProperty, value);
        }

        /// <summary>Takes the sizes the splitters are at into the layout, for saving.</summary>
        public void CaptureSizes()
        {
            foreach (var (grid, split) in _grids)
                Capture(grid, split);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == LanesProperty)
            {
                if (_subscribed is not null)
                    _subscribed.LayoutChanged -= OnLayoutChanged;
                _subscribed = Lanes;
                if (_subscribed is not null)
                    _subscribed.LayoutChanged += OnLayoutChanged;

                // Another song's lanes: new views for them, as a lane's view keeps what it drew of the last one. Its
                // lanes' Ids, from the same saved layout, would otherwise be taken as the same lanes. The old views
                // leave with the content Rebuild takes down.
                _views.Clear();
                Rebuild();
            }
            else if (change.Property == SplitterBrushProperty)
            {
                Rebuild();
            }
            else if (change.Property == GuideBrushProperty || change.Property == GuideBorderBrushProperty
                || change.Property == AccentBrushProperty || change.Property == PreviewBrushProperty)
            {
                _overlay.InvalidateVisual();
            }
        }

        private void OnLayoutChanged(object? sender, EventArgs e) => Rebuild();

        private void Rebuild()
        {
            CancelDrag();

            foreach (var view in _views.Values)
            {
                if (view.Parent is Panel parent)
                    parent.Children.Remove(view);
            }

            if (_content is not null)
                Children.Remove(_content);
            _content = null;
            _grids.Clear();

            var lanes = Lanes;
            var open = lanes?.Layout.Lanes.Select(l => l.Id).ToHashSet() ?? [];
            foreach (int id in _views.Keys.Where(id => !open.Contains(id)).ToList())
                _views.Remove(id);

            if (lanes?.Layout.Root is not { } root)
                return;

            _content = Build(root, lanes);
            Children.Insert(0, _content);
        }

        private Control Build(DockNode node, LaneDockViewModel lanes)
        {
            if (node is DockLane lane)
            {
                if (!_views.TryGetValue(lane.Id, out var view))
                {
                    view = new EditorLane();
                    HookDrag(view);
                    _views.Add(lane.Id, view);
                }

                view.DataContext = lanes.LaneFor(lane);
                return view;
            }

            var split = (DockSplit)node;
            bool across = split.Orientation == DockOrientation.Horizontal;
            var grid = new Grid();
            _grids.Add((grid, split));

            for (int i = 0; i < split.Children.Count; i++)
            {
                if (i > 0)
                {
                    var splitter = new GridSplitter
                    {
                        ResizeDirection = across ? GridResizeDirection.Columns : GridResizeDirection.Rows,
                        Background = SplitterBrush,
                    };
                    if (across)
                    {
                        splitter.Width = SplitterSize;
                        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                        Grid.SetColumn(splitter, grid.ColumnDefinitions.Count - 1);
                    }
                    else
                    {
                        splitter.Height = SplitterSize;
                        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                        Grid.SetRow(splitter, grid.RowDefinitions.Count - 1);
                    }

                    // Dragged, or moved with the arrow keys.
                    splitter.DragCompleted += (_, _) => Capture(grid, split);
                    splitter.AddHandler(KeyUpEvent, (_, _) => Capture(grid, split), handledEventsToo: true);
                    grid.Children.Add(splitter);
                }

                var child = split.Children[i];
                var share = new GridLength(split.Weights[i], GridUnitType.Star);
                var min = MinSize(child);
                var content = Build(child, lanes);
                if (across)
                {
                    grid.ColumnDefinitions.Add(new ColumnDefinition(share) { MinWidth = min.Width });
                    Grid.SetColumn(content, grid.ColumnDefinitions.Count - 1);
                }
                else
                {
                    grid.RowDefinitions.Add(new RowDefinition(share) { MinHeight = min.Height });
                    Grid.SetRow(content, grid.RowDefinitions.Count - 1);
                }

                grid.Children.Add(content);
            }

            return grid;
        }

        /// <summary>The least room <paramref name="node"/> takes: its lanes at their minimum size, and its splitters.</summary>
        private static Size MinSize(DockNode node)
        {
            if (node is not DockSplit split)
                return new Size(MinLaneWidth, MinLaneHeight);

            var sizes = split.Children.Select(MinSize).ToList();
            double splitters = SplitterSize * (sizes.Count - 1);
            return split.Orientation == DockOrientation.Horizontal
                ? new Size(sizes.Sum(s => s.Width) + splitters, sizes.Max(s => s.Height))
                : new Size(sizes.Max(s => s.Width), sizes.Sum(s => s.Height) + splitters);
        }

        /// <summary>
        /// Takes the sizes <paramref name="grid"/>'s splitters left its children as <paramref name="split"/>'s weights, and
        /// gives them back as shares, so they keep them as the window changes size.
        /// </summary>
        private void Capture(Grid grid, DockSplit split)
        {
            if (Lanes is not { } lanes)
                return;

            bool across = split.Orientation == DockOrientation.Horizontal;
            var sizes = new double[split.Children.Count];
            for (int i = 0; i < sizes.Length; i++)
            {
                // The children are in every other definition, between the splitters'.
                sizes[i] = across ? grid.ColumnDefinitions[i * 2].ActualWidth : grid.RowDefinitions[i * 2].ActualHeight;
                if (!(sizes[i] > 0))
                    return;
            }

            lanes.Layout.SetWeights(split, sizes);
            for (int i = 0; i < sizes.Length; i++)
            {
                var share = new GridLength(split.Weights[i], GridUnitType.Star);
                if (across)
                    grid.ColumnDefinitions[i * 2].Width = share;
                else
                    grid.RowDefinitions[i * 2].Height = share;
            }
        }

        private void HookDrag(EditorLane view)
        {
            var handle = view.DragHandle;
            handle.PointerPressed += (_, e) => OnHeaderPressed(view, e);
            handle.PointerMoved += (_, e) => OnHeaderMoved(view, e);
            handle.PointerReleased += (_, e) => OnHeaderReleased(view, e);
            handle.PointerCaptureLost += (_, _) =>
            {
                if (_pressed == view)
                    CancelDrag();
            };
        }

        private void OnHeaderPressed(EditorLane view, PointerPressedEventArgs e)
        {
            // The header's buttons take their own presses, so only the header itself starts a drag. Buttons mark
            // their presses handled before they bubble here, but a ComboBox's closed box doesn't, so presses from
            // anywhere inside an interactive header control are ignored explicitly (else the lane would capture the
            // pointer and the difficulty drop-down would never open).
            if (!e.GetCurrentPoint(view.DragHandle).Properties.IsLeftButtonPressed)
                return;
            if (e.Source is Visual source
                && (source.FindAncestorOfType<Button>(includeSelf: true) is not null
                    || source.FindAncestorOfType<ToggleButton>(includeSelf: true) is not null
                    || source.FindAncestorOfType<ComboBox>(includeSelf: true) is not null
                    || source.FindAncestorOfType<ComboBoxItem>(includeSelf: true) is not null
                    || source.FindAncestorOfType<Slider>(includeSelf: true) is not null))
                return;

            CancelDrag();
            _pressed = view;
            _pointer = e.Pointer;
            _pressPoint = e.GetPosition(this);
            e.Pointer.Capture(view.DragHandle);
            e.Handled = true;
        }

        private void OnHeaderMoved(EditorLane view, PointerEventArgs e)
        {
            if (_pressed != view)
                return;

            var point = e.GetPosition(this);
            if (!_isDragging)
            {
                var moved = point - _pressPoint;
                if (Math.Abs(moved.X) < DragThreshold && Math.Abs(moved.Y) < DragThreshold)
                    return;
                StartDrag(view);
            }

            UpdateDrop(point);
        }

        private void OnHeaderReleased(EditorLane view, PointerReleasedEventArgs e)
        {
            if (_pressed != view)
                return;

            var drop = _isDragging ? _drop : null;
            int laneId = LaneId(view);
            CancelDrag();
            e.Handled = true;

            // After the drag has ended, as the move rebuilds the lanes, the dragged one's header among them.
            if (drop is { } guide)
                Lanes?.Move(laneId, guide.LaneId, guide.Edge);
        }

        private void StartDrag(EditorLane view)
        {
            _isDragging = true;
            view.Classes.Add("dragging");
            _overlay.IsVisible = true;

            _keyHost = TopLevel.GetTopLevel(this);
            _keyHost?.AddHandler(KeyDownEvent, OnDragKeyDown, RoutingStrategies.Tunnel);
        }

        private void OnDragKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape)
                return;

            CancelDrag();
            e.Handled = true;
        }

        /// <summary>Ends a press or a drag, leaving the lane where it was.</summary>
        private void CancelDrag()
        {
            if (_pressed is not { } view)
                return;

            // Cleared first: releasing the capture below raises PointerCaptureLost, which comes back here.
            _pressed = null;
            view.Classes.Remove("dragging");
            _isDragging = false;
            _drop = null;
            _overlay.Show([], null, null);
            _overlay.IsVisible = false;

            _keyHost?.RemoveHandler(KeyDownEvent, OnDragKeyDown);
            _keyHost = null;

            if (_pointer?.Captured == view.DragHandle)
                _pointer.Capture(null);
            _pointer = null;
        }

        /// <summary>Shows the guides for the pointer at <paramref name="point"/>, and where the lane would go.</summary>
        private void UpdateDrop(Point point)
        {
            if (_pressed is not { } dragged)
                return;

            var bounds = new Rect(Bounds.Size);
            var guides = new List<Guide>(8)
            {
                new(GuideAt(bounds.Left + GuideGap + GuideSize / 2, bounds.Center.Y), null, DockEdge.Left),
                new(GuideAt(bounds.Right - GuideGap - GuideSize / 2, bounds.Center.Y), null, DockEdge.Right),
                new(GuideAt(bounds.Center.X, bounds.Top + GuideGap + GuideSize / 2), null, DockEdge.Top),
                new(GuideAt(bounds.Center.X, bounds.Bottom - GuideGap - GuideSize / 2), null, DockEdge.Bottom),
            };

            Rect? targetBounds = null;
            int? targetId = null;
            foreach (var view in _views.Values)
            {
                if (view == dragged || view.Parent is null || view.TranslatePoint(default, this) is not { } topLeft)
                    continue;

                var rect = new Rect(topLeft, view.Bounds.Size);
                if (!rect.Contains(point))
                    continue;

                targetBounds = rect;
                targetId = LaneId(view);
                const double offset = GuideSize + GuideGap;
                var c = rect.Center;
                guides.Add(new(GuideAt(c.X - offset, c.Y), targetId, DockEdge.Left));
                guides.Add(new(GuideAt(c.X + offset, c.Y), targetId, DockEdge.Right));
                guides.Add(new(GuideAt(c.X, c.Y - offset), targetId, DockEdge.Top));
                guides.Add(new(GuideAt(c.X, c.Y + offset), targetId, DockEdge.Bottom));
                break;
            }

            // A lane's own guides are over the whole dock's when they overlap, so they're tried first.
            _drop = guides.AsEnumerable().Reverse().FirstOrDefault(g => g.Bounds.Contains(point));

            Rect? preview = _drop switch
            {
                null => null,
                { LaneId: null } g => Side(bounds, g.Edge, LaneDockLayout.EdgeShare),
                { } g => Side(targetBounds!.Value, g.Edge, 0.5),
            };
            _overlay.Show(guides, _drop, preview);
        }

        private static Rect GuideAt(double x, double y) => new(x - GuideSize / 2, y - GuideSize / 2, GuideSize, GuideSize);

        /// <summary>The <paramref name="share"/> of <paramref name="rect"/> along its <paramref name="edge"/>.</summary>
        private static Rect Side(Rect rect, DockEdge edge, double share) => edge switch
        {
            DockEdge.Left => rect.WithWidth(rect.Width * share),
            DockEdge.Right => new Rect(rect.Right - rect.Width * share, rect.Y, rect.Width * share, rect.Height),
            DockEdge.Top => rect.WithHeight(rect.Height * share),
            _ => new Rect(rect.X, rect.Bottom - rect.Height * share, rect.Width, rect.Height * share),
        };

        private static int LaneId(EditorLane view) => ((EditorLaneViewModel)view.DataContext!).Lane.Id;

        /// <summary>A drop guide: docks against <see cref="Edge"/> of the lane <see cref="LaneId"/>, or of the whole dock when null.</summary>
        private sealed record Guide(Rect Bounds, int? LaneId, DockEdge Edge);

        /// <summary>The drop guides and the outline of where the lane will go, over the lanes while one is dragged.</summary>
        private sealed class DropOverlay(LaneDockHost owner) : Control
        {
            private IReadOnlyList<Guide> _guides = [];
            private Guide? _hovered;
            private Rect? _preview;

            public void Show(IReadOnlyList<Guide> guides, Guide? hovered, Rect? preview)
            {
                _guides = guides;
                _hovered = hovered;
                _preview = preview;
                InvalidateVisual();
            }

            public override void Render(DrawingContext context)
            {
                var accent = owner.AccentBrush is { } a ? new Pen(a, 1.5) : null;
                if (_preview is { } preview)
                    context.DrawRectangle(owner.PreviewBrush, accent, preview.Deflate(1), 4, 4);

                var border = owner.GuideBorderBrush is { } b ? new Pen(b, 1) : null;
                foreach (var guide in _guides)
                {
                    bool hovered = guide == _hovered;
                    context.DrawRectangle(hovered ? owner.PreviewBrush : owner.GuideBrush, hovered ? accent : border, guide.Bounds, 6, 6);

                    // A box with the side the lane would go on filled in.
                    var box = guide.Bounds.Deflate(8);
                    context.DrawRectangle(owner.PreviewBrush, null, Side(box, guide.Edge, 0.5));
                    context.DrawRectangle(null, accent, box);
                }
            }
        }
    }
}
