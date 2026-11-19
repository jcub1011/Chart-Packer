using System;
using System.Collections.Generic;
using Avalonia.Media;
using ChartShared.Core.Charting.Layout;

namespace ChartBuilder.Controls.Editing
{
    /// <summary>
    /// The vocal parts' colours, as their lanes and the bottom timeline draw them: the lead and the first harmony as the games
    /// colour them, the others apart from it.
    /// </summary>
    public static class VocalPalette
    {
        private static readonly Color[] Colors =
        [
            Color.FromRgb(0x7F, 0xE9, 0xFF),
            Color.FromRgb(0x7F, 0xE9, 0xFF),
            Color.FromRgb(0xFF, 0xB5, 0x47),
            Color.FromRgb(0xFF, 0x7A, 0x50),
        ];

        /// <summary>Every part's colour, indexed by its <see cref="VocalTrack"/>.</summary>
        public static IReadOnlyList<Color> All => Colors;

        /// <summary><paramref name="track"/>'s colour.</summary>
        public static Color Of(VocalTrack track) => Colors[(int)track];

        /// <summary>What <paramref name="make"/> makes of each part's colour, indexed by its <see cref="VocalTrack"/>.</summary>
        public static T[] Each<T>(Func<Color, T> make) => Array.ConvertAll(Colors, c => make(c));
    }
}
