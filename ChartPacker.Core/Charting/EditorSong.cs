using ChartPacker.Core.Charts;
using ChartPacker.Core.Scanning;

namespace ChartPacker.Core.Charting;

/// <summary>
/// What the Editor has open (charter-spec §4.5): a song opened from a folder or <c>.sng</c> (C2), or a new chart that
/// hasn't been saved yet (C1), which has no folder and plays its audio from the files it was given.
/// </summary>
public sealed class EditorSong
{
    private EditorSong(OpenedSong? opened, EditableChart chart, IReadOnlyList<SongTrack> tracks)
    {
        Opened = opened;
        Chart = chart;
        Tracks = tracks;
        LoadedParts = opened is null ? new HashSet<ChartInstrument>() : chart.Parts.Select(p => p.Instrument).ToHashSet();
    }

    /// <summary>The song as it was opened, or null for a new chart.</summary>
    public OpenedSong? Opened { get; }

    /// <summary>Whether it's a new chart, which has no folder until it's first saved.</summary>
    public bool IsNew => Opened is null;

    /// <summary>The chart, in the editable model.</summary>
    public EditableChart Chart { get; }

    /// <summary>The song's title, as the Editor's title shows it; null for a new chart.</summary>
    public string? Title => Opened?.Title;

    /// <summary>The folder or <c>.sng</c> the song is in; null for a new chart.</summary>
    public SongEntry? Entry => Opened?.Entry;

    /// <summary>The song's <c>song.ini</c> as it was opened; none for a new chart.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Pairs => Opened?.Pairs ?? [];

    /// <summary>How much later than its ticks say the chart plays against the audio; none for a new chart.</summary>
    public TimeSpan Delay => Opened?.Delay ?? TimeSpan.Zero;

    /// <summary>The audio tracks: the song's stems, or a new chart's files, each played from where it is.</summary>
    public IReadOnlyList<SongTrack> Tracks { get; }

    /// <summary>The parts the chart had when it was opened, which need no rating added; none for a new chart.</summary>
    public IReadOnlySet<ChartInstrument> LoadedParts { get; }

    /// <summary>How many things in the chart were left out as it was read; none for a new chart.</summary>
    public int LoadIssueCount => Opened?.Load.Issues.Count ?? 0;

    /// <summary>The value of the metadata key <paramref name="key"/>, as <see cref="OpenedSong.Value"/> reads it; null for a new chart.</summary>
    public string? Value(string key) => Opened?.Value(key);

    /// <summary>A song opened from a folder or <c>.sng</c>, with its stems.</summary>
    public static EditorSong Of(OpenedSong song)
    {
        ArgumentNullException.ThrowIfNull(song);
        return new EditorSong(song, song.Chart, SongTracks.Resolve(song.StoredNames, null));
    }

    /// <summary>A new chart, with the audio it was given, played from those files until it's first saved.</summary>
    public static EditorSong New(EditableChart chart, IReadOnlyList<SongTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(tracks);
        if (tracks.Any(t => !t.IsStaged))
            throw new ArgumentException("A new chart's tracks are played from the files it was given.", nameof(tracks));
        return new EditorSong(null, chart, tracks);
    }
}
