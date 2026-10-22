using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ChartBuilder.Resources;
using ChartBuilder.Services;
using ChartShared.UI.Controls;

namespace ChartBuilder.Controls
{
    /// <summary>A tab's title on an auto-hiding <see cref="EditorSidePanel"/>'s strip.</summary>
    /// <param name="Index">The tab's index, which clicking the title selects.</param>
    /// <param name="IsActive">Whether it's the tab the panel has flown out on.</param>
    public sealed record EditorSidePanelStripTab(int Index, string? Title, bool IsActive);

    /// <summary>
    /// One of the Editor's side panels, like a Visual Studio tool window: its <see cref="TabItem"/>s, as a row of compact tabs
    /// along the top that scrolls when they don't fit, and a pin. Pinned, the panel is docked beside the chart. Unpinned, it
    /// auto-hides: a narrow strip along the window's edge with each tab's title, a click on one flying the panel out over the
    /// chart on that tab, until a click lands anywhere else. The tabs can be dragged into another order, which is kept by key
    /// (<see cref="KeyProperty"/>) in the inherited <see cref="PanelLayout.Store"/>. Its look is in EditorSidePanel.axaml.
    /// </summary>
    [TemplatePart("PART_StripTabs", typeof(ItemsControl))]
    [TemplatePart("PART_TabScroller", typeof(ScrollViewer))]
    [TemplatePart("PART_PinButton", typeof(Button))]
    [TemplatePart("PART_ResizeGrip", typeof(Thumb))]
    [PseudoClasses(":autohide", ":open", ":right")]
    public sealed class EditorSidePanel : TabControl
    {
        /// <summary>How wide the strip of an auto-hiding panel is. The Editor sizes its columns with it.</summary>
        public const double StripWidth = 28;

        /// <summary>A tab's key, by which its place among the panel's tabs is kept.</summary>
        public static readonly AttachedProperty<string?> KeyProperty =
            AvaloniaProperty.RegisterAttached<EditorSidePanel, TabItem, string?>("Key");

        public static readonly StyledProperty<bool> IsAutoHideProperty =
            AvaloniaProperty.Register<EditorSidePanel, bool>(nameof(IsAutoHide), defaultBindingMode: BindingMode.TwoWay);

        public static readonly StyledProperty<bool> IsOpenProperty =
            AvaloniaProperty.Register<EditorSidePanel, bool>(nameof(IsOpen), defaultBindingMode: BindingMode.TwoWay);

        public static readonly StyledProperty<double> PanelWidthProperty =
            AvaloniaProperty.Register<EditorSidePanel, double>(nameof(PanelWidth), BuilderSettings.DefaultEditorLeftPanelWidth, defaultBindingMode: BindingMode.TwoWay);

        public static readonly StyledProperty<bool> IsRightEdgeProperty =
            AvaloniaProperty.Register<EditorSidePanel, bool>(nameof(IsRightEdge));

        public static readonly StyledProperty<string?> LayoutKeyProperty =
            AvaloniaProperty.Register<EditorSidePanel, string?>(nameof(LayoutKey));

        public static readonly StyledProperty<string?> SelectedKeyProperty =
            AvaloniaProperty.Register<EditorSidePanel, string?>(nameof(SelectedKey), defaultBindingMode: BindingMode.TwoWay);

        public static readonly DirectProperty<EditorSidePanel, IReadOnlyList<EditorSidePanelStripTab>> StripTabsProperty =
            AvaloniaProperty.RegisterDirect<EditorSidePanel, IReadOnlyList<EditorSidePanelStripTab>>(nameof(StripTabs), p => p.StripTabs);

        // How far one notch of the mouse wheel scrolls the tabs sideways.
        private const double WheelStep = 50;

        private IReadOnlyList<EditorSidePanelStripTab> _stripTabs = [];
        private IReadOnlyList<string> _tabOrder = [];
        private ItemsControl? _strip;
        private ScrollViewer? _tabScroller;
        private Button? _pin;
        private Thumb? _grip;
        private PanelLayoutStore? _store;
        private TopLevel? _dismissHost;
        private bool _syncingSelection;

