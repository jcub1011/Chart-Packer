using System;

namespace ChartShared.UI.Controls
{
    /// <summary>The value a number box dragged sideways takes, in whole steps of the step it's given.</summary>
    internal static class ScrubMath
    {
        /// <summary>
        /// <paramref name="start"/> plus the whole steps in <paramref name="moved"/>, a step being <paramref name="step"/>:
        /// a step of 0.1 gives tenths rather than whole numbers, and a value that didn't start on a step keeps its offset
        /// from one.
        /// </summary>
        internal static decimal DraggedValue(decimal start, double moved, double step)
        {
            double stepped = step > 0 ? Math.Round(moved / step, MidpointRounding.AwayFromZero) * step : moved;
            return start + (decimal)stepped;
        }
    }
}
