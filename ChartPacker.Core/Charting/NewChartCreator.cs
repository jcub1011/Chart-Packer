using ChartPacker.Core.Ini;
using ChartPacker.Core.Packing;
using ChartPacker.Core.Scanning;

namespace ChartPacker.Core.Charting;

/// <summary>Makes a new chart's song folder (charter-spec C1, Create).</summary>
public static class NewChartCreator
{
    private const int BufferSize = 81920;

    /// <summary>
    /// Makes the song folder <see cref="NewChartRequest.FolderName"/> in <see cref="NewChartRequest.ParentFolder"/>,
    /// holding each stem as <c>&lt;stem&gt;.&lt;ext&gt;</c> with the extension lowercased, the album art as
    /// <c>album.&lt;ext&gt;</c>, <c>notes.mid</c> from <see cref="NewChartMidi"/> and <c>song.ini</c> from
    /// <see cref="NewChartIni"/>.
    /// </summary>
    /// <remarks>
    /// Everything is written into a temp folder beside the target, which is then moved into place, so
    /// a failure or cancel leaves nothing behind. An existing folder is never used or touched.
    /// <c>song_length</c> is the longest stem other than the preview clip, or
    /// <see cref="NewChartRequest.Length"/> when no stem's length can be read.
    /// </remarks>
    /// <returns>The new song folder's full path.</returns>
    /// <exception cref="ArgumentException">The folder name, a stem or the album art isn't valid, or two files share a stem.</exception>
    /// <exception cref="DirectoryNotFoundException">The parent folder doesn't exist.</exception>
    /// <exception cref="IOException">The target already exists, or a file can't be read or written.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">Creating was cancelled; nothing was left behind.</exception>
    public static async Task<string> CreateAsync(NewChartRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Check(request);

        string parent = Path.GetFullPath(request.ParentFolder);
        switch (NewChartFolder.ProblemOf(parent, request.FolderName))
        {
            case null:
                break;
            case { Kind: NewChartFolderProblemKind.Exists }:
                throw new IOException($"'{Path.Combine(parent, request.FolderName)}' already exists.");
            case { Kind: NewChartFolderProblemKind.ParentMissing }:
                throw new DirectoryNotFoundException($"'{parent}' doesn't exist.");
            case var problem:
                throw new ArgumentException($"'{request.FolderName}' can't be a song folder's name: {problem.Reason}", nameof(request));
        }

        string target = Path.Combine(parent, request.FolderName);
        string staging = StagingPath.For(target);
        try
        {
            Directory.CreateDirectory(staging);

            long? longest = null;
            foreach (var stem in request.Stems)
            {
                string stored = stem.Stem.ToLowerInvariant() + Path.GetExtension(stem.SourcePath).ToLowerInvariant();
                string path = Path.Combine(staging, stored);
                await CopyAsync(stem.SourcePath, path, cancellationToken).ConfigureAwait(false);
                if (!stem.Stem.Equals(AudioStems.Preview, StringComparison.OrdinalIgnoreCase))
                    longest = Max(longest, Measure(path));
            }

            if (request.AlbumArtPath is { } art)
                await CopyAsync(art, Path.Combine(staging, AlbumArt.StoredNameFor(art)!), cancellationToken).ConfigureAwait(false);

            await File.WriteAllBytesAsync(Path.Combine(staging, ChartFiles.MidiName), NewChartMidi.Build(request.Chart), cancellationToken).ConfigureAwait(false);

            long? length = longest ?? (request.Length is { } entered ? (long)entered.TotalMilliseconds : null);
            await SongIni.WriteAsync(Path.Combine(staging, SongIni.FileName), NewChartIni.Pairs(request, length), cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, target);
            return target;
        }
        finally
        {
            StagingPath.TryDeleteDirectory(staging);
        }
    }

    private static void Check(NewChartRequest request)
    {
        foreach (var stem in request.Stems)
        {
            if (!AudioStems.IsStem(stem.Stem))
                throw new ArgumentException($"'{stem.Stem}' isn't an audio stem.", nameof(request));
            if (!AudioStems.IsAudioExtension(Path.GetExtension(stem.SourcePath)))
                throw new ArgumentException($"'{stem.SourcePath}' isn't an audio file.", nameof(request));
        }

        if (NewChartStems.Problems([.. request.Stems.Select(s => s.Stem)]).Count > 0)
            throw new ArgumentException("Two audio files share a stem.", nameof(request));

        if (request.AlbumArtPath is { } art && AlbumArt.StoredNameFor(art) is null)
            throw new ArgumentException($"'{art}' isn't a png, jpg or jpeg.", nameof(request));

        if (!NewChartSettings.Resolutions.Contains(request.Chart.Resolution))
            throw new ArgumentException($"{request.Chart.Resolution} isn't a resolution a new chart can have.", nameof(request));
    }

    private static async Task CopyAsync(string source, string destination, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    private static long? Measure(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
        return AudioLength.MeasureMs(stream, path);
    }

    private static long? Max(long? a, long? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
}
