using System;
using Avalonia.Media;
using ChartShared.Core.Charts;

namespace ChartShared.UI.Controls.Highway
{
    /// <summary>
    /// The colours notes are drawn in, as the games colour them, for every view that draws notes: Chart Packer's highway
    /// and alignment timeline, and Chart Builder's editor.
    /// </summary>
    public static class HighwayPalette
    {
        public static readonly Color StarPower = Color.FromRgb(0x7F, 0xE9, 0xFF);
        public static readonly Color Open = Color.FromRgb(0xB0, 0x4C, 0xE0);
        public static readonly Color Kick = Color.FromRgb(0xFB, 0x8C, 0x00);
        public static readonly Color WhiteFret = Color.FromRgb(0xF2, 0xF2, 0xF4);
        public static readonly Color BlackFret = Color.FromRgb(0x2A, 0x2A, 0x32);

        /// <summary>A black fret's tail, and its edge when outlined, which its own colour would leave unseen on the dark highway.</summary>
        public static readonly Color BlackFretEdge = Color.FromRgb(0x8A, 0x8A, 0x96);

        /// <summary>A black fret's light outline, which sets it off from the dark highway.</summary>
        public static readonly Color BlackFretOutline = Color.FromRgb(0xC8, 0xC8, 0xD0);

        public static readonly Color Green = Color.FromRgb(0x2E, 0xC4, 0x4E);
        public static readonly Color Red = Color.FromRgb(0xEE, 0x3B, 0x3B);
        public static readonly Color Yellow = Color.FromRgb(0xFF, 0xD8, 0x2E);
        public static readonly Color Blue = Color.FromRgb(0x25, 0x8C, 0xF0);
        public static readonly Color Orange = Color.FromRgb(0xFF, 0x8A, 0x10);

        /// <summary>The ring round a note that changed (<see cref="HighwayNoteMark.Flagged"/>): a pink no fret, pad or star power has.</summary>
        public static readonly Color ChangeMark = Color.FromRgb(0xFF, 0x4F, 0xA3);

        private static readonly Color[] FretColors = [Green, Red, Yellow, Blue, Orange];
        private static readonly Color[] FourPadColors = [Red, Yellow, Blue, Green];
        private static readonly Color[] FivePadColors = [Red, Yellow, Blue, Orange, Green];
        private static readonly Color SixFretTarget = Color.FromRgb(0xB8, 0xB8, 0xC4);

        /// <summary>
        /// The column a note's lane is drawn in, of <paramref name="laneCount"/>: six-fret's black and white frets share
        /// three. Lanes are numbered as <see cref="ChartNote.Lane"/> is.
        /// </summary>
        public static int Column(ChartInstrumentKind kind, int laneCount, int noteLane) =>
            kind == ChartInstrumentKind.SixFret ? noteLane % 3 : Math.Min(noteLane, laneCount - 1);

        /// <summary>Whether a six-fret note is a black fret.</summary>
        public static bool IsBlackFret(ChartInstrumentKind kind, int noteLane) => kind == ChartInstrumentKind.SixFret && noteLane >= 3;

        /// <summary>The colour of a column's target on the strike line.</summary>
        public static Color TargetColor(ChartInstrumentKind kind, int laneCount, int column) => kind switch
        {
            ChartInstrumentKind.FiveFret => FretColors[column],
            ChartInstrumentKind.Drums => (laneCount == 5 ? FivePadColors : FourPadColors)[column],
            _ => SixFretTarget,
        };

        /// <summary>The colour a column lights up in as it's struck.</summary>
        public static Color HitColor(ChartInstrumentKind kind, int laneCount, int column) =>
            kind == ChartInstrumentKind.SixFret ? WhiteFret : TargetColor(kind, laneCount, column);

        /// <summary>The colour of <paramref name="note"/>, as the games read it: star power over every other colour.</summary>
        public static Color NoteColor(ChartInstrumentKind kind, int laneCount, ChartNote note)
        {
            if ((note.Flags & NoteFlags.StarPower) != 0)
                return StarPower;
            if (note.Lane == ChartNote.OpenLane)
                return kind == ChartInstrumentKind.Drums ? Kick : Open;
            if (kind == ChartInstrumentKind.SixFret)
                return note.Lane >= 3 ? BlackFret : WhiteFret;
            return TargetColor(kind, laneCount, Column(kind, laneCount, note.Lane));
        }

        public static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

        public static Color Lighten(Color color, double amount) => Color.FromArgb(
            color.A,
            (byte)(color.R + ((255 - color.R) * amount)),
            (byte)(color.G + ((255 - color.G) * amount)),
            (byte)(color.B + ((255 - color.B) * amount)));

        public static Color Darken(Color color, double amount) => Color.FromArgb(
            color.A, (byte)(color.R * (1 - amount)), (byte)(color.G * (1 - amount)), (byte)(color.B * (1 - amount)));
    }
}
