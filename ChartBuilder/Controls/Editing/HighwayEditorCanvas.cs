using System;
using Avalonia;
using ChartPacker.Core.Charting.Editing;

namespace ChartBuilder.Controls.Editing
{
    /// <summary>
    /// The highway view of a lane (charter-spec C3 item 1): time runs upwards, as in the games, with the waveform behind
    /// the lanes, measure numbers and tempo markers in a rail on the left, and chords' forcing marks on the right. Its
    /// columns are no wider than <see cref="MaxLaneSize"/>, so a wide lane doesn't stretch the highway across it; the
    /// highway is centred in it instead.
    /// </summary>
    public sealed class HighwayEditorCanvas : EditorCanvas
    {
        // The rail on the left, for measure numbers and tempo markers, and the margin on the right, for forcing marks.
        private const double RailWidth = 56;
        private const double MarkWidth = 18;

        // The widest a column gets, in pixels.
        private const double MaxLaneSize = 64;

        protected override LaneView View => LaneView.Highway;

        protected override LaneProjection Project(Size size, LaneSlots slots, EditorViewport viewport, double lookAhead)
        {
            int count = slots.Slots.Count;
            double laneSize = Math.Min(MaxLaneSize, Math.Max(1, size.Width - RailWidth - MarkWidth) / count);
            double left = Math.Max(0, (size.Width - (RailWidth + (laneSize * count) + MarkWidth)) / 2);
            return LaneProjection.Of(LaneView.Highway, 0, Math.Max(1, size.Height), left + RailWidth, laneSize, slots, viewport, lookAhead);
        }

        protected override Rect LabelsArea(Size size, LaneProjection projection) => new(projection.LaneFrom - RailWidth, 0, RailWidth, size.Height);

        protected override Rect WaveformArea(Size size, LaneProjection projection) =>
            new(projection.LaneFrom + 4, 0, Math.Max(0, (projection.LaneSize * projection.Slots.Slots.Count) - 8), size.Height);
    }
}
