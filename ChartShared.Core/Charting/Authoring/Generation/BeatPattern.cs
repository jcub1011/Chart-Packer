using System.Collections.Concurrent;
using System.Numerics;

namespace ChartShared.Core.Charting.Authoring.Generation;

/// <summary>Compact musical contour of the source notes inside one beat: chord distribution,
/// whether the downbeat repeats the previous fret/chord, whether an off-beat changes fret/chord,
/// and whether an off-beat holds a sustain or anticipates the next beat's downbeat.</summary>
[Flags]
public enum BeatShape : byte
{
    None = 0,
    DownbeatChord = 1 << 0,
    OffbeatChord = 1 << 1,
    MultiChords = DownbeatChord | OffbeatChord,
    DownbeatRepeatsPrev = 1 << 2,
    OffbeatChangesFret = 1 << 3,
    OffbeatSustainsOrAnticipates = 1 << 4,
}

/// <summary>A beat pattern: the onsets in one beat as a 48th-note mask (spec 2).
/// Bit p (0..47) is set when the beat has an onset at 48th p. 48ths make sixteenths
/// (0,12,24,36) and triplets (0,16,32) exact. Rare off-grid onsets round to the nearest 48th.</summary>
public readonly record struct BeatPattern(ulong Mask)
{
    public const int Divisions = 48;
    public const int SchemaVersion = 1;

    public static readonly BeatPattern Empty = new(0);

    private static readonly ConcurrentDictionary<ulong, string> DisplayCache = new()
    {
        [0UL] = "-",
        [1UL << 0] = "1",
        [1UL << 12] = "e",
        [1UL << 16] = "t",
        [1UL << 24] = "&",
        [1UL << 32] = "T",
        [1UL << 36] = "a",
        [(1UL << 0) | (1UL << 24)] = "1 &",
        [(1UL << 0) | (1UL << 12) | (1UL << 24) | (1UL << 36)] = "1 e & a",
    };

    public bool IsEmpty => Mask == 0;

    public int OnsetCount => BitOperations.PopCount(Mask);

    /// <summary>Whether this pattern keeps only onsets <paramref name="source"/> has.</summary>
    public bool IsSubsetOf(BeatPattern source) => (Mask & ~source.Mask) == 0;

    public IEnumerable<int> Offsets()
    {
        ulong m = Mask;
        while (m != 0)
        {
            int p = BitOperations.TrailingZeroCount(m);
            if (p < Divisions)
                yield return p;
            m &= m - 1;
        }
    }

    public override string ToString() => ToDisplay();

    /// <summary>Counting-notation display: 1 e &amp; a, t T, - for none, pN for other 48ths.</summary>
    public string ToDisplay()
    {
        if (DisplayCache.TryGetValue(Mask, out string? cached))
            return cached;
        return DisplayCache.GetOrAdd(Mask, static mask => FormatMask(mask));
    }

    private static string FormatMask(ulong mask)
    {
        if (mask == 0)
            return "-";
        var parts = new List<string>(BitOperations.PopCount(mask));
        ulong m = mask;
        while (m != 0)
        {
            int p = BitOperations.TrailingZeroCount(m);
            m &= m - 1;
            if (p >= Divisions)
                break;
            parts.Add(p switch
            {
                0 => "1",
                12 => "e",
                24 => "&",
                36 => "a",
                16 => "t",
                32 => "T",
                _ => $"p{p}",
            });
        }

        return string.Join(" ", parts);
    }

    /// <summary>Parse a vocabulary entry: - for none, or space-separated 1/e/&amp;/a/t/T/pN.</summary>
    public static BeatPattern Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = text.Trim();
        if (text is "" or "-")
            return Empty;
        ulong mask = 0;
        foreach (var token in text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            int p = token switch
            {
                "1" => 0,
                "e" => 12,
                "&" => 24,
                "a" => 36,
                "t" => 16,
                "T" => 32,
                _ when token.Length > 1 && (token[0] == 'p' || token[0] == 'P') && int.TryParse(token[1..], out int n) && n is >= 0 and < 48 => n,
                _ => throw new FormatException($"Unknown pattern token '{token}'."),
            };
            mask |= 1UL << p;
        }

        return new BeatPattern(mask);
    }

    public static bool TryParse(string text, out BeatPattern pattern)
    {
        try
        {
            pattern = Parse(text);
            return true;
        }
        catch
        {
            pattern = Empty;
            return false;
        }
    }
}
