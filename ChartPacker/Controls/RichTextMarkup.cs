using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using ChartPacker.Core.Text;

namespace ChartPacker.Controls
{
    /// <summary>
    /// Shows text containing Clone Hero/YARG rich-text tags (<c>&lt;color=…&gt;</c>, <c>&lt;b&gt;</c>, …)
    /// in a <see cref="TextBlock"/> with the formatting applied. See <see cref="RichText"/> for the
    /// supported tags.
    /// </summary>
    /// <example><c>&lt;SelectableTextBlock ctl:RichTextMarkup.Text="{Binding Value}" /&gt;</c></example>
    public sealed class RichTextMarkup
    {
        public static readonly AttachedProperty<string?> TextProperty =
            AvaloniaProperty.RegisterAttached<RichTextMarkup, TextBlock, string?>("Text");

        static RichTextMarkup()
        {
            TextProperty.Changed.AddClassHandler<TextBlock>((block, e) => Apply(block, e.NewValue as string));
        }

        private RichTextMarkup()
        {
        }

        public static string? GetText(TextBlock block) => block.GetValue(TextProperty);

        public static void SetText(TextBlock block, string? value) => block.SetValue(TextProperty, value);

        private static void Apply(TextBlock block, string? text)
        {
            var inlines = new InlineCollection();
            foreach (RichTextSpan span in RichText.Parse(text ?? string.Empty))
            {
                if (span.Text == "\n")
                {
                    inlines.Add(new LineBreak());
                    continue;
                }

                var run = new Run(span.Text);
                RichTextStyle style = span.Style;
                if (style.Bold)
                    run.FontWeight = FontWeight.Bold;
                if (style.Italic)
                    run.FontStyle = FontStyle.Italic;
                if (style.Underline || style.Strikethrough)
                {
                    var decorations = new TextDecorationCollection();
                    if (style.Underline)
                        decorations.AddRange(TextDecorations.Underline);
                    if (style.Strikethrough)
                        decorations.AddRange(TextDecorations.Strikethrough);
                    run.TextDecorations = decorations;
                }
                if (style.Color is uint argb)
                    run.Foreground = new SolidColorBrush(Color.FromUInt32(argb));

                inlines.Add(run);
            }

            block.Inlines = inlines;
        }
    }
}
