using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using ChartBuilder.Resources;
using ChartShared.UI.Controls;

namespace ChartBuilder.Controls
{
    /// <summary>
    /// A section of a side-panel tab: a header that collapses and expands it, and that drags it to another place among
    /// its tab's sections (<see cref="PanelSectionStack"/>), with what it holds below. Its look is in PanelSection.axaml.
    /// </summary>
    /// <remarks>
    /// The header is a key stop: Enter or Space collapses or expands it, and Alt+Up and Alt+Down move it. Its context menu
    /// does those and more for every section of the tab.
    /// </remarks>
    [TemplatePart("PART_Header", typeof(Border))]
    [PseudoClasses(":collapsed", ":dragging")]
    public sealed class PanelSection : HeaderedContentControl
    {
        /// <summary>How far the pointer moves, pressed on the header, before it's a drag rather than a click.</summary>
        public const double DragThreshold = 4;

        public static readonly StyledProperty<string?> KeyProperty =
            AvaloniaProperty.Register<PanelSection, string?>(nameof(Key));

        public static readonly StyledProperty<bool> IsExpandedProperty =
            AvaloniaProperty.Register<PanelSection, bool>(nameof(IsExpanded), true);

        public static readonly StyledProperty<bool> FillsProperty =
            AvaloniaProperty.Register<PanelSection, bool>(nameof(Fills));

        public static readonly StyledProperty<double> FillMinHeightProperty =
            AvaloniaProperty.Register<PanelSection, double>(nameof(FillMinHeight), 140);

        private Border? _header;
        private IPointer? _pointer;
        private Point _pressPoint;
        private bool _isDragging;

        /// <summary>Which section this is, among its tab's, for remembering where it was put and whether it's collapsed.</summary>
        public string? Key
        {
            get => GetValue(KeyProperty);
            set => SetValue(KeyProperty, value);
        }

        /// <summary>Whether what the section holds is shown, or only its header.</summary>
        public bool IsExpanded
        {
            get => GetValue(IsExpandedProperty);
            set => SetValue(IsExpandedProperty, value);
        }

        /// <summary>
        /// Whether the section, expanded, takes the height its tab's other sections leave, as a list does, rather than the
        /// height of what it holds.
        /// </summary>
        public bool Fills
        {
            get => GetValue(FillsProperty);
            set => SetValue(FillsProperty, value);
        }

        /// <summary>The least a section that <see cref="Fills"/> is given, header and all; past it, the tab scrolls.</summary>
        public double FillMinHeight
        {
            get => GetValue(FillMinHeightProperty);
            set => SetValue(FillMinHeightProperty, value);
        }

        private PanelSectionStack? Stack => Parent as PanelSectionStack;

        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            base.OnApplyTemplate(e);

            if (_header is { } old)
            {
                old.PointerPressed -= OnHeaderPressed;
                old.PointerMoved -= OnHeaderMoved;
                old.PointerReleased -= OnHeaderReleased;
                old.PointerCaptureLost -= OnHeaderCaptureLost;
                old.KeyDown -= OnHeaderKeyDown;
                old.RemoveHandler(ContextRequestedEvent, OnHeaderContextRequested);
            }

            _header = e.NameScope.Find<Border>("PART_Header");
            if (_header is not { } header)
                return;

            header.PointerPressed += OnHeaderPressed;
            header.PointerMoved += OnHeaderMoved;
            header.PointerReleased += OnHeaderReleased;
            header.PointerCaptureLost += OnHeaderCaptureLost;
            header.KeyDown += OnHeaderKeyDown;
            NameHeader();

