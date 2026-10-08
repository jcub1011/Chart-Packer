using System;
using Avalonia;
using ChartPacker.Core.Charting.Editing;

namespace ChartBuilder.Controls.Editing
{
    /// <summary>
    /// The timeline view of a lane (charter-spec C3 item 1), in the style of a DAW: time runs left to right and each note
    /// lane is a row, under a ruler of measure numbers and tempo markers, a strip of the waveform and the phrase rail (C8).
    /// It suits long-range editing and lining notes up with the audio.
    /// </summary>
    public sealed class TimelineEditorCanvas : EditorCanvas
    {
        // The ruler along the top, then the waveform's strip, then the phrase rail, then the forcing marks just above the rows.
        private const double RulerHeight = 18;
        private const double MaxStripHeight = 48;
        private const double MarkHeight = 14;

        protected override LaneView View => LaneView.Timeline;

        protected override LaneProjection Project(Size size, LaneSlots slots, EditorViewport viewport, double lookAhead, int phraseRows)
        {
            double top = RulerHeight + StripHeight(size) + PhraseRailSize(phraseRows) + MarkHeight;
            double height = Math.Max(1, size.Height - top - 4);
            return LaneProjection.Of(LaneView.Timeline, 0, Math.Max(1, size.Width), top, height / slots.Slots.Count, slots, viewport, lookAhead);
        }

        protected override Rect LabelsArea(Size size, LaneProjection projection) => new(0, 0, size.Width, RulerHeight);

        protected override Rect WaveformArea(Size size, LaneProjection projection) => new(0, RulerHeight, size.Width, StripHeight(size));

        // Above the forcing marks.
        protected override double PhraseRailFrom(LaneProjection projection, int phraseRows) => projection.LaneFrom - MarkHeight - PhraseRailSize(phraseRows);

        // A fifth of the lane's height, so the rows keep most of it.
        private static double StripHeight(Size size) => Math.Clamp((size.Height - RulerHeight) * 0.2, 0, MaxStripHeight);
    }
}
