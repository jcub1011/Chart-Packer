namespace ChartShared.Core.Metadata;

/// <summary>How a row differs from what's saved.</summary>
public enum MetadataRowState
{
    /// <summary>No selected item has a pending change to the key.</summary>
    Unchanged,

    /// <summary>At least one selected item has a pending change to the key.</summary>
    Edited,

    /// <summary>No selected item had the key, and at least one will have it once saved.</summary>
    Added,

    /// <summary>At least one selected item had the key, and none will have it once saved.</summary>
    Removed,
}

/// <summary>One song's saved metadata together with its unsaved changes.</summary>
/// <param name="Original">The pairs as stored, in order.</param>
/// <param name="Draft">The pending changes. Edits through <see cref="BulkMetadata"/> write to it.</param>
/// <param name="DuplicateKeys">
/// The keys a <c>song.ini</c> repeats, which its reader merged out of <paramref name="Original"/>; none for a
/// <c>.sng</c>, whose repeats stay in <paramref name="Original"/>.
/// </param>
public sealed record MetadataItem(
    IReadOnlyList<KeyValuePair<string, string>> Original,
    MetadataDraft Draft,
    IReadOnlyCollection<string>? DuplicateKeys = null)
{
    /// <summary>
    /// The keys the <c>song.ini</c> repeats that saving the draft would leave repeated: those it doesn't change, as
    /// saving a key writes it once and removing it drops every line of it.
    /// </summary>
    public IReadOnlyList<string> RemainingDuplicateKeys() =>
        DuplicateKeys is null or { Count: 0 } ? [] : [.. DuplicateKeys.Where(k => !Draft.TryGetChange(k, out _))];
}

/// <summary>A row of the metadata editor, merged across every selected item.</summary>
/// <param name="Key">The key, as first seen.</param>
/// <param name="Value">
/// The value every item shares, or <see langword="null"/> when the items' values differ or some
/// item doesn't have the key. For a <see cref="MetadataRowState.Removed"/> row it's the saved value.
/// </param>
/// <param name="State">How the row differs from what's saved.</param>
public sealed record BulkMetadataRow(string Key, string? Value, MetadataRowState State)
{
    /// <summary>True when the selected items don't all share one value.</summary>
    public bool IsDistinct => Value is null;
}

/// <summary>
/// Merges the metadata of one or more songs into editor rows, and applies edits to all of them at
/// once. One item works the same way as many.
/// </summary>
/// <remarks>
/// Edits only ever touch the key being edited. Setting <c>name</c> across several songs records a
/// change to <c>name</c> in each song's draft and nothing else, so the songs' other values, including
/// ones that differ between them, are left as they are.
/// </remarks>
public static class BulkMetadata
{
    /// <summary>Builds the rows to show for <paramref name="items"/>.</summary>
    /// <remarks>
    /// <para>The rows are the union of every item's saved and drafted keys, compared ignoring case,
    /// sorted by key ignoring case. Each key is spelled as it's first seen, saved keys before drafted ones.</para>
    /// <para>A row that is unchanged and holds an unset difficulty (<c>diff_* = -1</c>) in every item
    /// that has it is left out, as the viewer always has.</para>
    /// </remarks>
    /// <param name="items">The items to merge.</param>
    /// <param name="exclude">Keys to leave out, such as the ones edited elsewhere; none when <see langword="null"/>.</param>
    public static IReadOnlyList<BulkMetadataRow> BuildRows(IReadOnlyList<MetadataItem> items, Func<string, bool>? exclude = null)
    {
        ArgumentNullException.ThrowIfNull(items);

        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            foreach (var pair in item.Original)
            {
                if (seen.Add(pair.Key))
                    keys.Add(pair.Key);
            }
        }

        foreach (var item in items)
        {
            foreach (var change in item.Draft.Changes)
            {
                if (!change.IsRemoval && seen.Add(change.Key))
                    keys.Add(change.Key);
            }
        }

        keys.Sort(StringComparer.OrdinalIgnoreCase);

        var rows = new List<BulkMetadataRow>(keys.Count);
        foreach (string key in keys)
        {
            if (exclude?.Invoke(key) != true && BuildRow(items, key, includeUnset: false) is { } row)
                rows.Add(row);
        }

