using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ChartPacker.Controls
{
    /// <summary>
    /// Focuses a control whenever it becomes visible, selecting a <see cref="TextBox"/>'s text, so an
    /// edit box that appears on click is ready to type into. An <see cref="AutoCompleteBox"/> passes
    /// focus on to its own text box.
    /// </summary>
    /// <example><c>&lt;TextBox ctl:FocusWhenShown.IsEnabled="True" IsVisible="{Binding IsEditing}" /&gt;</c></example>
    public sealed class FocusWhenShown
    {
        public static readonly AttachedProperty<bool> IsEnabledProperty =
            AvaloniaProperty.RegisterAttached<FocusWhenShown, Control, bool>("IsEnabled");

        static FocusWhenShown()
        {
            Visual.IsVisibleProperty.Changed.AddClassHandler<Control>((control, e) =>
            {
                if (GetIsEnabled(control) && e.NewValue is true)
                    Focus(control);
            });
        }

        private FocusWhenShown()
        {
        }

        public static bool GetIsEnabled(Control element) => element.GetValue(IsEnabledProperty);

        public static void SetIsEnabled(Control element, bool value) => element.SetValue(IsEnabledProperty, value);

        // Posted so it runs after layout, once the box can actually take focus.
        private static void Focus(Control control) => Dispatcher.UIThread.Post(() =>
        {
            if (!control.IsEffectivelyVisible)
                return;
            control.Focus();
            (control as TextBox)?.SelectAll();
        }, DispatcherPriority.Loaded);
    }
}
