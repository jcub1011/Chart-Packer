using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charts;
using ChartShared.Core.Metadata;
using ChartShared.Core.Scanning;

namespace ChartShared.Core.Validation;

/// <summary>What surveying a song's chart for its lower difficulties found (<see cref="SongDifficultySurvey"/>).</summary>
public enum SongSurveyStatus
{
    /// <summary>The chart was read.</summary>
    Ok,

    /// <summary>The song's draft changes its chart some other way, which has to be saved first (<see cref="SongDifficultyGeneration.CanGenerate"/>).</summary>
    ChartEdits,

    /// <summary>The song has no chart.</summary>
    NoChart,

    /// <summary>The chart is too large to read.</summary>
    ChartTooLarge,

    /// <summary>The song or its chart couldn't be read.</summary>
    Unreadable,
}

/// <summary>A part of a surveyed chart that can be filled, and its difficulties with notes.</summary>
public sealed record PartSurvey(ChartInstrument Part, IReadOnlySet<ChartDifficulty> Charted);

/// <summary>A part as a <see cref="LowerDifficultyScope"/> would fill it.</summary>
/// <param name="Charted">Its difficulties with notes.</param>
/// <param name="Targets">The difficulties it'd fill, highest first; none when it has nothing to fill.</param>
public sealed record PartPlan(ChartInstrument Part, IReadOnlySet<ChartDifficulty> Charted, IReadOnlyList<ChartDifficulty> Targets)
{
    /// <summary>Whether <paramref name="difficulty"/> has notes and would be drafted again in their place.</summary>
    public bool Replaces(ChartDifficulty difficulty) => Charted.Contains(difficulty) && Targets.Contains(difficulty);
}

/// <summary>
/// Which difficulties of a song's chart have notes, part by part, and nothing more of the chart, so a batch of many songs
/// can be planned and planned again (<see cref="Plan"/>) as its scope changes, without reading a chart again.
/// </summary>
/// <param name="Parts">Each part that can be filled with notes in it, in the chart's order.</param>
/// <param name="Error">Why the song couldn't be read, when <paramref name="Status"/> is <see cref="SongSurveyStatus.Unreadable"/>.</param>
public sealed record SongDifficultySurvey(SongSurveyStatus Status, IReadOnlyList<PartSurvey> Parts, string? Error = null)
{
    /// <summary>The survey of <paramref name="chart"/>.</summary>
    public static SongDifficultySurvey Of(EditableChart chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        return new SongDifficultySurvey(
            SongSurveyStatus.Ok,
            [.. chart.Parts
                .Where(static p => LowerDifficultyScope.Missing.Covers(p.Instrument))
                .Select(static p => new PartSurvey(p.Instrument, DifficultyGenerator.Charted(p)))
                .Where(static p => p.Charted.Count > 0)]);
    }

    /// <summary>
    /// Surveys the chart the song open as <paramref name="files"/> plays as <paramref name="draft"/> leaves it, read with
    /// <paramref name="pairs"/>, its <c>song.ini</c> as drafted, as <see cref="SongDifficultyGeneration"/> reads it. A
    /// song that can't be read is reported, not thrown.
    /// </summary>
    /// <exception cref="OperationCanceledException">Reading was cancelled.</exception>
    public static async Task<SongDifficultySurvey> ReadAsync(
        SongFileReader files,
        IReadOnlyList<KeyValuePair<string, string>> pairs,
        MetadataDraft? draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(pairs);
        if (!SongDifficultyGeneration.CanGenerate(draft))
            return new SongDifficultySurvey(SongSurveyStatus.ChartEdits, []);
        if (DraftedChart.Of(files.TopLevel.Select(static f => f.Name), draft) is not { } chart)
            return new SongDifficultySurvey(SongSurveyStatus.NoChart, []);

        try
        {
            return await SongDifficultyGeneration.ReadChartAsync(files, chart, pairs, cancellationToken).ConfigureAwait(false) is { } read
                ? Of(read.Load.Chart)
                : new SongDifficultySurvey(SongSurveyStatus.ChartTooLarge, []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            return new SongDifficultySurvey(SongSurveyStatus.Unreadable, [], ex.Message);
        }
    }

    /// <summary>Whether the chart was read.</summary>
    public bool IsOk => Status == SongSurveyStatus.Ok;

    /// <summary>Each part <paramref name="scope"/> covers, as it would fill it, in the chart's order; a part with nothing to fill has no targets.</summary>
    public IReadOnlyList<PartPlan> Plan(LowerDifficultyScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return [.. Parts.Where(p => scope.Covers(p.Part)).Select(p => new PartPlan(p.Part, p.Charted, scope.TargetsOf(p.Part, p.Charted)))];
    }
}
