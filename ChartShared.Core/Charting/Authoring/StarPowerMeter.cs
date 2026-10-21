namespace ChartShared.Core.Charting.Authoring;

/// <summary>A planned activation as <see cref="StarPowerMeter.Simulate"/> played it.</summary>
/// <param name="Measure">Where the player activates, as a measure position.</param>
/// <param name="Meter">How full the meter was just before, from 0 to 1.</param>
/// <param name="Fired">Whether it activated: the meter was at the threshold or more, and star power wasn't already on.</param>
/// <param name="EndMeasure">Where star power runs out, as phrases completed while it's on stretch it; the measure, when it didn't fire.</param>
internal readonly record struct MeterActivation(double Measure, double Meter, bool Fired, double EndMeasure);

/// <summary>The meter at a moment of <see cref="StarPowerMeter.StateAt"/>.</summary>
/// <param name="Meter">How full it is, from 0 to 1, when star power is off; 0 while it's on.</param>
/// <param name="ActiveUntil">Where star power that's on runs out; null when it's off.</param>
/// <param name="LastEnd">Where the last activation before it ran out; null for none.</param>
internal readonly record struct MeterState(double Meter, double? ActiveUntil, double? LastEnd);

/// <summary>
/// The star power meter as the games fill and drain it, for a perfect player who never whammies: each phrase completed
/// gives <see cref="StarPowerParameters.MeterPerPhrase"/> of it, up to a full one; activating needs
/// <see cref="StarPowerParameters.ActivationThreshold"/>, and spends it all; star power then lasts
/// <see cref="StarPowerParameters.FullMeterMeasures"/> measures for a full meter, and each phrase completed while it's
/// on adds its share to what's left, up to a full one. All in measure positions (<see cref="StarPowerTimeline"/>).
/// </summary>
/// <remarks>YARG keeps the meter in measures whatever the time signature, and so does this.</remarks>
internal static class StarPowerMeter
{
    /// <summary>
    /// Each of <paramref name="activations"/> as a player who completes every phrase at <paramref name="completions"/> and
    /// activates at each would play it, in order. A phrase completed where an activation is counts first.
    /// </summary>
    public static List<MeterActivation> Simulate(IEnumerable<double> completions, IEnumerable<double> activations, StarPowerParameters p)
    {
        var played = new List<MeterActivation>();
        Run(completions, activations, double.PositiveInfinity, p, played);
        return played;
    }

    /// <summary>The meter just before <paramref name="measure"/>: every completion there or before counted, every activation before it.</summary>
    public static MeterState StateAt(IEnumerable<double> completions, IEnumerable<double> activations, double measure, StarPowerParameters p) =>
        Run(completions, activations, measure, p, null);

    /// <summary>Whether every one of <paramref name="activations"/> fires.</summary>
    public static bool AllFire(IEnumerable<double> completions, IEnumerable<double> activations, StarPowerParameters p) =>
        Simulate(completions, activations, p).All(static a => a.Fired);

    private static MeterState Run(IEnumerable<double> completions, IEnumerable<double> activations, double until, StarPowerParameters p, List<MeterActivation>? played)
    {
        // Completions before activations where they're at one place.
        var events = completions.Where(c => c <= until).Select(static c => (At: c, Activates: false))
            .Concat(activations.Where(a => a < until).Select(static a => (At: a, Activates: true)))
            .OrderBy(static e => e.At).ThenBy(static e => e.Activates)
            .ToList();

        double meter = 0, full = p.FullMeterMeasures;
        double? activeUntil = null, lastEnd = null;
        foreach (var (at, activates) in events)
        {
            if (activeUntil is { } end && at >= end)
            {
                lastEnd = end;
                activeUntil = null;
            }

            if (!activates)
            {
                if (activeUntil is { } on)
                    activeUntil = Math.Min(on + (p.MeterPerPhrase * full), at + full);
                else
                    meter = Math.Min(1, meter + p.MeterPerPhrase);
                continue;
            }

            if (activeUntil is { } running)
            {
                played?.Add(new MeterActivation(at, (running - at) / full, false, at));
                continue;
            }

            if (meter < p.ActivationThreshold - 1e-9)
            {
                played?.Add(new MeterActivation(at, meter, false, at));
                continue;
            }

            activeUntil = at + (meter * full);
            played?.Add(new MeterActivation(at, meter, true, activeUntil.Value));
            meter = 0;
        }

        if (activeUntil is { } last && until >= last)
            return new MeterState(meter, null, last);
        return new MeterState(meter, activeUntil, lastEnd);
    }
}
