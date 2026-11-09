using System;
using Avalonia;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Editing;

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

        // The widest a column gets, in pixels, on a flat highway.
        private const double MaxLaneSize = 64;

        // The widest a perspective highway's columns get together, in pixels of the plane: as wide as the room allows, to this, for
        // the plane narrows towards its far end and the notes are best large near the strike line.
        private const double MaxPerspectiveWidth = 480;

        protected override LaneView View => LaneView.Highway;

        // The projection last worked out, and what it was for: every layer of the lane, the notes standing up and the pointer ask for
        // it each frame, and a lane laid out the same with the cursor in the same place is projected the same.
        private (Size Size, LaneSlots Slots, EditorViewport Viewport, double Cursor, HighwayLength Length, int PhraseRows, LaneExtras Extras, bool Tilted) _projectedFor;
        private LaneProjection? _projected;

        protected override double BesideSizeFor(Size size) => Math.Clamp(size.Width * 0.2, 48, 140);

        protected override double TimeAxisLength(Size size) => Math.Max(1, size.Height);

        protected override LaneProjection Project(Size size, LaneSlots slots, EditorViewport viewport, HighwayLength length, int phraseRows)
        {
            var key = (size, slots, viewport, viewport.Cursor, length, phraseRows, Extras, Camera is not null);
            if (_projected is { } known && key.Equals(_projectedFor))
                return known;

            _projectedFor = key;
            return _projected = ProjectNow(size, slots, viewport, length, phraseRows);
        }

        private LaneProjection ProjectNow(Size size, LaneSlots slots, EditorViewport viewport, HighwayLength length, int phraseRows)
        {
            int count = slots.Slots.Count;
            double phrases = PhraseRailSize(phraseRows);
            double onsets = OnsetStripSize;
            double fixedWidth = RailWidth + onsets + MarkWidth + phrases + Extras.BesideSize;
            double laneSize = LaneSizeFor(size.Width, fixedWidth, count, Camera is not null);
            double left = Math.Max(0, (size.Width - (fixedWidth + (laneSize * count))) / 2);
            return LaneProjection.Of(LaneView.Highway, 0, Math.Max(1, size.Height), left + RailWidth + onsets, laneSize, slots, viewport, length,
                Camera is null ? EditorViewport.StrikeFraction : EditorViewport.PerspectiveStrikeFraction);
        }

        /// <summary>
        /// How wide each of <paramref name="count"/> columns is in a highway <paramref name="width"/> wide, of which <paramref name="fixedWidth"/>
        /// is the margins and rails: as wide as the room allows, but no more than 64 pixels flat, and in perspective no more than 480
        /// pixels across all of them.
        /// </summary>
        internal static double LaneSizeFor(double width, double fixedWidth, int count, bool perspective) =>
            Math.Min(perspective ? MaxPerspectiveWidth / count : MaxLaneSize, Math.Max(1, width - fixedWidth) / count);

        protected override Rect LabelsArea(Size size, LaneProjection projection) =>
            new(projection.LaneFrom - OnsetStripSize - RailWidth, projection.TimeFrom, RailWidth, projection.TimeLength);

        protected override Rect BehindArea(Size size, LaneProjection projection) =>
            new(projection.LaneFrom + 4, projection.TimeFrom, Math.Max(0, LanesWidth(projection) - 8), projection.TimeLength);

        // Highways have no strip: the column beside them is their full-strength backdrop.
        protected override Rect StripArea(Size size, LaneProjection projection) => default;

        protected override Rect BesideArea(Size size, LaneProjection projection, int phraseRows) =>
            new(PhraseRailFrom(projection, phraseRows) + PhraseRailSize(phraseRows) + 2, projection.TimeFrom, Math.Max(0, Extras.BesideSize - 4), projection.TimeLength);

        protected override Rect OnsetArea(Size size, LaneProjection projection) =>
            new(projection.LaneFrom - OnsetStripSize, projection.TimeFrom, OnsetStripSize, projection.TimeLength);

        // Right of the forcing marks.
        protected override double PhraseRailFrom(LaneProjection projection, int phraseRows) =>
            projection.LaneFrom + LanesWidth(projection) + MarkWidth;

        private static double LanesWidth(LaneProjection projection) => projection.LaneSize * projection.Slots.Slots.Count;
    }
}
