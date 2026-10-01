using System.Windows.Input;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ChartPacker.Controls
{
    /// <summary>
    /// Runs <see cref="CommandProperty">Command</see> when a control is right-clicked: the right button
    /// pressed and released over it. Buttons only click with the left button, so this adds a second action.
    /// </summary>
    /// <example><c>&lt;Button ctl:RightClick.Command="{Binding ResetCommand}" /&gt;</c></example>
    public sealed class RightClick
    {
        public static readonly AttachedProperty<ICommand?> CommandProperty =
            AvaloniaProperty.RegisterAttached<RightClick, InputElement, ICommand?>("Command");

        static RightClick()
        {
            CommandProperty.Changed.AddClassHandler<InputElement>((element, e) =>
            {
                element.RemoveHandler(InputElement.PointerReleasedEvent, OnReleased);
                if (e.NewValue is not null)
                    element.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Bubble, handledEventsToo: true);
            });
        }

        private RightClick()
        {
        }

        public static ICommand? GetCommand(InputElement element) => element.GetValue(CommandProperty);

        public static void SetCommand(InputElement element, ICommand? value) => element.SetValue(CommandProperty, value);

        private static void OnReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (e.InitialPressMouseButton != MouseButton.Right || sender is not InputElement element)
                return;

            // Released somewhere else, it's not a click, as with the left button.
            if (!new Rect(element.Bounds.Size).Contains(e.GetPosition(element)))
                return;

            if (GetCommand(element) is { } command && command.CanExecute(null))
            {
                command.Execute(null);
                e.Handled = true;
            }
        }
    }
}
