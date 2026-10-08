using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using ChartPacker.Core.Metadata;

namespace ChartPacker.UI.Converters
{
    /// <summary>Maps a <see cref="DifficultyGlyph"/> to the icon geometry of its instrument.</summary>
    public sealed class DifficultyGlyphConverter : IValueConverter
    {
        public Geometry? Guitar { get; set; }

        public Geometry? Bass { get; set; }

        public Geometry? Rhythm { get; set; }

        public Geometry? Drums { get; set; }

        public Geometry? ProDrums { get; set; }

        public Geometry? Keys { get; set; }

        public Geometry? ProKeys { get; set; }

        public Geometry? Vocals { get; set; }

        public Geometry? Harmonies { get; set; }

        public Geometry? Dance { get; set; }

        public Geometry? Band { get; set; }

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
        {
            DifficultyGlyph.Guitar => Guitar,
            DifficultyGlyph.Bass => Bass,
            DifficultyGlyph.Rhythm => Rhythm,
            DifficultyGlyph.Drums => Drums,
            DifficultyGlyph.ProDrums => ProDrums,
            DifficultyGlyph.Keys => Keys,
            DifficultyGlyph.ProKeys => ProKeys,
            DifficultyGlyph.Vocals => Vocals,
            DifficultyGlyph.Harmonies => Harmonies,
            DifficultyGlyph.Dance => Dance,
            DifficultyGlyph.Band => Band,
            _ => null,
        };

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
