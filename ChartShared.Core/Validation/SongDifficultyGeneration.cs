using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charting.Saving;
using ChartShared.Core.Charts;
using ChartShared.Core.Metadata;
using ChartShared.Core.Scanning;

namespace ChartShared.Core.Validation;

/// <summary>
/// A library song's chart with its missing difficulties filled (<see cref="SongDifficultyGeneration.Prepare"/>), before it's
/// written.
/// </summary>
/// <param name="Parts">Each part filled, with its drafts, highest first.</param>
/// <param name="Drafted">The chart with every draft in it.</param>
/// <param name="After">The settings the chart is read with once it's saved, with <paramref name="SongIniChanges"/> made.</param>
/// <param name="SongIniChanges">
/// What saving the chart as a <c>notes.mid</c> changes in <c>song.ini</c>, as a save in Chart Builder would: an imported
/// <c>notes.chart</c>'s values and offset, and the drum keys.
/// </param>
/// <param name="RemoveChart">The <c>notes.chart</c> the new <c>notes.mid</c> takes the place of; null when the song plays a <c>notes.mid</c>.</param>
public sealed record SongGeneration(
    IReadOnlyDictionary<ChartInstrument, GeneratedDifficulties> Parts,
    EditableChart Drafted,
    ChartReadOptions After,
    IReadOnlyList<PendingSongIniChange> SongIniChanges,
    string? RemoveChart)
{
    /// <summary>Whether nothing was filled.</summary>
    public bool IsEmpty => Parts.Values.All(static p => p.Drafts.Count == 0);

    /// <summary>The check of <paramref name="part"/>'s drafts: their findings, each difficulty's suggested rating, and their order.</summary>
    public ReductionCheck Check(ChartInstrument part) =>
        ReductionCheck.Of(Drafted, part, [.. Parts[part].Drafts.Select(static d => d.Target.Difficulty)], After);

    /// <summary>How each of <paramref name="part"/>'s drafts differs from its source as the games read them, highest first, for a preview.</summary>
    public IReadOnlyList<ReductionDiff> Diff(ChartInstrument part) =>
        ReductionDiff.Of(Drafted, part, [.. Parts[part].Drafts.Select(static d => (d.Source, d.Target.Difficulty))], null, After);
}

/// <summary>A library song's chart as <see cref="SongDifficultyGeneration.ReadAsync"/> read it, as the games read it.</summary>
/// <param name="Options">The settings it was read with.</param>
/// <param name="Delay">How far into the audio its tick 0 is.</param>
/// <param name="Import">What converting a <c>notes.chart</c> gave; null for a <c>notes.mid</c>.</param>
/// <param name="ChartName">The name of the chart read.</param>
public sealed record SongChartRead(ChartLoadResult Load, ChartReadOptions Options, TimeSpan Delay, ChartImportResult? Import, string ChartName);

/// <summary>A library song's chart with its missing difficulties filled, kept in a <see cref="GeneratedChartStash"/> for its draft to add.</summary>
/// <param name="StagedPath">Where the new <c>notes.mid</c> is kept.</param>
/// <param name="Length">Its size in bytes.</param>
/// <param name="RemoveChart">The <c>notes.chart</c> it takes the place of; null for none.</param>
/// <param name="SongIniChanges">What saving it changes in <c>song.ini</c> (<see cref="SongGeneration.SongIniChanges"/>).</param>
public sealed record GeneratedChart(string StagedPath, long Length, string? RemoveChart, IReadOnlyList<PendingSongIniChange> SongIniChanges)
{
    /// <summary>Stages the chart, the removal of the one it replaces, and its <c>song.ini</c> changes in <paramref name="draft"/>.</summary>
    public void ApplyTo(MetadataDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        draft.AddFile(new AddedFile(ChartFiles.MidiName, StagedPath, Length));
        if (RemoveChart is { } removed)
            draft.RemoveFile(removed);
        foreach (var change in SongIniChanges)
        {
            if (change.Value is { } value)
                draft.Set(change.Key, value);
            else
                draft.Remove(change.Key);
        }
    }
}

/// <summary>
/// The library's quick fix for CHR087 (charter-spec C16 item 5): a song's missing difficulties filled from those above them
/// by the reduction rules (<see cref="DifficultyGenerator"/>), and the chart written as a <c>notes.mid</c> and read back as
/// a Chart Builder save is, to be staged in the song's draft. Nothing in the song is written.
/// </summary>
/// <remarks>
/// The chart is the one the song plays as drafted (<see cref="DraftedChart"/>), so a song filled once can be filled again.
/// A draft that changes the chart some other way, its offsets or its vocals, is refused: those changes are made to the
/// stored chart as it's saved, which the new chart would take the place of.
/// </remarks>
public static class SongDifficultyGeneration
{
    /// <summary>Whether a song with <paramref name="draft"/> can be filled: its draft makes no other change to its chart.</summary>
    public static bool CanGenerate(MetadataDraft? draft) => draft is not { HasChartEdits: true };

