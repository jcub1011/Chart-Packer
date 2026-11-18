using System;
using System.Globalization;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using ChartBuilder.Resources;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charting.Layout;
using ChartShared.UI.Controls.Highway;
using ChartShared.UI.Controls.Skia;

namespace ChartBuilder.Controls.Editing
{
    // The Vocal Alignment tool in a vocal part's lane (charter-spec C3 item 7): while it's picked, every note of a part it
    // aligns is outlined in the accent, the stretch from the first note to the last note's end is shaded, with a handle
    // either end to stretch it by, and the offset and speed are shown over it.
    public abstract partial class EditorCanvas
    {
        // A handle: the line on the span's edge, and the grip outside it, how wide and how tall.
        private const double AlignEdgeWidth = 2;
        private const double AlignGripWidth = 9;
        private const double AlignGripHeight = 30;

        private static readonly IPen AlignGripLinePen = new Avalonia.Media.Immutable.ImmutablePen(Color.FromArgb(0xB0, 0x10, 0x12, 0x18).ToUInt32(), 1);

        /// <summary>Whether the Vocal Alignment tool is picked and aligns <paramref name="vocal"/>'s part.</summary>
        private static bool IsAligning(EditorSession session, VocalTrack vocal) =>
            session.Tool == EditorTool.VocalAlign && session.VocalAlignment.Aligns(vocal);

        /// <summary>
        /// The span from the first note to the last note's end, shaded, its handles, brighter where the pointer would take
        /// hold of one, with the span outlined where it would take hold of the notes; and the offset and speed over it.
        /// </summary>
        private void DrawVocalAlignment(IHighwayCanvas context, EditorSession session, VocalTrack vocal, LaneProjection screen)
        {
            if (!IsAligning(session, vocal) || session.VocalAlignment.Span is not { } span)
                return;

            var rows = RowsOf(screen);
            double start = screen.AlongOf(span.Start), end = Math.Max(screen.AlongOf(span.End), start + 1);
            double top = rows.Top, bottom = rows.Bottom;
            var band = Box(start, end, top, bottom);
            context.FillRectangle(AccentFill(0x16), band);

            // What the pointer would take hold of, or has.
            VocalAlignGrip? grip = null;
            if (_hover is { } hover && VocalPointAt(hover, clamp: _isPressed) is { } point)
                grip = session.Tools.VocalAlignGripAt(point);
            if (grip == VocalAlignGrip.Body)
                context.DrawRectangle(null, AccentLine(0xA0, 1.5), band.Deflate(0.75), 2, 2);

            double middle = (top + bottom) / 2;
            DrawAlignHandle(context, start, middle, top, bottom, before: true, grip == VocalAlignGrip.Start);
            DrawAlignHandle(context, end, middle, top, bottom, before: false, grip == VocalAlignGrip.End);

            // The offset and speed, at the span's start, or the screen's when the start is scrolled out of view.
            var alignment = session.VocalAlignment;
            string text = string.Format(
                CultureInfo.CurrentCulture,
                Strings.VocalAlignReadoutFormat,
                alignment.Offset.ToString("+0.000;-0.000;0.000", CultureInfo.CurrentCulture),
                (alignment.Speed * 100).ToString("0.###", CultureInfo.CurrentCulture));
            var label = Label(text, 11, DefaultLabelBrush);
            double left = Math.Max(start + AlignGripWidth, screen.TimeFrom) + 6;
            if (left + label.Width + 12 > end && end - start > label.Width + 24)
                left = end - label.Width - 18;
            var badge = new Rect(left, top + 4, label.Width + 12, label.Height + 6);
            context.DrawRectangle(BadgeBrush, AccentLine(0x90), badge, 4, 4);
            context.DrawText(label, new Point(badge.Left + 6, badge.Top + 3));
        }

        // A handle: a line on the edge, and a grip outside the span, before its start or after its end.
        private void DrawAlignHandle(IHighwayCanvas context, double edge, double middle, double top, double bottom, bool before, bool hot)
        {
            byte alpha = hot ? (byte)0xFF : (byte)0xC0;
            double width = hot ? AlignEdgeWidth + 1 : AlignEdgeWidth;
            context.FillRectangle(AccentFill(alpha), Box(edge - (width / 2), edge + (width / 2), top, bottom));

            double height = Math.Min(AlignGripHeight, bottom - top);
            double from = before ? edge - AlignGripWidth : edge, to = before ? edge : edge + AlignGripWidth;
            var grip = Box(from, to, middle - (height / 2), middle + (height / 2));
            context.DrawRectangle(AccentFill(alpha), null, grip, 3, 3);

            // Two short lines on the grip, as a handle has.
            double x1 = from + (AlignGripWidth / 2) - 1.5, x2 = from + (AlignGripWidth / 2) + 1.5;
            context.DrawLine(AlignGripLinePen, At(x1, middle - 6), At(x1, middle + 6));
            context.DrawLine(AlignGripLinePen, At(x2, middle - 6), At(x2, middle + 6));
        }

        /// <summary>
        /// The cursor and tip over a vocal lane with the Vocal Alignment tool: a sideways arrow over a handle, and the move
        /// cursor over the notes. Whether it showed one.
        /// </summary>
        private bool ShowVocalAlignCursor(EditorSession session, Point position)
        {
            if (session.Tool != EditorTool.VocalAlign || VocalPointAt(position, clamp: false) is not { } point
                || session.Tools.VocalAlignGripAt(point) is not { } grip)
            {
                return false;
            }

            SetCursor(grip == VocalAlignGrip.Body ? StandardCursorType.SizeAll : StandardCursorType.SizeWestEast);
            ShowTip(grip switch
            {
                VocalAlignGrip.Start => Strings.VocalAlignStretchStartToolTip,
                VocalAlignGrip.End => Strings.VocalAlignStretchEndToolTip,
                _ => Strings.VocalAlignMoveToolTip,
            });
            return true;
        }
    }
}