            // The menu is filled as it's asked for, on the way down, so it's never stale: which moves there are depends on
            // where the section is now. It's the header's from the start, as a menu set while it's asked for wouldn't open.
            header.ContextMenu = new ContextMenu();
            header.ContextMenu.Opening += OnMenuOpening;
            header.AddHandler(ContextRequestedEvent, OnHeaderContextRequested, RoutingStrategies.Tunnel);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == IsExpandedProperty)
                PseudoClasses.Set(":collapsed", !change.GetNewValue<bool>());
            else if (change.Property == HeaderProperty)
                NameHeader();
        }

        // A screen reader reads the header as the section's title.
        private void NameHeader()
        {
            if (_header is { } header)
                Avalonia.Automation.AutomationProperties.SetName(header, Header?.ToString());
        }

        /// <summary>Collapses or expands the section, as the user asked, remembering it.</summary>
        public void Toggle()
        {
            IsExpanded = !IsExpanded;
            Stack?.Remember(this);
        }

        private void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
        {
            if (_header is not { } header || !e.GetCurrentPoint(header).Properties.IsLeftButtonPressed)
                return;

            _pointer = e.Pointer;
            _pressPoint = e.GetPosition(header);
            _isDragging = false;
            e.Pointer.Capture(header);
            header.Focus(NavigationMethod.Pointer);
            e.Handled = true;
        }

        private void OnHeaderMoved(object? sender, PointerEventArgs e)
        {
            if (_header is not { } header || _pointer != e.Pointer || Stack is not { } stack)
                return;

            if (!_isDragging)
            {
                var moved = e.GetPosition(header) - _pressPoint;
                if (Math.Abs(moved.X) < DragThreshold && Math.Abs(moved.Y) < DragThreshold)
                    return;
                _isDragging = true;
                PseudoClasses.Set(":dragging", true);
                stack.BeginDrag(this, e, _pressPoint.Y);
            }

            stack.DragTo(this, e.GetPosition(stack));
        }

        private void OnHeaderReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_pointer != e.Pointer)
                return;

            bool dragged = _isDragging;
            EndPress(drop: dragged);
            e.Handled = true;
            if (!dragged)
                Toggle();
        }

        private void OnHeaderCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        {
            if (_pointer is not null)
                EndPress(drop: false);
        }

        /// <summary>Ends a press on the header: a drag drops the section where it was shown going, or puts it back.</summary>
        internal void EndPress(bool drop)
        {
            if (_pointer is not { } pointer)
                return;

            // Cleared first: letting go of the capture raises PointerCaptureLost, which comes back here.
            _pointer = null;
            bool wasDragging = _isDragging;
            _isDragging = false;
            PseudoClasses.Set(":dragging", false);
            if (pointer.Captured == _header)
                pointer.Capture(null);
            if (wasDragging)
                Stack?.EndDrag(this, drop);
        }

        private void OnHeaderKeyDown(object? sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Avalonia.Input.Key.Enter or Avalonia.Input.Key.Space when e.KeyModifiers == KeyModifiers.None:
                    Toggle();
                    e.Handled = true;
                    break;
                case Avalonia.Input.Key.Up when e.KeyModifiers == KeyModifiers.Alt:
                    Stack?.MoveBy(this, -1);
                    _header?.Focus(NavigationMethod.Directional);
                    e.Handled = true;
                    break;
                case Avalonia.Input.Key.Down when e.KeyModifiers == KeyModifiers.Alt:
                    Stack?.MoveBy(this, 1);
                    _header?.Focus(NavigationMethod.Directional);
                    e.Handled = true;
                    break;
                case Avalonia.Input.Key.Escape when _isDragging:
                    EndPress(drop: false);
                    e.Handled = true;
                    break;
            }
        }

        private void OnHeaderContextRequested(object? sender, ContextRequestedEventArgs e)
        {
            if (_header?.ContextMenu is not { } menu || Stack is not { } stack)
                return;

            menu.Items.Clear();
            menu.Items.Add(Turned(MenuActions.Create(Strings.SectionMoveUp, () => stack.MoveBy(this, -1), "ArrowLeftStroke", stack.CanMoveBy(this, -1))));
            menu.Items.Add(Turned(MenuActions.Create(Strings.SectionMoveDown, () => stack.MoveBy(this, 1), "ArrowRightStroke", stack.CanMoveBy(this, 1))));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuActions.Create(Strings.SectionCollapseAll, () => stack.ExpandAll(false), null));
            menu.Items.Add(MenuActions.Create(Strings.SectionExpandAll, () => stack.ExpandAll(true), null));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuActions.Create(Strings.SectionResetLayout, stack.ResetLayout, "RevertStroke"));
        }

        // Nothing to show outside a tab's sections.
        private void OnMenuOpening(object? sender, CancelEventArgs e)
        {
            if (sender is ContextMenu { ItemCount: 0 })
                e.Cancel = true;
        }

        // The arrows point along the tab, which runs down.
        private static MenuItem Turned(MenuItem item)
        {
            if (item.Icon is Control glyph)
                glyph.RenderTransform = new Avalonia.Media.RotateTransform(90);
            return item;
        }
    }
}
