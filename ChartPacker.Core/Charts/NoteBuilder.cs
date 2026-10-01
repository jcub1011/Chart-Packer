namespace ChartPacker.Core.Charts;

/// <summary>A note as a chart stores it, before its chord and HOPO state are worked out.</summary>
internal readonly record struct Gem(long Tick, long Length, int Lane);

/// <summary>What a chart marks at a fret chord's tick.</summary>
/// <param name="Toggle">A <c>.chart</c> forced flag: a natural HOPO becomes a strum and a strum a HOPO.</param>
internal readonly record struct FretMarks(bool Toggle, bool ForceHopo, bool ForceStrum, bool Tap);

/// <summary>Turns a track's gems into <see cref="ChartNote"/>s, the same way for <c>.chart</c> and MIDI.</summary>
internal static class NoteBuilder
{
    /// <summary>
    /// Fret notes: a single note is a natural HOPO when it follows a different chord or note within
    /// <paramref name="hopoThreshold"/> ticks; <paramref name="marks"/> can toggle or force it, or make it a tap.
    /// </summary>
    public static List<ChartNote> Frets(IEnumerable<Gem> gems, long hopoThreshold, Func<long, FretMarks> marks, IReadOnlyList<StarPowerPhrase> starPower)
    {
        var notes = new List<ChartNote>();
        long? previousTick = null;
        HashSet<int> previousLanes = [];

        foreach (var chord in gems.GroupBy(g => g.Tick).OrderBy(g => g.Key))
        {
            var lanes = chord.Select(g => g.Lane).ToHashSet();
            bool isChord = lanes.Count > 1;
            bool natural = !isChord
                && previousTick is { } before
                && chord.Key - before <= hopoThreshold
                && !previousLanes.Contains(lanes.First());

            var mark = marks(chord.Key);
            var flags = NoteFlags.None;
            if (mark.Tap)
            {
                flags = NoteFlags.Tap;
            }
            else
            {
                bool hopo = natural != mark.Toggle;
                if (mark.ForceHopo)
                    hopo = true;
                if (mark.ForceStrum)
                    hopo = false;
                if (hopo)
                    flags = NoteFlags.Hopo;
            }

            if (InStarPower(starPower, chord.Key))
                flags |= NoteFlags.StarPower;

            foreach (var gem in chord.GroupBy(g => g.Lane).Select(g => g.Last()).OrderBy(g => g.Lane))
                notes.Add(new ChartNote(gem.Tick, gem.Length, gem.Lane, flags));

            previousTick = chord.Key;
            previousLanes = lanes;
        }

        return notes;
    }

    /// <summary>
    /// Drum notes, one per tick and lane, without sustains. Where a lane has more than one gem at a
    /// tick their flags are combined, except that a kick is only a double kick when every kick there is.
    /// </summary>
    public static List<ChartNote> Drums(IEnumerable<(Gem Gem, NoteFlags Flags)> gems, IReadOnlyList<StarPowerPhrase> starPower)
    {
        var notes = new List<ChartNote>();
        foreach (var at in gems.GroupBy(g => (g.Gem.Tick, g.Gem.Lane)).OrderBy(g => g.Key.Tick).ThenBy(g => g.Key.Lane))
        {
            var flags = at.Aggregate(NoteFlags.None, (all, g) => all | g.Flags);
            if (at.Any(g => (g.Flags & NoteFlags.DoubleKick) == 0))
                flags &= ~NoteFlags.DoubleKick;
            if (InStarPower(starPower, at.Key.Tick))
                flags |= NoteFlags.StarPower;
            notes.Add(new ChartNote(at.Key.Tick, 0, at.Key.Lane, flags));
        }

        return notes;
    }

    /// <summary>Whether <paramref name="tick"/> is inside one of <paramref name="phrases"/>, which are in order.</summary>
    public static bool InStarPower(IReadOnlyList<StarPowerPhrase> phrases, long tick)
    {
        foreach (var phrase in phrases)
        {
            if (phrase.Tick > tick)
                return false;
            if (tick < phrase.Tick + Math.Max(1, phrase.Length))
                return true;
        }

        return false;
    }
}
