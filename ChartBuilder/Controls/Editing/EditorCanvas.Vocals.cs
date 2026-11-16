using System;
using System.Collections.Generic;
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
using ChartShared.UI.Controls;
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

        // A black key's row, a little darker than a white key's. The line under each C, the phrases' shading and bars, and the
        // key being sung are the accent's, in tints of it (AccentFill, AccentLine), so they follow the theme's.
        private static readonly IBrush BlackKeyBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x50, 0x00, 0x00, 0x00));
        private static readonly IBrush LyricsBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xFF, 0x0C, 0x0D, 0x14));

        // A vocal part's star power: its bar in the rail, in a highway's star power colour, and the rail's ground.
        private static readonly IBrush VocalStarPowerBrush = new ImmutableSolidColorBrush(HighwayPalette.WithAlpha(PhraseColors.Of(PhraseKind.StarPower), 0xD0));
        private static readonly IBrush VocalStarPowerRailBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x30, 0x00, 0x00, 0x00));


        // A vocal phrase's bar in the strip, and a selected one's.
        // (A phrase's bar is the accent at BarAlpha, and a selected one's at SelectedBarAlpha.)
        private const byte BarAlpha = 0x78;
        private const byte SelectedBarAlpha = 0xD0;

        // The keyboard down the lane's left edge, in the app's dark surfaces (Theme.axaml) rather than a piano's ivory: the app's
        // background behind it, the white keys a lifted slate and the black keys sunk into the background, the lines between them,
        // and the names on them, muted, but for each C's, which is the accent's.
        private static readonly IBrush KeyboardBackBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x0E, 0x0F, 0x14));
        private static readonly IBrush WhiteKeyBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x2B, 0x2D, 0x3E));
        private static readonly IBrush BlackKeyFaceBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x14, 0x15, 0x1C));
        private static readonly IBrush WhiteKeyNameBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x8E, 0x91, 0xAB));
        private static readonly IBrush BlackKeyNameBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x6A, 0x6D, 0x86));
        private static readonly IPen KeySeparatorPen = new ImmutablePen(Color.FromArgb(0xB0, 0x0E, 0x0F, 0x14).ToUInt32(), 1);
        private static readonly IPen KeyboardEdgePen = new ImmutablePen(Color.FromRgb(0x26, 0x28, 0x36).ToUInt32(), 1);
        private static readonly IBrush LitKeyNameBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x0E, 0x0F, 0x14));

        // How strongly the spectrogram shows behind the rows: more than a highway's, as it's what the notes are picked by.
        private const double VocalSpectrogramOpacity = 0.8;

        // How much of a key's width a black key takes, from the left edge, and how short a row may be and still be named.
        private const double BlackKeyFraction = 0.6;
        private const double MinNamedRowHeight = 9;

        // The names of the notes in an octave, from C.
        private static readonly string[] NoteNames = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

        // One notch of the wheel over the keyboard moves the notes shown this many semitones, an octave with Shift held.
        private const int KeyboardScrollNotch = 2;
        private const int KeyboardFastScrollNotch = 12;

        // What the wheel over the keyboard has turned but not yet taken a notch of.
        private double _pitchScrollTurn;
        private double _pitchZoomTurn;

        // The badges that say notes are out of view above or below the rows, as last drawn, with the pitch each scrolls to.
        private (Rect Badge, int Pitch)? _notesAbove;
        private (Rect Badge, int Pitch)? _notesBelow;

        /// <summary>The accent, as the theme gives it.</summary>
        private Color AccentColor => ((AccentBrush ?? DefaultAccentBrush) as ISolidColorBrush)?.Color ?? Color.FromRgb(0x7C, 0xB4, 0xFF);

        /// <summary>The accent at <paramref name="alpha"/>, as a fill.</summary>
        private IBrush AccentFill(byte alpha) => _gems.Brush(HighwayPalette.WithAlpha(AccentColor, alpha));

        /// <summary>The accent at <paramref name="alpha"/>, as a line.</summary>
        private IPen AccentLine(byte alpha, double thickness = 1) => _gems.Pen(HighwayPalette.WithAlpha(AccentColor, alpha), thickness);

        // The pitch classes of the black keys, from C.
        private static bool IsBlackKey(int pitch) => (pitch % 12) is 1 or 3 or 6 or 8 or 10;

        /// <summary>The rows of the vocal lane <paramref name="lane"/>: one a semitone, from the highest pitch shown down, for the pitches its keyboard shows.</summary>
        private static LaneSlots VocalSlotsOf(EditorLaneViewModel lane) => lane.PitchView.Slots;

        /// <summary>Whether <paramref name="position"/> is on a vocal lane's keyboard, which no note is placed on.</summary>
        private bool IsOnVocalKeyboard(Point position) => _lane is { Vocal: not null } && VocalGutterWidth > 0 && position.X < VocalGutterWidth;

        /// <summary>The pitch of the row at <paramref name="position"/>'s height, or null above or below the rows.</summary>
        private int? PitchAt(Point position)
        {
            if (Session is not { } session || _lane is not { Vocal: not null } lane
                || ProjectVocals(Bounds.Size, VocalSlotsOf(lane), session.Viewport, lane.Length) is not { } projection)
            {
                return null;
            }

            int row = (int)Math.Floor((AcrossOf(position) - projection.LaneFrom) / projection.LaneSize);
            return row >= 0 && row < projection.Slots.Slots.Count ? projection.Slots.Slots[row] : null;
        }

        /// <summary>
        /// The wheel over a vocal lane's keyboard: a notch scrolls the notes shown, two semitones or an octave with Shift
        /// held, turned up for higher ones; with Ctrl held it zooms, turned up to show fewer, about the pitch it's over. Marks
        /// the event handled when the wheel turned some way.
        /// </summary>
        private void WheelVocalKeyboard(PointerWheelEventArgs e, EditorLaneViewModel lane)
        {
            double delta = e.Delta.Y;
            if (delta == 0)
                return;

            var view = lane.PitchView;
            if ((e.KeyModifiers & KeyModifiers.Control) != 0)
            {
                int zoom = WheelSteps.TakeZoomStep(ref _pitchZoomTurn, delta);
                if (zoom != 0)
                    lane.PitchView = view.Zoomed(zoom, PitchAt(e.GetPosition(this)) ?? view.Top - (view.Span / 2));
            }
            else
            {
                int notch = WheelSteps.TakeZoomStep(ref _pitchScrollTurn, delta);
                if (notch != 0)
                    lane.PitchView = view.Scrolled(notch * ((e.KeyModifiers & KeyModifiers.Shift) != 0 ? KeyboardFastScrollNotch : KeyboardScrollNotch));
            }

            e.Handled = true;
        }

        /// <summary>Scrolls the notes shown to the pitch of the badge at <paramref name="position"/>, if it's on one; whether it was.</summary>
        private bool TakeNotesBadge(Point position)
        {
            if (_lane is not { Vocal: not null } lane)
                return false;
            foreach (var badge in (ReadOnlySpan<(Rect Badge, int Pitch)?>)[_notesAbove, _notesBelow])
            {
                if (badge is { } found && found.Badge.Contains(position))
                {
                    lane.PitchView = lane.PitchView.Revealing(found.Pitch);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The point in a vocal part's lane under <paramref name="position"/>, or null when there's no part to edit. Its
        /// pitch is the row's, or null outside the rows; with <paramref name="clamp"/>, as a drag goes on, the nearest row's.
        /// </summary>
        private VocalPoint? VocalPointAt(Point position, bool clamp)
        {
            if (Session is not { } session || _lane is not { Vocal: { } vocal } lane || session.VocalsOf(vocal) is not { } vocals)
                return null;
            var size = Bounds.Size;
            if (ProjectVocals(size, VocalSlotsOf(lane), session.Viewport, lane.Length) is not { } projection)
                return null;

            // The keyboard isn't the lane's, though a drag that began in it goes on over it.
            if (!clamp && AlongOf(position) < projection.TimeFrom)
                return null;
            double seconds = projection.SecondsAt(AlongOf(position));
            double tick = session.TickAt(seconds);
            double tolerance = Math.Max(1, projection.TicksOf(HitPixels, Math.Max(0, tick), session.TempoMap));
            double across = AcrossOf(position);
            var strip = VocalPhraseArea(size, projection);
            bool inPhrases = strip.Height > 0 && across >= strip.Top && across < strip.Bottom;
            var rail = VocalStarPowerArea(size, projection);
            bool inStarPower = rail.Height > 0 && across >= rail.Top && across < rail.Bottom;
            var pitches = projection.Slots.Slots;
            int row = (int)Math.Floor((across - projection.LaneFrom) / projection.LaneSize);
            int? pitch = row >= 0 && row < pitches.Count ? pitches[row]
                : clamp ? pitches[Math.Clamp(row, 0, pitches.Count - 1)]
                : null;
            return new VocalPoint(vocal, tick, inPhrases || inStarPower ? null : pitch, tolerance, inPhrases, inStarPower);
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

            // A badge of notes out of view scrolls to them, and is no edit.
            if (_pressButton == MouseButton.Left && TakeNotesBadge(position))
                return;

            if (VocalPointAt(position, clamp: false) is not { } point)
                return;
            e.Pointer.Capture(this);
            _isPressed = true;
            _lastVocalPoint = point;
            _boxFrom = _pressButton == MouseButton.Left && session.Tool == EditorTool.Select && !session.Tools.LoopActive && !point.InPhrases && !point.InStarPower && session.Tools.HitTest(point) is null ? position : null;
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
            if (_notesAbove is { } above && above.Badge.Contains(position) || _notesBelow is { } below && below.Badge.Contains(position))
                wanted = StandardCursorType.Hand;
            else if (Session is { } session && VocalPointAt(position, clamp: false) is { } point)
            {
                if (point.InPhrases)
                {
                    tip = Strings.RailVocalPhraseToolTip;
                    if (session.Tools.HitTestPhrase(point) is { } phrase)
                        wanted = phrase.Grip == PhraseGrip.Body ? StandardCursorType.SizeAll : StandardCursorType.SizeWestEast;
                    else if ((_keys & KeyModifiers.Shift) == 0 && session.Tools.VocalPhraseDrawStart(point) is not null)
                        wanted = StandardCursorType.Cross;
                }
                else if (point.InStarPower)
                {
                    tip = Strings.RailVocalStarPowerToolTip;
                    if (session.Tools.HitTestStarPower(point) is { } starPower)
                        wanted = starPower.Grip == PhraseGrip.Body ? StandardCursorType.SizeAll : StandardCursorType.SizeWestEast;
                    else if ((_keys & KeyModifiers.Shift) == 0 && session.Tools.VocalStarPowerDrawStart(point) is not null)
                        wanted = StandardCursorType.Cross;
                }
                else if (session.Tool is EditorTool.Select or EditorTool.Place && session.Tools.HitTest(point) is { Grip: VocalNoteGrip.End })
                {
                    wanted = StandardCursorType.SizeWestEast;
                }
            }

            SetCursor(wanted);
            ShowTip(tip);
        }

        /// <summary>
        /// Shows the resize arrow over a tempo or time signature change the Tempo tool would drag in a vocal lane, or the
        /// usual cursor over the one the song starts with. Whether it showed anything.
        /// </summary>
        private bool ShowVocalTempoCursor(Point position)
        {
            if (Session is not { } session || session.Tool != EditorTool.Tempo || (_keys & KeyModifiers.Shift) != 0
                || session.Tools.IsSyncPress(ModifiersOf(_keys)))
            {
                return false;
            }

            if (VocalPointAt(position, clamp: false) is not { InPhrases: false, InStarPower: false } point
                || session.Tools.TempoChangeAt(point.Tick, point.Tolerance) is not { } tick)
            {
                return false;
            }

            ShowTempoCursorFor(tick);
            return true;
        }

        /// <summary>Draws <paramref name="layer"/> of a vocal part's lane, as <see cref="RenderLayer"/> does a highway's.</summary>
        private void RenderVocalLayer(DrawingContext context, Layer layer, EditorSession session, EditorLaneViewModel lane, VocalTrack vocal, VocalLane vocals)
        {
            var size = Bounds.Size;
            Extras = default;
            if (ProjectVocals(size, VocalSlotsOf(lane), session.Viewport, lane.Length) is not { } screen)
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
                    FillPitchRows(context, projection);
                    DrawVocalBackdrop(context, projection);
                    DrawPitchRowLines(context, projection);
                    DrawVocalPhrases(context, session, vocals, projection, fromTick, toTick);
                    DrawVocalPhraseStrip(context, session, vocal, projection, fromTick, toTick, size);
                    DrawVocalStarPowerRail(context, session, vocal, projection, fromTick, toTick, size);
                    DrawSpans(context, session, projection);
                    DrawBeats(context, session, session.TempoMap, projection, fromTick, toTick, size, RailPass.Both);
                    DrawTempoMarkers(context, session, projection, fromTick, toTick, size, RailPass.Both);
                    DrawTempoProposal(context, session, projection, size, RailPass.Both);
                    DrawSections(context, session, projection, fromTick, toTick, size, RailPass.Both);
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

        /// <summary>The rows' ground, under the audio.</summary>
        private void FillPitchRows(DrawingContext context, LaneProjection projection) =>
            context.FillRectangle(LanesBrush, RowsOf(projection));

        /// <summary>The rows, as a piano roll's: the black keys' darker, and a line under each C, over the audio.</summary>
        private void DrawPitchRowLines(DrawingContext context, LaneProjection projection)
        {
            double from = projection.TimeFrom, to = projection.TimeFrom + projection.TimeLength;
            var pitches = projection.Slots.Slots;
            for (int i = 0; i < pitches.Count; i++)
            {
                double edge = projection.LaneFrom + (i * projection.LaneSize);
                if (IsBlackKey(pitches[i]))
                    context.FillRectangle(BlackKeyBrush, Box(from, to, edge, edge + projection.LaneSize));
                if (pitches[i] % 12 == 0 && i < pitches.Count - 1)
                    context.DrawLine(AccentLine(0x44), At(from, edge + projection.LaneSize), At(to, edge + projection.LaneSize));
            }
        }

        /// <summary>
        /// The lane's audio behind the rows, as a waveform or a spectrogram, the same as a highway's (charter-spec C12, C13). The
        /// waveform is centred on the rows whatever the pitches shown. The spectrogram is drawn with each row of the keyboard
        /// across the band of its note's frequency, so a note on the roll sits on the sound that's at its pitch; it's cropped
        /// to the rows, and moves with them as the keyboard is scrolled and zoomed. Pictures there was no time to draw this
        /// frame are drawn once it's out.
        /// </summary>
        private void DrawVocalBackdrop(DrawingContext context, LaneProjection projection)
        {
            if (_lane?.Audio is not { Source: { } source } audio || _editor is not { } editor)
                return;

            var rows = RowsOf(projection);
            bool complete = true;
            if (audio.IsSpectrogram)
            {
                // Its chunks near the middle of the view are computed first.
                if (editor.Audio.Analysis.SpectrogramFor(source, (projection.Start + projection.End) / 2) is { } spectrogram)
                {
                    var frame = SpectrogramPainter.PitchFrame.Of(projection.Slots.Slots[0], projection.LaneFrom, projection.LaneSize);
                    using (context.PushOpacity(VocalSpectrogramOpacity))
                        complete &= DrawSpectrogram(context, audio, spectrogram, rows, projection, frame);
                }
            }
            else
            {
                complete &= DrawWaveform(context, editor, audio, source, rows, projection, full: false);
            }

            if (!complete)
                PostRedraw();
        }

        /// <summary>Each phrase faintly shaded across the rows, with a line at either end.</summary>
        private void DrawVocalPhrases(DrawingContext context, EditorSession session, VocalLane vocals, LaneProjection projection, double fromTick, double toTick)
        {
            double across1 = projection.LaneFrom, across2 = projection.LaneFrom + (projection.LaneSize * projection.Slots.Slots.Count);
            var shade = AccentFill(0x12);
            var edge = AccentLine(0x58);
            foreach (var phrase in vocals.Phrases)
            {
                long end = phrase.Tick + phrase.Length;
                if (end < fromTick || phrase.Tick > toTick)
                    continue;
                double a = projection.AlongOf(session.AudioAt(phrase.Tick)), b = projection.AlongOf(session.AudioAt(end));
                context.FillRectangle(shade, Box(a, b, across1, across2));
                context.DrawLine(edge, At(Limited(a), across1), At(Limited(a), across2));
                context.DrawLine(edge, At(Limited(b), across1), At(Limited(b), across2));
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
            var barBrush = AccentFill(BarAlpha);
            var selectedBrush = AccentFill(SelectedBarAlpha);
            context.FillRectangle(VocalStarPowerRailBrush, Box(projection.TimeFrom, projection.TimeFrom + projection.TimeLength, strip.Top, strip.Bottom));
            double across1 = strip.Top + 2, across2 = strip.Bottom - 2;
            foreach (var phrase in content.Phrases)
            {
                if (phrase.EndTick < fromTick || phrase.Tick > toTick)
                    continue;
                double a = projection.AlongOf(session.AudioAt(phrase.Tick)), b = projection.AlongOf(session.AudioAt(phrase.EndTick));
                var bar = Box(a, Math.Max(b, a + 2), across1, across2);
                bool selected = session.Selection.Contains(vocal, phrase);
                context.DrawRectangle(selected ? selectedBrush : barBrush, selected ? selectedPen : null, bar, 2, 2);
            }
        }

        /// <summary>
        /// The part's star power as bars in the rail under the phrase strip, in the colour of a highway's, to select, drag and
        /// resize as the phrase bars are; a selected one with the accent's edge. The rail is shaded, so it's seen to be there
        /// to draw in when there's none.
        /// </summary>
        private void DrawVocalStarPowerRail(DrawingContext context, EditorSession session, VocalTrack vocal, LaneProjection projection, double fromTick, double toTick, Size size)
        {
            var rail = VocalStarPowerArea(size, projection);
            if (rail.Height <= 0 || session.VocalContentOf(vocal) is not { } content)
                return;
            context.FillRectangle(VocalStarPowerRailBrush, Box(projection.TimeFrom, projection.TimeFrom + projection.TimeLength, rail.Top, rail.Bottom));
            var selectedPen = AccentPens().Selected;
            double across1 = rail.Top + 2, across2 = rail.Bottom - 2;
            foreach (var starPower in content.StarPower)
            {
                if (starPower.EndTick < fromTick || starPower.Tick > toTick)
                    continue;
                double a = projection.AlongOf(session.AudioAt(starPower.Tick)), b = projection.AlongOf(session.AudioAt(starPower.EndTick));
                var bar = Box(a, Math.Max(b, a + 2), across1, across2);
                bool selected = session.Selection.Contains(vocal, starPower);
                context.DrawRectangle(VocalStarPowerBrush, selected ? selectedPen : null, bar, 2, 2);
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
            var phrases = vocals.Phrases;
            for (int i = 0; i < phrases.Count; i++)
            {
                var phrase = phrases[i];
                if (phrase.Tick > tick)
                    break;
                if (tick < phrase.Tick + phrase.Length)
                {
                    context.FillRectangle(AccentFill(0x20), Box(screen.AlongOf(session.AudioAt(phrase.Tick)), screen.AlongOf(session.AudioAt(phrase.Tick + phrase.Length)), across1, across2));
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
            var label = Label(Joined(lyric), 11, VocalSingingBrushes[color]);
            var at = new Point(screen.AlongOf(session.AudioAt(lyric.Tick)) + 1, area.Y + Math.Max(0, (area.Height - label.Height) / 2));
            using (context.PushClip(area))
            {
                context.FillRectangle(LyricsBrush, new Rect(at, new Size(label.Width, label.Height)).Inflate(1));
                label.Draw(context, at);
            }
        }

        /// <summary>
        /// The bar of <paramref name="note"/>: on its pitch's row, or middle C's when it's spoken; never too short to see. Out
        /// of the rows shown, above or below them, when its pitch is, which the rows' clip hides.
        /// </summary>
        private Rect VocalNoteRect(EditorSession session, VocalLane vocals, LaneProjection projection, VocalLaneNote note, double thickness)
        {
            int row = projection.Slots.Slots[0] - (note.IsUnpitched ? VocalLane.SpokenPitch : note.Pitch);
            double center = projection.AcrossOf(row);
            double start = projection.AlongOf(session.AudioAt(note.Tick));
            double end = Math.Max(projection.AlongOf(session.AudioAt(note.EndTick)), start + thickness);
            return Box(start, end, center - (thickness / 2), center + (thickness / 2));
        }

        /// <summary>How thick a note's bar is: most of a row, within reason.</summary>
        private static double VocalNoteThickness(LaneProjection projection) => Math.Clamp(projection.LaneSize * 0.8, 3, 24);

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
                var label = Label(Joined(lyric), 11, brush);
                label.Draw(context, new Point(along + 1, area.Y + Math.Max(0, (area.Height - label.Height) / 2)));
                reached = along + 1 + label.Width + 3;
            }
        }

        /// <summary>
        /// What's on the strike line, lit; the place tool's ghost; the octaves' names at the rows' left edge; where the Loop
        /// tool would start drawing the loop region, and its X and what of it the pointer would take hold of; the cursor; a
        /// box being drawn; and the focus's edge.
        /// </summary>
        private void RenderVocalOverlay(DrawingContext context, EditorSession session, VocalTrack vocal, VocalLane vocals, LaneProjection screen, Size size)
        {
            DrawVocalSinging(context, session, vocal, vocals, screen, size);
            DrawVocalHover(context, session, vocal, vocals, screen);
            DrawVocalStripHover(context, session, screen, size);
            DrawVocalTempoHover(context, session, screen);
            DrawLoopLine(context, session, screen, screen.Slots);
            DrawLoopHover(context, session, screen, screen.Slots);

            var lyrics = LyricsArea(size, screen);
            var reach = new Rect(RowsOf(screen).TopLeft, new Point(screen.TimeFrom + screen.TimeLength, Math.Max(RowsOf(screen).Bottom, lyrics.Bottom)));
            DrawCursor(context, session, screen, reach);
            DrawBox(context, screen);
            DrawHiddenNotes(context, session, vocal, vocals, screen);

            // Last of the lane's own drawing, over the pages and the overlay's reach, which run on under it.
            DrawKeyboard(context, screen, size, SungPitch(session, vocals));
            if (IsFocused)
                context.DrawRectangle(null, AccentPens().Focus, new Rect(Bounds.Size).Deflate(0.5));
        }

        /// <summary>
        /// The piano keyboard down the left edge: a key a row, white or black as the pitch is, with its name on it when the
        /// rows are tall enough, otherwise only the Cs'. Covers the whole edge, which the pages run on under.
        /// </summary>
        private void DrawKeyboard(DrawingContext context, LaneProjection screen, Size size, int? sung)
        {
            double width = VocalGutterWidth;
            context.FillRectangle(KeyboardBackBrush, new Rect(0, 0, width, size.Height));
            var pitches = screen.Slots.Slots;
            double row = screen.LaneSize;
            bool namesAll = row >= MinNamedRowHeight;
            double blackWidth = width * BlackKeyFraction;
            using (context.PushClip(new Rect(0, screen.LaneFrom, width, row * pitches.Count)))
            {
                for (int i = 0; i < pitches.Count; i++)
                {
                    int pitch = pitches[i];
                    double top = screen.LaneFrom + (i * row), bottom = top + row;
                    bool black = IsBlackKey(pitch);
                    bool lit = pitch == sung;
                    context.FillRectangle(lit && !black ? AccentFill(0xE0) : WhiteKeyBrush, new Rect(0, top, width, row));
                    context.DrawLine(KeySeparatorPen, new Point(0, bottom), new Point(width, bottom));
                    if (black)
                        context.FillRectangle(lit ? AccentFill(0xE0) : BlackKeyFaceBrush, new Rect(0, top + 0.5, blackWidth, Math.Max(1, row - 1)));

                    if (!namesAll && pitch % 12 != 0)
                        continue;
                    var nameBrush = lit ? LitKeyNameBrush : pitch % 12 == 0 ? _gems.Brush(AccentColor) : black ? BlackKeyNameBrush : WhiteKeyNameBrush;
                    var label = Label(PitchName(pitch), 9, nameBrush);
                    double x = black ? 3 : Math.Max(blackWidth + 1, width - label.Width - 3);
                    label.Draw(context, new Point(x, top + ((row - label.Height) / 2)));
                }
            }

            context.DrawLine(KeyboardEdgePen, new Point(width - 0.5, 0), new Point(width - 0.5, size.Height));
        }

        /// <summary>The pitch of the sung note on the strike line, or null between notes or on a spoken one.</summary>
        private static int? SungPitch(EditorSession session, VocalLane vocals) =>
            vocals.NoteAt(session.TickAt(session.Viewport.Cursor)) is { } index && !vocals.Notes[index].IsUnpitched ? vocals.Notes[index].Pitch : null;

        /// <summary>
        /// A badge at the rows' top or bottom, with an arrow and a count, where notes in view along the time axis lie above or
        /// below the pitches shown, so none are lost to a narrow range or a range off to one side. Pressing one scrolls to the
        /// nearest of its notes.
        /// </summary>
        private void DrawHiddenNotes(DrawingContext context, EditorSession session, VocalTrack vocal, VocalLane vocals, LaneProjection screen)
        {
            _notesAbove = _notesBelow = null;
            if (_lane is not { } lane || lane.PitchView.IsFull)
                return;

            var view = lane.PitchView;
            var (fromTick, toTick) = TicksOf(session, screen);
            int above = 0, below = 0, nearestAbove = int.MaxValue, nearestBelow = int.MinValue;
            var notes = vocals.Notes;
            var (first, last) = vocals.NotesBetween(fromTick, toTick);
            for (int i = first; i < last; i++)
            {
                var note = notes[i];
                if (note.EndTick < fromTick)
                    continue;
                int pitch = note.IsUnpitched ? VocalLane.SpokenPitch : note.Pitch;
                if (pitch > view.Top)
                {
                    above++;
                    nearestAbove = Math.Min(nearestAbove, pitch);
                }
                else if (pitch < view.Bottom)
                {
                    below++;
                    nearestBelow = Math.Max(nearestBelow, pitch);
                }
            }

            var rows = RowsOf(screen);
            if (above > 0)
                _notesAbove = (DrawNotesBadge(context, vocal, above, rows.Left + 6, rows.Top + 3, up: true), nearestAbove);
            if (below > 0)
                _notesBelow = (DrawNotesBadge(context, vocal, below, rows.Left + 6, rows.Bottom - 3 - NotesBadgeHeight, up: false), nearestBelow);
        }

        private const double NotesBadgeHeight = 16;

        /// <summary>One badge of <see cref="DrawHiddenNotes"/>: a triangle pointing the way to scroll, and how many notes lie that way. Where it is.</summary>
        private Rect DrawNotesBadge(DrawingContext context, VocalTrack vocal, int count, double left, double top, bool up)
        {
            var label = Label(count.ToString(CultureInfo.CurrentCulture), 10, DefaultLabelBrush);
            var badge = new Rect(left, top, label.Width + 24, NotesBadgeHeight);
            var color = VocalNoteBrushes[(int)vocal];
            context.DrawRectangle(BadgeBrush, new ImmutablePen(VocalColors[(int)vocal].ToUInt32(), 1), badge, 4, 4);

            double mid = badge.Left + 11, center = badge.Center.Y;
            var arrow = new StreamGeometry();
            using (var ctx = arrow.Open())
            {
                double tip = up ? center - 3.5 : center + 3.5, back = up ? center + 3 : center - 3;
                ctx.BeginFigure(new Point(mid, tip), true);
                ctx.LineTo(new Point(mid + 5, back));
                ctx.LineTo(new Point(mid - 5, back));
                ctx.EndFigure(true);
            }

            context.DrawGeometry(color, null, arrow);
            label.Draw(context, new Point(badge.Left + 20, badge.Top + ((badge.Height - label.Height) / 2)));
            return badge;
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

        /// <summary>
        /// What a press in the phrase strip or the star power rail would do, as the highway's phrase rail shows it: where a
        /// press would start drawing, on the grid, a line across the rows in the colour of what's drawn and a mark in its row;
        /// or, over a bar, its outline in the accent, so the hover shows what a drag moves or resizes. None where a press does
        /// something else: with Shift held, the Loop tool, or a tool that doesn't draw there.
        /// </summary>
        private void DrawVocalStripHover(DrawingContext context, EditorSession session, LaneProjection screen, Size size)
        {
            if (_hover is not { } hover || _isPressed || (_keys & KeyModifiers.Shift) != 0 || session.Tools.LoopActive
                || VocalPointAt(hover, clamp: false) is not { } point || !(point.InPhrases || point.InStarPower))
            {
                return;
            }

            var tools = session.Tools;
            var area = point.InPhrases ? VocalPhraseArea(size, screen) : VocalStarPowerArea(size, screen);
            double across1 = area.Top + 2, across2 = area.Bottom - 2;
            (long Tick, long EndTick)? bar = point.InPhrases
                ? tools.HitTestPhrase(point) is { } phrase ? (phrase.Phrase.Tick, phrase.Phrase.EndTick) : null
                : tools.HitTestStarPower(point) is { } starPower ? (starPower.StarPower.Tick, starPower.StarPower.EndTick) : null;
            if (bar is { } grabbed)
            {
                var marks = HoverMarks(strong: true);
                double a = screen.AlongOf(session.AudioAt(grabbed.Tick)), b = screen.AlongOf(session.AudioAt(grabbed.EndTick));
                context.DrawRectangle(marks.Brush, marks.Pen, Box(a, Math.Max(b, a + 2), across1, across2).Inflate(1), 2, 2);
                return;
            }

            var modifiers = ModifiersOf(_keys);
            if ((point.InPhrases ? tools.VocalPhraseDrawStart(point, modifiers) : tools.VocalStarPowerDrawStart(point, modifiers)) is not { } tick)
                return;
            var brush = point.InPhrases ? AccentFill(0xD0) : _gems.Brush(PhraseColor(PhraseKind.StarPower));
            double along = screen.AlongOf(session.AudioAt(tick));
            var rows = RowsOf(screen);
            context.FillRectangle(brush, Box(along - 1, along + 1, rows.Top, rows.Bottom));
            context.FillRectangle(brush, Box(along - 1.5, along + 1.5, across1 - 1, across2 + 1));
        }

        /// <summary>
        /// Where a press of the Tempo tool would add a tempo marker in a vocal lane: a line across the pitch rows in the
        /// tempo markers' amber, on the grid step the press would snap to. None over a change the press would drag, in the
        /// phrase strip, or with Shift held or the Sync tool's press.
        /// </summary>
        private void DrawVocalTempoHover(DrawingContext context, EditorSession session, LaneProjection screen)
        {
            if (_hover is not { } hover || _isPressed || session.Tool != EditorTool.Tempo || (_keys & KeyModifiers.Shift) != 0
                || session.Tools.IsSyncPress(ModifiersOf(_keys)))
            {
                return;
            }

            if (VocalPointAt(hover, clamp: false) is not { Pitch: not null, InPhrases: false, InStarPower: false } point
                || session.Tools.TempoAddAt(point.Tick, point.Tolerance) is not { } tick)
            {
                return;
            }

            double along = screen.AlongOf(session.AudioAt(tick));
            var rows = RowsOf(screen);
            context.FillRectangle(TempoBrush, Box(along - 1, along + 1, rows.Top, rows.Bottom));
        }

        /// <summary>The name of a pitch, with its octave as MIDI numbers them from middle C, C4 (60): C#3 for 49.</summary>
        private static string PitchName(int pitch)
        {
            // Drawn for every row every frame, so the names are made once.
            if ((uint)pitch >= (uint)PitchNames.Length)
                return string.Create(CultureInfo.InvariantCulture, $"{NoteNames[((pitch % 12) + 12) % 12]}{(pitch / 12) - 1}");
            return PitchNames[pitch] ??= string.Create(CultureInfo.InvariantCulture, $"{NoteNames[pitch % 12]}{(pitch / 12) - 1}");
        }

        private static readonly string?[] PitchNames = new string?[128];

        // The syllables made again with their hyphen, for the ones that join the next, kept so each frame doesn't build them anew.
        private readonly Dictionary<string, string> _hyphenated = new(StringComparer.Ordinal);

        /// <summary>A lyric as drawn: with a hyphen after it when it joins the next syllable.</summary>
        private string Joined(VocalLaneLyric lyric)
        {
            string text = lyric.Text;
            if (!lyric.JoinsNext || text.EndsWith('-'))
                return text;
            if (_hyphenated.TryGetValue(text, out var found))
                return found;
            if (_hyphenated.Count >= 1024)
                _hyphenated.Clear();
            return _hyphenated[text] = text + "-";
        }
    }
}
