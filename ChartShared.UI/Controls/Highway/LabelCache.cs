using System;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace ChartShared.UI.Controls.Highway
{
    /// <summary>
    /// The text a highway draws, made once and kept: the names under a highway, the measure numbers beside an editor's.
    /// </summary>
    /// <remarks>
    /// A <see cref="TextLayout"/>, not a <see cref="FormattedText"/>: that lays its text out again each time it's drawn
    /// or measured, which, for the measure numbers alone, was most of the time a frame took to draw. Drawn in an
    /// immutable copy of the brush, so it isn't one of the frame's resources.
    /// </remarks>
    public sealed class LabelCache
    {
        // Cut off before it grows without end, as a width that changes with the window makes a label of its own each time: the label
        // used longest ago goes first, so those in view stay.
        private const int Capacity = 512;

        private static readonly Typeface Face = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

        // A solid brush is told by its colour and opacity, not by which brush it is: one recoloured in place (a theme's) is the
        // same brush with a colour the label was not made in. Any other is told by the brush itself.
        private readonly LruCache<(string Text, double Size, uint Color, double Opacity, IBrush? Other, double? MaxWidth), TextLayout> _labels = new(Capacity);

        // What each label was made for, as KeyOf gives it: boxed once, as it's made.
        private readonly ConditionalWeakTable<TextLayout, object> _keys = [];

        /// <summary>The label for <paramref name="text"/>, cut short with an ellipsis on one line at <paramref name="maxWidth"/> when that's given.</summary>
        public TextLayout Get(string text, double size, IBrush brush, double? maxWidth = null)
        {
            var key = brush is ISolidColorBrush solid
                ? (text, size, solid.Color.ToUInt32(), solid.Opacity, (IBrush?)null, maxWidth)
                : (text, size, 0u, brush.Opacity, brush, maxWidth);
            if (_labels.TryGetValue(key, out var label))
                return label;

            label = maxWidth is { } width
                ? new TextLayout(text, Face, size, brush.ToImmutable(), textTrimming: TextTrimming.CharacterEllipsis, maxWidth: width, maxLines: 1)
                : new TextLayout(text, Face, size, brush.ToImmutable());
            _keys.AddOrUpdate(label, key);
            return _labels.Add(key, label);
        }

        /// <summary>
        /// What <paramref name="label"/>, one of this cache's, was made for, equal to any other label's made for the same: for what
        /// keeps something made from a label, as a picture of it, past the label, which is let go when the cache is full and made
        /// again when it's next asked for.
        /// </summary>
        public object KeyOf(TextLayout label) =>
            _keys.TryGetValue(label, out var key) ? key : throw new ArgumentException("Not a label of this cache.", nameof(label));
    }
}