        return rows;
    }

    /// <summary>
    /// Merges <paramref name="key"/> across <paramref name="items"/> as <see cref="BuildRows"/> does,
    /// but keeps an unset difficulty.
    /// </summary>
    /// <returns>The row, or <see langword="null"/> when no item had or will have the key.</returns>
    public static BulkMetadataRow? RowOf(IReadOnlyList<MetadataItem> items, string key)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrEmpty(key);
        return BuildRow(items, key, includeUnset: true);
    }

    /// <summary>Sets <paramref name="key"/> to <paramref name="value"/> in every item, adding it where it's missing.</summary>
    /// <remarks>
    /// An item that already stores exactly <paramref name="value"/> has its change reverted instead, so a no-op edit
    /// doesn't mark it, unless it stores the key more than once: then the change stays, as saving it removes the repeats.
    /// </remarks>
    public static void Set(IReadOnlyList<MetadataItem> items, string key, string value)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);

        foreach (var item in items)
        {
            if (TryGetOriginal(item, key, out string? original) && original == value && !HasDuplicates(item, key))
                item.Draft.Revert(key);
            else
                item.Draft.Set(key, value);
        }
    }

    /// <summary>Removes <paramref name="key"/> from every item that has it, and drops any pending set on the others.</summary>
    public static void Remove(IReadOnlyList<MetadataItem> items, string key)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrEmpty(key);

        foreach (var item in items)
        {
            if (TryGetOriginal(item, key, out _))
                item.Draft.Remove(key);
            else
                item.Draft.Revert(key);
        }
    }

    /// <summary>Forgets every item's pending change to <paramref name="key"/>.</summary>
    public static void Revert(IReadOnlyList<MetadataItem> items, string key)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(key);

        foreach (var item in items)
            item.Draft.Revert(key);
    }

    private static BulkMetadataRow? BuildRow(IReadOnlyList<MetadataItem> items, string key, bool includeUnset)
    {
        bool anyOriginal = false, anyEffective = false, anyChange = false;
        bool allOriginal = true, allEffective = true;
        string? originalValue = null, effectiveValue = null;
        bool originalsAgree = true, effectivesAgree = true;
        bool allUnset = true;

        foreach (var item in items)
        {
            bool hasOriginal = TryGetOriginal(item, key, out string? original);
            bool hasChange = item.Draft.TryGetChange(key, out var change);
            string? effective = hasChange ? change.Value : original;
            anyChange |= hasChange;

            if (hasOriginal)
            {
                Merge(ref originalValue, ref originalsAgree, anyOriginal, original!);
                anyOriginal = true;
            }
            else
            {
                allOriginal = false;
            }

            if (effective is not null)
            {
                Merge(ref effectiveValue, ref effectivesAgree, anyEffective, effective);
                anyEffective = true;
                allUnset &= MetadataDifficulty.IsUnset(key, effective);
            }
            else
            {
                allEffective = false;
            }
        }

        if (!anyEffective)
        {
            // Nothing will hold the key after saving. If nothing held it before either, there's no row.
            return anyOriginal
                ? new BulkMetadataRow(key, allOriginal && originalsAgree ? originalValue : null, MetadataRowState.Removed)
                : null;
        }

        var state = !anyOriginal ? MetadataRowState.Added
            : anyChange ? MetadataRowState.Edited
            : MetadataRowState.Unchanged;

        if (state == MetadataRowState.Unchanged && allUnset && !includeUnset)
            return null;

        return new BulkMetadataRow(key, allEffective && effectivesAgree ? effectiveValue : null, state);
    }

    private static void Merge(ref string? shared, ref bool agree, bool seenBefore, string value)
    {
        if (!seenBefore)
            shared = value;
        else if (!string.Equals(shared, value, StringComparison.Ordinal))
            agree = false;
    }

    /// <summary>
    /// Gets the item's saved value for <paramref name="key"/>. With duplicate keys (possible in a
    /// <c>.sng</c>) the first one is what's shown; saving a change collapses them.
    /// </summary>
    private static bool TryGetOriginal(MetadataItem item, string key, out string? value)
    {
        foreach (var pair in item.Original)
        {
            if (pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    /// <summary>Whether the item stores <paramref name="key"/> more than once, so saving it, even unchanged, collapses the repeats.</summary>
    private static bool HasDuplicates(MetadataItem item, string key) =>
        item.Original.Count(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) > 1
        || item.DuplicateKeys?.Contains(key, StringComparer.OrdinalIgnoreCase) == true;
}
