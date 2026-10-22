using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ChartBuilder.Services;

namespace ChartBuilder.Controls
{
    /// <summary>
    /// A side-panel tab's <see cref="PanelSection"/>s, one under another, in the order the user dragged them to, each
    /// collapsed or expanded as it was left (kept in the inherited <see cref="PanelLayout.Store"/>, by <see cref="TabKey"/>).
    /// </summary>
    /// <remarks>
    /// Meant to be inside a vertical <see cref="ScrollViewer"/>. A section that <see cref="PanelSection.Fills"/> is given what
    /// the scroller shows less the other sections, but never less than its <see cref="PanelSection.FillMinHeight"/>: a list
    /// fills a tall panel, and a short one scrolls rather than overlapping. Until the scroller has been laid out, it's given
    /// its least, so a list in it never makes every row. The sections stay this panel's children in the order they're
    /// written; only where they're laid out changes, so nothing in them is torn down when they're moved. A section with the
    /// same key as one before it is laid out after them all, as one without a key is.
    /// </remarks>
    public sealed class PanelSectionStack : Panel
    {
        public static readonly StyledProperty<string?> TabKeyProperty =
            AvaloniaProperty.Register<PanelSectionStack, string?>(nameof(TabKey));

        // Near the scroller's top or bottom, a drag scrolls it this much each time the pointer moves.
        private const double ScrollZone = 28;
        private const double ScrollStep = 12;

        private readonly Dictionary<PanelSection, double> _fillHeights = [];
        private List<Control> _order = [];

        // The sections that are moved and remembered: the first with each key.
        private HashSet<PanelSection> _keyed = [];
        private PanelLayoutStore? _store;
        private ScrollViewer? _scroller;
        private InsertionMarker? _marker;
        private int _dropAt = -1;

        // While a section is dragged, every section is collapsed, so each is only its header and the drag is short; this
        // is whether each was expanded before, to put back after.
        private Dictionary<PanelSection, bool>? _expandedBeforeDrag;

        static PanelSectionStack()
        {
            AffectsParentMeasure<PanelSectionStack>(PanelSection.IsExpandedProperty, PanelSection.FillsProperty, PanelSection.FillMinHeightProperty);
            AffectsMeasure<PanelSectionStack>(TabKeyProperty);
        }

        /// <summary>The tab these are the sections of, for remembering how they're arranged.</summary>
        public string? TabKey
        {
            get => GetValue(TabKeyProperty);
            set => SetValue(TabKeyProperty, value);
        }

        private IEnumerable<PanelSection> Sections => Children.OfType<PanelSection>();

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);

