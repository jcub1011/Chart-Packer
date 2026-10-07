using System;
using Avalonia;
using ChartPacker.Core.Charting.Editing;

namespace ChartBuilder.Controls.Editing
{
    /// <summary>
    /// The highway view of a lane (charter-spec C3 item 1): time runs upwards, as in the games, with the waveform behind
    /// the lanes, measure numbers and tempo markers in a rail on the left, and chords' forcing marks on the right.
    /// </summary>
    public sealed class HighwayEditorCanvas : EditorCanvas
    {
        // The rail on the left, for measure numbers and tempo markers, and the margin on the right, for forcing marks.
        private const double RailWidth = 56;
        private const double MarkWidth = 18;

        protected override LaneView View => LaneView.Highway;

        protected override LaneProjection Project(Size size, LaneSlots slots, EditorViewport viewport, double lookAhead)
        {
            double width = Math.Max(1, size.Width - RailWidth - MarkWidth);
            return LaneProjection.Of(LaneView.Highway, 0, Math.Max(1, size.Height), RailWidth, width / slots.Slots.Count, slots, viewport, lookAhead);
        }

        protected override Rect LabelsArea(Size size, LaneProjection projection) => new(0, 0, RailWidth, size.Height);

        protected override Rect WaveformArea(Size size, LaneProjection projection) =>
            new(projection.LaneFrom + 4, 0, Math.Max(0, (projection.LaneSize * projection.Slots.Slots.Count) - 8), size.Height);
    }
}