        // While flown out, the panel's own popups that a press closes: its flyouts, drop-downs and menus, not its tooltips.
        // A press that closes one isn't also taken as a press away from the panel.
        private readonly HashSet<Popup> _openPopups = [];
        private IDisposable? _popupWatch;

        // A tab being pressed, then dragged along the row.
        private TabItem? _pressedTab;
        private IPointer? _pointer;
        private Point _pressPoint;
        private bool _isDragging;
        private IReadOnlyList<string>? _orderBeforeDrag;

        public EditorSidePanel()
        {
            Items.CollectionChanged += (_, _) =>
            {
                RefreshOrder();
                SelectByKey();
            };
            AddHandler(KeyDownEvent, OnTabKeyDown, RoutingStrategies.Tunnel);
        }

        public static string? GetKey(TabItem tab) => tab.GetValue(KeyProperty);

        public static void SetKey(TabItem tab, string? value) => tab.SetValue(KeyProperty, value);

        /// <summary>Whether the panel auto-hides to its strip, rather than being pinned beside the chart.</summary>
        public bool IsAutoHide
        {
            get => GetValue(IsAutoHideProperty);
            set => SetValue(IsAutoHideProperty, value);
        }

        /// <summary>Whether the panel, auto-hiding, has flown out over the chart.</summary>
        public bool IsOpen
        {
            get => GetValue(IsOpenProperty);
            set => SetValue(IsOpenProperty, value);
        }

        /// <summary>How wide the panel is, flown out; its grip changes it. Pinned, its column's splitter does.</summary>
        public double PanelWidth
        {
            get => GetValue(PanelWidthProperty);
            set => SetValue(PanelWidthProperty, value);
        }

        /// <summary>Whether the panel is along the window's right edge, which mirrors its strip and its grip.</summary>
        public bool IsRightEdge
        {
            get => GetValue(IsRightEdgeProperty);
            set => SetValue(IsRightEdgeProperty, value);
        }

        /// <summary>Which panel this is, for keeping its tabs' order.</summary>
        public string? LayoutKey
        {
            get => GetValue(LayoutKeyProperty);
            set => SetValue(LayoutKeyProperty, value);
        }

        /// <summary>The key of the tab shown, which stays with it wherever it's dragged.</summary>
        public string? SelectedKey
        {
            get => GetValue(SelectedKeyProperty);
            set => SetValue(SelectedKeyProperty, value);
        }

        /// <summary>The tabs' titles, for the strip, in the order the tabs are shown.</summary>
        public IReadOnlyList<EditorSidePanelStripTab> StripTabs
        {
            get => _stripTabs;
            private set => SetAndRaise(StripTabsProperty, ref _stripTabs, value);
        }

        private IEnumerable<TabItem> Tabs => Items.OfType<TabItem>();

        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            base.OnApplyTemplate(e);

            _strip?.RemoveHandler(Button.ClickEvent, OnStripTabClick);
            _strip = e.NameScope.Find<ItemsControl>("PART_StripTabs");
            _strip?.AddHandler(Button.ClickEvent, OnStripTabClick);

            if (_tabScroller is { } old)
            {
                old.RemoveHandler(PointerWheelChangedEvent, OnTabScrollerWheel);
                old.RemoveHandler(PointerPressedEvent, OnTabPressed);
                old.RemoveHandler(PointerMovedEvent, OnTabMoved);
                old.RemoveHandler(PointerReleasedEvent, OnTabReleased);
                old.RemoveHandler(PointerCaptureLostEvent, OnTabCaptureLost);
                old.RemoveHandler(ContextRequestedEvent, OnTabContextRequested);
            }

            _tabScroller = e.NameScope.Find<ScrollViewer>("PART_TabScroller");
            if (_tabScroller is { } scroller)
            {
                scroller.AddHandler(PointerWheelChangedEvent, OnTabScrollerWheel, RoutingStrategies.Tunnel);
                // Handled too, as a tab marks the press that selects it handled.
                scroller.AddHandler(PointerPressedEvent, OnTabPressed, RoutingStrategies.Bubble, handledEventsToo: true);
                scroller.AddHandler(PointerMovedEvent, OnTabMoved, RoutingStrategies.Bubble, handledEventsToo: true);
                scroller.AddHandler(PointerReleasedEvent, OnTabReleased, RoutingStrategies.Bubble, handledEventsToo: true);
                scroller.AddHandler(PointerCaptureLostEvent, OnTabCaptureLost);
                scroller.AddHandler(ContextRequestedEvent, OnTabContextRequested, RoutingStrategies.Tunnel);
            }

