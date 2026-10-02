namespace ChartPacker.Core.Charts;

/// <summary>A note as a chart stores it, before its chord and HOPO state are worked out.</summary>
internal readonly record struct Gem(long Tick, long Length, int Lane);

/// <summary>What a chart marks at a fret chord's tick.</summary>
/// <param name="Toggle">A <c>.chart</c> forced flag: a natural HOPO becomes a strum and a strum a HOPO.</param>
internal readonly record struct FretMarks(bool Toggle, bool ForceHopo, bool ForceStrum, bool Tap);

/// <summary>
/// Turns a track's gems into <see cref="ChartNote"/>s, as YARG works out each note's state
/// (<c>MoonNote</c> and <c>MoonSongLoader</c> in YARG.Core).
/// </summary>
internal static class NoteBuilder
{
    /// <summary>
    /// Fret notes, every gem kept in the order given at its tick. A single note is a natural HOPO when it's no
    /// more than <paramref name="hopoThreshold"/> ticks after the chord or note before, unless that was the same
    /// single note; or, with <paramref name="midiChordRule"/>, a chord that has its lane. More than one gem at a
    /// tick is a chord, even on one lane. <paramref name="marks"/> can toggle or force it, or make it a tap;
    /// forcing it a HOPO wins over forcing it a strum, and a tap over both.
    /// </summary>
    /// <param name="midiChordRule">Whether a note contained in the chord before is a strum: MIDI's rule, not a <c>.chart</c>'s.</param>
    public static List<ChartNote> Frets(IEnumerable<Gem> gems, long hopoThreshold, Func<long, FretMarks> marks, IReadOnlyList<StarPowerPhrase> starPower, bool midiChordRule)
    {
        var notes = new List<ChartNote>();
        long? previousTick = null;
        List<Gem> previous = [];

        foreach (var group in gems.GroupBy(g => g.Tick).OrderBy(g => g.Key))
        {
            var chord = group.ToList();
            bool natural = chord.Count == 1
                && previousTick is { } before
                && chord[0].Tick - before <= hopoThreshold
                && (previous.Count == 1 ? previous[0].Lane != chord[0].Lane : !midiChordRule || previous.All(g => g.Lane != chord[0].Lane));

            var mark = marks(group.Key);
            var flags = NoteFlags.None;
            if (mark.Tap)
            {
                flags = NoteFlags.Tap;
            }
            else
            {
                bool hopo = mark.ForceHopo || (!mark.ForceStrum && natural != mark.Toggle);
                if (hopo)
                    flags = NoteFlags.Hopo;
            }

            if (InStarPower(starPower, group.Key))
                flags |= NoteFlags.StarPower;

            foreach (var gem in chord.OrderBy(g => g.Lane))
                notes.Add(new ChartNote(gem.Tick, gem.Length, gem.Lane, flags));

            previousTick = group.Key;
            previous = chord;
        }

        return notes;
    }

    /// <summary>
    /// Drum notes without sustains, one for each gem with the flags given, in tick then lane order; two at
    /// the same tick and lane are both kept, as YARG keeps them.
    /// </summary>
    public static List<ChartNote> Drums(IEnumerable<(Gem Gem, NoteFlags Flags)> gems, IReadOnlyList<StarPowerPhrase> starPower) =>
    [
        .. gems
            .OrderBy(g => g.Gem.Tick)
            .ThenBy(g => g.Gem.Lane)
            .ThenBy(g => g.Flags)
            .Select(g => new ChartNote(g.Gem.Tick, 0, g.Gem.Lane, InStarPower(starPower, g.Gem.Tick) ? g.Flags | NoteFlags.StarPower : g.Flags)),
    ];

    /// <summary>
    /// Whether <paramref name="tick"/> is in star power, as YARG takes it: in the phrase, of <paramref name="phrases"/>
    /// in order, that started last at or before it, the last given of those that start together. A phrase's end
    /// isn't in it, and one without a length has only its start.
    /// </summary>
    public static bool InStarPower(IReadOnlyList<StarPowerPhrase> phrases, long tick)
    {
        StarPowerPhrase? current = null;
        foreach (var phrase in phrases)
        {
            if (phrase.Tick > tick)
                break;
            current = phrase;
        }

        return current is { } p && (p.Length == 0 ? tick == p.Tick : tick < p.Tick + p.Length);
    }
}