    /// <summary>
    /// Reads the chart <paramref name="entry"/> plays as <paramref name="draft"/> leaves it, with <paramref name="pairs"/>,
    /// its <c>song.ini</c> as drafted.
    /// </summary>
    /// <exception cref="InvalidOperationException">The draft changes the chart otherwise, or the song has no chart, or it's too large.</exception>
    /// <exception cref="IOException">The song or its chart can't be read, or the chart is malformed.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">Reading was cancelled.</exception>
    public static async Task<SongChartRead> ReadAsync(
        SongEntry entry,
        IReadOnlyList<KeyValuePair<string, string>> pairs,
        MetadataDraft? draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(pairs);
        if (!CanGenerate(draft))
            throw new InvalidOperationException("The song's chart has other changes to save first.");

        using var files = SongFileReader.Open(entry, cancellationToken);
        var chart = DraftedChart.Of(files.TopLevel.Select(static f => f.Name), draft)
            ?? throw new InvalidOperationException("The song has no chart.");
        byte[] bytes = await chart.ReadAsync(files, ChartReader.MaxBytes, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The song's chart is too large to read.");
        var loaded = SongOpener.LoadChart(chart.StoredName, bytes, pairs);
        return new SongChartRead(loaded.Load, loaded.Options, loaded.Delay, loaded.Import, chart.StoredName);
    }

    /// <summary>
    /// Fills every missing difficulty of <paramref name="read"/>'s chart (<see cref="DifficultyGenerator.MissingTargets"/>)
    /// by <paramref name="rules"/>. Nothing is written. It can run off the UI thread.
    /// </summary>
    /// <param name="pairs">The song's <c>song.ini</c> as drafted.</param>
    /// <exception cref="InvalidOperationException">A draft can't be added to the chart.</exception>
    /// <exception cref="ArgumentException">A draft can't be added to the chart.</exception>
    public static SongGeneration Prepare(SongChartRead read, IReadOnlyList<KeyValuePair<string, string>> pairs, ReductionDefaults rules)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(rules);
        var (load, options, _, import, chartName) = read;
        var chart = load.Chart;
        var variant = DifficultyGenerator.DrumsVariantOf(chart, options);
        var parts = new Dictionary<ChartInstrument, GeneratedDifficulties>();
        foreach (var (part, targets) in DifficultyGenerator.MissingTargets(chart))
        {
            var generated = DifficultyGenerator.Generate(chart, part, targets, rules, variant);
            parts[part] = generated;
            chart = generated.Drafted;
        }

        var loaded = chart.Parts.Select(static p => p.Instrument).ToHashSet();
        var changes = PendingSongIniChanges.Of(chart, new PendingSongIniInputs(loaded, pairs) { Import = import });
        var after = MidiChartWriter.SettingsAfterSave(chart, ChartReadOptions.From(PendingSongIniChanges.Apply(pairs, changes)));
        string? removeChart = chartName.Equals(ChartFiles.MidiName, StringComparison.OrdinalIgnoreCase) ? null : chartName;
        return new SongGeneration(parts, chart, after, changes, removeChart);
    }

    /// <summary>
    /// Writes <paramref name="generation"/>'s chart as a <c>notes.mid</c>, reads it back both ways as a save does, and keeps
    /// it in <paramref name="stash"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">The chart didn't read back as it was written, so it isn't kept.</exception>
    /// <exception cref="ChartFormatException">The chart can't be written as MIDI at all.</exception>
    /// <exception cref="IOException">The chart can't be kept.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">Keeping was cancelled.</exception>
    public static async Task<GeneratedChart> StageAsync(SongGeneration generation, GeneratedChartStash stash, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(stash);
        var written = MidiChartWriter.WriteChecked(generation.Drafted, generation.After);
        if (!written.IsValid)
            throw new InvalidDataException("The chart with its new difficulties didn't read back as it was written, so it isn't kept.");
        string path = await stash.KeepAsync(written.Bytes, cancellationToken).ConfigureAwait(false);
        return new GeneratedChart(path, written.Bytes.Length, generation.RemoveChart, generation.SongIniChanges);
    }

    /// <summary>
    /// Reads, fills and keeps <paramref name="entry"/>'s chart in one go (<see cref="ReadAsync"/>, <see cref="Prepare"/>,
    /// <see cref="StageAsync"/>), off the calling thread.
    /// </summary>
    /// <returns>The chart kept; null when the song has nothing to fill.</returns>
    /// <exception cref="InvalidOperationException">The draft changes the chart otherwise, the song has no chart, or a draft can't be added.</exception>
    /// <exception cref="InvalidDataException">The chart didn't read back as it was written.</exception>
    /// <exception cref="IOException">The song can't be read, or the chart can't be kept.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">It was cancelled.</exception>
    public static Task<GeneratedChart?> GenerateAsync(
        SongEntry entry,
        IReadOnlyList<KeyValuePair<string, string>> pairs,
        MetadataDraft? draft,
        ReductionDefaults rules,
        GeneratedChartStash stash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(stash);
        return Task.Run(async () =>
        {
            var read = await ReadAsync(entry, pairs, draft, cancellationToken).ConfigureAwait(false);
            var generation = Prepare(read, pairs, rules);
            return generation.IsEmpty ? null : await StageAsync(generation, stash, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }
}
