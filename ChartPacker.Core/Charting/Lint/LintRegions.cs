using ChartPacker.Core.Charts;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Validation;

namespace ChartPacker.Core.Charting.Lint;

/// <summary>
/// The regions a range rule checks again after a change, each a changed region widened by the rule's reach, and which
/// of them a finding overlaps.
/// </summary>
/// <remarks>
/// The regions are in <see cref="ChartChange.Regions"/>' order, with those of one part and difficulty that meet joined,
/// so within each part and difficulty they're apart and in tick order, and a lookup is a binary search in each.
/// </remarks>
internal sealed class LintRegions
{
    // Each part and difficulty's regions (the difficulty's index, or -1 for every difficulty), in tick order, each with
    // its index in Regions.
    private readonly Dictionary<(ChartInstrument Part, int Difficulty), List<(long Tick, long EndTick, int Index)>> _groups = [];

    private LintRegions(IReadOnlyList<ChartLocation> regions)
    {
        Regions = regions;
        for (int i = 0; i < regions.Count; i++)
        {
            var region = regions[i];
            var key = (region.Part, DifficultyKey(region.Difficulty));
            if (!_groups.TryGetValue(key, out var group))
                _groups[key] = group = [];
            group.Add((region.Tick, region.EndTick, i));
        }
    }

    /// <summary>The regions, in order of part, difficulty (every difficulty first), then tick.</summary>
    public IReadOnlyList<ChartLocation> Regions { get; }

    /// <summary>
    /// The regions a range rule of <paramref name="reach"/> checks again after a change to <paramref name="changed"/>:
    /// each widened to the <paramref name="reach"/>th chord before it and after it on its difficulty's track, or on every
    /// difficulty's when it has none (to the first or last chord when there are fewer, and not at all when there's none),
    /// then joined where they meet.
    /// </summary>
    public static LintRegions Widen(EditableChart chart, IReadOnlyList<ChartLocation> changed, int reach)
    {
        if (reach == 0)
            return new LintRegions(changed);

        var widened = new List<ChartLocation>(changed.Count);
        foreach (var region in changed)
        {
            if (chart.PartOf(region.Part) is not { } part)
            {
                widened.Add(region);
                continue;
            }

            long from = region.Tick, to = region.EndTick;
            IReadOnlyList<EditableTrack> tracks = region.Difficulty is { } difficulty ? [part[difficulty]] : part.Tracks;
            foreach (var track in tracks)
            {
                if (ChartLintContext.ChordTickBefore(track, region.Tick, reach) is { } before)
                    from = Math.Min(from, before);
                if (ChartLintContext.ChordTickAfter(track, region.EndTick, reach) is { } after)
                    to = Math.Max(to, after);
            }

            widened.Add(region with { Tick = from, EndTick = to });
        }

        return new LintRegions(ChartChange.Of(widened).Regions);
    }

    /// <summary>The index of the first of <see cref="Regions"/> that <paramref name="location"/> overlaps, or -1 when it overlaps none.</summary>
    public int FirstOverlapping(ChartLocation location)
    {
        int first = -1;
        if (location.Difficulty is { } difficulty)
        {
            Find(location, -1, ref first);
            Find(location, (int)difficulty, ref first);
        }
        else
        {
            for (int key = -1; key <= (int)ChartDifficulty.Expert; key++)
                Find(location, key, ref first);
        }

        return first;
    }

    /// <summary>Whether <paramref name="location"/> overlaps any of <see cref="Regions"/>.</summary>
    public bool Overlaps(ChartLocation location) => FirstOverlapping(location) >= 0;

    private static int DifficultyKey(ChartDifficulty? difficulty) => difficulty is { } d ? (int)d : -1;

    // Lowers first to the index of the first region of the group that location overlaps, if it's lower.
    private void Find(ChartLocation location, int difficulty, ref int first)
    {
        if (!_groups.TryGetValue((location.Part, difficulty), out var group))
            return;

        // The group's regions are apart and in order, so their last ticks are in order too: find the first that ends at
        // or after the location's start, which is the only one that can be the first to overlap it.
        int lo = 0, hi = group.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (group[mid].EndTick < location.Tick)
                lo = mid + 1;
            else
                hi = mid;
        }

        if (lo < group.Count && group[lo].Tick <= location.EndTick && (first < 0 || group[lo].Index < first))
            first = group[lo].Index;
    }
}
