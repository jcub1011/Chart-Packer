using Avalonia;
using Avalonia.Media;
using ChartShared.Core.Charts;

namespace ChartShared.UI.Controls.Highway
{
    /// <summary>
    /// What a <see cref="NotePainter"/> draws a note's shapes with: <see cref="GemPainter"/> draws them flat on the highway,
    /// as it always has, and <c>UprightGemPainter</c> stands them up on a tilted one. Each takes the rectangle the
    /// note occupies on the flat highway, so a host places notes the same way whichever it draws them with.
    /// </summary>
    public interface IGemPainter
    {
        /// <summary>Draws a gem in <paramref name="rect"/>: a cymbal, a tap, or a gem with a HOPO's mark, as its flags say.</summary>
        /// <param name="kind">How the part's notes are laid out, which says whether the note is a black fret.</param>
        /// <param name="outlined">Whether only its outline is drawn, so what's behind it shows through.</param>
        void DrawGem(DrawingContext context, ChartInstrumentKind kind, ChartNote note, Color color, Rect rect, bool outlined);

        /// <summary>Draws an open note or a kick: a bar across the lanes.</summary>
        void DrawBar(DrawingContext context, ChartInstrumentKind kind, ChartNote note, Color color, Rect bar, bool outlined);

        /// <summary>Draws a sustain's tail filling <paramref name="tail"/>.</summary>
        void DrawTail(DrawingContext context, ChartInstrumentKind kind, ChartNote note, Color color, Rect tail, bool outlined);

        /// <summary>Draws an open note's sustain filling <paramref name="tail"/>: a faint band across the lanes.</summary>
        void DrawWideTail(DrawingContext context, Color color, Rect tail, bool outlined);

        /// <summary>Draws a ring in <paramref name="color"/> round the gem or bar drawn in <paramref name="rect"/>.</summary>
        void DrawRing(DrawingContext context, ChartNote note, Rect rect, Color color);
    }
}
