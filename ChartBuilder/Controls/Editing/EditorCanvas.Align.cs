using System;
using System.Globalization;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using ChartBuilder.Resources;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charting.Layout;
using ChartShared.Core.Charts;
using ChartShared.UI.Controls.Highway;
using ChartShared.UI.Controls.Skia;

namespace ChartBuilder.Controls.Editing
{
    // The Alignment tool in a lane (charter-spec C3 item 7): while it's picked, every note of a part or track it aligns is
    // outlined in the accent, the stretch from the first note to the last note's end is shaded, with a handle either end
    // to stretch it by, and the offset and speed are shown over it. A vocal lane and an instrument's draw it alike.
    public abstract partial class EditorCanvas
    {
        // A handle: the line on the span's edge, and the grip outside it, how wide and how tall.
        private const double AlignEdgeWidth = 2;
        private const double AlignGripWidth = 9;
        private const double AlignGripHeight = 30;

        private static readonly IPen AlignGripLinePen = new Avalonia.Media.Immutable.ImmutablePen(Color.FromArgb(0xB0, 0x10, 0x12, 0x18).ToUInt32(), 1);

        /// <summary>Whether the Alignment tool is picked and aligns <paramref name="vocal"/>'s part.</summary>
        private static bool IsAligning(EditorSession session, VocalTrack vocal) =>
            session.Tool == EditorTool.Align && session.Alignment.Aligns(vocal);

        /// <summary>Whether the Alignment tool is picked and aligns <paramref name="key"/>'s track.</summary>
        private static bool IsAligning(EditorSession session, ChartTrackKey key) =>
            session.Tool == EditorTool.Align && session.Alignment.Aligns(key);

        /// <summary>Whether the Alignment tool aligns this lane's part or track, so its span is drawn over it.</summary>
        private bool IsAligningLane =>
            Session is { } session && _lane is { } lane
            && (lane.Vocal is { } vocal ? IsAligning(session, vocal) : lane.Track is { } key && IsAligning(session, key));

        /// <summary>The Alignment tool's span over a vocal part's rows, when it aligns the part.</summary>
        private void DrawVocalAlignment(IHighwayCanvas context, EditorSession session, VocalTrack vocal, LaneProjection screen)
        {
            if (!IsAligning(session, vocal))
                return;
            AlignGrip? grip = null;
            if (_hover is { } hover && VocalPointAt(hover, clamp: _isPressed) is { } point)
                grip = session.Tools.AlignGripAt(point);
            DrawAlignment(context, session, screen, screen.Slots.Slots.Count, grip);
        }

        /// <summary>The Alignment tool's span over an instrument's lanes, when it aligns the track.</summary>
        private void DrawTrackAlignment(IHighwayCanvas context, EditorSession session, ChartTrackKey key, LaneProjection screen, LaneSlots slots)
        {
            if (!IsAligning(session, key))
                return;
            AlignGrip? grip = null;
            if (_hover is { } hover && TrackAlignPointAt(hover) is { } point)
                grip = session.Tools.AlignGripAt(point);
            DrawAlignment(context, session, screen, slots.Slots.Count, grip);
        }

        /// <summary>
        /// The span from the first note to the last note's end, shaded across <paramref name="slots"/> lanes or rows, its
        /// handles, brighter where the pointer would take hold of one (<paramref name="grip"/>), with the span outlined
        /// where it would take hold of the notes; and the offset and speed over it, standing up beside a tilted lane
        /// instead (<see cref="DrawStandingAlignReadout"/>). Time may run either way along the lane.
        /// </summary>
        /// <remarks>
        /// Only what's on the screen, and a grip's width past its ends, is drawn: the span may cover the whole song, and a
        /// tilted lane's plane can't be drawn far past its far end, where the tilt sends it past the horizon.
        /// </remarks>
        private void DrawAlignment(IHighwayCanvas context, EditorSession session, LaneProjection screen, int slots, AlignGrip? grip)
        {
            if (AlignEdgesOnScreen(session, screen) is not var (start, end, forward, from, to))
                return;

            double top = screen.LaneFrom, bottom = screen.LaneFrom + (screen.LaneSize * slots);
            var band = Box(Math.Clamp(start, from, to), Math.Clamp(end, from, to), top, bottom);
            context.FillRectangle(AccentFill(0x16), band);
            if (grip == AlignGrip.Body)
                context.DrawRectangle(null, AccentLine(0xA0, 1.5), band.Deflate(0.75), 2, 2);

            double middle = (top + bottom) / 2;
            if (start >= from && start <= to)
                DrawAlignHandle(context, start, middle, top, bottom, -forward, grip == AlignGrip.Start);
            if (end >= from && end <= to)
                DrawAlignHandle(context, end, middle, top, bottom, forward, grip == AlignGrip.End);

            if (_camera is not null)
                return;
            var (label, lead, length) = AlignReadout(session, screen, start, end, forward);
            double width = View == LaneView.Highway ? label.Width + 12 : label.Height + 6;
            var badge = Box(lead, lead + (forward * length), top + 4, top + 4 + width);
            context.DrawRectangle(BadgeBrush, AccentLine(0x90), badge, 4, 4);
            context.DrawText(label, new Point(badge.Left + 6, badge.Top + 3));
        }

        /// <summary>The offset and speed beside a tilted lane the Alignment tool aligns, standing up to be read, as its other labels are.</summary>
        private void DrawStandingAlignReadout(IHighwayCanvas context, EditorSession session, ChartTrackKey key, LaneProjection screen)
        {
            if (!IsAligning(session, key) || AlignEdgesOnScreen(session, screen) is not var (start, end, forward, _, _))
                return;
            var (label, lead, _) = AlignReadout(session, screen, start, end, forward);
            DrawStandingLabel(context, label, At(lead, screen.LaneFrom + 3), rightAligned: false, raise: 2, badge: BadgeBrush);
        }

