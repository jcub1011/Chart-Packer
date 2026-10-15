namespace ChartShared.Core.Charts;

/// <summary>A drum gem as a chart stores it: its pad, 0 the kick to 5 the fifth pad, and what's marked on it.</summary>
/// <param name="Pad">0 kick, 1 red, 2 yellow, 3 blue, 4 the fourth pad (green, or five-lane orange), 5 the fifth (five-lane green).</param>
/// <param name="Cymbal">Marked a cymbal: yellow, blue and the fourth pad only.</param>
/// <param name="DoubleKick">An Expert+ second kick.</param>
/// <param name="Dynamics"><see cref="NoteFlags.Accent"/>, <see cref="NoteFlags.Ghost"/>, or none: added to the note's flags as it is.</param>
internal readonly record struct DrumGem(long Tick, int Pad, bool Cymbal, bool DoubleKick, NoteFlags Dynamics = NoteFlags.None);

/// <summary>
/// Text events as YARG reads them (<c>TextEvents</c> in YARG.Core): the text between the first <c>[</c> and
/// the <c>]</c> after it, or all of it, trimmed.
/// </summary>
internal static class ChartTextEvents
{
    public static string Normalize(string text)
    {
        int start = text.IndexOf('[');
        int end = text.IndexOf(']');
        return start >= 0 && end >= 0 && start <= end ? text[(start + 1)..end].Trim() : text.Trim();
    }

    /// <summary>
    /// A drums mix event, <c>mix &lt;difficulty&gt; drums&lt;configuration&gt;&lt;setting&gt;</c>: the difficulty it's for,
    /// and whether it turns disco flip on (setting <c>d</c>) or, any other setting, off.
    /// </summary>
    public static bool TryParseMix(string text, out ChartDifficulty difficulty, out bool discoFlip)
    {
        difficulty = ChartDifficulty.Expert;
        discoFlip = false;
        var rest = text.AsSpan();
        if (!rest.StartsWith("mix", StringComparison.Ordinal))
            return false;
        rest = Skip(rest[3..]);
        if (rest.IsEmpty || rest[0] is < '0' or > '3')
            return false;
        difficulty = (ChartDifficulty)(rest[0] - '0');
        rest = Skip(rest[1..]);
        if (!rest.StartsWith("drums", StringComparison.Ordinal))
            return false;
        rest = Skip(rest[5..]);
        if (rest.IsEmpty || rest[0] is < '0' or > '5')
            return false;
        discoFlip = Skip(rest[1..]).SequenceEqual("d");
        return true;

        static ReadOnlySpan<char> Skip(ReadOnlySpan<char> text) => text.TrimStart('_').TrimStart();
    }
}

/// <summary>
/// Turns drum gems into <see cref="ChartNote"/>s for a <see cref="DrumsLayout"/>, as YARG plays them
/// (<c>MoonSongLoader.Drums</c> in YARG.Core): four-lane drums without cymbals; pro drums with them, where disco
/// flip swaps red and yellow; five-lane drums with five pads. Four-pad drums play the fifth pad as a green tom.
/// </summary>
internal static class DrumNotes
{
    /// <summary>
    /// Each difficulty's notes, from <paramref name="gems"/> and its <paramref name="mixes"/>, the mix events at
    /// their ticks for it, in the order they're read. Each note's disco flip is <see cref="GameDiscoFlips"/>': a mix
    /// event takes effect from the first note at or after it, and, as YARG has it, one still in effect at a difficulty's
    /// last note carries on into the next difficulty up.
    /// </summary>
    public static Dictionary<ChartDifficulty, List<ChartNote>> Build(
        IReadOnlyDictionary<ChartDifficulty, List<DrumGem>> gems,
        IReadOnlyDictionary<ChartDifficulty, List<(long Tick, bool DiscoFlip)>> mixes,
        IReadOnlyDictionary<ChartDifficulty, IReadOnlyList<StarPowerPhrase>> starPower,
        DrumsLayout layout)
    {
        var result = new Dictionary<ChartDifficulty, List<ChartNote>>();
        var flips = GameDiscoFlips.Read(LastTicks(gems), mixes, layout);
        foreach (var difficulty in Enum.GetValues<ChartDifficulty>())
        {
            if (!gems.TryGetValue(difficulty, out var list))
                continue;

            var built = new List<(Gem, NoteFlags)>(list.Count);
            foreach (var gem in TickOrder.Sorted(list, static g => g.Tick))
            {
                // Expert+ kicks are only played on Expert.
                if (gem.DoubleKick && difficulty != ChartDifficulty.Expert)
                    continue;
                built.Add(Shape(gem, layout, flips.IsFlipped(difficulty, gem.Tick)));
            }

            result[difficulty] = NoteBuilder.Drums(built, starPower.GetValueOrDefault(difficulty) ?? []);
        }

        return result;
    }

    /// <summary>The tick of each difficulty's last note, Expert+ kicks among them; a difficulty without notes left out.</summary>
    private static Dictionary<ChartDifficulty, long> LastTicks(IReadOnlyDictionary<ChartDifficulty, List<DrumGem>> gems)
    {
        var last = new Dictionary<ChartDifficulty, long>();
        foreach (var (difficulty, list) in gems)
        {
            if (list.Count > 0)
                last[difficulty] = list.Max(static g => g.Tick);
        }

        return last;
    }

    private static (Gem, NoteFlags) Shape(DrumGem gem, DrumsLayout layout, bool flip)
    {
        var flags = (gem.DoubleKick ? NoteFlags.DoubleKick : NoteFlags.None) | gem.Dynamics;
        if (gem.Pad == 0)
            return (new Gem(gem.Tick, 0, ChartNote.OpenLane), flags);
        if (layout == DrumsLayout.FiveLane)
            return (new Gem(gem.Tick, 0, gem.Pad - 1), flags);

        int lane = Math.Min(gem.Pad, 4) - 1;
        bool cymbal = layout == DrumsLayout.Pro && gem.Pad is >= 2 and <= 4 && gem.Cymbal;
        if (layout == DrumsLayout.Pro && flip && gem.Pad is 1 or 2)
        {
            // Disco flip: red plays as a yellow cymbal, and yellow, cymbal or tom, as red.
            cymbal = gem.Pad == 1;
            lane = gem.Pad == 1 ? 1 : 0;
        }

        return (new Gem(gem.Tick, 0, lane), cymbal ? flags | NoteFlags.Cymbal : flags);
    }
}
