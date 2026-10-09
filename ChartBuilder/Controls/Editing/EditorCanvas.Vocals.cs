using System;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ChartBuilder.ViewModels;
using ChartPacker.Core.Charting.Editing;
using ChartPacker.Core.Charting.Layout;
using ChartPacker.UI.Controls.Highway;

namespace ChartBuilder.Controls.Editing
{
    // A vocal part's lane: its notes on rows of pitch, as a piano roll, with the lyrics under them and its phrases shaded
    // behind, over the same beats, sections and tempo markers as a highway's lane, on the same pages. Only a timeline
    // draws one (ProjectVocals), and it isn't edited yet: the rows are slots, one a semitone from the highest pitch down,
    // so the pointer maps to a tick and a pitch as it does to a tick and a lane.
    public abstract partial class EditorCanvas
    {
        // The parts' colours: the lead and the first harmony as the games colour them, the others apart from it.
        private static readonly Color[] VocalColors =
        [
            Color.FromRgb(0x7F, 0xE9, 0xFF),
            Color.FromRgb(0x7F, 0xE9, 0xFF),
            Color.FromRgb(0xFF, 0xB5, 0x47),
            Color.FromRgb(0xFF, 0x7A, 0x50),
        ];

        private static readonly IBrush[] VocalNoteBrushes = [.. Array.ConvertAll(VocalColors, c => (IBrush)new ImmutableSolidColorBrush(c))];
        private static readonly IPen[] VocalEdgePens = [.. Array.ConvertAll(VocalColors, c => (IPen)new ImmutablePen(HighwayPalette.Darken(c, 0.45).ToUInt32(), 1))];
        private static readonly IPen[] VocalSlidePens = [.. Array.ConvertAll(VocalColors, c => (IPen)new ImmutablePen(HighwayPalette.WithAlpha(c, 0xB0).ToUInt32(), 2))];
        private static readonly IPen[] VocalUnpitchedPens = [.. Array.ConvertAll(VocalColors, c => (IPen)new ImmutablePen(c.ToUInt32(), 1.5))];

