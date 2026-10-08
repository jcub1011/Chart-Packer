using System;
using Avalonia;
using ChartPacker.Core.Charting.Editing;

namespace ChartBuilder.Controls.Editing
{
    /// <summary>
    /// The timeline view of a lane (charter-spec C3 item 1), in the style of a DAW: time runs left to right and each note
    /// lane is a row, under a ruler of measure numbers, tempo markers and section names, a strip of the waveform or
    /// spectrogram (C12, C13) and the phrase rail (C8), with the onsets in a strip under the rows (C14). It suits
    /// long-range editing and lining notes up with the audio.
    /// </summary>
    public sealed class TimelineEditorCanvas : EditorCanvas
    {
        // The ruler along the top, then the audio's strip, then the phrase rail, then the forcing marks just above the rows.
        private const double RulerHeight = 18;
        private const double MaxStripHeight = 48;
        private const double MarkHeight = 14;

        protected override LaneView View => LaneView.Timeline;

        // The strip is the timeline's column beside the rows: it's always there, and takes no more room when it's chosen.
        protected override double BesideSizeFor(Size size) => 0;

        protected override LaneProjection Project(Size size, LaneSlots slots, EditorViewport viewport, double lookAhead, int phraseRows)
        {
            double top = RulerHeight + StripHeight(size) + PhraseRailSize(phraseRows) + MarkHeight;
            double height = Math.Max(1, size.Height - top - 4 - OnsetStripSize);
            return LaneProjection.Of(LaneView.Timeline, 0, Math.Max(1, size.Width), top, height / slots.Slots.Count, slots, viewport, lookAhead);
        }

        protected override Rect LabelsArea(Size size, LaneProjection projection) => new(0, 0, size.Width, RulerHeight);

        protected override Rect BehindArea(Size size, LaneProjection projection) =>
            new(0, projection.LaneFrom, size.Width, projection.LaneSize * projection.Slots.Slots.Count);

        protected override Rect StripArea(Size size, LaneProjection projection) => new(0, RulerHeight, size.Width, StripHeight(size));

        protected override Rect BesideArea(Size size, LaneProjection projection, int phraseRows) => StripArea(size, projection);

        protected override Rect OnsetArea(Size size, LaneProjection projection) =>
            new(0, projection.LaneFrom + (projection.LaneSize * projection.Slots.Slots.Count) + 2, size.Width, OnsetStripSize);

        // Above the forcing marks.
        protected override double PhraseRailFrom(LaneProjection projection, int phraseRows) => projection.LaneFrom - MarkHeight - PhraseRailSize(phraseRows);

        // A fifth of the lane's height, so the rows keep most of it.
        private static double StripHeight(Size size) => Math.Clamp((size.Height - RulerHeight) * 0.2, 0, MaxStripHeight);
    }
}
