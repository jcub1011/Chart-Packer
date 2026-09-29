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
    /// <remarks>
    /// <see cref="PrefixProperty">Prefix</see> is plain text shown before the markup, unformatted, so a line
    /// mixing the two is one text and is trimmed as a whole.
    /// </remarks>
    /// <example><c>&lt;SelectableTextBlock ctl:RichTextMarkup.Text="{Binding Value}" /&gt;</c></example>
    public sealed class RichTextMarkup
    {
        public static readonly AttachedProperty<string?> TextProperty =
            AvaloniaProperty.RegisterAttached<RichTextMarkup, TextBlock, string?>("Text");

        public static readonly AttachedProperty<string?> PrefixProperty =
            AvaloniaProperty.RegisterAttached<RichTextMarkup, TextBlock, string?>("Prefix");

        static RichTextMarkup()
        {
            TextProperty.Changed.AddClassHandler<TextBlock>((block, _) => Apply(block));
            PrefixProperty.Changed.AddClassHandler<TextBlock>((block, _) => Apply(block));
        }

        private RichTextMarkup()
        {
        }

        public static string? GetText(TextBlock block) => block.GetValue(TextProperty);

        public static void SetText(TextBlock block, string? value) => block.SetValue(TextProperty, value);

        public static string? GetPrefix(TextBlock block) => block.GetValue(PrefixProperty);

        public static void SetPrefix(TextBlock block, string? value) => block.SetValue(PrefixProperty, value);

        private static void Apply(TextBlock block)
        {
            var inlines = new InlineCollection();
            if (GetPrefix(block) is { Length: > 0 } prefix)
                inlines.Add(new Run(prefix));

            foreach (RichTextSpan span in RichText.Parse(GetText(block) ?? string.Empty))
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
