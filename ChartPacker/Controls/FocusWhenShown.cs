using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ChartPacker.Controls
{
    /// <summary>
    /// Focuses a <see cref="TextBox"/> and selects its text whenever it becomes visible, so an
    /// edit box that appears on click is ready to type into.
    /// </summary>
    /// <example><c>&lt;TextBox ctl:FocusWhenShown.IsEnabled="True" IsVisible="{Binding IsEditing}" /&gt;</c></example>
    public sealed class FocusWhenShown
    {
        public static readonly AttachedProperty<bool> IsEnabledProperty =
            AvaloniaProperty.RegisterAttached<FocusWhenShown, TextBox, bool>("IsEnabled");

        static FocusWhenShown()
        {
            Visual.IsVisibleProperty.Changed.AddClassHandler<TextBox>((box, e) =>
            {
                if (GetIsEnabled(box) && e.NewValue is true)
                    Focus(box);
            });
        }

        private FocusWhenShown()
        {
        }

        public static bool GetIsEnabled(TextBox element) => element.GetValue(IsEnabledProperty);

        public static void SetIsEnabled(TextBox element, bool value) => element.SetValue(IsEnabledProperty, value);

        // Posted so it runs after layout, once the box can actually take focus.
        private static void Focus(TextBox box) => Dispatcher.UIThread.Post(() =>
        {
            if (!box.IsEffectivelyVisible)
                return;
            box.Focus();
            box.SelectAll();
        }, DispatcherPriority.Loaded);
    }
}
