using System;
using ChartShared.UI.Controls;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// A Properties field dragged sideways (<see cref="DeltaScrub"/>): each step moves every selected item by the same
    /// amount from where it was, so items of different values keep their differences, as one undoable step.
    /// </summary>
    /// <param name="pixelsPerStep">How far a step is dragged, in pixels, as a drag begins.</param>
    /// <param name="begin">Starts the scrub; false with nothing to scrub.</param>
    /// <param name="scrubTo">Moves the scrub to the steps given; the steps it's at.</param>
    /// <param name="end">Ends the scrub, keeping what it shows.</param>
    internal sealed class FieldScrubTarget(Func<double> pixelsPerStep, Func<bool> begin, Func<int, int> scrubTo, Action end) : IDeltaScrubTarget
    {
        public double PixelsPerStep => pixelsPerStep();

        public bool Begin() => begin();

        public int ScrubTo(int steps) => scrubTo(steps);

        public void End() => end();
    }
}
