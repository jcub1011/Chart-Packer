using System;
using System.Collections.Generic;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace ChartBuilder.Controls
{
    /// <summary>A tab's title on a collapsed <see cref="EditorSidePanel"/>'s strip.</summary>
    /// <param name="Index">The tab's index, which clicking the title selects.</param>
    public sealed record EditorSidePanelStripTab(int Index, string? Title);

    /// <summary>
    /// One of the Editor's side panels: its <see cref="TabItem"/>s, as a row of compact tabs along the top that scrolls
    /// when they don't fit, with a button that collapses the panel to a narrow strip along the window's edge, like a
    /// Visual Studio tool window set to auto-hide. The strip shows each tab's title, scrolling too; clicking one brings
    /// the panel back on that tab. Its look is in EditorSidePanel.axaml.
    /// </summary>
    [TemplatePart("PART_StripTabs", typeof(ItemsControl))]
    [TemplatePart("PART_TabScroller", typeof(ScrollViewer))]
    [PseudoClasses(":collapsed", ":right")]
    public sealed class EditorSidePanel : TabControl
    {
        /// <summary>How wide the panel is while collapsed. The Editor sizes its columns with it.</summary>
        public const double StripWidth = 28;

        public static readonly StyledProperty<bool> IsCollapsedProperty =
            AvaloniaProperty.Register<EditorSidePanel, bool>(nameof(IsCollapsed));

        public static readonly StyledProperty<ICommand?> ToggleCommandProperty =
            AvaloniaProperty.Register<EditorSidePanel, ICommand?>(nameof(ToggleCommand));

        public static readonly StyledProperty<bool> IsRightEdgeProperty =
            AvaloniaProperty.Register<EditorSidePanel, bool>(nameof(IsRightEdge));

        public static readonly DirectProperty<EditorSidePanel, IReadOnlyList<EditorSidePanelStripTab>> StripTabsProperty =
            AvaloniaProperty.RegisterDirect<EditorSidePanel, IReadOnlyList<EditorSidePanelStripTab>>(nameof(StripTabs), p => p.StripTabs);

        // How far one notch of the mouse wheel scrolls the tabs sideways.
        private const double WheelStep = 50;

        private IReadOnlyList<EditorSidePanelStripTab> _stripTabs = [];
        private ItemsControl? _strip;
        private ScrollViewer? _tabScroller;

        public EditorSidePanel() => Items.CollectionChanged += (_, _) => RefreshStripTabs();

        /// <summary>Whether the panel is only its strip.</summary>
        public bool IsCollapsed
        {
            get => GetValue(IsCollapsedProperty);
            set => SetValue(IsCollapsedProperty, value);
        }

        /// <summary>Collapses or expands the panel, from its collapse button or its strip.</summary>
        public ICommand? ToggleCommand
        {
            get => GetValue(ToggleCommandProperty);
            set => SetValue(ToggleCommandProperty, value);
        }

        /// <summary>Whether the panel is along the window's right edge, which mirrors its chevron and strip.</summary>
        public bool IsRightEdge
        {
            get => GetValue(IsRightEdgeProperty);
            set => SetValue(IsRightEdgeProperty, value);
        }

        /// <summary>The tabs' titles, for the strip.</summary>
        public IReadOnlyList<EditorSidePanelStripTab> StripTabs
        {
            get => _stripTabs;
            private set => SetAndRaise(StripTabsProperty, ref _stripTabs, value);
        }

        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            base.OnApplyTemplate(e);

            _strip?.RemoveHandler(Button.ClickEvent, OnStripTabClick);
            _strip = e.NameScope.Find<ItemsControl>("PART_StripTabs");
            _strip?.AddHandler(Button.ClickEvent, OnStripTabClick);

            if (_tabScroller is { } old)
                old.RemoveHandler(PointerWheelChangedEvent, OnTabScrollerWheel);

            _tabScroller = e.NameScope.Find<ScrollViewer>("PART_TabScroller");
            if (_tabScroller is { } scroller)
                scroller.AddHandler(PointerWheelChangedEvent, OnTabScrollerWheel, RoutingStrategies.Tunnel);

            RefreshStripTabs();
        }

        protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
        {
            base.PrepareContainerForItemOverride(container, item, index);

            // Compact tabs, like a docked tool window's, rather than the theme's large ones.
            if (container is TabItem tab)
            {
                tab.FontSize = 13;
                tab.FontWeight = FontWeight.SemiBold;
                tab.MinHeight = 34;
                tab.Margin = default;
                tab.Padding = new Thickness(8, 0);
            }
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == IsCollapsedProperty)
                PseudoClasses.Set(":collapsed", change.GetNewValue<bool>());
            else if (change.Property == IsRightEdgeProperty)
                PseudoClasses.Set(":right", change.GetNewValue<bool>());
        }

        private void RefreshStripTabs()
        {
            var tabs = new List<EditorSidePanelStripTab>();
            for (int i = 0; i < Items.Count; i++)
                tabs.Add(new EditorSidePanelStripTab(i, (Items[i] as HeaderedContentControl)?.Header?.ToString()));
            StripTabs = tabs;
        }

        // A title on the strip: the panel comes back on that tab.
        private void OnStripTabClick(object? sender, RoutedEventArgs e)
        {
            if (e.Source is not Control { DataContext: EditorSidePanelStripTab tab })
                return;

            e.Handled = true;
            SelectedIndex = tab.Index;
            if (ToggleCommand is { } toggle && toggle.CanExecute(null))
                toggle.Execute(null);

            // Its tab may be scrolled out of sight; once the panel is laid out again, it's brought into view.
            Dispatcher.UIThread.Post(() => ContainerFromIndex(tab.Index)?.BringIntoView(), DispatcherPriority.Loaded);
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
    }
}
