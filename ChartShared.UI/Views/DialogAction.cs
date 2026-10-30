using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using ChartShared.UI.Controls;

namespace ChartShared.UI.Views
{
    /// <summary>A dialog's footer buttons: each a label with its line icon before it.</summary>
    internal static class DialogAction
    {
        /// <summary>Gives <paramref name="button"/> its label, with the line icon keyed <paramref name="icon"/> before it when there is one.</summary>
        public static void Set(Button button, string label, string? icon)
        {
            AutomationProperties.SetName(button, label);
            if (LineIcon.For(icon) is not { } glyph)
            {
                button.Content = label;
                return;
            }

            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(glyph);
            content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            button.Content = content;
        }
    }
}
