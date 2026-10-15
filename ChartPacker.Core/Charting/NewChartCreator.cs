using ChartPacker.Core.Ini;
using ChartPacker.Core.Packing;
using ChartPacker.Core.Scanning;

namespace ChartPacker.Core.Charting;

/// <summary>Makes a new chart's song folder (charter-spec C1, The first save).</summary>
/// <remarks>The chart and <c>song.ini</c> are written as any save writes them.</remarks>
public static class NewChartCreator
{
    private const int BufferSize = 81920;

    /// <summary>
    /// Makes the song folder <see cref="NewSongFolder.FolderName"/> in <see cref="NewSongFolder.ParentFolder"/>, holding
    /// each stem as <c>&lt;stem&gt;.&lt;ext&gt;</c> with the extension lowercased, the album art as <c>album.&lt;ext&gt;</c>,
    /// <see cref="NewSongFolder.NotesMid"/> as <c>notes.mid</c> and <see cref="NewSongFolder.SongIni"/> as <c>song.ini</c>.
    /// </summary>
    /// <remarks>
    /// Everything is written into a temp folder beside the target, which is then moved into place, so a failure or
    /// cancel leaves nothing behind. An existing folder is never used or touched.
    /// </remarks>
    /// <returns>The new song folder's full path.</returns>
    /// <exception cref="ArgumentException">The folder name, a stem or the album art isn't valid, or two files share a stem.</exception>
    /// <exception cref="DirectoryNotFoundException">The parent folder doesn't exist.</exception>
    /// <exception cref="IOException">The target already exists, or a file can't be read or written.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">Creating was cancelled; nothing was left behind.</exception>
    public static async Task<string> CreateAsync(NewSongFolder song, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(song);
        Check(song.Stems, song.AlbumArtPath);
        if (song.BackgroundImagePath is { } imagePath && SongBackgrounds.StoredNameFor(BackgroundKind.Image, imagePath) is null)
            throw new ArgumentException($"'{imagePath}' isn't a background image.", nameof(song));
        if (song.BackgroundVideoPath is { } videoPath && SongBackgrounds.StoredNameFor(BackgroundKind.Video, videoPath) is null)
            throw new ArgumentException($"'{videoPath}' isn't a background video.", nameof(song));

        string parent = Path.GetFullPath(song.ParentFolder);
        switch (NewChartFolder.ProblemOf(parent, song.FolderName))
        {
            case null:
                break;
            case { Kind: NewChartFolderProblemKind.Exists }:
                throw new IOException($"'{Path.Combine(parent, song.FolderName)}' already exists.");
            case { Kind: NewChartFolderProblemKind.ParentMissing }:
                throw new DirectoryNotFoundException($"'{parent}' doesn't exist.");
            case var problem:
                throw new ArgumentException($"'{song.FolderName}' can't be a song folder's name: {problem.Reason}", nameof(song));
        }

        string target = Path.Combine(parent, song.FolderName);
        string staging = StagingPath.For(target);
        try
        {
            Directory.CreateDirectory(staging);

            foreach (var stem in song.Stems)
            {
                string stored = stem.Stem.ToLowerInvariant() + Path.GetExtension(stem.SourcePath).ToLowerInvariant();
                await CopyAsync(stem.SourcePath, Path.Combine(staging, stored), cancellationToken).ConfigureAwait(false);
            }

            if (song.AlbumArtPath is { } art)
                await CopyAsync(art, Path.Combine(staging, AlbumArt.StoredNameFor(art)!), cancellationToken).ConfigureAwait(false);
            if (song.BackgroundImagePath is { } image)
                await CopyAsync(image, Path.Combine(staging, SongBackgrounds.StoredNameFor(BackgroundKind.Image, image)!), cancellationToken).ConfigureAwait(false);
            if (song.BackgroundVideoPath is { } video)
                await CopyAsync(video, Path.Combine(staging, SongBackgrounds.StoredNameFor(BackgroundKind.Video, video)!), cancellationToken).ConfigureAwait(false);

            await File.WriteAllBytesAsync(Path.Combine(staging, ChartFiles.MidiName), song.NotesMid, cancellationToken).ConfigureAwait(false);
            await SongIni.WriteAsync(Path.Combine(staging, SongIni.FileName), song.SongIni, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, target);
            return target;
        }
        finally
        {
            StagingPath.TryDeleteDirectory(staging);
        }
    }

    private static void Check(IReadOnlyList<NewChartStem> stems, string? albumArtPath)
    {
        foreach (var stem in stems)
        {
            if (!AudioStems.IsStem(stem.Stem))
                throw new ArgumentException($"'{stem.Stem}' isn't an audio stem.", nameof(stems));
            if (!AudioStems.IsAudioExtension(Path.GetExtension(stem.SourcePath)))
                throw new ArgumentException($"'{stem.SourcePath}' isn't an audio file.", nameof(stems));
        }

        if (NewChartStems.Problems([.. stems.Select(s => s.Stem)]).Count > 0)
            throw new ArgumentException("Two audio files share a stem.", nameof(stems));

        if (albumArtPath is { } art && AlbumArt.StoredNameFor(art) is null)
            throw new ArgumentException($"'{art}' isn't a png, jpg or jpeg.", nameof(albumArtPath));
    }

    private static async Task CopyAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }
}
