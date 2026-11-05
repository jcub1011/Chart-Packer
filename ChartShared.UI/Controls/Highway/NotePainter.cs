using System;
using Avalonia;
using Avalonia.Media;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;

namespace ChartShared.UI.Controls.Highway
{
    /// <summary>What a <see cref="NoteGlyph"/> is to draw, and how.</summary>
    [Flags]
    public enum NoteGlyphParts : ushort
    {
        None = 0,

        /// <summary>A sustain's tail, between <see cref="NoteGlyph.TailFrom"/> and <see cref="NoteGlyph.TailTo"/>.</summary>
        Tail = 1,

        /// <summary>The note itself: a gem, or a bar.</summary>
        Note = 2,

        /// <summary>It's drawn across every lane, behind the frets' (an open note, a kick), rather than in a lane of its own.</summary>
        Across = 4,

        /// <summary>The note is a bar in its own lane, as a timeline gives the kick or an open note a row of its own.</summary>
        Bar = 8,

        /// <summary>The note is a black fret played with a white one, which it covers the top half of, as in the game.</summary>
        Barre = 16,

        /// <summary>The note is drawn as its outline only.</summary>
        Outlined = 32,

        /// <summary>The tail is drawn as its outline only.</summary>
        OutlinedTail = 64,

        /// <summary>The note is ringed, as one a draft has changed is.</summary>
        Flagged = 128,

        /// <summary>The tail isn't a sustain to the games, so only its dashed outline is drawn, by the <see cref="INoteDecorator"/>.</summary>
        DashedTail = 256,
    }

    /// <summary>
    /// One note, ready to draw: what a <see cref="NotePainter"/> is given in place of a chart's notes, which the highway
    /// keeps one way and the editor another. Its place along the time axis is already worked out, in the pixels of the
    /// <see cref="LaneProjection"/> it's drawn with.
    /// </summary>
    public struct NoteGlyph
    {
        /// <summary>The host's own number for the note, given back to its <see cref="INoteDecorator"/>.</summary>
        public int Index;

        /// <summary>The note as the games read it, which says what shape it is.</summary>
        public ChartNote Note;

        public Color Color;

        /// <summary>The column or row it's in, or -1 when it's <see cref="NoteGlyphParts.Across"/> them all.</summary>
        public int Column;

        /// <summary>Where the note is along the time axis, in pixels.</summary>
        public double Along;

        /// <summary>Where the tail starts and ends along the time axis, in pixels, in either order.</summary>
        public double TailFrom;

        public double TailTo;

        /// <summary>How opaque it's drawn, from 0 to 1.</summary>
        public double Opacity;

        public NoteGlyphParts Parts;

        public readonly bool Has(NoteGlyphParts part) => (Parts & part) != 0;
    }

    /// <summary>What a host draws with its notes that the others don't: the editor's rings, marks and dashed tails.</summary>
    public interface INoteDecorator
    {
        /// <summary>Draws a tail of <paramref name="glyph"/> that isn't a sustain to the games, only an outline of one, in its place at <paramref name="tail"/>.</summary>
        void DrawDashedTail(DrawingContext context, in NoteGlyph glyph, Rect tail);

        /// <summary>Draws over <paramref name="glyph"/>'s note, which was drawn at <paramref name="rect"/>.</summary>
        void AfterNote(DrawingContext context, in NoteGlyph glyph, Rect rect);
    }

    /// <summary>
    /// Draws a lane's notes, the same on Chart Packer's highway and in Chart Builder's editor, in either orientation: the
    /// tails at the back, an open note's across the lanes behind the frets', then the bars across, then the gems, each
    /// layer with the latest note first so an earlier one is drawn over it.
    /// </summary>
    public sealed class NotePainter
    {
        // How far in from the lanes' edges a note across them stops, in pixels.
        private const double AcrossInset = 4;

        private readonly GemPainter _gems;

        public NotePainter(GemPainter gems)
        {
            _gems = gems;
        }

        /// <summary>A gem's width across its lane, as a share of the lane's: a highway's are wide, a timeline's rows crowd closer.</summary>
        private static double GemWidth(LaneView view) => view == LaneView.Highway ? 0.86 : 0.70;

        /// <summary>A bar across the lanes is this share of a gem's size thick, and no thinner than <see cref="MinimumBar"/>.</summary>
        private const double BarShare = 0.45;

        private const double MinimumBar = 4;

        /// <summary>A gem in <paramref name="column"/> at <paramref name="along"/>, <paramref name="size"/> long.</summary>
        public static Rect GemRect(LaneProjection projection, int column, double along, double size, double alongFrom = double.NegativeInfinity, double alongTo = double.PositiveInfinity)
        {
            double center = projection.AcrossOf(column);
            double half = projection.LaneSize * GemWidth(projection.View) / 2;
            return HighwayChrome.Box(projection.View, along - (size / 2), along + (size / 2), center - half, center + half, alongFrom, alongTo);
        }

