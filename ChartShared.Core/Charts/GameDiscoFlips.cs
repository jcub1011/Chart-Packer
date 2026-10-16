namespace ChartShared.Core.Charts;

/// <summary>
/// Where the games swap red and yellow on each drums difficulty (charter-spec C6 item 7), as <see cref="DrumNotes.Build"/>
/// reads the mix events, which works out each note's flip from this. The editor draws and places notes by it, so it can't
/// disagree with the games. Only Pro drums are flipped.
/// </summary>
/// <remarks>
/// A note is flipped as the last mix event at or before its tick says, so a mix takes effect from the first note at or
/// after it. Before a difficulty's first mix, a note is as the difficulty below left it at its last note: a flip still on
/// there carries on into the next difficulty up, as YARG has it. A note added to a difficulty doesn't change whether that
/// difficulty is flipped at its own tick, so the answer for a tick holds for a note about to be placed there too.
/// </remarks>
public sealed class GameDiscoFlips
{
    // Each difficulty's mix events, in the order they're read: whether each turns disco flip on, and the latest tick of
    // those up to it. A note reads the mixes before the first one after its tick, which that tick finds.
    private readonly (long Reach, bool DiscoFlip)[][] _mixes;

    // Whether each difficulty starts flipped, as the one below it left it.
    private readonly bool[] _carried;

    private GameDiscoFlips(bool isProDrums, (long Reach, bool DiscoFlip)[][] mixes, bool[] carried)
    {
        IsProDrums = isProDrums;
        _mixes = mixes;
        _carried = carried;
    }

    /// <summary>No disco flip anywhere: a chart without drums.</summary>
    public static GameDiscoFlips None { get; } = new(false, [[], [], [], []], new bool[4]);

    /// <summary>Whether the games read the drums as Pro, the only layout disco flip applies to.</summary>
    public bool IsProDrums { get; }

    /// <summary>Whether the games play a note at <paramref name="tick"/> of <paramref name="difficulty"/> with red and yellow swapped.</summary>
    public bool IsFlipped(ChartDifficulty difficulty, long tick)
    {
        if (!IsProDrums)
            return false;
        var mixes = _mixes[(int)difficulty];
        int read = MixesReadBy(mixes, tick);
        return read > 0 ? mixes[read - 1].DiscoFlip : _carried[(int)difficulty];
    }

    /// <summary>
    /// The flips of the drums whose difficulties' last notes are at <paramref name="lastTicks"/> (a difficulty without
    /// notes left out) and whose mix events are <paramref name="mixes"/>, each difficulty's in the order they're read.
    /// </summary>
    internal static GameDiscoFlips Read(
        IReadOnlyDictionary<ChartDifficulty, long> lastTicks,
        IReadOnlyDictionary<ChartDifficulty, List<(long Tick, bool DiscoFlip)>> mixes,
        DrumsLayout layout)
    {
        var difficulties = Enum.GetValues<ChartDifficulty>();
        var read = new (long, bool)[difficulties.Length][];
        var carried = new bool[difficulties.Length];
        bool flip = false;
        foreach (var difficulty in difficulties)
        {
            var events = mixes.GetValueOrDefault(difficulty) ?? [];
            var list = new (long Reach, bool DiscoFlip)[events.Count];
            long reach = long.MinValue;
            for (int i = 0; i < events.Count; i++)
            {
                reach = Math.Max(reach, events[i].Tick);
                list[i] = (reach, events[i].DiscoFlip);
            }

            int index = (int)difficulty;
            read[index] = list;
            carried[index] = flip;

            // What the difficulty's last note leaves on is what the next one up starts with.
            if (lastTicks.TryGetValue(difficulty, out long last))
            {
                int count = MixesReadBy(list, last);
                if (count > 0)
                    flip = list[count - 1].DiscoFlip;
            }
        }

        return new GameDiscoFlips(layout == DrumsLayout.Pro, read, carried);
    }

    // How many of the mixes a note at the tick has read: those before the first after it, as the reader takes them in
    // order while they're at or before the note.
    private static int MixesReadBy((long Reach, bool DiscoFlip)[] mixes, long tick)
    {
        int lo = 0, hi = mixes.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (mixes[mid].Reach <= tick)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }
}
