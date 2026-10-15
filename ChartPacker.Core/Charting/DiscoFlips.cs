using ChartPacker.Core.Charting.Commands;

namespace ChartPacker.Core.Charting;

/// <summary>
/// A disco flip section of one drums difficulty (charter-spec C6 item 7): from a mix that turns disco flip on to the next
/// mix that turns it off. Red and yellow swap in the games inside it.
/// </summary>
/// <param name="Tick">Where it starts: the tick of the mix that turns disco flip on.</param>
/// <param name="EndTick">Where it ends: the tick of the mix that turns it off; null when none does, as only a loaded file can have.</param>
/// <param name="Configuration">The stem configuration of the mix it starts with.</param>
public readonly record struct DiscoFlipRange(long Tick, long? EndTick, int Configuration)
{
    /// <summary>Whether it covers <paramref name="tick"/>.</summary>
    public bool Covers(long tick) => tick >= Tick && (EndTick is not { } end || tick < end);
}

/// <summary>
/// A drums difficulty's disco flip sections, read from its mix events, which the model keeps as the file has them
/// (charter-spec C6 item 7), and the changes to them that the disco flip commands make.
/// </summary>
/// <remarks>
/// The mix in effect at a tick is the last one at or before it, as the games read them. A range starts at a mix with
/// <see cref="DrumMix.DiscoFlipSetting"/> where disco flip was off, and ends at the next mix without it; a disco flip mix
/// inside it changes only the stems, and stays in it. Mix events spelt otherwise, which the model keeps as text, aren't
/// read here.
/// </remarks>
public static class DiscoFlips
{
    /// <summary>The disco flip sections of <paramref name="track"/>, in tick order.</summary>
    public static IReadOnlyList<DiscoFlipRange> RangesOf(EditableTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return RangesOf(track.Mixes);
    }

    /// <summary>Whether disco flip is on at <paramref name="tick"/> of <paramref name="track"/>.</summary>
    public static bool IsFlippedAt(EditableTrack track, long tick)
    {
        ArgumentNullException.ThrowIfNull(track);
        int index = track.Mixes.FirstAfter(tick) - 1;
        return index >= 0 && track.Mixes[index].DiscoFlip;
    }

    /// <summary>
    /// The stem configuration in effect at <paramref name="tick"/> of <paramref name="mixes"/>: the last mix's at or before
    /// it, otherwise the first mix's after it, otherwise 0.
    /// </summary>
    internal static int ConfigurationAt(TickList<DrumMix> mixes, long tick)
    {
        int index = mixes.FirstAfter(tick);
        return index > 0 ? mixes[index - 1].Configuration : index < mixes.Count ? mixes[index].Configuration : 0;
    }

    internal static List<DiscoFlipRange> RangesOf(TickList<DrumMix> mixes)
    {
        var ranges = new List<DiscoFlipRange>();
        DrumMix? start = null;
        foreach (var mix in mixes)
        {
            if (mix.DiscoFlip && start is null)
            {
                start = mix;
            }
            else if (!mix.DiscoFlip && start is { } on)
            {
                ranges.Add(new DiscoFlipRange(on.Tick, mix.Tick, on.Configuration));
                start = null;
            }
        }

        if (start is { } open)
            ranges.Add(new DiscoFlipRange(open.Tick, null, open.Configuration));
        return ranges;
    }

    /// <summary>
    /// Adds a section from <paramref name="tick"/> up to <paramref name="endTick"/> to <paramref name="mixes"/>: the mixes
    /// in it turn disco flip on, a mix at its start does if there's none, and one at its end turns it off if there's none
    /// there. Each new mix keeps the stem configuration in effect where it goes. A section ending where another starts, or
    /// starting where one ends, joins it.
    /// </summary>
    /// <exception cref="InvalidOperationException">It overlaps a section, or covers a mix with another setting.</exception>
    internal static void Add(TickList<DrumMix> mixes, long tick, long endTick, EditLog log)
    {
        foreach (var range in RangesOf(mixes))
        {
            if (range.Tick < endTick && (range.EndTick is not { } end || end > tick))
                throw new InvalidOperationException(FormattableString.Invariant($"Disco flip is already on from tick {range.Tick}, which the new section would overlap."));
        }

        int configuration = ConfigurationAt(mixes, tick);
        bool atStart = false;
        for (int i = mixes.FirstAtOrAfter(tick); i < mixes.Count && mixes[i].Tick < endTick; i++)
        {
            var mix = mixes[i];
            if (mix.Setting.Length != 0)
                throw new InvalidOperationException(FormattableString.Invariant($"The mix at tick {mix.Tick} has the setting \"{mix.Setting}\", which disco flip can't be added to."));
            log.Replace(mixes, i, mix with { Setting = DrumMix.DiscoFlipSetting });
            atStart |= mix.Tick == tick;
        }

        if (!atStart)
            log.Place(mixes, new DrumMix(tick, configuration, DrumMix.DiscoFlipSetting));
        if (mixes.At(endTick).IsEmpty)
            log.Place(mixes, new DrumMix(endTick, ConfigurationAt(mixes, endTick - 1), string.Empty));
    }

    /// <summary>
    /// Removes <paramref name="range"/> from <paramref name="mixes"/>: its mixes turn disco flip off, and each then left
    /// the same as the mix before it, its end's included, is left out.
    /// </summary>
    /// <exception cref="InvalidOperationException">The track hasn't the section.</exception>
    internal static void Remove(TickList<DrumMix> mixes, DiscoFlipRange range, EditLog log)
    {
        if (!RangesOf(mixes).Contains(range))
            throw new InvalidOperationException(FormattableString.Invariant($"There's no disco flip section from tick {range.Tick}."));

        long end = range.EndTick ?? long.MaxValue;
        for (int i = mixes.FirstAtOrAfter(range.Tick); i < mixes.Count && mixes[i].Tick < end; i++)
        {
            if (mixes[i].DiscoFlip)
                log.Replace(mixes, i, mixes[i] with { Setting = string.Empty });
        }

        int first = mixes.FirstAtOrAfter(range.Tick);
        for (int i = Math.Max(1, first); i < mixes.Count && mixes[i].Tick <= end;)
        {
            if (mixes[i] with { Tick = mixes[i - 1].Tick } == mixes[i - 1])
                log.RemoveAt(mixes, i);
            else
                i++;
        }
    }
}