        /// <summary>
        /// Draws <paramref name="glyphs"/>, in the order they're to be drawn from the back: the last is drawn first of its layer.
        /// </summary>
        /// <param name="size">How long a gem is, along the time axis, in pixels.</param>
        /// <param name="alongFrom">The nearest along the time axis anything is drawn, in pixels, so something reaching far past what's shown stays within it.</param>
        public void Draw(DrawingContext context, LaneProjection projection, ChartInstrumentKind kind, ReadOnlySpan<NoteGlyph> glyphs, double size, INoteDecorator? decorator = null, double alongFrom = double.NegativeInfinity, double alongTo = double.PositiveInfinity)
        {
            var view = projection.View;
            double across1 = projection.LaneFrom;
            double across2 = projection.LaneFrom + (projection.LaneSize * projection.Slots.Slots.Count);

            foreach (bool wide in (ReadOnlySpan<bool>)[true, false])
            {
                for (int i = glyphs.Length - 1; i >= 0; i--)
                {
                    ref readonly var glyph = ref glyphs[i];
                    if (!glyph.Has(NoteGlyphParts.Tail) || glyph.Has(NoteGlyphParts.Across) != wide)
                        continue;

                    Rect tail;
                    if (wide)
                    {
                        tail = HighwayChrome.Box(view, glyph.TailFrom, glyph.TailTo, across1 + AcrossInset, across2 - AcrossInset, alongFrom, alongTo);
                    }
                    else
                    {
                        double width = Math.Max(3, projection.LaneSize * 0.2);
                        double center = projection.AcrossOf(glyph.Column);
                        tail = HighwayChrome.Box(view, glyph.TailFrom, glyph.TailTo, center - (width / 2), center + (width / 2), alongFrom, alongTo);
                    }

                    bool outlined = glyph.Has(NoteGlyphParts.OutlinedTail);
                    using (Faded(context, glyph))
                    {
                        if (glyph.Has(NoteGlyphParts.DashedTail))
                            decorator?.DrawDashedTail(context, glyph, tail);
                        else if (wide)
                            _gems.DrawWideTail(context, glyph.Color, tail, outlined);
                        else
                            _gems.DrawTail(context, kind, glyph.Note, glyph.Color, tail, outlined);
                    }
                }
            }

            foreach (bool bars in (ReadOnlySpan<bool>)[true, false])
            {
                for (int i = glyphs.Length - 1; i >= 0; i--)
                {
                    ref readonly var glyph = ref glyphs[i];
                    if (!glyph.Has(NoteGlyphParts.Note) || glyph.Has(NoteGlyphParts.Across) != bars)
                        continue;

                    Rect rect;
                    bool outlined = glyph.Has(NoteGlyphParts.Outlined);
                    using (Faded(context, glyph))
                    {
                        if (bars)
                        {
                            double thickness = Math.Max(MinimumBar, size * BarShare);
                            rect = HighwayChrome.Box(view, glyph.Along - (thickness / 2), glyph.Along + (thickness / 2), across1 + AcrossInset, across2 - AcrossInset, alongFrom, alongTo);
                            _gems.DrawBar(context, kind, glyph.Note, glyph.Color, rect, outlined);
                        }
                        else
                        {
                            rect = GemRect(projection, glyph.Column, glyph.Along, size, alongFrom, alongTo);
                            if (glyph.Has(NoteGlyphParts.Bar))
                            {
                                _gems.DrawBar(context, kind, glyph.Note, glyph.Color, rect, outlined);
                            }
                            else
                            {
                                if (glyph.Has(NoteGlyphParts.Barre))
                                    rect = rect.WithHeight(rect.Height / 2);
                                _gems.DrawGem(context, kind, glyph.Note, glyph.Color, rect, outlined);
                            }
                        }
                    }

                    if (glyph.Has(NoteGlyphParts.Flagged))
                        _gems.DrawRing(context, glyph.Note, rect, HighwayPalette.ChangeMark);
                    decorator?.AfterNote(context, glyph, rect);
                }
            }
        }

        // Draws what follows faint, when the glyph is. A struct used directly by `using`, so it isn't boxed for each glyph drawn.
        private static FadeScope Faded(DrawingContext context, in NoteGlyph glyph) => new(context, glyph.Opacity);

        private readonly struct FadeScope : IDisposable
        {
            private readonly DrawingContext.PushedState _pushed;
            private readonly bool _isFaded;

            public FadeScope(DrawingContext context, double opacity)
            {
                _isFaded = opacity < 1;
                if (_isFaded)
                    _pushed = context.PushOpacity(opacity);
            }

            public void Dispose()
            {
                if (_isFaded)
                    _pushed.Dispose();
            }
        }
    }
}
