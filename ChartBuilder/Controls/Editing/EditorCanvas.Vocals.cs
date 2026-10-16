using System;
using System.Globalization;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ChartBuilder.Resources;
using ChartBuilder.ViewModels;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charting.Layout;
using ChartShared.UI.Controls.Highway;

namespace ChartBuilder.Controls.Editing
{
    // A vocal part's lane (charter-spec C3 item 7): its notes on rows of pitch, as a piano roll, with the lyrics under them,
    // its phrases shaded behind and as bars in a strip above, over the same beats, sections and tempo markers as a
    // highway's lane, on the same pages. Only a timeline draws one (ProjectVocals). The rows are slots, one a semitone from
    // the highest sung pitch down, so the pointer maps to a tick and a pitch (VocalPointAt) as it does to a tick and a
    // lane, and the tools edit the part from there.
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

        // A vocal phrase's bar in the strip, and a selected one's.
        private static readonly IBrush VocalPhraseBarBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF));
        private static readonly IBrush VocalPhraseBarSelectedBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));

        // The rows every vocal part has: every sung pitch, from the highest down.
        private static LaneSlots? _vocalSlots;

        // The pitch classes of the black keys, from C.
        private static bool IsBlackKey(int pitch) => (pitch % 12) is 1 or 3 or 6 or 8 or 10;

        /// <summary>The rows of <paramref name="vocals"/>: one a semitone, from the highest pitch down, the same for every part.</summary>
        private static LaneSlots VocalSlotsOf(VocalLane vocals) => _vocalSlots ??= new LaneSlots(vocals.Pitches, null);

        /// <summary>
        /// The point in a vocal part's lane under <paramref name="position"/>, or null when there's no part to edit. Its
        /// pitch is the row's, or null outside the rows; with <paramref name="clamp"/>, as a drag goes on, the nearest row's.
        /// </summary>
        private VocalPoint? VocalPointAt(Point position, bool clamp)
        {
            if (Session is not { } session || _lane is not { Vocal: { } vocal } lane || session.VocalsOf(vocal) is not { } vocals)
                return null;
            var size = Bounds.Size;
            if (ProjectVocals(size, VocalSlotsOf(vocals), session.Viewport, lane.LookAhead) is not { } projection)
                return null;
            double seconds = projection.SecondsAt(AlongOf(position));
            double tick = session.TickAt(seconds);
            double tolerance = Math.Max(1, projection.TicksOf(HitPixels, Math.Max(0, tick), session.TempoMap));
            double across = AcrossOf(position);
            var strip = VocalPhraseArea(size, projection);
            bool inPhrases = strip.Height > 0 && across >= strip.Top && across < strip.Bottom;
            var pitches = vocals.Pitches;
            int row = (int)Math.Floor((across - projection.LaneFrom) / projection.LaneSize);
            int? pitch = row >= 0 && row < pitches.Count ? pitches[row]
                : clamp ? pitches[Math.Clamp(row, 0, pitches.Count - 1)]
                : null;
            return new VocalPoint(vocal, tick, inPhrases ? null : pitch, tolerance, inPhrases);
        }

        /// <summary>
        /// A press in a vocal part's lane: with the left button on a proposed beat's handle, while a proposed tempo map is
        /// reviewed, a drag of it; otherwise the button's gesture, as in a highway's lane, the select tool's drawing a box
        /// from a press on no note.
        /// </summary>
        private void PressVocals(EditorSession session, Point position, PointerPressedEventArgs e)
        {
            Focus();
            e.Handled = true;
            if (_pressButton == MouseButton.Left && ProposalHandleAt(position) is { } handle)
            {
                e.Pointer.Capture(this);
                _proposalDrag = handle;
                return;
            }

            if (VocalPointAt(position, clamp: false) is not { } point)
                return;
            e.Pointer.Capture(this);
            _isPressed = true;
            _lastVocalPoint = point;
            _boxFrom = _pressButton == MouseButton.Left && session.Tool == EditorTool.Select && !point.InPhrases && session.Tools.HitTest(point) is null ? position : null;
            _boxTo = position;
            _boxFromSeconds = _boxFrom is not null && CurrentProjection() is { } anchorScreen
                ? anchorScreen.SecondsAt(AlongOf(position))
                : null;
            var modifiers = ModifiersOf(e.KeyModifiers);
            if (_pressButton == MouseButton.Middle)
                session.Tools.PressErase(point);
            else if (_pressButton == MouseButton.Right)
                session.Tools.PressContext(point, modifiers);
            else
                session.Tools.Press(point, modifiers);
            _overlay.Redraw();
        }

        /// <summary>
        /// Shows a resize arrow over a vocal note's end with the tools that drag it, and over a phrase bar's end in the
        /// strip, and a hand over a phrase bar's body, with a tip saying what it is.
        /// </summary>
        private void ShowVocalCursor(Point position)
        {
            StandardCursorType? wanted = null;
            string? tip = null;
            if (Session is { } session && VocalPointAt(position, clamp: false) is { } point)
            {
                if (point.InPhrases)
                {
                    if (session.Tools.HitTestPhrase(point) is { } phrase)
                    {
                        wanted = phrase.Grip == PhraseGrip.Body ? StandardCursorType.Hand : StandardCursorType.SizeWestEast;
                        tip = Strings.RailVocalPhraseToolTip;
                    }
                }
                else if (session.Tool is EditorTool.Select or EditorTool.Sustain && session.Tools.HitTest(point) is { Grip: VocalNoteGrip.End })
                {
                    wanted = StandardCursorType.SizeWestEast;
                }
            }

            SetCursor(wanted);
            ShowTip(tip);
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

                // A vocal part isn't playtested (charter-spec C19 item 4).
                case LayerKind.Playtest:
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
                    DrawVocalPhraseStrip(context, session, vocal, projection, fromTick, toTick, size);
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
        /// Each phrase as a bar in the strip above the rows, to select, drag and resize as a highway's phrase rail's are; a
        /// selected one lit, with the accent's edge.
        /// </summary>
        private void DrawVocalPhraseStrip(DrawingContext context, EditorSession session, VocalTrack vocal, LaneProjection projection, double fromTick, double toTick, Size size)
        {
            var strip = VocalPhraseArea(size, projection);
            if (strip.Height <= 0 || session.VocalContentOf(vocal) is not { } content)
                return;
            var selectedPen = AccentPens().Selected;
            double across1 = strip.Top + 2, across2 = strip.Bottom - 2;
            foreach (var phrase in content.Phrases)
            {
                if (phrase.EndTick < fromTick || phrase.Tick > toTick)
                    continue;
                double a = projection.AlongOf(session.AudioAt(phrase.Tick)), b = projection.AlongOf(session.AudioAt(phrase.EndTick));
                var bar = Box(a, Math.Max(b, a + 2), across1, across2);
                bool selected = session.Selection.Contains(vocal, phrase);
                context.DrawRectangle(selected ? VocalPhraseBarSelectedBrush : VocalPhraseBarBrush, selected ? selectedPen : null, bar, 2, 2);
            }
        }

        /// <summary>
        /// The notes: a bar on its pitch's row for each sung one, a line from the note before for a slide, and an outline
        /// along the middle row for a spoken one, which has no pitch. A selected note has the accent's edge around it.
        /// </summary>
        private void DrawVocalNotes(DrawingContext context, EditorSession session, VocalTrack vocal, VocalLane vocals, LaneProjection projection, double fromTick, double toTick)
        {
            int color = (int)vocal;
            var selection = session.Selection;
            var selectedPen = AccentPens().Selected;
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
                if (selection.Contains(vocal, new VocalNotePlace(note.Tick, note.Pitch)))
                    context.DrawRectangle(null, selectedPen, bar.Inflate(2), radius + 2, radius + 2);
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

        /// <summary>
        /// What's on the strike line, lit; the place tool's ghost; the octaves' names at the rows' left edge; the cursor; a
        /// box being drawn; and the focus's edge.
        /// </summary>
        private void RenderVocalOverlay(DrawingContext context, EditorSession session, VocalTrack vocal, VocalLane vocals, LaneProjection screen, Size size)
        {
            DrawVocalSinging(context, session, vocal, vocals, screen, size);
            DrawVocalHover(context, session, vocal, vocals, screen);
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
            DrawBox(context, screen);
            if (IsFocused)
                context.DrawRectangle(null, AccentPens().Focus, new Rect(Bounds.Size).Deflate(0.5));
        }

        /// <summary>The note the place tool would place under the pointer, faint: on the grid, a grid step long.</summary>
        private void DrawVocalHover(DrawingContext context, EditorSession session, VocalTrack vocal, VocalLane vocals, LaneProjection screen)
        {
            if (_hover is not { } hover || _isPressed || session.Tool != EditorTool.Place
                || VocalPointAt(hover, clamp: false) is not { Pitch: { } pitch } point || session.Tools.HitTest(point) is not null)
                return;
            var map = session.TempoMap;
            long tick = session.Snap.Nearest(Math.Max(0, point.Tick), map);
            long end = Math.Max(tick + 1, session.Snap.Step(tick, 1, map));
            double thickness = VocalNoteThickness(screen);
            var bar = VocalNoteRect(session, vocals, screen, new VocalLaneNote(tick, end, pitch, false, false), thickness);
            int color = (int)vocal;
            using (context.PushClip(RowsOf(screen)))
            using (context.PushOpacity(0.45))
                context.DrawRectangle(VocalNoteBrushes[color], VocalEdgePens[color], bar, thickness / 2, thickness / 2);
        }

        /// <summary>The name of a C's pitch, with its octave as MIDI numbers them from middle C, C4 (60).</summary>
        private static string PitchName(int pitch) => string.Create(CultureInfo.InvariantCulture, $"C{(pitch / 12) - 1}");
    }
}
