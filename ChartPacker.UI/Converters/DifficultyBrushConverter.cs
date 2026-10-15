using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using ChartPacker.Core.Charts;

namespace ChartPacker.UI.Converters
{
    /// <summary>
    /// Maps a difficulty to its colour: a <see cref="ChartDifficulty"/>, or a song.ini tier (an
    /// <see cref="int"/>), picks the level of that number; a number past the last level set takes
    /// that last level. Anything else, such as an unset tier, takes <see cref="Fallback"/>.
    /// </summary>
    public sealed class DifficultyBrushConverter : IValueConverter
    {
        public IBrush? Level0 { get; set; }

        public IBrush? Level1 { get; set; }

        public IBrush? Level2 { get; set; }

        public IBrush? Level3 { get; set; }

        public IBrush? Level4 { get; set; }

        public IBrush? Level5 { get; set; }

        public IBrush? Level6 { get; set; }

        public IBrush? Fallback { get; set; }

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            int? index = value switch
            {
                ChartDifficulty difficulty => (int)difficulty,
                int tier when tier >= 0 => tier,
                _ => null,
            };
            if (index is not { } i)
                return Fallback;

            IBrush?[] levels = [Level0, Level1, Level2, Level3, Level4, Level5, Level6];
            for (int level = Math.Min(i, levels.Length - 1); level >= 0; level--)
            {
                if (levels[level] is { } brush)
                    return brush;
            }

            return Fallback;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
