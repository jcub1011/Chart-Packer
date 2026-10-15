using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using ChartShared.Core.Scanning;

namespace ChartPacker.Converters
{
    /// <summary>Maps a <see cref="SongEntryKind"/> to the icon geometry shown in the library list.</summary>
    public sealed class EntryKindToIconConverter : IValueConverter
    {
        public Geometry? SngFile { get; set; }

        public Geometry? SongFolder { get; set; }

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
        {
            SongEntryKind.SngFile => SngFile,
            SongEntryKind.SongFolder => SongFolder,
            _ => null,
        };

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