            if (_pin is not null)
                _pin.Click -= OnPinClick;
            _pin = e.NameScope.Find<Button>("PART_PinButton");
            if (_pin is not null)
                _pin.Click += OnPinClick;

            if (_grip is not null)
                _grip.DragDelta -= OnGripDragged;
            _grip = e.NameScope.Find<Thumb>("PART_ResizeGrip");
            if (_grip is not null)
                _grip.DragDelta += OnGripDragged;

            RefreshOrder();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            Follow(PanelLayout.GetStore(this));
            ListenForDismiss();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            Follow(null);
            StopListeningForDismiss();
        }

        protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
        {
            base.PrepareContainerForItemOverride(container, item, index);

            // Compact tabs, like a docked tool window's, rather than the theme's large ones; each with a menu to move it,
            // filled as it's asked for (OnTabContextRequested).
            if (container is TabItem tab)
            {
                tab.FontSize = 13;
                tab.FontWeight = FontWeight.SemiBold;
                tab.MinHeight = 34;
                tab.Margin = default;
                tab.Padding = new Thickness(8, 0);
                tab.ContextMenu ??= new ContextMenu();
            }
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == IsAutoHideProperty)
            {
                PseudoClasses.Set(":autohide", change.GetNewValue<bool>());
                IsOpen = false;
                RefreshStripTabs();
            }
            else if (change.Property == IsOpenProperty)
            {
                PseudoClasses.Set(":open", change.GetNewValue<bool>());
                ListenForDismiss();
                RefreshStripTabs();
            }
            else if (change.Property == IsRightEdgeProperty)
            {
                PseudoClasses.Set(":right", change.GetNewValue<bool>());
            }
            else if (change.Property == LayoutKeyProperty)
            {
                RefreshOrder();
            }
            else if (change.Property == PanelLayout.StoreProperty && this.IsAttachedToVisualTree())
            {
                Follow(change.GetNewValue<PanelLayoutStore?>());
            }
            else if (change.Property == SelectedKeyProperty)
            {
                SelectByKey();
            }
            else if (change.Property == SelectedItemProperty && !_syncingSelection)
            {
                if (change.GetNewValue<object?>() is TabItem tab)
                {
                    _syncingSelection = true;
                    SelectedKey = GetKey(tab);
                    _syncingSelection = false;
                }
                RefreshStripTabs();
            }
        }

        private void SelectByKey()
        {
            if (_syncingSelection || SelectedKey is not { } key || Tabs.FirstOrDefault(t => GetKey(t) == key) is not { } tab || SelectedItem == tab)
                return;

            _syncingSelection = true;
            SelectedItem = tab;
            _syncingSelection = false;
            RefreshStripTabs();
        }

        private void Follow(PanelLayoutStore? store)
        {
            if (_store is not null)
                _store.Changed -= OnStoreChanged;
            _store = store;
            if (_store is not null)
                _store.Changed += OnStoreChanged;
            RefreshOrder();
        }

        private void OnStoreChanged(object? sender, string key)
        {
            if (key == LayoutKey)
                RefreshOrder();
        }

        // The tabs' keys in their default order: as they're written.
        private List<string> PresentKeys() => [.. Tabs.Select(GetKey).OfType<string>()];

        /// <summary>Lays the tabs out in the order they were left in.</summary>
        private void RefreshOrder()
        {
            var present = PresentKeys();
            var saved = _store is { } store && LayoutKey is { } panel ? store.TabOrder(panel, present) : _tabOrder;
            SetOrder(PanelLayoutStore.Arrange(saved, present), remember: false);
        }

        private void SetOrder(IReadOnlyList<string> order, bool remember)
        {
            _tabOrder = order;
            if (remember && _store is { } store && LayoutKey is { } panel)
                store.SetTabOrder(panel, order);
            ItemsPanelRoot?.InvalidateMeasure();
            RefreshStripTabs();
        }

        // The tabs as they're shown, with those without a key after them.
        private List<TabItem> OrderedTabs() => [.. Tabs.OrderBy(PlaceOf)];

        /// <summary>
        /// Where <paramref name="child"/>, a tab, is shown among the tabs, which <see cref="EditorSidePanelTabsPanel"/> lays
        /// them out by; anything without a key goes after them.
        /// </summary>
        internal int PlaceOf(Control child) =>
            child is TabItem tab && GetKey(tab) is { } key && IndexIn(_tabOrder, key) is int at and >= 0 ? at : int.MaxValue;

        private static int IndexIn(IReadOnlyList<string> order, string key)
        {
            for (int i = 0; i < order.Count; i++)
            {
                if (order[i] == key)
                    return i;
            }
            return -1;
        }

        private void RefreshStripTabs()
        {
            var tabs = new List<EditorSidePanelStripTab>();
            foreach (var tab in OrderedTabs())
            {
                int index = Items.IndexOf(tab);
                tabs.Add(new EditorSidePanelStripTab(index, tab.Header?.ToString(), IsOpen && SelectedIndex == index));
            }
            StripTabs = tabs;
        }

        private void OnPinClick(object? sender, RoutedEventArgs e) => IsAutoHide = !IsAutoHide;

        // The flown-out panel's grip, on its inner edge, makes it wider or narrower. From the width it's shown at, which the
        // window may hold below PanelWidth: the grip only moves as far as the panel does, so taking the move from PanelWidth
        // would add it again with every move past where the window stops it.
        private void OnGripDragged(object? sender, VectorEventArgs e)
        {
            double change = IsRightEdge ? -e.Vector.X : e.Vector.X;
            double shown = double.IsNaN(Width) ? PanelWidth : Width - StripWidth;
            PanelWidth = Math.Max(BuilderSettings.MinEditorPanelWidth, shown + change);
        }

        // A title on the strip: the panel flies out on that tab, or, already out on it, goes back.
        private void OnStripTabClick(object? sender, RoutedEventArgs e)
        {
            if (e.Source is not Control { DataContext: EditorSidePanelStripTab tab })
                return;

            e.Handled = true;
            if (IsOpen && SelectedIndex == tab.Index)
            {
                IsOpen = false;
                return;
            }

            SelectedIndex = tab.Index;
            IsOpen = true;

            // Its tab may be scrolled out of sight; once the panel is laid out again, it's brought into view.
            Dispatcher.UIThread.Post(() => ContainerFromIndex(tab.Index)?.BringIntoView(), DispatcherPriority.Loaded);
        }

        // While flown out, a press anywhere but on the panel sends it back, unless it's closing a drop-down, a flyout or a menu
        // of the panel's. A press on one of those isn't seen here at all, as each is a window of its own.
        private void ListenForDismiss()
        {
            bool listen = IsOpen && this.IsAttachedToVisualTree();
            if (listen && _dismissHost is null)
            {
                _dismissHost = TopLevel.GetTopLevel(this);
                _dismissHost?.AddHandler(PointerPressedEvent, OnPressedAnywhere, RoutingStrategies.Tunnel, handledEventsToo: true);
                _dismissHost?.AddHandler(KeyDownEvent, OnKeyAnywhere, RoutingStrategies.Bubble);
                _popupWatch ??= Popup.IsOpenProperty.Changed.AddClassHandler<Popup>(OnPopupOpenChanged);
            }
            else if (!listen)
            {
                StopListeningForDismiss();
            }
        }

        private void StopListeningForDismiss()
        {
            _dismissHost?.RemoveHandler(PointerPressedEvent, OnPressedAnywhere);
            _dismissHost?.RemoveHandler(KeyDownEvent, OnKeyAnywhere);
            _dismissHost = null;
            _popupWatch?.Dispose();
            _popupWatch = null;
            _openPopups.Clear();
        }

        // Only popups a press closes count, so a tooltip never does. One closed is still counted until the press that closed
        // it has been handled: a menu closes as the press comes in, before the panel sees it.
        private void OnPopupOpenChanged(Popup popup, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.GetNewValue<bool>())
            {
                if (popup.IsLightDismissEnabled && (IsMine(popup) || IsMine(popup.PlacementTarget)))
                    _openPopups.Add(popup);
            }
            else if (_openPopups.Contains(popup))
            {
                Dispatcher.UIThread.Post(() => { if (!popup.IsOpen) _openPopups.Remove(popup); }, DispatcherPriority.Background);
            }
        }

        private void OnPressedAnywhere(object? sender, PointerPressedEventArgs e)
        {
            if (e.Source is Visual source && (source == this || this.IsVisualAncestorOf(source)))
                return;

            // A flyout, drop-down or menu of the panel's is open: this press closes it, and only it, so the panel stays
            // out until a press with nothing of its open.
            if (HasPopupOpen())
                return;
            IsOpen = false;
        }

        private bool HasPopupOpen() => _openPopups.Count > 0;

        private bool IsMine(Visual? visual) => visual is not null && (visual == this || this.IsVisualAncestorOf(visual));

        // Esc sends the panel back, unless something in it took Esc for itself, as a field does to undo its edit.
        private void OnKeyAnywhere(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && !e.Handled)
            {
                IsOpen = false;
                e.Handled = true;
            }
        }

        // The tabs only scroll sideways, so the wheel, which scrolls up and down, scrolls them sideways instead.
        private void OnTabScrollerWheel(object? sender, PointerWheelEventArgs e)
        {
            if (_tabScroller is not { } scroller || e.Delta.Y == 0 || e.Delta.X != 0)
                return;

            double most = Math.Max(0, scroller.Extent.Width - scroller.Viewport.Width);
            scroller.Offset = scroller.Offset.WithX(Math.Clamp(scroller.Offset.X - e.Delta.Y * WheelStep, 0, most));
            e.Handled = true;
        }

        private void OnTabPressed(object? sender, PointerPressedEventArgs e)
        {
            if (_tabScroller is not { } scroller || !e.GetCurrentPoint(scroller).Properties.IsLeftButtonPressed)
                return;
            if ((e.Source as Visual)?.FindAncestorOfType<TabItem>(includeSelf: true) is not { } tab || GetKey(tab) is null)
                return;

            _pressedTab = tab;
            _pointer = e.Pointer;
            _pressPoint = e.GetPosition(scroller);
            _isDragging = false;
        }

        private void OnTabMoved(object? sender, PointerEventArgs e)
        {
            if (_pressedTab is not { } tab || _pointer != e.Pointer || _tabScroller is not { } scroller || ItemsPanelRoot is not { } row)
                return;

            // Let go of somewhere else, where the release wasn't seen: it's no longer a press.
            if (!_isDragging && !e.GetCurrentPoint(scroller).Properties.IsLeftButtonPressed)
            {
                _pressedTab = null;
                _pointer = null;
                return;
            }

            if (!_isDragging)
            {
                var moved = e.GetPosition(scroller) - _pressPoint;
                if (Math.Abs(moved.X) < PanelSection.DragThreshold && Math.Abs(moved.Y) < PanelSection.DragThreshold)
                    return;
                _isDragging = true;
                _orderBeforeDrag = _tabOrder;
                tab.Classes.Add("dragging");
                e.Pointer.Capture(scroller);
            }

            // Live, as tabs in a browser go: past the middle of the tab beside it, the dragged one swaps with it.
            double x = e.GetPosition(row).X;
            var others = OrderedTabs().Where(t => t != tab && t.IsVisible).ToList();
            int at = others.Count(t => t.Bounds.Center.X < x);
            var order = others.Select(GetKey).OfType<string>().ToList();
            order.Insert(Math.Min(at, order.Count), GetKey(tab)!);
            if (!order.SequenceEqual(_tabOrder))
                SetOrder(order, remember: false);
        }

        private void OnTabReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_pressedTab is null || _pointer != e.Pointer)
                return;
            EndTabDrag(keep: true);
        }

        private void OnTabCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        {
            if (_isDragging)
                EndTabDrag(keep: true);
        }

        private void EndTabDrag(bool keep)
        {
            var tab = _pressedTab;
            var pointer = _pointer;
            bool dragged = _isDragging;

            // Cleared first: letting go of the capture raises PointerCaptureLost, which comes back here.
            _pressedTab = null;
            _pointer = null;
            _isDragging = false;
            tab?.Classes.Remove("dragging");
            if (pointer?.Captured == _tabScroller)
                pointer?.Capture(null);

            if (!dragged)
                return;
            if (keep)
                SetOrder(_tabOrder, remember: true);
            else if (_orderBeforeDrag is { } before)
                SetOrder(before, remember: false);
            _orderBeforeDrag = null;
        }

        // On a tab: Esc puts back a drag; the arrows go to the tab beside it as they're shown, and with Alt, move it.
        private void OnTabKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && _isDragging)
            {
                EndTabDrag(keep: false);
                e.Handled = true;
                return;
            }

            if (e.Key is not (Key.Left or Key.Right) || e.Source is not TabItem tab || !Tabs.Contains(tab))
                return;

            int by = e.Key == Key.Left ? -1 : 1;
            if (e.KeyModifiers == KeyModifiers.Alt)
            {
                MoveTab(tab, by);
            }
            else if (e.KeyModifiers == KeyModifiers.None)
            {
                var shown = OrderedTabs().Where(t => t.IsVisible && t.IsEnabled).ToList();
                int at = shown.IndexOf(tab) + by;
                if (at < 0 || at >= shown.Count)
                    return;
                SelectedItem = shown[at];
                shown[at].Focus(NavigationMethod.Directional);
            }
            else
            {
                return;
            }
            e.Handled = true;
        }

        private bool CanMoveTab(TabItem tab, int by)
        {
            int at = IndexIn(_tabOrder, GetKey(tab) ?? "");
            return at >= 0 && at + by >= 0 && at + by < _tabOrder.Count;
        }

        private void MoveTab(TabItem tab, int by)
        {
            if (!CanMoveTab(tab, by))
                return;
            var order = _tabOrder.ToList();
            int at = order.IndexOf(GetKey(tab)!);
            (order[at], order[at + by]) = (order[at + by], order[at]);
            SetOrder(order, remember: true);
        }

        private void ResetTabs()
        {
            if (_store is { } store && LayoutKey is { } panel)
                store.ResetTabs(panel);
            _tabOrder = [];
            RefreshOrder();
        }

        // A tab's menu, filled as it's asked for, so its moves are those it has where it is now.
        private void OnTabContextRequested(object? sender, ContextRequestedEventArgs e)
        {
            if ((e.Source as Visual)?.FindAncestorOfType<TabItem>(includeSelf: true) is not { ContextMenu: { } menu } tab || GetKey(tab) is null)
                return;

            menu.Items.Clear();
            menu.Items.Add(MenuActions.Create(Strings.TabMoveLeft, () => MoveTab(tab, -1), "ArrowLeftStroke", CanMoveTab(tab, -1)));
            menu.Items.Add(MenuActions.Create(Strings.TabMoveRight, () => MoveTab(tab, 1), "ArrowRightStroke", CanMoveTab(tab, 1)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuActions.Create(Strings.TabResetOrder, ResetTabs, "RevertStroke"));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuActions.Create(IsAutoHide ? Strings.PanelPinToolTip : Strings.PanelAutoHideToolTip, () => IsAutoHide = !IsAutoHide, "PinStroke"));
        }
    }

    /// <summary>
    /// The row of an <see cref="EditorSidePanel"/>'s tabs, laid out in the order they were dragged to, rather than the order
    /// they're written in, so moving one never takes it out of the panel and back.
    /// </summary>
    public sealed class EditorSidePanelTabsPanel : Panel
    {
        private EditorSidePanel? Owner => this.FindAncestorOfType<EditorSidePanel>();

        private IEnumerable<Control> Ordered() => Owner is { } owner ? Children.OrderBy(owner.PlaceOf) : Children;

        protected override Size MeasureOverride(Size availableSize)
        {
            double width = 0;
            double height = 0;
            foreach (var child in Children)
            {
                child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
                width += child.DesiredSize.Width;
                height = Math.Max(height, child.DesiredSize.Height);
            }
            return new Size(width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double x = 0;
            foreach (var child in Ordered())
            {
                child.Arrange(new Rect(x, 0, child.DesiredSize.Width, finalSize.Height));
                if (child.IsVisible)
                    x += child.DesiredSize.Width;
            }
            return finalSize;
        }
    }
}
