using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ChartPacker.Controls
{
    /// <summary>
    /// Shows the tooltip of whatever is under the pointer in <see cref="Region"/> as a strip of its own, the full width
    /// it's given and as tall as the tip, wrapped, needs, rather than as a popup beside the pointer that hides what's
    /// under it. While a control in the region holds the pointer, as a lane or the seek bar does while it's scrubbed,
    /// its tip stays for the whole drag, wherever the pointer goes. Hidden while there's no tip to show; it fades and
    /// slides up into view, and back down out of it.
    /// </summary>
    /// <remarks>
    /// <para>The tips are the region's own <see cref="ToolTip.TipProperty">ToolTip.Tip</see>, so the popups should be
    /// switched off over it with <see cref="ToolTip.ServiceEnabledProperty">ToolTip.ServiceEnabled</see>. A tip that's a
    /// control is shown here itself, with its owner's data context; a disabled control's tip only with
    /// <see cref="ToolTip.ShowOnDisabledProperty">ToolTip.ShowOnDisabled</see>, as a popup would be.</para>
    /// <para>The strip takes no pointer input, so a control it's drawn over is still the one under the pointer.
    /// Moving between controls swaps the tip at once; leaving them, it goes after a moment, so sweeping across a row
    /// of buttons doesn't make it blink.</para>
    /// </remarks>
    /// <example><c>&lt;ctl:ToolTipStrip VerticalAlignment="Bottom" /&gt;</c>, with <c>Region</c> set to the panel it's in.</example>
    public sealed class ToolTipStrip : Border
    {
        /// <summary>The area whose controls' tips are shown.</summary>
        public static readonly StyledProperty<Control?> RegionProperty =
            AvaloniaProperty.Register<ToolTipStrip, Control?>(nameof(Region));

        // How long the tip stays once the pointer is over nothing with one, so moving between controls doesn't hide it.
        private static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(150);

        // How long the strip takes to fade and slide in or out, and how far below its place it slides from.
        private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(150);
        private static readonly TransformOperations Lowered = TransformOperations.Parse("translateY(8px)");

        // Holds a tip without touching its data context, which a ContentPresenter would clear for a control.
        private readonly Decorator _host = new();
        private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap };
        private readonly DispatcherTimer _hideTimer;
        private readonly DispatcherTimer _fadeTimer;

        // The control whose tip is shown, and where the pointer was last in the region.
        private Control? _owner;
        private Point _lastPosition;

        public ToolTipStrip()
        {
            IsHitTestVisible = false;
            IsVisible = false;
            Opacity = 0;
            RenderTransform = Lowered;
            Child = _host;
            Transitions =
            [
                new DoubleTransition { Property = OpacityProperty, Duration = FadeDuration, Easing = new CubicEaseOut() },
                new TransformOperationsTransition { Property = RenderTransformProperty, Duration = FadeDuration, Easing = new CubicEaseOut() },
            ];

            _hideTimer = new DispatcherTimer { Interval = HideDelay };
            _hideTimer.Tick += (_, _) =>
            {
                _hideTimer.Stop();
                SetOwner(null);
            };

            // Once it has faded out, the strip lets go of the tip it was showing.
            _fadeTimer = new DispatcherTimer { Interval = FadeDuration };
            _fadeTimer.Tick += (_, _) =>
            {
                _fadeTimer.Stop();
                _host.Child = null;
                IsVisible = false;
            };
        }

        public Control? Region
        {
            get => GetValue(RegionProperty);
            set => SetValue(RegionProperty, value);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == RegionProperty)
            {
                if (change.OldValue is Control old)
                    Detach(old);
                if (change.NewValue is Control region)
                    Attach(region);
            }
        }

        private void Attach(Control region)
        {
            // Tunnelled and with handled events too, so a lane that handles its own pointer still has its tip shown.
            region.AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            region.AddHandler(PointerPressedEvent, OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            region.AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
            region.PointerExited += OnPointerExited;
            region.DetachedFromVisualTree += OnRegionDetached;
        }

        private void Detach(Control region)
        {
            region.RemoveHandler(PointerMovedEvent, OnPointerMoved);
            region.RemoveHandler(PointerPressedEvent, OnPointerMoved);
            region.RemoveHandler(PointerReleasedEvent, OnPointerReleased);
            region.PointerExited -= OnPointerExited;
            region.DetachedFromVisualTree -= OnRegionDetached;
            Hide();
        }

        private void OnPointerMoved(object? sender, PointerEventArgs e)
        {
            if (Region is not { } region)
                return;

            _lastPosition = e.GetPosition(region);
            Show(FindOwner(region, CapturedIn(region, e.Pointer) ?? HitTest(region, _lastPosition)));
        }

        /// <summary>The lane or box let go of the pointer once this has run, so what's under the pointer is looked at after.</summary>
        private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (Region is not { } region)
                return;

            _lastPosition = e.GetPosition(region);
            Dispatcher.UIThread.Post(() =>
            {
                if (Region == region)
                    Show(FindOwner(region, HitTest(region, _lastPosition)));
            });
        }

        private void OnPointerExited(object? sender, PointerEventArgs e)
        {
            if (Region is { } region && CapturedIn(region, e.Pointer) is null)
                Show(null);
        }

        private void OnRegionDetached(object? sender, VisualTreeAttachmentEventArgs e) => Hide();

        /// <summary>The control in the region holding the pointer, if one is.</summary>
        private static Visual? CapturedIn(Control region, IPointer pointer) =>
            pointer.Captured is Visual captured && region.IsVisualAncestorOf(captured) ? captured : null;

        /// <summary>
        /// The topmost visual at <paramref name="position"/>, disabled ones included, which input passes over, so a
        /// disabled control's tip can still be shown. Not the strip itself, which takes no input.
        /// </summary>
        private static Visual? HitTest(Control region, Point position)
        {
            foreach (var visual in region.GetVisualsAt(position, v => v.IsVisible && (v is not InputElement input || input.IsHitTestVisible)))
                return visual;
            return null;
        }

        /// <summary>The nearest control from <paramref name="visual"/> up to the region with a tip that can be shown.</summary>
        private static Control? FindOwner(Control region, Visual? visual)
        {
            for (var current = visual; current is not null; current = current.GetVisualParent())
            {
                if (current is Control control
                    && TipOf(control) is not null
                    && (control.IsEffectivelyEnabled || ToolTip.GetShowOnDisabled(control)))
                    return control;
                if (current == region)
                    break;
            }

            return null;
        }

        /// <summary>Shows <paramref name="owner"/>'s tip at once, or with none, hides the strip after a moment.</summary>
        private void Show(Control? owner)
        {
            if (owner is null)
            {
                if (_owner is not null && !_hideTimer.IsEnabled)
                    _hideTimer.Start();
                return;
            }

            _hideTimer.Stop();
            SetOwner(owner);
        }

        private void Hide()
        {
            _hideTimer.Stop();
            SetOwner(null);
        }

        private void SetOwner(Control? owner)
        {
            if (owner == _owner)
                return;

            if (_owner is { } old)
            {
                old.PropertyChanged -= OnOwnerPropertyChanged;
                old.DetachedFromVisualTree -= OnOwnerDetached;
            }

            _owner = owner;
            if (owner is not null)
            {
                owner.PropertyChanged += OnOwnerPropertyChanged;
                owner.DetachedFromVisualTree += OnOwnerDetached;
            }

            Present();
        }

        /// <summary>A tip that changes while it's shown, as the scrub tips do with the scrub step, is shown as it is now.</summary>
        private void OnOwnerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == ToolTip.TipProperty || e.Property == DataContextProperty)
                Present();
        }

        /// <summary>A control taken away under the pointer, as a song's rows are when another is picked, takes its tip with it.</summary>
        private void OnOwnerDetached(object? sender, VisualTreeAttachmentEventArgs e) => Hide();

        /// <summary>What <paramref name="control"/>'s tip shows: a <see cref="ToolTip"/>'s content, and none for blank text.</summary>
        private static object? TipOf(Control control)
        {
            var tip = ToolTip.GetTip(control);
            if (tip is ToolTip { Content: not Control } toolTip)
                tip = toolTip.Content;
            return tip is string text && string.IsNullOrWhiteSpace(text) ? null : tip;
        }

        /// <summary>Shows the owner's tip, fading and sliding the strip in if it was hidden; with none, fades it out.</summary>
        private void Present()
        {
            var tip = _owner is null ? null : TipOf(_owner);
            if (tip is null)
            {
                // The last tip stays while the strip fades, rather than it fading out empty.
                if (IsVisible && !_fadeTimer.IsEnabled)
                {
                    Opacity = 0;
                    RenderTransform = Lowered;
                    _fadeTimer.Start();
                }
                return;
            }

            // A tip that's a control has its owner's data context, as it would in a popup, e.g. a row of a list.
            _fadeTimer.Stop();
            _host.DataContext = _owner!.DataContext;
            if (tip is Control control)
            {
                _host.Child = control;
            }
            else
            {
                _text.Text = tip.ToString();
                _host.Child = _text;
            }

            IsVisible = true;
            Opacity = 1;
            RenderTransform = TransformOperations.Identity;
        }
    }
}
