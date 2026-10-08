using System;
using Avalonia;
using ChartPacker.Core.Charting.Editing;

namespace ChartBuilder.Controls.Editing
{
    /// <summary>
    /// The highway view of a lane (charter-spec C3 item 1): time runs upwards, as in the games, with the waveform or
    /// spectrogram behind the lanes or in a column beside them (C12, C13), measure numbers, tempo markers and section
    /// names in a rail on the left with the onsets beside it (C14), chords' forcing marks on the right, and the phrase
    /// rail beyond them (C8). Its columns are no wider than <see cref="MaxLaneSize"/>, so a wide lane doesn't stretch
    /// the highway across it; the highway is centred in it instead.
    /// </summary>
    public sealed class HighwayEditorCanvas : EditorCanvas
    {
        // The rail on the left, for measure numbers, tempo markers and section names, and the margin on the right, for
        // forcing marks.
        private const double RailWidth = 56;
        private const double MarkWidth = 18;

        // The widest a column gets, in pixels.
        private const double MaxLaneSize = 64;

        protected override LaneView View => LaneView.Highway;

        protected override double BesideSizeFor(Size size) => Math.Clamp(size.Width * 0.2, 48, 140);

        protected override LaneProjection Project(Size size, LaneSlots slots, EditorViewport viewport, double lookAhead, int phraseRows)
        {
            int count = slots.Slots.Count;
            double phrases = PhraseRailSize(phraseRows);
            double onsets = OnsetStripSize;
            double fixedWidth = RailWidth + onsets + MarkWidth + phrases + Extras.BesideSize;
            double laneSize = Math.Min(MaxLaneSize, Math.Max(1, size.Width - fixedWidth) / count);
            double left = Math.Max(0, (size.Width - (fixedWidth + (laneSize * count))) / 2);
            return LaneProjection.Of(LaneView.Highway, 0, Math.Max(1, size.Height), left + RailWidth + onsets, laneSize, slots, viewport, lookAhead);
        }

        protected override Rect LabelsArea(Size size, LaneProjection projection) =>
            new(projection.LaneFrom - OnsetStripSize - RailWidth, 0, RailWidth, size.Height);

        protected override Rect BehindArea(Size size, LaneProjection projection) =>
            new(projection.LaneFrom + 4, 0, Math.Max(0, LanesWidth(projection) - 8), size.Height);

        // Highways have no strip: the column beside them is their full-strength backdrop.
        protected override Rect StripArea(Size size, LaneProjection projection) => default;

        protected override Rect BesideArea(Size size, LaneProjection projection, int phraseRows) =>
            new(PhraseRailFrom(projection, phraseRows) + PhraseRailSize(phraseRows) + 2, 0, Math.Max(0, Extras.BesideSize - 4), size.Height);

        protected override Rect OnsetArea(Size size, LaneProjection projection) =>
            new(projection.LaneFrom - OnsetStripSize, 0, OnsetStripSize, size.Height);

        // Right of the forcing marks.
        protected override double PhraseRailFrom(LaneProjection projection, int phraseRows) =>
            projection.LaneFrom + LanesWidth(projection) + MarkWidth;

        private static double LanesWidth(LaneProjection projection) => projection.LaneSize * projection.Slots.Slots.Count;
    }
}
