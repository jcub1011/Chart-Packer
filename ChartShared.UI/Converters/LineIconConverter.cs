using System;
using System.Globalization;
using Avalonia.Data.Converters;
using ChartShared.UI.Controls;

namespace ChartShared.UI.Converters
{
    /// <summary>Maps the key of a line icon (<c>SaveStroke</c>) to its geometry, so a view model can name an icon without drawing it.</summary>
    public sealed class LineIconConverter : IValueConverter
    {
        public static LineIconConverter Instance { get; } = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            LineIcon.Find(value as string);

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
