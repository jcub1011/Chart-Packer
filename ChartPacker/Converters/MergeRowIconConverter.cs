using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using ChartPacker.ViewModels;

namespace ChartPacker.Converters
{
    /// <summary>Maps a merge row's fixed icon to its geometry.</summary>
    public sealed class MergeRowIconConverter : IValueConverter
    {
        public Geometry? Waveform { get; set; }

        public Geometry? Midi { get; set; }

        public Geometry? AlbumArt { get; set; }

        public Geometry? Background { get; set; }

        public Geometry? Video { get; set; }

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
        {
            MergeRowIcon.Waveform => Waveform,
            MergeRowIcon.Midi => Midi,
            MergeRowIcon.AlbumArt => AlbumArt,
            MergeRowIcon.Background => Background,
            MergeRowIcon.Video => Video,
            _ => null,
        };

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
