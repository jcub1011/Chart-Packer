using System.Security.Cryptography;
using System.Text;
using ChartPacker.Core.Metadata;

namespace ChartPacker.Core.Dedup;

/// <summary>Reduces metadata to a key that two songs share only when neither has a value the other lacks.</summary>
/// <remarks>
/// Stricter than <see cref="Merging.SongMergeAnalysis.Normalize"/>: that folds legacy keys into
/// their replacements and keeps one value per key, which is right for choosing values to merge
/// but would call two songs identical when one holds a <c>frets</c> credit the other doesn't.
/// </remarks>
internal static class StrictMetadata
{
    /// <summary>
    /// A hash of <paramref name="pairs"/> with keys case-folded (known keys to their canonical
    /// form), every pair kept, values compared exactly, empty values dropped and order ignored.
    /// </summary>
    public static string Key(IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        var folded = new List<(string Key, string Value)>(pairs.Count);
        foreach (var (key, value) in pairs)
        {
            if (value.Length > 0)
                folded.Add((KnownKeys.TryGet(key, out var known) ? known.Key : key.ToLowerInvariant(), value));
        }

        folded.Sort((a, b) =>
        {
            int byKey = string.CompareOrdinal(a.Key, b.Key);
            return byKey != 0 ? byKey : string.CompareOrdinal(a.Value, b.Value);
        });

        var text = new StringBuilder();
        foreach (var (key, value) in folded)
            text.Append(key).Append('\u001F').Append(value).Append('\u001E');

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
