using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Charting.Lint;

/// <summary>What chart lint checks: a snapshot of the chart, and the song's settings it's read with.</summary>
/// <remarks>
/// The chart is a snapshot, an <see cref="EditableChart.Clone"/> taken when the work starts, which nothing changes while
/// lint reads it, so lint can run off the editing thread (charter-spec §11). A change to song.ini, the read options or
/// the audio length isn't a <see cref="Commands.ChartChange"/>: after one, lint the chart again with
/// <see cref="ChartLinter.LintAll"/>.
/// </remarks>
public sealed class ChartLintInput
{
    /// <param name="chart">A snapshot of the chart, which nothing changes while lint runs.</param>
    /// <param name="songIni">The song's metadata as drafted; none when null.</param>
    /// <param name="options">The settings the chart is read with; when null, those <paramref name="songIni"/> sets.</param>
    /// <param name="audioLength">How long the song's audio is, when it's known.</param>
    public ChartLintInput(
        EditableChart chart,
        IReadOnlyList<KeyValuePair<string, string>>? songIni = null,
        ChartReadOptions? options = null,
        TimeSpan? audioLength = null)
    {
        ArgumentNullException.ThrowIfNull(chart);
        Chart = chart;
        SongIni = songIni is null ? [] : [.. songIni];
        Options = options ?? ChartReadOptions.From(SongIni);
        AudioLength = audioLength;
    }

    /// <summary>The snapshot of the chart.</summary>
    public EditableChart Chart { get; }

    /// <summary>The song's metadata, as drafted.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> SongIni { get; }

    /// <summary>The settings the chart is read with, as the game view uses them.</summary>
    public ChartReadOptions Options { get; }

    /// <summary>How long the song's audio is, or null when it isn't known (for rules such as CHR003).</summary>
    public TimeSpan? AudioLength { get; }
}