            _scroller = this.FindAncestorOfType<ScrollViewer>();
            if (_scroller is not null)
                _scroller.PropertyChanged += OnScrollerPropertyChanged;
            Follow(PanelLayout.GetStore(this));
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);

            if (_scroller is not null)
                _scroller.PropertyChanged -= OnScrollerPropertyChanged;
            _scroller = null;
            Follow(null);
            HideMarker();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == PanelLayout.StoreProperty && this.IsAttachedToVisualTree())
                Follow(change.GetNewValue<PanelLayoutStore?>());
            else if (change.Property == TabKeyProperty)
                Restore();
        }

        protected override void ChildrenChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            base.ChildrenChanged(sender, e);
            Restore();
        }

        private void Follow(PanelLayoutStore? store)
        {
            if (_store is not null)
                _store.Changed -= OnStoreChanged;
            _store = store;
            if (_store is not null)
                _store.Changed += OnStoreChanged;
            Restore();
        }

        private void OnStoreChanged(object? sender, string key)
        {
            if (key == TabKey)
                Restore();
        }

        // The scroller's height decides how tall a filling section is.
        private void OnScrollerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == ScrollViewer.ViewportProperty
                && e.GetOldValue<Size>().Height != e.GetNewValue<Size>().Height
                && Sections.Any(s => s.Fills))
                InvalidateMeasure();
        }

        /// <summary>Lays the sections out as they were left, and collapses those that were.</summary>
        private void Restore()
        {
            var sections = Sections.Where(s => s.Key is not null).DistinctBy(s => s.Key, StringComparer.Ordinal).ToList();
            var saved = _store is { } store && TabKey is { } tab
                ? store.SectionOrder(tab, [.. sections.Select(s => s.Key!)])
                : _order.Where(IsKeyed).Cast<PanelSection>().Select(s => s.Key!).ToList();
            var byKey = sections.ToDictionary(s => s.Key!, StringComparer.Ordinal);
            _keyed = [.. sections];

            var order = new List<Control>();
            foreach (string key in PanelLayoutStore.Arrange(saved, [.. sections.Select(s => s.Key!)]))
                order.Add(byKey[key]);

            // Anything without a key of its own keeps its place after them, in the order it's written.
            order.AddRange(Children.Where(c => !IsKeyed(c)));
            _order = order;

            // Mid-drag, every section stays collapsed until the drag puts them back.
            if (_store is { } layout && TabKey is { } tabKey && _expandedBeforeDrag is null)
            {
                foreach (var section in sections)
                    section.IsExpanded = !layout.IsCollapsed(tabKey, section.Key!);
            }
            InvalidateMeasure();
        }

        /// <summary>Remembers whether <paramref name="section"/> is collapsed, as the user just left it.</summary>
        internal void Remember(PanelSection section)
        {
            if (_store is { } store && TabKey is { } tab && IsKeyed(section) && section.Key is { } key)
                store.SetCollapsed(tab, key, !section.IsExpanded);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double width = availableSize.Width;
            double room = double.IsFinite(availableSize.Height) ? availableSize.Height
                : _scroller is { } scroller && scroller.Viewport.Height > 0 ? scroller.Viewport.Height
                : double.PositiveInfinity;

            _fillHeights.Clear();
            double used = 0;
            double widest = 0;
            var fills = new List<PanelSection>();
            foreach (var child in _order)
            {
                if (child is PanelSection { Fills: true, IsExpanded: true, IsVisible: true } fill)
                {
                    fills.Add(fill);
                    continue;
                }
                child.Measure(new Size(width, double.PositiveInfinity));
                used += child.DesiredSize.Height;
                widest = Math.Max(widest, child.DesiredSize.Width);
            }

            if (fills.Count > 0)
            {
                // Not yet laid out, the scroller has no height to share: each gets its least until it has.
                double share = double.IsFinite(room) ? Math.Max(0, room - used) / fills.Count : 0;
                foreach (var fill in fills)
                {
                    double height = Math.Max(fill.FillMinHeight, share);
                    fill.Measure(new Size(width, height));
                    _fillHeights[fill] = height;
                    used += height;
                    widest = Math.Max(widest, fill.DesiredSize.Width);
                }
            }

            return new Size(double.IsFinite(width) ? width : widest, used);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double y = 0;
            foreach (var child in _order)
            {
                double height = child is PanelSection section && _fillHeights.TryGetValue(section, out double fill)
                    ? fill
                    : child.DesiredSize.Height;
                child.Arrange(new Rect(0, y, finalSize.Width, height));
                if (child.IsVisible)
                    y += height;
            }
            return finalSize;
        }

        private bool IsKeyed(Control child) => child is PanelSection section && _keyed.Contains(section);

        // The sections that can be moved, in the order they're laid out.
        private List<PanelSection> Keyed() => [.. _order.Where(IsKeyed).Cast<PanelSection>()];

        // Those of them shown.
        private List<PanelSection> Shown() => [.. Keyed().Where(s => s.IsVisible)];

        /// <summary>Whether <paramref name="section"/> can move <paramref name="by"/> places among the sections shown.</summary>
        internal bool CanMoveBy(PanelSection section, int by)
        {
            var shown = Shown();
            int at = shown.IndexOf(section);
            return at >= 0 && at + by >= 0 && at + by < shown.Count;
        }

        /// <summary>Moves <paramref name="section"/> up (-1) or down (1) past the next section shown.</summary>
        internal void MoveBy(PanelSection section, int by)
        {
            if (!CanMoveBy(section, by))
                return;

            var shown = Shown();
            var past = shown[shown.IndexOf(section) + by];
            var order = Keyed();
            order.Remove(section);
            int at = order.IndexOf(past) + (by > 0 ? 1 : 0);
            order.Insert(at, section);
            Arrange(order);
        }

        /// <summary>Collapses or expands every section.</summary>
        internal void ExpandAll(bool expanded)
        {
            foreach (var section in Sections)
            {
                section.IsExpanded = expanded;
                Remember(section);
            }
        }

        /// <summary>Puts the sections back in the order they're written, all expanded.</summary>
        internal void ResetLayout()
        {
            foreach (var section in Sections)
                section.IsExpanded = true;
            _order = [];
            if (_store is { } store && TabKey is { } tab)
                store.ResetSections(tab);
            Restore();
        }

        private void Arrange(List<PanelSection> order)
        {
            var keys = order.Select(s => s.Key!).ToList();
            _order = [.. order, .. Children.Where(c => !IsKeyed(c))];
            if (_store is { } store && TabKey is { } tab)
                store.SetSectionOrder(tab, keys);
            InvalidateMeasure();
        }

        /// <summary>
        /// <paramref name="section"/>'s header has been dragged far enough to move it, the pointer <paramref name="grip"/>
        /// below the header's top. Every section collapses until the drag ends, the dragged one's header kept under the
        /// pointer.
        /// </summary>
        internal void BeginDrag(PanelSection section, PointerEventArgs e, double grip)
        {
            if (_marker is not null || AdornerLayer.GetAdornerLayer(this) is not { } layer)
                return;

            _marker = new InsertionMarker { Brush = this.FindResource("AccentTextBrush") as IBrush ?? Brushes.CornflowerBlue };
            AdornerLayer.SetAdornedElement(_marker, this);
            layer.Children.Add(_marker);

            // Collapsed as they are, not as the user left them, so nothing's remembered.
            _expandedBeforeDrag = Sections.ToDictionary(s => s, s => s.IsExpanded);
            foreach (var other in Sections)
                other.IsExpanded = false;

            if (_scroller is { } scroller)
            {
                double pointer = e.GetPosition(scroller).Y;
                UpdateLayout();
                if (section.TranslatePoint(default, scroller) is { } top)
                {
                    double most = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
                    scroller.Offset = scroller.Offset.WithY(Math.Clamp(scroller.Offset.Y + top.Y - (pointer - grip), 0, most));
                }
            }
        }

        /// <summary>Shows where <paramref name="section"/> would go with the pointer at <paramref name="point"/>.</summary>
        internal void DragTo(PanelSection section, Point point)
        {
            ScrollNear(point);

            var shown = Shown();
            int from = shown.IndexOf(section);
            int at = shown.Count;
            for (int i = 0; i < shown.Count; i++)
            {
                if (point.Y < shown[i].Bounds.Center.Y)
                {
                    at = i;
                    break;
                }
            }

            // Where it is already isn't a move.
            if (from < 0 || at == from || at == from + 1)
            {
                _dropAt = -1;
                _marker?.Show(null);
                return;
            }

            _dropAt = at;
            double y = at < shown.Count ? shown[at].Bounds.Top : shown[^1].Bounds.Bottom;
            _marker?.Show(y);
        }

        /// <summary>The drag of <paramref name="section"/> is over: it goes where it was shown going, if <paramref name="drop"/>.</summary>
        internal void EndDrag(PanelSection section, bool drop)
        {
            int at = _dropAt;
            HideMarker();
            if (drop && at >= 0)
            {
                var shown = Shown();
                var order = Keyed();
                order.Remove(section);
                int index = at < shown.Count ? order.IndexOf(shown[at]) : order.IndexOf(shown[^1]) + 1;
                order.Insert(index, section);
                Arrange(order);
            }

            // Each section back as it was, and the one dragged in sight where it went.
            if (_expandedBeforeDrag is { } expanded)
            {
                _expandedBeforeDrag = null;
                foreach (var (other, wasExpanded) in expanded)
                    other.IsExpanded = wasExpanded;
                Dispatcher.UIThread.Post(() => section.BringIntoView(), DispatcherPriority.Loaded);
            }
        }

        private void HideMarker()
        {
            _dropAt = -1;
            if (_marker is { } marker && marker.Parent is AdornerLayer layer)
                layer.Children.Remove(marker);
            _marker = null;
        }

        // A drag near the scroller's top or bottom scrolls it, to reach sections out of sight.
        private void ScrollNear(Point point)
        {
            if (_scroller is not { } scroller || this.TranslatePoint(point, scroller) is not { } at)
                return;

            double by = at.Y < ScrollZone ? -ScrollStep : at.Y > scroller.Viewport.Height - ScrollZone ? ScrollStep : 0;
            if (by != 0)
            {
                double most = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
                scroller.Offset = scroller.Offset.WithY(Math.Clamp(scroller.Offset.Y + by, 0, most));
            }
        }

        /// <summary>The line across the stack where a dragged section would go.</summary>
        private sealed class InsertionMarker : Control
        {
            private const double Thickness = 2;
            private double? _y;

            public InsertionMarker() => IsHitTestVisible = false;

            public IBrush? Brush { get; init; }

            public void Show(double? y)
            {
                _y = y;
                InvalidateVisual();
            }

            public override void Render(DrawingContext context)
            {
                if (_y is not { } y || Brush is null)
                    return;

                // Inset from the edges, with round ends, and kept inside the stack at its top and bottom.
                double top = Math.Clamp(y, Thickness / 2, Math.Max(Thickness / 2, Bounds.Height - Thickness / 2));
                var pen = new Pen(Brush, Thickness, lineCap: PenLineCap.Round);
                context.DrawLine(pen, new Point(6, top), new Point(Math.Max(6, Bounds.Width - 6), top));
            }
        }
    }
}
