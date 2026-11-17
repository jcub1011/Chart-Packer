using System;
using Avalonia;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Editing;

namespace ChartBuilder.Controls.Editing
{
    /// <summary>
    /// The timeline view of a lane (charter-spec C3 item 1), in the style of a DAW: time runs left to right and each note
    /// lane is a row, under a ruler of measure numbers, tempo markers and section names, a strip of the waveform or
    /// spectrogram (C12, C13) and the phrase rail (C8), with the onsets in a strip under the rows (C14). It suits
    /// long-range editing and lining notes up with the audio. It's the view a vocal part is drawn in, as a piano roll with
    /// each note's lyric in a bubble above it.
    /// </summary>
    public sealed class TimelineEditorCanvas : EditorCanvas
    {
        // The ruler along the top, then the audio's strip, then the phrase rail, then the forcing marks just above the rows.
        private const double RulerHeight = 18;
        private const double MaxStripHeight = 48;
        private const double MarkHeight = 14;

        // A vocal part's strip of phrases and rail of star power, above its rows.
        private const double VocalPhraseHeight = 10;
        private const double VocalStarPowerHeight = 10;

        // The piano keyboard down a vocal part's left edge, whose names and keys line up with the rows.
        private const double KeyboardWidth = 46;

        protected override LaneView View => LaneView.Timeline;

        protected override double VocalGutterWidth => KeyboardWidth;

        // The strip is the timeline's column beside the rows: it's always there, and takes no more room when it's chosen.
        protected override double BesideSizeFor(Size size) => 0;

        // A vocal part's time axis starts after its keyboard.
        protected override double TimeAxisLength(Size size) => Math.Max(1, size.Width - (IsVocalLane ? KeyboardWidth : 0));

        // The projection last worked out, and what it was for: every layer of the lane, the notes standing up and the pointer ask for
        // it each frame, and a lane laid out the same with the cursor in the same place is projected the same.
        private (Size Size, LaneSlots Slots, EditorViewport Viewport, double Cursor, HighwayLength Length, int PhraseRows, LaneExtras Extras) _projectedFor;
        private LaneProjection? _projected;

        protected override LaneProjection Project(Size size, LaneSlots slots, EditorViewport viewport, HighwayLength length, int phraseRows)
        {
            var key = (size, slots, viewport, viewport.Cursor, length, phraseRows, Extras);
            if (_projected is { } known && key.Equals(_projectedFor))
                return known;

            _projectedFor = key;
            double top = RulerHeight + StripHeight(size) + PhraseRailSize(phraseRows) + MarkHeight;
            double height = Math.Max(1, size.Height - top - 4 - OnsetStripSize);
            return _projected = LaneProjection.Of(LaneView.Timeline, 0, Math.Max(1, size.Width), top, height / slots.Slots.Count, slots, viewport, length);
        }

        protected override Rect LabelsArea(Size size, LaneProjection projection) => new(projection.TimeFrom, 0, projection.TimeLength, RulerHeight);

        protected override Rect BehindArea(Size size, LaneProjection projection) =>
            new(projection.TimeFrom, projection.LaneFrom, projection.TimeLength, projection.LaneSize * projection.Slots.Slots.Count);

        protected override Rect StripArea(Size size, LaneProjection projection) => new(projection.TimeFrom, RulerHeight, projection.TimeLength, StripHeight(size));

        protected override Rect BesideArea(Size size, LaneProjection projection, int phraseRows) => StripArea(size, projection);

        protected override Rect OnsetArea(Size size, LaneProjection projection) =>
            new(projection.TimeFrom, projection.LaneFrom + (projection.LaneSize * projection.Slots.Slots.Count) + 2, projection.TimeLength, OnsetStripSize);

        // Above the forcing marks.
        protected override double PhraseRailFrom(LaneProjection projection, int phraseRows) => projection.LaneFrom - MarkHeight - PhraseRailSize(phraseRows);

        // A vocal part's phrase strip is under the ruler, its star power rail under that, and its rows under that, all after its
        // keyboard.
        protected override LaneProjection? ProjectVocals(Size size, LaneSlots slots, EditorViewport viewport, HighwayLength length)
        {
            double top = RulerHeight + VocalPhraseHeight + VocalStarPowerHeight;
            double height = Math.Max(1, size.Height - top);
            return LaneProjection.Of(LaneView.Timeline, KeyboardWidth, Math.Max(1, size.Width - KeyboardWidth), top, height / slots.Slots.Count, slots, viewport, length);
        }

        protected override Rect VocalPhraseArea(Size size, LaneProjection projection) =>
            new(projection.TimeFrom, RulerHeight, projection.TimeLength, VocalPhraseHeight);

        protected override Rect VocalStarPowerArea(Size size, LaneProjection projection) =>
            new(projection.TimeFrom, RulerHeight + VocalPhraseHeight, projection.TimeLength, VocalStarPowerHeight);

        // A fifth of the lane's height, so the rows keep most of it.
        private static double StripHeight(Size size) => Math.Clamp((size.Height - RulerHeight) * 0.2, 0, MaxStripHeight);
    }
}
