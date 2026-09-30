using ChartPacker.Core.Merging;

namespace ChartPacker.Core.Dedup;

/// <summary>How one song's files compare with another's.</summary>
public enum FileDifferenceKind
{
    /// <summary>Only the other song has the file.</summary>
    OnlyInOther,

    /// <summary>Only the reference song has the file.</summary>
    OnlyInReference,

    /// <summary>Both have it, with different contents.</summary>
    Differs,
}

/// <summary>A file that differs between two songs.</summary>
public sealed record FileDifference(string Name, FileDifferenceKind Kind);

/// <summary>A metadata key whose value differs between two songs.</summary>
/// <param name="Key">The key, in its canonical form.</param>
/// <param name="ReferenceValue">The reference song's value; <see langword="null"/> when it has none.</param>
/// <param name="OtherValue">The other song's value; <see langword="null"/> when it has none.</param>
public sealed record MetadataDifference(string Key, string? ReferenceValue, string? OtherValue);

/// <summary>How a song differs from a reference copy of the same chart.</summary>
/// <param name="Metadata">Metadata keys that differ.</param>
/// <param name="Files">Files that differ.</param>
/// <param name="IdenticalFiles">How many files both have with the same contents.</param>
public sealed record SongDifferences(IReadOnlyList<MetadataDifference> Metadata, IReadOnlyList<FileDifference> Files, int IdenticalFiles);

/// <summary>Describes how two songs differ.</summary>
public static class DedupDifferences
{
    /// <summary>
    /// Compares <paramref name="other"/> with <paramref name="reference"/>. Files both have with the
    /// same size are hashed through <paramref name="hasher"/>, so hashes already taken aren't repeated.
    /// </summary>
    /// <exception cref="IOException">A file can't be read, or a song changed since it was fingerprinted.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">The comparison was cancelled.</exception>
    public static async Task<SongDifferences> CompareAsync(
        SongFingerprint reference,
        SongFingerprint other,
        ContentHasher hasher,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(other);
        ArgumentNullException.ThrowIfNull(hasher);

        var theirs = other.Files.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        var files = new List<FileDifference>();
        int identical = 0;
        foreach (var file in reference.Files)
        {
            if (!theirs.Remove(file.Name, out var match))
            {
                files.Add(new FileDifference(file.Name, FileDifferenceKind.OnlyInReference));
                continue;
            }

            bool same = match.Length == file.Length
                && await hasher.HashAsync(reference, file.Name, cancellationToken).ConfigureAwait(false)
                    == await hasher.HashAsync(other, match.Name, cancellationToken).ConfigureAwait(false);
            if (same)
                identical++;
            else
                files.Add(new FileDifference(file.Name, FileDifferenceKind.Differs));
        }

        files.AddRange(theirs.Values.Select(f => new FileDifference(f.Name, FileDifferenceKind.OnlyInOther)));
        files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        return new SongDifferences(CompareMetadata(reference.Metadata, other.Metadata), files, identical);
    }

    /// <summary>The keys whose values differ, with keys normalised as the merger reads them.</summary>
    public static IReadOnlyList<MetadataDifference> CompareMetadata(
        IReadOnlyList<KeyValuePair<string, string>> reference,
        IReadOnlyList<KeyValuePair<string, string>> other)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(other);

        var ours = SongMergeAnalysis.Normalize(reference).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        var theirs = SongMergeAnalysis.Normalize(other).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

        var result = new List<MetadataDifference>();
        foreach (string key in ours.Keys.Union(theirs.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            ours.TryGetValue(key, out string? a);
            theirs.TryGetValue(key, out string? b);
            if (!string.Equals(a, b, StringComparison.Ordinal))
                result.Add(new MetadataDifference(key, a, b));
        }

        return result;
    }
}
