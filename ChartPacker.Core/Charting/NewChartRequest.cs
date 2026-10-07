namespace ChartPacker.Core.Charting;

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
}

/// <summary>Everything the removed new-chart wizard asked for (charter-spec C1).</summary>
public sealed record NewChartRequest
{
    /// <summary>The furthest the lead-in can be either way, in milliseconds, for <c>delay</c> to still be read.</summary>
    public const long MaxLeadInMs = Charts.SongChartSettings.MaxDelayMilliseconds;

    /// <summary>The folder the song's folder is made in.</summary>
    public required string ParentFolder { get; init; }

    /// <summary>The song folder's name.</summary>
    public required string FolderName { get; init; }

    /// <summary>The song's name; <c>name</c> in <c>song.ini</c>.</summary>
    public string Name { get; init; } = "";

    /// <summary><c>artist</c> in <c>song.ini</c>.</summary>
    public string Artist { get; init; } = "";

    /// <summary><c>album</c> in <c>song.ini</c>.</summary>
    public string Album { get; init; } = "";

    /// <summary><c>genre</c> in <c>song.ini</c>.</summary>
    public string Genre { get; init; } = "";

    /// <summary><c>year</c> in <c>song.ini</c>.</summary>
    public string Year { get; init; } = "";

    /// <summary><c>charter</c> in <c>song.ini</c>.</summary>
    public string Charter { get; init; } = "";

    /// <summary>The album art to copy in, a png, jpg or jpeg; <see langword="null"/> for none.</summary>
    public string? AlbumArtPath { get; init; }

    /// <summary>The audio files, each with its own stem.</summary>
    public IReadOnlyList<NewChartStem> Stems { get; init; } = [];

    /// <summary>The song's length when it has no audio to measure; <see langword="null"/> to measure the stems.</summary>
    public TimeSpan? Length { get; init; }

    /// <summary>The audio lead-in, saved as <c>delay</c>; 0 for none.</summary>
    public long LeadInMs { get; init; }

    /// <summary>The chart's timing and parts.</summary>
    public required NewChartSettings Chart { get; init; }
}
