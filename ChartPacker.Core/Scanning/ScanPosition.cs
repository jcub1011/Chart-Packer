namespace ChartPacker.Core.Scanning;

/// <summary>
/// Where a song source comes in the library scan's depth-first order, however the folders were
/// actually visited.
/// </summary>
/// <remarks>
/// A position is the path of ranks from the scanned folder down to the entry. In a folder, its
/// <c>.sng</c> files rank first and its subfolders after them, each in case-insensitive name order.
/// Positions compare a rank at a time, and a position sorts before every position it is a prefix
/// of, which is the order <see cref="SongLibraryScanner.Enumerate"/> yields entries in.
/// </remarks>
public sealed class ScanPosition : IComparable<ScanPosition>, IEquatable<ScanPosition>
{
    private readonly int[] _ranks;

    private ScanPosition(int[] ranks) => _ranks = ranks;

    /// <summary>The scanned folder itself.</summary>
    public static ScanPosition Root { get; } = new([]);

    /// <summary>How many folder levels below the scanned folder, plus one for a file.</summary>
    public int Length => _ranks.Length;

    /// <summary>The position of the item ranked <paramref name="rank"/> inside this one.</summary>
    public ScanPosition Child(int rank)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rank);
        int[] ranks = new int[_ranks.Length + 1];
        _ranks.CopyTo(ranks, 0);
        ranks[^1] = rank;
        return new ScanPosition(ranks);
    }

    public int CompareTo(ScanPosition? other)
    {
        if (other is null)
            return 1;

        int common = Math.Min(_ranks.Length, other._ranks.Length);
        for (int i = 0; i < common; i++)
        {
            int result = _ranks[i].CompareTo(other._ranks[i]);
            if (result != 0)
                return result;
        }

        return _ranks.Length.CompareTo(other._ranks.Length);
    }

    public bool Equals(ScanPosition? other) => other is not null && _ranks.AsSpan().SequenceEqual(other._ranks);

    public override bool Equals(object? obj) => Equals(obj as ScanPosition);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (int rank in _ranks)
            hash.Add(rank);
        return hash.ToHashCode();
    }

    public override string ToString() => _ranks.Length == 0 ? "/" : string.Join('/', _ranks);
}
