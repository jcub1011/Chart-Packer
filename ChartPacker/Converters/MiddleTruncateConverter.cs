using System;
using System.Globalization;
using Avalonia.Data.Converters;
namespace ChartPacker.Converters
{
    /// <summary>
    /// Shortens text in the middle (<c>Long song ti…eup outro</c>) so a drop-down keeps a bounded
    /// width instead of expanding to fit its longest item; the full text stays in the tooltip.
    /// The bound value must be a string. <c>ConverterParameter</c> optionally sets the maximum
    /// length (default 40); anything at or under it is returned as is.
    /// </summary>
    public sealed class MiddleTruncateConverter : IValueConverter
    {
        private const int DefaultMax = 40;

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not string text)
                return value;

            int max = DefaultMax;
            if (parameter is string s && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fromString) && fromString > 4)
                max = fromString;
            else if (parameter is int fromInt && fromInt > 4)
                max = fromInt;

            if (text.Length <= max)
                return text;

            int tail = Math.Max(1, (max - 1) / 3);
            int head = max - 1 - tail;
            return string.Concat(text.AsSpan(0, head), "…", text.AsSpan(text.Length - tail));
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
