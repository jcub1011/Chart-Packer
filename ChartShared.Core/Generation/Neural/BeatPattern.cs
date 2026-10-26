namespace ChartShared.Core.Generation.Neural;

/// <summary>
/// Beat patterns in counting notation (spec §2): the onsets in one beat, positions measured in
/// 48ths of a beat so sixteenths (12ths) and triplets (16ths) are both exact. A pattern is the
/// sorted set of source onsets in the beat; a candidate is a subset the draft keeps, plus empty.
/// </summary>
public static class BeatPattern
{
    /// <summary>Positions within a beat in 48ths. Sixteenths: 0,12,24,36. Triplets: 0,16,32.</summary>
    public const int UnitsPerBeat = 48;

    /// <summary>Id of the empty pattern ("-"): the beat is left unplayed.</summary>
    public const int EmptyId = 0;

    /// <summary>
    /// The pack vocabulary v1: the commonest shapes. Index 0 is empty; the rest are fixed shapes.
    /// Covers ~95% of official Easy beats; anything else maps to its nearest shape (§3.4 step 2).
    /// </summary>
    public static readonly string[] Vocabulary =
    [
        "-",        // 0: nothing kept
        "1",        // 1: downbeat
        "&",        // 2: off-beat alone
        "1 &",      // 3
        "e",        // 4
        "a",        // 5
        "1 e",      // 6
        "1 a",      // 7
        "e &",      // 8
        "& a",      // 9
        "1 e &",    // 10
        "1 & a",    // 11
        "1 e & a",  // 12
        "t",        // 13: triplet partial
        "T",        // 14: triplet partial
        "t T",      // 15: triplet full
    ];

    /// <summary>The 48th-offsets of each vocabulary pattern's slots.</summary>
    public static readonly int[][] VocabularyOffsets =
    [
        [],
        [0],
        [24],
        [0, 24],
        [12],
        [36],
        [0, 12],
        [0, 36],
        [12, 24],
        [24, 36],
        [0, 12, 24],
        [0, 24, 36],
        [0, 12, 24, 36],
        [16],
        [32],
        [16, 32],
    ];

    /// <summary>How many chords each vocabulary pattern keeps (for the density control, §3.4).</summary>
    public static IReadOnlyList<int> ChordsKept { get; } =
        VocabularyOffsets.Select(static o => o.Length == 0 ? 0 : o.Length).ToList();

    /// <summary>Encodes the source onsets in a beat (48th offsets) as the nearest vocabulary id.</summary>
    public static int EncodeSource(IReadOnlyList<int> offsets48)
    {
        if (offsets48.Count == 0)
            return EmptyId;
        int best = 1;
        int bestScore = int.MaxValue;
        for (int i = 1; i < VocabularyOffsets.Length; i++)
        {
            int score = SymmetricDifference(offsets48, VocabularyOffsets[i]);
            if (score < bestScore)
            {
                bestScore = score;
                best = i;
                if (score == 0)
                    break;
            }
        }

        return best;
    }

    /// <summary>
    /// The candidates for a beat: every vocabulary pattern whose slots are all on source onsets
    /// (a subset of what the source plays), plus empty. Always non-empty: empty is always allowed.
    /// </summary>
    public static IReadOnlyList<int> CandidatesFor(IReadOnlyList<int> sourceOffsets48)
    {
        var source = new HashSet<int>(sourceOffsets48);
        var candidates = new List<int> { EmptyId };
        for (int i = 1; i < VocabularyOffsets.Length; i++)
        {
            if (VocabularyOffsets[i].All(source.Contains))
                candidates.Add(i);
        }

        return candidates;
    }

    /// <summary>
    /// The candidate nearest to <paramref name="slots"/> by symmetric difference: the reference's
    /// kept onsets mapped onto what the draft may keep. Always a member of
    /// <paramref name="candidates"/> (empty is always a candidate), so training labels are
    /// always decodable. Rare off-grid onsets fall back to the nearest supported shape.
    /// </summary>
    public static int NearestCandidate(IReadOnlyList<int> candidates, IReadOnlyList<int> slots)
    {
        int best = EmptyId;
        int bestScore = int.MaxValue;
        foreach (int c in candidates)
        {
            int score = SymmetricDifference(slots, VocabularyOffsets[c]);
            if (score < bestScore)
            {
                bestScore = score;
                best = c;
            }
        }

        return best;
    }
    public static IReadOnlyList<int> MapToSource(IReadOnlyList<int> sourceOffsets48, int patternId)
    {
        var source = new HashSet<int>(sourceOffsets48);
        return VocabularyOffsets[patternId].Where(source.Contains).ToList();
    }

    private static int SymmetricDifference(IReadOnlyList<int> a, int[] b)
    {
        var set = new HashSet<int>(a);
        int score = b.Count(o => !set.Contains(o));
        foreach (int o in set)
        {
            if (!b.Contains(o))
                score++;
        }

        return score;
    }
}
