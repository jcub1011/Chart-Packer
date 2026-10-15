using ChartShared.Core.Metadata;

namespace ChartShared.Core.Scanning;

/// <summary>An audio track of a song: a stem stored in it, or one a draft adds.</summary>
/// <param name="StoredName">The name it's stored under, relative to the song.</param>
/// <param name="Stem">Its stem, lowercased, e.g. <c>guitar</c>.</param>
/// <param name="StagedSourcePath">For a track a draft adds, the file it will be copied from; otherwise <see langword="null"/>.</param>
public sealed record SongTrack(string StoredName, string Stem, string? StagedSourcePath)
{
    /// <summary>A draft adds the track: it isn't in the song yet.</summary>
    public bool IsStaged => StagedSourcePath is not null;
}

/// <summary>Works out a song's audio tracks once its draft is saved.</summary>
public static class SongTracks
{
    /// <summary>
    /// The registered audio stems among <paramref name="storedNames"/>, less the ones
    /// <paramref name="draft"/> removes or replaces, plus the ones it adds; in stem order, then by name.
    /// </summary>
    /// <param name="storedNames">The song's files as stored, relative to the song.</param>
    /// <param name="draft">The song's unsaved changes, if any.</param>
    public static IReadOnlyList<SongTrack> Resolve(IEnumerable<string> storedNames, MetadataDraft? draft)
    {
        ArgumentNullException.ThrowIfNull(storedNames);

        var tracks = new List<SongTrack>();
        foreach (string name in storedNames)
        {
            // An add under the same name replaces the stored file.
            if (AudioStems.StemOf(name) is { } stem && draft?.IsFileRemoved(name) != true && draft?.IsFileAdded(name) != true)
                tracks.Add(new SongTrack(name, stem, null));
        }

        if (draft is not null)
        {
            foreach (var added in draft.AddedFiles)
            {
                if (AudioStems.StemOf(added.StoredName) is { } stem)
                    tracks.Add(new SongTrack(added.StoredName, stem, added.SourcePath));
            }
        }

        return [.. tracks
            .OrderBy(t => AudioStems.OrderOf(t.Stem))
            .ThenBy(t => t.StoredName, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The stored files that <paramref name="stem"/> is stored as, in any format, e.g. both
    /// <c>guitar.ogg</c> and <c>Guitar.mp3</c> for <c>guitar</c>. Adding a new file for a stem
    /// replaces all of them.
    /// </summary>
    public static IReadOnlyList<string> FilesOfStem(IEnumerable<string> storedNames, string stem)
    {
        ArgumentNullException.ThrowIfNull(storedNames);
        ArgumentNullException.ThrowIfNull(stem);
        return [.. storedNames.Where(n => stem.Equals(AudioStems.StemOf(n), StringComparison.OrdinalIgnoreCase))];
    }
}
