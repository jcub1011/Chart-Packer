using ChartPacker.Core.Jobs;
using ChartPacker.Core.Scanning;

namespace ChartPacker.Core.Dedup;

/// <summary>The copy that stays when duplicates are deleted: a song in the library, or a merge's output.</summary>
/// <param name="Path">Its path.</param>
/// <param name="Kind">Whether it's a <c>.sng</c> or a folder.</param>
/// <param name="Stamp">How it looked when it was chosen.</param>
public sealed record DeletionKeeper(string Path, SongEntryKind Kind, EntryStamp Stamp)
{
    /// <summary>Keeps <paramref name="song"/>.</summary>
    public static DeletionKeeper Of(SongFingerprint song)
    {
        ArgumentNullException.ThrowIfNull(song);
        return new DeletionKeeper(song.Entry.FullPath, song.Entry.Kind, song.Stamp);
    }
}

/// <summary>One copy to delete, and the copy that must still be there when it is.</summary>
/// <param name="Victim">The copy to delete.</param>
/// <param name="VictimStamp">How it looked when it was compared.</param>
/// <param name="Keeper">The copy that stays.</param>
/// <param name="Tier">Why it's a duplicate.</param>
/// <param name="Bytes">Its size.</param>
public sealed record DeletionItem(SongEntry Victim, EntryStamp VictimStamp, DeletionKeeper Keeper, DedupTier Tier, long Bytes);

/// <summary>Builds and checks the list of copies to delete. The rules that protect the last copy of a song live here.</summary>
public static class DeletionPlan
{
    /// <summary>Items deleting <paramref name="victims"/> while <paramref name="keeper"/> stays.</summary>
    /// <exception cref="InvalidOperationException">A victim is the keeper.</exception>
    public static IReadOnlyList<DeletionItem> Create(DedupTier tier, DeletionKeeper keeper, IEnumerable<SongFingerprint> victims)
    {
        ArgumentNullException.ThrowIfNull(keeper);
        ArgumentNullException.ThrowIfNull(victims);

        var items = victims.Select(v => new DeletionItem(v.Entry, v.Stamp, keeper, tier, v.Stamp.TotalBytes)).ToList();
        if (items.Any(i => OutputPaths.PathComparer.Equals(Full(i.Victim.FullPath), Full(keeper.Path))))
            throw new InvalidOperationException("The copy being kept can't also be deleted.");
        return items;
    }

    /// <summary>
    /// Checks that <paramref name="items"/> can't lose a song: no copy is deleted twice, no copy
    /// being kept is deleted or sits inside one being deleted, and no copy with unsaved edits is deleted.
    /// </summary>
    /// <exception cref="InvalidOperationException">A rule is broken.</exception>
    public static void Validate(IReadOnlyList<DeletionItem> items, Func<string, bool>? hasDrafts = null)
    {
        ArgumentNullException.ThrowIfNull(items);

        var victims = new HashSet<string>(OutputPaths.PathComparer);
        foreach (var item in items)
        {
            string victim = Full(item.Victim.FullPath);
            if (!victims.Add(victim))
                throw new InvalidOperationException($"'{victim}' is listed twice.");
            if (hasDrafts?.Invoke(item.Victim.FullPath) == true)
                throw new InvalidOperationException($"'{victim}' has unsaved changes.");
        }

        foreach (var keeper in items.Select(i => Full(i.Keeper.Path)).Distinct(OutputPaths.PathComparer))
        {
            foreach (string victim in victims)
            {
                if (IsSameOrInside(keeper, victim))
                    throw new InvalidOperationException($"'{keeper}' is being kept, so '{victim}' can't be deleted.");
            }
        }
    }

    private static bool IsSameOrInside(string path, string folder)
    {
        if (OutputPaths.PathComparer.Equals(path, folder))
            return true;
        string prefix = Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, OutputPaths.PathComparer == StringComparer.Ordinal ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
