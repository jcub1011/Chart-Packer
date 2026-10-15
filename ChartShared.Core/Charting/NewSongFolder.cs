namespace ChartShared.Core.Charting;

/// <summary>An audio file to copy into a new chart.</summary>
/// <param name="SourcePath">The file.</param>
/// <param name="Stem">The stem it's stored as, from <see cref="Scanning.AudioStems.Stems"/>.</param>
public sealed record NewChartStem(string SourcePath, string Stem);

/// <summary>
/// A new chart's song folder as its first save writes it (charter-spec C1, The first save): the chart and
/// <c>song.ini</c> as they're saved, and the files copied in.
/// </summary>
public sealed record NewSongFolder
{
    /// <summary>The folder the song's folder is made in.</summary>
    public required string ParentFolder { get; init; }

    /// <summary>The song folder's name.</summary>
    public required string FolderName { get; init; }

    /// <summary>The chart, written as <c>notes.mid</c>.</summary>
    public required byte[] NotesMid { get; init; }

    /// <summary>The <c>[song]</c> pairs, written as <c>song.ini</c>.</summary>
    public required IReadOnlyList<KeyValuePair<string, string>> SongIni { get; init; }

    /// <summary>The audio files, each with its own stem.</summary>
    public IReadOnlyList<NewChartStem> Stems { get; init; } = [];

    /// <summary>The album art to copy in, a png, jpg or jpeg; <see langword="null"/> for none.</summary>
    public string? AlbumArtPath { get; init; }

    /// <summary>The background image to copy in, as <c>background.&lt;ext&gt;</c>; <see langword="null"/> for none.</summary>
    public string? BackgroundImagePath { get; init; }

    /// <summary>The background video to copy in, as <c>video.&lt;ext&gt;</c>; <see langword="null"/> for none.</summary>
    public string? BackgroundVideoPath { get; init; }
}
