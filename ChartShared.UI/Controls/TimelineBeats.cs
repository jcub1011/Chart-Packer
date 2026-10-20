using System;
using System.Collections.Generic;
using ChartShared.Core.Charts;

namespace ChartShared.UI.Controls
{
    /// <summary>A beat line on a timeline, in audio time (charter-spec §4.4.1).</summary>
    public readonly record struct TimelineBeat(double Seconds, bool IsMeasure);

    /// <summary>A chart's beat lines in audio time, and stepping along them a bar at a time, as the timelines and the change strip do.</summary>
    public static class TimelineBeats
    {
        // How near a bar line a position counts as on it, so one put there by the wheel doesn't stick to it.
        private const double BarTolerance = 1e-3;

        /// <summary>The beat lines of <paramref name="tempoMap"/>, its audio <paramref name="delay"/> later, from 0 to <paramref name="length"/> seconds of audio.</summary>
        public static IReadOnlyList<TimelineBeat> Of(TempoMap tempoMap, TimeSpan delay, double length)
        {
            ArgumentNullException.ThrowIfNull(tempoMap);
            double offset = delay.TotalSeconds;
            long lastTick = (long)Math.Ceiling(tempoMap.TickAt(length - offset));
            if (lastTick < 0)
                return [];

            var beats = new List<TimelineBeat>();
            foreach (var line in tempoMap.BeatLines(0, lastTick))
            {
                double seconds = tempoMap.SecondsAt(line.Tick) + offset;
                if (seconds >= 0 && seconds <= length)
                    beats.Add(new TimelineBeat(seconds, line.IsMeasure));
            }

            return beats;
        }

        /// <summary>
        /// The bar line <paramref name="bars"/> bars from <paramref name="position"/>, later for more than none: from
        /// partway through a bar, the first back is the bar's own start. As far as there are bar lines, or null for none
        /// that way.
        /// </summary>
        public static double? StepBars(IReadOnlyList<TimelineBeat> beats, double position, int bars)
        {
            ArgumentNullException.ThrowIfNull(beats);
            double? found = null;
            int left = Math.Abs(bars);
            if (bars > 0)
            {
                foreach (var beat in beats)
                {
                    if (!beat.IsMeasure || beat.Seconds <= position + BarTolerance)
                        continue;
                    found = beat.Seconds;
                    if (--left == 0)
                        break;
                }
            }
            else if (bars < 0)
            {
                for (int i = beats.Count - 1; i >= 0; i--)
                {
                    var beat = beats[i];
                    if (!beat.IsMeasure || beat.Seconds >= position - BarTolerance)
                        continue;
                    found = beat.Seconds;
                    if (--left == 0)
                        break;
                }
            }

            return found;
        }
    }

    /// <summary>How a turn of the mouse wheel is taken, a notch at a time, by the lanes, the timelines and the change strip.</summary>
    public static class WheelSteps
    {
        /// <summary>How many grid steps one notch of the wheel scrolls while Shift is held.</summary>
        public const int FastScrollSteps = 4;

        /// <summary>Takes at most one notch out of the wheel turn carried in <paramref name="carry"/>, adding <paramref name="delta"/> first, so a mouse reporting several units per physical notch still moves a single step. A turn the other way starts over, and fractions add up across events.</summary>
        public static int TakeZoomStep(ref double carry, double delta)
        {
            if (Math.Sign(delta) != Math.Sign(carry))
                carry = 0;
            carry += delta;
            int notches = (int)Math.Truncate(carry);
            carry -= notches;
            return Math.Clamp(notches, -1, 1);
        }

        /// <summary>Takes at most one notch out of the wheel turn carried in <paramref name="carry"/>, as grid steps: one step a notch, or <see cref="FastScrollSteps"/> while Shift is held (<paramref name="fast"/>).</summary>
        public static int TakeScrollStep(ref double carry, double delta, bool fast)
        {
            int notch = TakeZoomStep(ref carry, delta);
            return notch * (fast ? FastScrollSteps : 1);
        }

        /// <summary>A wheel turn, up for more than none, as a turn later for more than none: as it is when <paramref name="wheelUpIsLater"/>, otherwise the other way.</summary>
        public static double TowardLater(double delta, bool wheelUpIsLater) => wheelUpIsLater ? delta : -delta;
    }
}
