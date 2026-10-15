using System.Runtime.InteropServices;

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
        // In tick order, those at a tick in the order given. Each run of one tick is a chord. Worked through
        // in place rather than grouped, since this runs for every chord of every track a library scan reads.
        var sorted = InTickOrder(gems);
        var notes = new List<ChartNote>(sorted.Count);
        var byLane = new List<Gem>();
        int previousStart = 0, previousCount = 0;

        for (int start = 0; start < sorted.Count;)
        {
            long tick = sorted[start].Tick;
            int end = start + 1;
            while (end < sorted.Count && sorted[end].Tick == tick)
                end++;

            int count = end - start;
            int lane = sorted[start].Lane;
            bool natural = count == 1
                && previousCount > 0
                && tick - sorted[previousStart].Tick <= hopoThreshold
                && (previousCount == 1 ? sorted[previousStart].Lane != lane : !midiChordRule || !HasLane(sorted, previousStart, previousCount, lane));

            var mark = marks(tick);
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

            if (InStarPower(starPower, tick))
                flags |= NoteFlags.StarPower;

            // By lane, those on a lane in the order given.
            byLane.Clear();
            for (int i = start; i < end; i++)
            {
                var gem = sorted[i];
                int at = byLane.Count;
                while (at > 0 && byLane[at - 1].Lane > gem.Lane)
                    at--;
                byLane.Insert(at, gem);
            }

            foreach (var gem in byLane)
                notes.Add(new ChartNote(gem.Tick, gem.Length, gem.Lane, flags));

            previousStart = start;
            previousCount = count;
            start = end;
        }

        return notes;
    }

    /// <summary>The gems in tick order, those at a tick in the order given; as given when they already are.</summary>
    private static List<Gem> InTickOrder(IEnumerable<Gem> gems) => TickOrder.Sorted(gems as List<Gem> ?? [.. gems], static g => g.Tick);

    private static bool HasLane(List<Gem> gems, int start, int count, int lane)
    {
        for (int i = start; i < start + count; i++)
        {
            if (gems[i].Lane == lane)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Drum notes without sustains, one for each gem with the flags given, in tick, then lane, then flags order (but
    /// for accents and ghosts), those alike in the order given; two at the same tick and lane are both kept, as YARG
    /// keeps them.
    /// </summary>
    /// <param name="gems">Sorted in place.</param>
    public static List<ChartNote> Drums(List<(Gem Gem, NoteFlags Flags)> gems, IReadOnlyList<StarPowerPhrase> starPower)
    {
        TickOrder.Sort(gems, static g => g.Gem.Tick);

        // Each tick's few by lane and then flags, each moving only past those that sort after it.
        var items = CollectionsMarshal.AsSpan(gems);
        for (int i = 1; i < items.Length; i++)
        {
            var item = items[i];
            int at = i;
            while (at > 0 && items[at - 1].Gem.Tick == item.Gem.Tick && SortsAfter(items[at - 1], item))
            {
                items[at] = items[at - 1];
                at--;
            }

            items[at] = item;
        }

        var notes = new List<ChartNote>(gems.Count);
        foreach (var (gem, flags) in gems)
            notes.Add(new ChartNote(gem.Tick, 0, gem.Lane, InStarPower(starPower, gem.Tick) ? flags | NoteFlags.StarPower : flags));
        return notes;

        // Accents and ghosts don't count, so a MIDI chart that has them sorts as it would read without them.
        static bool SortsAfter((Gem Gem, NoteFlags Flags) a, (Gem Gem, NoteFlags Flags) b)
        {
            const NoteFlags dynamics = NoteFlags.Accent | NoteFlags.Ghost;
            return a.Gem.Lane != b.Gem.Lane ? a.Gem.Lane > b.Gem.Lane : (a.Flags & ~dynamics) > (b.Flags & ~dynamics);
        }
    }

    /// <summary>
    /// Whether <paramref name="tick"/> is in star power, as YARG takes it: in the phrase, of <paramref name="phrases"/>
    /// in order, that started last at or before it, the last given of those that start together. A phrase's end
    /// isn't in it, and one without a length has only its start.
    /// </summary>
    public static bool InStarPower(IReadOnlyList<StarPowerPhrase> phrases, long tick)
    {
        // Indexed rather than foreach, which would box the list's enumerator on every call.
        StarPowerPhrase? current = null;
        for (int i = 0; i < phrases.Count; i++)
        {
            var phrase = phrases[i];
            if (phrase.Tick > tick)
                break;
            current = phrase;
        }

        return current is { } p && (p.Length == 0 ? tick == p.Tick : tick < p.Tick + p.Length);
    }
}

/// <summary>
/// Stable sorting by tick for lists of notes, which are almost always in order already, or nearly: a track's notes
/// come in the order they're let go.
/// </summary>
internal static class TickOrder
{
    /// <summary>Sorts <paramref name="list"/> by tick in place, those at a tick kept in the order given.</summary>
    /// <remarks>
    /// An insertion sort, which costs a move for each place a note is out of order and allocates nothing. A list far
    /// out of order is finished with a stable sort instead: those at a tick are still in the order given, so that
    /// gives the same as sorting the original.
    /// </remarks>
    public static void Sort<T>(List<T> list, Func<T, long> tickOf)
    {
        var items = CollectionsMarshal.AsSpan(list);
        long moves = 0, budget = 8L * items.Length;
        for (int i = 1; i < items.Length; i++)
        {
            T item = items[i];
            long tick = tickOf(item);
            int at = i;
            while (at > 0 && tickOf(items[at - 1]) > tick)
            {
                items[at] = items[at - 1];
                at--;
            }

            items[at] = item;
            moves += i - at;
            if (moves > budget)
            {
                T[] sorted = [.. list.OrderBy(tickOf)];
                sorted.CopyTo(items);
                return;
            }
        }
    }

    /// <summary><paramref name="list"/> itself when it's in tick order, otherwise a copy sorted by tick, those at a tick in the order given.</summary>
    public static List<T> Sorted<T>(List<T> list, Func<T, long> tickOf)
    {
        for (int i = 1; i < list.Count; i++)
        {
            // OrderBy is stable.
            if (tickOf(list[i]) < tickOf(list[i - 1]))
                return [.. list.OrderBy(tickOf)];
        }

        return list;
    }

    /// <summary>Of those in <paramref name="list"/>, which is in tick order, with the same tick and key, keeps only the first.</summary>
    public static void KeepFirstOfEach<T>(List<T> list, Func<T, long> tickOf, Func<T, int> keyOf)
    {
        var items = CollectionsMarshal.AsSpan(list);
        int kept = 0, tickStart = 0;
        for (int i = 0; i < items.Length; i++)
        {
            T item = items[i];
            long tick = tickOf(item);
            if (kept > 0 && tickOf(items[kept - 1]) != tick)
                tickStart = kept;

            int key = keyOf(item);
            bool seen = false;
            for (int j = tickStart; j < kept && !seen; j++)
                seen = keyOf(items[j]) == key;

            if (!seen)
                items[kept++] = item;
        }

        list.RemoveRange(kept, list.Count - kept);
    }
}
