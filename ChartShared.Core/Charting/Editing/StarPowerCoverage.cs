using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charts;

namespace ChartShared.Core.Charting.Editing;

/// <summary>The meter at a point of the song, for the star power window's meter ribbon.</summary>
/// <param name="Tick">Where it is, in chart ticks: the window maps it to audio seconds.</param>
/// <param name="Meter">How full it is, from 0 to 1; 0 while star power is on.</param>
/// <param name="Active">Whether star power is on there, so the ribbon shows it draining.</param>
public readonly record struct StarPowerMeterSample(long Tick, double Meter, bool Active);

/// <summary>A hard passage and whether the kept star power can be activated going into it.</summary>
public readonly record struct StarPowerSpikeCoverage(StarPowerSpike Spike, bool Charged);

/// <summary>What the kept regions would do to the meter and the hard passages, as the star power window shows it.</summary>
public sealed record StarPowerCoverage(
    IReadOnlyList<StarPowerMeterSample> Samples,
    IReadOnlyList<StarPowerSpikeCoverage> Spikes)
{
    /// <summary>No samples and no passages, for a part with nothing to show.</summary>
    public static StarPowerCoverage Empty { get; } = new([], []);

    /// <summary>How many hard passages the kept star power doesn't charge for.</summary>
    public int UnchargedSpikes => Spikes.Count(static s => !s.Charged);
}

/// <summary>
/// The meter and hard passages' coverage as a set of kept star power regions plays (<see cref="StarPowerCoverage"/>), for
/// the star power window's meter ribbon: the meter simulated as the games fill and drain it, with a player activating at
/// each hard passage the kept lead-ins plan for.
/// </summary>
public static class StarPowerCoverageCalculator
{
    /// <summary>
    /// The meter and passages' coverage of <paramref name="part"/>'s kept <paramref name="regions"/>, sampled
    /// <paramref name="samples"/> times from its first chord to its last.
    /// </summary>
    public static StarPowerCoverage Of(
        EditableChart chart,
        ChartInstrument part,
        IReadOnlyList<StarPowerRegion> regions,
        IReadOnlyList<StarPowerSpike> spikes,
        StarPowerParameters? parameters = null,
        int samples = 512,
        IReadOnlyList<Phrase>? alsoPlaying = null)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(spikes);
        var p = parameters ?? StarPowerParameters.Default;
        if (chart.PartOf(part) is not { } whole || DifficultyGenerator.HighestCharted(whole) is not { } highest)
            return StarPowerCoverage.Empty;

        var context = StarPowerContext.Of(chart, whole, highest, null, p);
        var timeline = context.Timeline;
        var completions = new List<double>();
        var activations = new List<double>();
        if (alsoPlaying is not null)
        {
            foreach (var phrase in alsoPlaying)
            {
                var covered = context.Source.Notes.Between(phrase.Tick, phrase.CoverEnd - 1);
                if (!covered.IsEmpty)
                    completions.Add(timeline.MeasureAt(covered[^1].Tick));
            }
        }

        foreach (var region in regions)
        {
            if (!region.Kept)
                continue;
            var covered = context.Source.Notes.Between(region.Phrase.Tick, region.Phrase.CoverEnd - 1);
            if (!covered.IsEmpty)
                completions.Add(timeline.MeasureAt(covered[^1].Tick));
            if (region.SpikeTick is { } spikeTick)
                activations.Add(timeline.MeasureAt(spikeTick));
        }

        activations.Sort();
        long first = context.Chords[0].Tick, last = context.LastChordTick;
        int count = Math.Max(2, samples);
        var curve = new List<StarPowerMeterSample>(count);
        for (int i = 0; i < count; i++)
        {
            long tick = first + (long)Math.Round((last - first) * (double)i / (count - 1));
            var state = StarPowerMeter.StateAt(completions, activations, timeline.MeasureAt(tick), p);
            curve.Add(new StarPowerMeterSample(tick, state.Meter, state.ActiveUntil is not null));
        }

        var coverage = new List<StarPowerSpikeCoverage>(spikes.Count);
        foreach (var spike in spikes)
        {
            var state = StarPowerMeter.StateAt(completions, activations, timeline.MeasureAt(spike.StartTick), p);
            bool charged = state.ActiveUntil is null && state.Meter >= p.ActivationThreshold - 1e-9;
            coverage.Add(new StarPowerSpikeCoverage(spike, charged));
        }

        return new StarPowerCoverage(curve, coverage);
    }
}
