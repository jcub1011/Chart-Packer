using System.Globalization;

namespace ChartPacker.Core.Metadata;

/// <summary>Difficulty keys (<c>diff_guitar</c>, <c>diff_drums_real</c>, …) and their "unset" convention.</summary>
public static class MetadataDifficulty
{
    private const string Prefix = "diff_";

    /// <summary>The value that conventionally means no difficulty is set.</summary>
    public const string UnsetValue = "-1";

    /// <summary>True when <paramref name="key"/> is a difficulty key, compared ignoring case.</summary>
    public static bool IsDifficultyKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when <paramref name="key"/> is a difficulty key and <paramref name="value"/> is a
    /// negative integer, which means no difficulty is set (conventionally <c>-1</c>).
    /// </summary>
    public static bool IsUnset(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return IsDifficultyKey(key)
            && long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long difficulty)
            && difficulty < 0;
    }

    /// <summary>
    /// The tier <paramref name="value"/> sets: a non-negative integer, or <see langword="null"/> when
    /// it's missing, unset (negative) or not an integer.
    /// </summary>
    public static int? TierOf(string? value) =>
        int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int tier) && tier >= 0 ? tier : null;

    /// <summary>
    /// Unsets the difficulty <paramref name="key"/> in every item: an item that stores a difficulty
    /// has it set to <c>-1</c>, and one that stores none, or an unset one, has its change reverted.
    /// </summary>
    public static void Unset(IReadOnlyList<MetadataItem> items, string key)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrEmpty(key);

        foreach (var item in items)
        {
            var stored = item.Original.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (stored.Key is null || IsUnset(key, stored.Value))
                item.Draft.Revert(key);
            else
                item.Draft.Set(key, UnsetValue);
        }
    }

    /// <summary>
    /// True when no item will have the difficulty <paramref name="key"/> set once its draft is saved:
    /// each will lack the key or hold a negative integer. A value that isn't an integer counts as set.
    /// </summary>
    public static bool IsUnsetIn(IReadOnlyList<MetadataItem> items, string key)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrEmpty(key);

        foreach (var item in items)
        {
            string? effective = item.Draft.TryGetChange(key, out var change)
                ? change.Value
                : item.Original.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
            if (effective is not null && !IsUnset(key, effective))
                return false;
        }

        return true;
    }
}
