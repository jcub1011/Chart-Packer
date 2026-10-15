using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace ChartPacker.UI.Controls
{
    /// <summary>
    /// A click inside a panel but outside its focused edit box takes focus from the box, so the box commits as it does
    /// when focus moves to another control. Clicks on empty space and labels otherwise leave the box focused. The panel
    /// takes the focus, so it must be focusable.
    /// </summary>
    /// <example><c>&lt;UserControl Focusable="True" FocusAdorner="{x:Null}" ui:CommitOnClickAway.IsEnabled="True"&gt;</c></example>
    public sealed class CommitOnClickAway
    {
        public static readonly AttachedProperty<bool> IsEnabledProperty =
            AvaloniaProperty.RegisterAttached<CommitOnClickAway, InputElement, bool>("IsEnabled");

        static CommitOnClickAway()
        {
            IsEnabledProperty.Changed.AddClassHandler<InputElement>((panel, e) =>
            {
                panel.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
                if (e.NewValue is true)
                    panel.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
            });
        }

        private CommitOnClickAway()
        {
        }

        public static bool GetIsEnabled(InputElement element) => element.GetValue(IsEnabledProperty);

        public static void SetIsEnabled(InputElement element, bool value) => element.SetValue(IsEnabledProperty, value);

        // Runs after the clicked control's own handling, so a click that already moved focus is left alone.
        private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not InputElement panel
                || TopLevel.GetTopLevel(panel)?.FocusManager?.GetFocusedElement() is not TextBox box
                || !panel.IsVisualAncestorOf(box))
                return;

            if (e.Source is Visual source && (source == box || box.IsVisualAncestorOf(source)))
                return;

            panel.Focus();
        }
    }
}
