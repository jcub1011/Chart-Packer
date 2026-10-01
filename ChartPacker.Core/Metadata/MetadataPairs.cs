using ChartPacker.Core.Text;

namespace ChartPacker.Core.Metadata;

/// <summary>Looks up values in stored metadata pairs the way the metadata table shows them.</summary>
internal static class MetadataPairs
{
    /// <summary>The first value stored under <paramref name="key"/>, matched ignoring case.</summary>
    public static string? Find(IReadOnlyList<KeyValuePair<string, string>> pairs, string key)
    {
        foreach (var pair in pairs)
        {
            if (pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        }

        return null;
    }

    /// <summary>The value under <paramref name="key"/> without rich-text tags, trimmed; <see langword="null"/> when blank.</summary>
    public static string? Text(IReadOnlyList<KeyValuePair<string, string>> pairs, string key) =>
        Find(pairs, key) is { } value && RichText.ToPlainText(value).Trim() is { Length: > 0 } plain ? plain : null;
}