        /// <summary>
        /// Where the span's start and end are along the lane, which way later times lie (1 or −1), and how far along it's
        /// drawn (the screen and a grip's width past it); null with nothing aligned, or none of it on the screen.
        /// </summary>
        private static (double Start, double End, double Forward, double From, double To)? AlignEdgesOnScreen(EditorSession session, LaneProjection screen)
        {
            if (session.Alignment.Span is not { } span)
                return null;

            // Rightward on a timeline, upward on a highway.
            double forward = screen.AlongOf(span.Start + 1) >= screen.AlongOf(span.Start) ? 1 : -1;
            double start = screen.AlongOf(span.Start), end = screen.AlongOf(span.End);
            if ((end - start) * forward < 1)
                end = start + forward;

            double from = screen.TimeFrom - AlignGripWidth - 2, to = screen.TimeFrom + screen.TimeLength + AlignGripWidth + 2;
            if (Math.Max(start, end) < from || Math.Min(start, end) > to)
                return null;
            return (start, end, forward, from, to);
        }

        /// <summary>
        /// The offset and speed's label, where along the lane its badge starts, and how long it is along it: just after the
        /// span's start, or the screen's when the start is scrolled out of view, and before the span's end rather than past it.
        /// </summary>
        private (TextLayout Label, double Lead, double Length) AlignReadout(EditorSession session, LaneProjection screen, double start, double end, double forward)
        {
            var alignment = session.Alignment;
            string text = string.Format(
                CultureInfo.CurrentCulture,
                Strings.AlignReadoutFormat,
                alignment.Offset.ToString("+0.000;-0.000;0.000", CultureInfo.CurrentCulture),
                (alignment.Speed * 100).ToString("0.###", CultureInfo.CurrentCulture));
            var label = Label(text, 11, DefaultLabelBrush);
            double length = View == LaneView.Highway ? label.Height + 6 : label.Width + 12;
            double screenStart = forward > 0 ? screen.TimeFrom : screen.TimeFrom + screen.TimeLength;
            double lead = start + (forward * AlignGripWidth);
            if ((lead - screenStart) * forward < 0)
                lead = screenStart;
            lead += forward * 6;
            if ((lead + (forward * length) - end) * forward > -6 && Math.Abs(end - start) > length + 24)
                lead = end - (forward * (length + 6));
            return (label, lead, length);
        }

        // A handle: a line on the edge, and a grip outside the span, before its start or after its end (outward).
        private void DrawAlignHandle(IHighwayCanvas context, double edge, double middle, double top, double bottom, double outward, bool hot)
        {
            byte alpha = hot ? (byte)0xFF : (byte)0xC0;
            double width = hot ? AlignEdgeWidth + 1 : AlignEdgeWidth;
            context.FillRectangle(AccentFill(alpha), Box(edge - (width / 2), edge + (width / 2), top, bottom));

            double height = Math.Min(AlignGripHeight, bottom - top);
            var grip = Box(edge, edge + (outward * AlignGripWidth), middle - (height / 2), middle + (height / 2));
            context.DrawRectangle(AccentFill(alpha), null, grip, 3, 3);

            // Two short lines on the grip, as a handle has.
            double centre = edge + (outward * AlignGripWidth / 2);
            context.DrawLine(AlignGripLinePen, At(centre - 1.5, middle - 6), At(centre - 1.5, middle + 6));
            context.DrawLine(AlignGripLinePen, At(centre + 1.5, middle - 6), At(centre + 1.5, middle + 6));
        }

        /// <summary>The cursor and tip over a vocal lane with the Alignment tool. Whether it showed one.</summary>
        private bool ShowVocalAlignCursor(EditorSession session, Point position) =>
            session.Tool == EditorTool.Align && VocalPointAt(position, clamp: false) is { } point && ShowAlignCursor(session.Tools.AlignGripAt(point));

        /// <summary>The cursor and tip over an instrument's lane with the Alignment tool. Whether it showed one.</summary>
        private bool ShowTrackAlignCursor(Point position) =>
            Session is { Tool: EditorTool.Align } session && TrackAlignPointAt(position) is { } point && ShowAlignCursor(session.Tools.AlignGripAt(point));

        /// <summary>
        /// Where in an instrument's lane the pointer at <paramref name="position"/> would press, or is dragging: a gem
        /// standing up on a perspective highway taken at its floor, as a press takes it, so the grip shown is the grip taken.
        /// </summary>
        private ChartPoint? TrackAlignPointAt(Point position) =>
            _isPressed ? PointAt(position + _pressOffset, onGems: false) : PointAt(OnGem(position), onGems: false);

        // A sideways arrow over a handle, and the move cursor over the notes, with a tip saying what a drag does.
        private bool ShowAlignCursor(AlignGrip? grip)
        {
            if (grip is not { } held)
                return false;
            SetCursor(held == AlignGrip.Body ? StandardCursorType.SizeAll : View == LaneView.Highway ? StandardCursorType.SizeNorthSouth : StandardCursorType.SizeWestEast);
            ShowTip(held switch
            {
                AlignGrip.Start => Strings.AlignStretchStartToolTip,
                AlignGrip.End => Strings.AlignStretchEndToolTip,
                _ => Strings.AlignMoveToolTip,
            });
            return true;
        }
    }
}