        // What's on the strike line, lit as the inspector's lyrics lane lights it: the note being sung near white in its
        // part's tint, with a glow around where it crosses the line; its syllable in the same tint; and its phrase shaded.
        private static readonly IBrush[] VocalSingingBrushes = [.. Array.ConvertAll(VocalColors, c => (IBrush)new ImmutableSolidColorBrush(HighwayPalette.Lighten(c, 0.75)))];
        private static readonly IPen[] VocalSingingPens = [.. Array.ConvertAll(VocalColors, c => (IPen)new ImmutablePen(HighwayPalette.Lighten(c, 0.75).ToUInt32(), 2))];
        private static readonly IBrush[] VocalGlowBrushes = [.. Array.ConvertAll(VocalColors, c => (IBrush)new ImmutableSolidColorBrush(HighwayPalette.WithAlpha(c, 0x60)))];
        private static readonly IBrush SingingPhraseBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));

        // A black key's row, a little darker than a white key's, and the line under each C.
        private static readonly IBrush BlackKeyBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x50, 0x00, 0x00, 0x00));
        private static readonly IPen OctavePen = new ImmutablePen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF).ToUInt32(), 1);
        private static readonly IBrush PhraseBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
        private static readonly IPen PhraseEdgePen = new ImmutablePen(Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF).ToUInt32(), 1);
        private static readonly IBrush LyricsBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xFF, 0x0C, 0x0D, 0x14));

        // The rows the last vocal part drawn has, made once for it.
        private (VocalLane Lane, LaneSlots Slots)? _vocalSlots;

        // The pitch classes of the black keys, from C.
        private static bool IsBlackKey(int pitch) => (pitch % 12) is 1 or 3 or 6 or 8 or 10;

        /// <summary>The rows of <paramref name="vocals"/>: one a semitone, from the highest pitch down.</summary>
        private LaneSlots VocalSlotsOf(VocalLane vocals)
        {
            if (_vocalSlots is { } known && ReferenceEquals(known.Lane, vocals))
                return known.Slots;
            var slots = new LaneSlots(vocals.Pitches, null);
            _vocalSlots = (vocals, slots);
            return slots;
        }

        /// <summary>Draws <paramref name="layer"/> of a vocal part's lane, as <see cref="RenderLayer"/> does a highway's.</summary>
        private void RenderVocalLayer(DrawingContext context, Layer layer, EditorSession session, EditorLaneViewModel lane, VocalTrack vocal, VocalLane vocals)
        {
            var size = Bounds.Size;
            Extras = default;
            if (ProjectVocals(size, VocalSlotsOf(vocals), session.Viewport, lane.LookAhead) is not { } screen)
                return;

            switch (layer.Kind)
            {
                case LayerKind.Strike:
                    DrawSingingPhrase(context, session, vocals, screen);
                    DrawStrikeBand(context, session, screen);
                    return;
                case LayerKind.Overlay:
                    RenderVocalOverlay(context, session, vocal, vocals, screen, size);
                    return;
            }

            var projection = ShowPage(layer, screen);
            try
            {
                var (fromTick, toTick) = TicksOf(session, projection);
                FillPage(context, projection);
                if (layer.Kind == LayerKind.Song)
                {
                    DrawPitchRows(context, vocals, projection);
                    DrawVocalPhrases(context, session, vocals, projection, fromTick, toTick);
                    DrawSpans(context, session, projection);
                    DrawBeats(context, session, session.TempoMap, projection, fromTick, toTick, size);
                    DrawTempoMarkers(context, session, projection, fromTick, toTick, size);
                    DrawTempoProposal(context, session, projection, size);
                    DrawSections(context, session, projection, fromTick, toTick, size);
                }
                else
                {
                    using (context.PushClip(RowsOf(projection)))
                        DrawVocalNotes(context, session, vocal, vocals, projection, fromTick, toTick);
                    DrawLyrics(context, session, vocals, projection, fromTick, toTick, size);
                }
            }
            finally
            {
                _alongLimit = (double.NegativeInfinity, double.PositiveInfinity);
            }
        }

        /// <summary>The pitch rows, along all of <paramref name="projection"/>'s time axis.</summary>
        private Rect RowsOf(LaneProjection projection) =>
            Box(projection.TimeFrom, projection.TimeFrom + projection.TimeLength, projection.LaneFrom, projection.LaneFrom + (projection.LaneSize * projection.Slots.Slots.Count));

        /// <summary>The rows, as a piano roll's: the black keys' darker, and a line under each C.</summary>
        private void DrawPitchRows(DrawingContext context, VocalLane vocals, LaneProjection projection)
        {
            double from = projection.TimeFrom, to = projection.TimeFrom + projection.TimeLength;
            context.FillRectangle(LanesBrush, RowsOf(projection));
            var pitches = vocals.Pitches;
            for (int i = 0; i < pitches.Count; i++)
            {
                double edge = projection.LaneFrom + (i * projection.LaneSize);
                if (IsBlackKey(pitches[i]))
                    context.FillRectangle(BlackKeyBrush, Box(from, to, edge, edge + projection.LaneSize));
                if (pitches[i] % 12 == 0 && i < pitches.Count - 1)
                    context.DrawLine(OctavePen, At(from, edge + projection.LaneSize), At(to, edge + projection.LaneSize));
            }
        }

        /// <summary>Each phrase faintly shaded across the rows, with a line at either end.</summary>
        private void DrawVocalPhrases(DrawingContext context, EditorSession session, VocalLane vocals, LaneProjection projection, double fromTick, double toTick)
        {
            double across1 = projection.LaneFrom, across2 = projection.LaneFrom + (projection.LaneSize * projection.Slots.Slots.Count);
            foreach (var phrase in vocals.Phrases)
            {
                long end = phrase.Tick + phrase.Length;
                if (end < fromTick || phrase.Tick > toTick)
                    continue;
                double a = projection.AlongOf(session.AudioAt(phrase.Tick)), b = projection.AlongOf(session.AudioAt(end));
                context.FillRectangle(PhraseBrush, Box(a, b, across1, across2));
                context.DrawLine(PhraseEdgePen, At(Limited(a), across1), At(Limited(a), across2));
                context.DrawLine(PhraseEdgePen, At(Limited(b), across1), At(Limited(b), across2));
            }
        }

        /// <summary>
        /// The notes: a bar on its pitch's row for each sung one, a line from the note before for a slide, and an outline
        /// along the middle row for a spoken one, which has no pitch.
        /// </summary>
        private void DrawVocalNotes(DrawingContext context, EditorSession session, VocalTrack vocal, VocalLane vocals, LaneProjection projection, double fromTick, double toTick)
        {
            int color = (int)vocal;
            double thickness = VocalNoteThickness(projection);
            var notes = vocals.Notes;
            var (first, last) = vocals.NotesBetween(fromTick, toTick);
            for (int i = first; i < last; i++)
            {
                var note = notes[i];
                if (note.EndTick < fromTick)
                    continue;
                var bar = VocalNoteRect(session, vocals, projection, note, thickness);
                double radius = thickness / 2;

                // The slide from the note before, end to start, when both are sung.
                if (note.IsSlide && !note.IsUnpitched && i > 0 && notes[i - 1] is { IsUnpitched: false } before)
                {
                    var from = VocalNoteRect(session, vocals, projection, before, thickness);
                    context.DrawLine(VocalSlidePens[color], At(from.Right, from.Center.Y), At(bar.Left, bar.Center.Y));
                }

                if (note.IsUnpitched)
                    context.DrawRectangle(null, VocalUnpitchedPens[color], bar, radius, radius);
                else
                    context.DrawRectangle(VocalNoteBrushes[color], VocalEdgePens[color], bar, radius, radius);
            }
        }

        /// <summary>The phrase the strike line is in, shaded a little more than the others, behind the notes.</summary>
        private void DrawSingingPhrase(DrawingContext context, EditorSession session, VocalLane vocals, LaneProjection screen)
        {
            double tick = session.TickAt(session.Viewport.Cursor);
            double across1 = screen.LaneFrom, across2 = screen.LaneFrom + (screen.LaneSize * screen.Slots.Slots.Count);
            foreach (var phrase in vocals.Phrases)
            {
                if (phrase.Tick > tick)
                    break;
                if (tick < phrase.Tick + phrase.Length)
                {
                    context.FillRectangle(SingingPhraseBrush, Box(screen.AlongOf(session.AudioAt(phrase.Tick)), screen.AlongOf(session.AudioAt(phrase.Tick + phrase.Length)), across1, across2));
                    return;
                }
            }
        }

        /// <summary>
        /// The note on the strike line, lit over the one drawn on the page, with a glow where it crosses the line; and the
        /// syllable being sung, lit in the lyrics. Drawn wherever the cursor stops, playing or not.
        /// </summary>
        private void DrawVocalSinging(DrawingContext context, EditorSession session, VocalTrack vocal, VocalLane vocals, LaneProjection screen, Size size)
        {
            double now = session.Viewport.Cursor;
            double tick = session.TickAt(now);
            if (vocals.NoteAt(tick) is not { } index)
                return;

            int color = (int)vocal;
            var note = vocals.Notes[index];
            double thickness = VocalNoteThickness(screen);
            var bar = VocalNoteRect(session, vocals, screen, note, thickness);
            double along = screen.AlongOf(now);
            double half = Math.Max(5, thickness);
            using (context.PushClip(RowsOf(screen)))
            {
                var glow = Box(along - half, along + half, bar.Top - (thickness * 0.6), bar.Bottom + (thickness * 0.6));
                context.DrawRectangle(VocalGlowBrushes[color], null, glow, half, half);
                if (note.IsUnpitched)
                    context.DrawRectangle(null, VocalSingingPens[color], bar, thickness / 2, thickness / 2);
                else
                    context.DrawRectangle(VocalSingingBrushes[color], null, bar, thickness / 2, thickness / 2);
            }

            // The syllable being sung: the last shown at or before the strike line, which a slide carries on.
            var (_, to) = vocals.LyricsBetween(0, tick);
            if (to == 0)
                return;
            var lyric = vocals.Lyrics[to - 1];
            var area = LyricsArea(size, screen);
            if (area.Height <= 0)
                return;
            string text = lyric.JoinsNext && !lyric.Text.EndsWith('-') ? lyric.Text + "-" : lyric.Text;
            var label = Label(text, 11, VocalSingingBrushes[color]);
            var at = new Point(screen.AlongOf(session.AudioAt(lyric.Tick)) + 1, area.Y + Math.Max(0, (area.Height - label.Height) / 2));
            using (context.PushClip(area))
            {
                context.FillRectangle(LyricsBrush, new Rect(at, new Size(label.Width, label.Height)).Inflate(1));
                label.Draw(context, at);
            }
        }

        /// <summary>The bar of <paramref name="note"/>: on its pitch's row, or the middle row when it's spoken; never too short to see.</summary>
        private Rect VocalNoteRect(EditorSession session, VocalLane vocals, LaneProjection projection, VocalLaneNote note, double thickness)
        {
            var pitches = vocals.Pitches;
            int row = note.IsUnpitched ? pitches.Count / 2 : Math.Clamp(pitches[0] - note.Pitch, 0, pitches.Count - 1);
            double center = projection.AcrossOf(row);
            double start = projection.AlongOf(session.AudioAt(note.Tick));
            double end = Math.Max(projection.AlongOf(session.AudioAt(note.EndTick)), start + thickness);
            return Box(start, end, center - (thickness / 2), center + (thickness / 2));
        }

        /// <summary>How thick a note's bar is: most of a row, within reason.</summary>
        private static double VocalNoteThickness(LaneProjection projection) => Math.Clamp(projection.LaneSize * 0.8, 3, 14);

        /// <summary>
        /// The lyrics, in their band under the rows: each syllable at its tick, with a hyphen where it runs on into the
        /// next. One that would run into the one before is left out, as the lane is zoomed out.
        /// </summary>
        private void DrawLyrics(DrawingContext context, EditorSession session, VocalLane vocals, LaneProjection projection, double fromTick, double toTick, Size size)
        {
            var area = LyricsArea(size, projection);
            if (area.Height <= 0)
                return;
            context.FillRectangle(LyricsBrush, area);

            var brush = LabelBrush ?? DefaultLabelBrush;
            double reached = double.NegativeInfinity;
            var lyrics = vocals.Lyrics;
            var (first, last) = vocals.LyricsBetween(fromTick, toTick);
            for (int i = first; i < last; i++)
            {
                var lyric = lyrics[i];
                double along = projection.AlongOf(session.AudioAt(lyric.Tick));
                if (along < reached)
                    continue;
                string text = lyric.JoinsNext && !lyric.Text.EndsWith('-') ? lyric.Text + "-" : lyric.Text;
                var label = Label(text, 11, brush);
                label.Draw(context, new Point(along + 1, area.Y + Math.Max(0, (area.Height - label.Height) / 2)));
                reached = along + 1 + label.Width + 3;
            }
        }

        /// <summary>What's on the strike line, lit; the octaves' names at the rows' left edge; the cursor; and the focus's edge.</summary>
        private void RenderVocalOverlay(DrawingContext context, EditorSession session, VocalTrack vocal, VocalLane vocals, LaneProjection screen, Size size)
        {
            DrawVocalSinging(context, session, vocal, vocals, screen, size);
            var pitches = vocals.Pitches;
            var brush = LabelBrush ?? DefaultLabelBrush;
            for (int i = 0; i < pitches.Count; i++)
            {
                if (pitches[i] % 12 != 0)
                    continue;
                var label = Label(PitchName(pitches[i]), 9, brush);
                double center = screen.AcrossOf(i);
                var at = new Point(2, center - (label.Height / 2));
                context.FillRectangle(BadgeBrush, new Rect(at, new Size(label.Width, label.Height)).Inflate(1));
                label.Draw(context, at);
            }

            var lyrics = LyricsArea(size, screen);
            var reach = new Rect(RowsOf(screen).TopLeft, new Point(screen.TimeFrom + screen.TimeLength, Math.Max(RowsOf(screen).Bottom, lyrics.Bottom)));
            DrawCursor(context, session, screen, reach);
            if (IsFocused)
                context.DrawRectangle(null, AccentPens().Focus, new Rect(Bounds.Size).Deflate(0.5));
        }

        /// <summary>The name of a C's pitch, with its octave as MIDI numbers them from middle C, C4 (60).</summary>
        private static string PitchName(int pitch) => string.Create(CultureInfo.InvariantCulture, $"C{(pitch / 12) - 1}");
    }
}
