using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ChartPacker.Core.Charting.Editing;
using ChartPacker.Core.Charting.Playtest;
using ChartPacker.Core.Charts;
using ChartPacker.UI.Controls.Highway;

namespace ChartBuilder.Controls.Editing
{
    // Playtest (charter-spec C19 item 4), over the notes of each lane played: a mark on each note missed or sustain dropped,
    // which stays until the note is played again or the playtest is cleared; each overstrum and overhit where it happened; a
    // meter by the strike line of how early or late the last hits were; and, while the lane is played, the columns lit as
    // notes are hit, in place of autoplay's glow.
    public abstract partial class EditorCanvas
    {
        private static readonly IPen MissPen = new ImmutablePen(Color.FromRgb(0xFF, 0x4D, 0x4D).ToUInt32(), 2.5, lineCap: PenLineCap.Round);
        private static readonly IPen DropPen = new ImmutablePen(Color.FromRgb(0xFF, 0xA9, 0x3A).ToUInt32(), 2, lineCap: PenLineCap.Round);
        private static readonly IPen StrayPen = new ImmutablePen(Color.FromArgb(0xC0, 0xFF, 0x4D, 0x4D).ToUInt32(), 2, new ImmutableDashStyle([2, 2], 0));
        private static readonly IPen MeterPen = new ImmutablePen(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF).ToUInt32(), 1);
        private static readonly IPen MeterCentrePen = new ImmutablePen(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF).ToUInt32(), 1.5);
        private static readonly Color EarlyColor = Color.FromRgb(0x5A, 0xB4, 0xFF);
        private static readonly Color LateColor = Color.FromRgb(0xFF, 0xB0, 0x4A);

        // How long a hit stays on the meter, in seconds of the song.
        private const double MeterSeconds = 3;

        /// <summary>What the playtest found in this lane's part and difficulty, if it's been played.</summary>
        private PlaytestRun? PlaytestOf(ChartTrackKey key) => _editor?.Playtest.RunOf(key);

        /// <summary>The playtest, while it's playing this lane's part and difficulty.</summary>
        private PlaytestRun? PlayingOf(ChartTrackKey key) =>
            _editor?.Playtest is { IsActive: true, Track: { } track } playtest && track == key ? playtest.Run : null;

        private void OnPlaytestChanged(object? sender, EventArgs e)
        {
            _playtest.Redraw();
            _strike.Redraw();
        }

        /// <summary>What the playtest found in the lane shown, and the meter at the strike line.</summary>
        private void RenderPlaytest(DrawingContext context, EditorSession session, ChartTrackKey key, LaneSlots slots, LaneProjection screen)
        {
            if (PlaytestOf(key) is not { } run)
                return;
            bool drums = ChartInstruments.KindOf(key.Instrument) == ChartInstrumentKind.Drums;
            var (fromTick, toTick) = TicksOf(session, screen);
            double size = GemSize(screen);
            var lanes = LanesOf(screen, slots);
            using (context.PushClip(lanes))
            {
                foreach (var ((tick, lane), mark) in run.Marks)
                {
                    if (mark.Kind == JudgeKind.Hit || tick < fromTick - 1 || tick > toTick + 1)
                        continue;
                    if (MarkRect(screen, slots, ShownOf(lane, drums), screen.AlongOf(session.AudioAt(tick)), size) is not { } rect)
                        continue;
                    if (mark.Kind == JudgeKind.Miss)
                        DrawCross(context, MissPen, rect);
                    else
                        context.DrawLine(DropPen, rect.BottomLeft, rect.TopRight);
                }

                foreach (var stray in run.Strays)
                {
                    if (stray.Song < screen.Start || stray.Song > screen.End)
                        continue;
                    double along = screen.AlongOf(stray.Song);
                    if (stray.Kind == JudgeKind.Overstrum)
                        context.DrawLine(StrayPen, At(along, screen.LaneFrom + 2), At(along, screen.LaneFrom + (screen.LaneSize * slots.Slots.Count) - 2));
                    else if (MarkRect(screen, slots, ShownOf(stray.Lane, drums), along, size) is { } rect)
                        DrawCross(context, StrayPen, rect.Deflate(rect.Width * 0.15));
                }
            }

            DrawTimingMeter(context, session, run, screen, lanes);
        }

        /// <summary>
        /// How brightly each column is lit as the playtest hits its notes, in place of autoplay's glow: lit only by the notes
        /// played, as they're played.
        /// </summary>
        private double[] PlaytestHitsAt(PlaytestRun run, ChartTrackKey key, LaneSlots slots, double now)
        {
            int count = slots.Slots.Count;
            if (_hits.Length < count)
                _hits = new double[count];
            var hits = _hits;
            Array.Clear(hits, 0, count);

            bool drums = ChartInstruments.KindOf(key.Instrument) == ChartInstrumentKind.Drums;
            var recent = run.Recent;
            for (int i = recent.Count - 1; i >= 0; i--)
            {
                var judged = recent[i];
                if (judged.Kind != JudgeKind.Hit || judged.Note < 0 || judged.Note >= run.Chart.Notes.Count)
                    continue;
                var note = run.Chart.Notes[judged.Note];
                double since = now - judged.Song;
                if (since < 0)
                    continue;
                double glow = StrikeGlow.Brightness(since, Math.Max(0, note.EndTime - judged.Song));
                if (glow <= 0)
                    continue;
                foreach (int lane in note.Lanes)
                {
                    int shown = ShownOf(lane, drums);
                    if (slots.Across == shown)
                    {
                        for (int c = 0; c < count; c++)
                            hits[c] = Math.Max(hits[c], glow * StrikeGlow.SpreadLevel);
                    }
                    else if (slots.IndexOf(shown) is var column and >= 0)
                    {
                        hits[column] = Math.Max(hits[column], glow);
                    }
                }
            }

            return hits;
        }

        /// <summary>
        /// The frets and pads pressed, on their targets at the strike line, as the games show theirs: a fret while it's held,
        /// a pad or the kick as it's hit, and the strum across the lanes.
        /// </summary>
        private void DrawPresses(DrawingContext context, EditorSession session, ChartTrackKey key, LaneProjection projection, LaneSlots slots)
        {
            var playtest = _editor!.Playtest;
            var kind = ChartInstruments.KindOf(key.Instrument);
            bool drums = kind == ChartInstrumentKind.Drums;
            int laneCount = drums ? slots.Slots.Count - (slots.Across is null ? 1 : 0) : 5;
            double now = session.Viewport.Cursor;
            double strike = projection.AlongOf(now);
            double size = GemSize(projection);
            for (int column = 0; column < slots.Slots.Count; column++)
            {
                int shown = slots.Slots[column];
                double pressed = playtest.PressOf(GameLaneOf(shown, drums), now);
                if (pressed <= 0)
                    continue;
                var color = ColumnColor(kind, drums, laneCount, shown, column);
                var rect = GemRect(projection, column, strike, size).Inflate(2);
                double radius = GemPainter.CornerOf(rect);
                using (context.PushOpacity(pressed))
                    context.DrawRectangle(_gems.Brush(HighwayPalette.WithAlpha(color, 0x70)), _gems.Pen(HighwayPalette.Lighten(color, 0.55), 2.5), rect, radius, radius);
            }

            // The kick, or the strum, across a highway's lanes.
            if (slots.Across is { } across && playtest.PressOf(GameLaneOf(across, drums), now) is var bar and > 0)
            {
                var color = drums ? HighwayPalette.Kick : HighwayPalette.Open;
                var rect = AcrossBar(projection, slots, strike, size);
                using (context.PushOpacity(bar * 0.8))
                    context.DrawRectangle(_gems.Brush(HighwayPalette.WithAlpha(color, 0xB0)), null, rect, rect.Height / 2, rect.Height / 2);
            }
        }

        /// <summary>A ring bursting out of each target a note was just hit on, brighter than the column's glow.</summary>
        private void DrawHitBursts(DrawingContext context, EditorSession session, ChartTrackKey key, PlaytestRun run, LaneProjection projection, LaneSlots slots)
        {
            var kind = ChartInstruments.KindOf(key.Instrument);
            bool drums = kind == ChartInstrumentKind.Drums;
            int laneCount = drums ? slots.Slots.Count - (slots.Across is null ? 1 : 0) : 5;
            double now = session.Viewport.Cursor;
            double strike = projection.AlongOf(now);
            double size = GemSize(projection);
            var recent = run.Recent;
            for (int i = recent.Count - 1; i >= 0; i--)
            {
                var judged = recent[i];
                double since = now - judged.Song;
                if (since > BurstSeconds)
                    break;
                if (judged.Kind != JudgeKind.Hit || since < 0 || judged.Note < 0 || judged.Note >= run.Chart.Notes.Count)
                    continue;
                double grown = since / BurstSeconds;
                foreach (int lane in run.Chart.Notes[judged.Note].Lanes)
                {
                    int shown = ShownOf(lane, drums);
                    Rect rect;
                    Color color;
                    if (slots.Across == shown)
                    {
                        rect = AcrossBar(projection, slots, strike, size);
                        color = drums ? HighwayPalette.Kick : HighwayPalette.Open;
                    }
                    else if (slots.IndexOf(shown) is var column and >= 0)
                    {
                        rect = GemRect(projection, column, strike, size);
                        color = ColumnColor(kind, drums, laneCount, shown, column);
                    }
                    else
                    {
                        continue;
                    }

                    var ring = rect.Inflate(size * 0.7 * grown);
                    double radius = GemPainter.CornerOf(ring);
                    var fill = HighwayPalette.WithAlpha(HighwayPalette.Lighten(color, 0.8), (byte)(0x90 * (1 - grown)));
                    using (context.PushOpacity(1 - grown))
                        context.DrawRectangle(_gems.Brush(fill), _gems.Pen(HighwayPalette.Lighten(color, 0.6), (3 * (1 - grown)) + 1), ring, radius, radius);
                }
            }
        }

        // How long a hit's burst lasts, in seconds of the song.
        private const double BurstSeconds = 0.18;

        // The bar an open note or the kick is drawn as across a highway's lanes, at the strike line.
        private Rect AcrossBar(LaneProjection projection, LaneSlots slots, double strike, double size)
        {
            double thickness = Math.Max(4, size * 0.45);
            return Box(strike - (thickness / 2), strike + (thickness / 2), projection.LaneFrom + 4, projection.LaneFrom + (projection.LaneSize * slots.Slots.Count) - 4);
        }

        // The colour a column lights in: the timeline's row for open notes or the kick in theirs, the rest by their lane.
        private static Color ColumnColor(ChartInstrumentKind kind, bool drums, int laneCount, int shown, int column) =>
            shown == (drums ? 0 : ChartNote.OpenLane)
                ? (drums ? HighwayPalette.Kick : HighwayPalette.Open)
                : HighwayPalette.HitColor(kind, laneCount, column);

        // A lane as the editor numbers it, as the game view does: a drum pad's from red at 0, and the kick at -1.
        private static int GameLaneOf(int shown, bool drums) => drums ? shown - 1 : shown;

        // A game view lane as the editor numbers and draws it: a drum pad's from the kick at 0 (the game view's disco flip
        // already gives the pad hit, which is the column drawn).
        private static int ShownOf(int lane, bool drums) => drums ? lane + 1 : lane;

        // Where a mark goes for a lane at a point along the time axis: on the gem, or across the lanes' middle for an open
        // note or the kick drawn across them; null for a lane not drawn.
        private Rect? MarkRect(LaneProjection projection, LaneSlots slots, int shown, double along, double size)
        {
            if (slots.Across == shown)
            {
                double middle = projection.LaneFrom + (projection.LaneSize * slots.Slots.Count / 2);
                return Box(along - (size / 2), along + (size / 2), middle - (size / 2), middle + (size / 2));
            }

            int column = slots.IndexOf(shown);
            return column < 0 ? null : GemRect(projection, column, along, size);
        }

        private static void DrawCross(DrawingContext context, IPen pen, Rect rect)
        {
            double side = Math.Min(rect.Width, rect.Height);
            var box = new Rect(rect.Center.X - (side / 2), rect.Center.Y - (side / 2), side, side).Deflate(1);
            context.DrawLine(pen, box.TopLeft, box.BottomRight);
            context.DrawLine(pen, box.TopRight, box.BottomLeft);
        }

        /// <summary>
        /// A meter just past the strike line, across the middle half of the lanes: early to the left (or up, along a timeline)
        /// and late to the right, the hit window's width, each of the last hits a tick fading as the song goes on.
        /// </summary>
        private void DrawTimingMeter(DrawingContext context, EditorSession session, PlaytestRun run, LaneProjection screen, Rect lanes)
        {
            if (_editor?.Playtest.Window is not { } window)
                return;
            double now = session.Viewport.Cursor;
            double strike = screen.AlongOf(now);
            double past = Math.Sign(screen.AlongOf(now - 1) - strike);
            double along = strike + (past * (Math.Max(3, GemSize(screen) * 0.6) + 7));
            double across1 = View == LaneView.Highway ? lanes.Left : lanes.Top;
            double across2 = View == LaneView.Highway ? lanes.Right : lanes.Bottom;
            double middle = (across1 + across2) / 2;
            double half = (across2 - across1) * 0.25;
            context.DrawLine(MeterPen, At(along, middle - half), At(along, middle + half));
            context.DrawLine(MeterCentrePen, At(along - 4, middle), At(along + 4, middle));

            var recent = run.Recent;
            for (int i = recent.Count - 1; i >= 0; i--)
            {
                var judged = recent[i];
                double age = now - judged.Song;
                if (age > MeterSeconds)
                    break;
                if (judged.Kind != JudgeKind.Hit || age < 0)
                    continue;
                double reach = judged.OffsetMs < 0 ? window.EarlyMs : window.LateMs;
                double at = middle + (Math.Clamp(judged.OffsetMs / reach, -1, 1) * half);
                var color = judged.OffsetMs < 0 ? EarlyColor : LateColor;
                using (context.PushOpacity(1 - (age / MeterSeconds)))
                    context.DrawLine(_gems.Pen(color, 2), At(along - 3, at), At(along + 3, at));
            }
        }
    }
}
