using Avalonia.Media;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Editing;
using ChartShared.UI.Controls.Highway;

namespace ChartBuilder.Controls.Editing
{
    /// <summary>
    /// The colour of each kind of phrase, as the lanes' phrase rail and the bottom timeline both draw it, and of the ghosts
    /// of a run of Generate fills or Generate star power (charter-spec C3 item 2, C6 item 5, C8 item 1).
    /// </summary>
    internal static class PhraseColors
    {
        public static readonly Color Solo = Color.FromRgb(0xC3, 0x8E, 0xFF);
        public static readonly Color DrumFill = Color.FromRgb(0x7C, 0xDB, 0x8A);
        public static readonly Color DiscoFlip = Color.FromRgb(0xF2, 0x7B, 0xD0);
        public static readonly Color Roll = Color.FromRgb(0xFF, 0xD2, 0x5E);
        public static readonly Color Trill = Color.FromRgb(0xFF, 0x9F, 0x5A);
        public static readonly Color BigRockEnding = Color.FromRgb(0xFF, 0x6E, 0x7A);

        /// <summary>
        /// A proposed phrase's ghost, whatever its kind: a bright amber no phrase is drawn in, so the ghosts stand out from
        /// the phrases there already.
        /// </summary>
        public static readonly Color Proposal = Color.FromRgb(0xFF, 0xB0, 0x00);

        /// <summary>The colour of a phrase of <paramref name="kind"/>.</summary>
        public static Color Of(PhraseKind kind) => kind switch
        {
            PhraseKind.StarPower => HighwayPalette.StarPower,
            PhraseKind.Solo => Solo,
            PhraseKind.DrumFill => DrumFill,
            PhraseKind.Roll => Roll,
            PhraseKind.Trill => Trill,
            _ => BigRockEnding,
        };

        /// <summary>The colour of a row of the phrase rail: its phrases', or disco flip's.</summary>
        public static Color Of(RailKind row) => row.Phrase is { } kind ? Of(kind) : DiscoFlip;
    }
}
