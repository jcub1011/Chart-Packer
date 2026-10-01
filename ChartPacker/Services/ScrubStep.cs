using ChartPacker.Resources;

namespace ChartPacker.Services
{
    /// <summary>How far Ctrl and a notch of the wheel move the audio: to the next note of this length on the chart's grid.</summary>
    public enum ScrubStep
    {
        Whole,
        Half,
        Quarter,
        Eighth,
        EighthTriplet,
        Sixteenth,
        SixteenthTriplet,
        ThirtySecond,
    }

    public static class ScrubSteps
    {
        /// <summary>How long a <paramref name="step"/> note is, in quarter notes: a third for an eighth-note triplet.</summary>
        public static double QuartersOf(ScrubStep step) => step switch
        {
            ScrubStep.Whole => 4,
            ScrubStep.Half => 2,
            ScrubStep.Eighth => 0.5,
            ScrubStep.EighthTriplet => 1.0 / 3,
            ScrubStep.Sixteenth => 0.25,
            ScrubStep.SixteenthTriplet => 1.0 / 6,
            ScrubStep.ThirtySecond => 0.125,
            _ => 1,
        };

        /// <summary>The name <paramref name="step"/> is shown by, in the settings and the scrubbing tooltips.</summary>
        public static string NameOf(ScrubStep step) => step switch
        {
            ScrubStep.Whole => Strings.ScrubStepWhole,
            ScrubStep.Half => Strings.ScrubStepHalf,
            ScrubStep.Eighth => Strings.ScrubStepEighth,
            ScrubStep.EighthTriplet => Strings.ScrubStepEighthTriplet,
            ScrubStep.Sixteenth => Strings.ScrubStepSixteenth,
            ScrubStep.SixteenthTriplet => Strings.ScrubStepSixteenthTriplet,
            ScrubStep.ThirtySecond => Strings.ScrubStepThirtySecond,
            _ => Strings.ScrubStepQuarter,
        };
    }
}
