using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charting.Saving;
using ChartShared.Core.Charts;
using ChartShared.Core.Metadata;
using ChartShared.Core.Scanning;

namespace ChartShared.Core.Validation;

/// <summary>
/// What <see cref="SongDifficultyGeneration"/> does to a library song's chart: its missing difficulties filled (CHR087),
/// star power generated for its parts without any (CHR066), or both.
/// </summary>
/// <param name="Difficulties">Whether every part's missing difficulties are filled.</param>
/// <param name="StarPower">The parts to generate star power for, each that still has none (<see cref="StarPowerGenerator"/>).</param>
/// <param name="Scope">Which difficulties are filled; null for every missing one (<see cref="LowerDifficultyScope.Missing"/>).</param>
public sealed record SongGenerationWork(bool Difficulties, IReadOnlyCollection<ChartInstrument> StarPower, LowerDifficultyScope? Scope = null)
{
    /// <summary>Only the missing difficulties.</summary>
    public static SongGenerationWork DifficultiesOnly { get; } = new(true, []);
}

/// <summary>
/// A library song's chart with its missing difficulties filled, or star power generated
/// (<see cref="SongDifficultyGeneration.Prepare(SongChartRead, IReadOnlyList{KeyValuePair{string, string}}, ReductionDefaults, SongGenerationWork)"/>),
/// before it's written.
/// </summary>
/// <param name="Parts">Each part filled, with its drafts, highest first.</param>
/// <param name="Drafted">The chart with every draft and star power phrase in it.</param>
/// <param name="After">The settings the chart is read with once it's saved, with <paramref name="SongIniChanges"/> made.</param>
/// <param name="SongIniChanges">
/// What saving the chart as a <c>notes.mid</c> changes in <c>song.ini</c>, as a save in Chart Builder would: an imported
/// <c>notes.chart</c>'s values and offset, and the drum keys.
/// </param>
/// <param name="RemoveChart">The <c>notes.chart</c> the new <c>notes.mid</c> takes the place of; null when the song plays a <c>notes.mid</c>.</param>
/// <param name="StarPower">Each part given star power, and its phrases; none when only difficulties were filled.</param>
public sealed record SongGeneration(
    IReadOnlyDictionary<ChartInstrument, GeneratedDifficulties> Parts,
    EditableChart Drafted,
    ChartReadOptions After,
    IReadOnlyList<PendingSongIniChange> SongIniChanges,
    string? RemoveChart,
    IReadOnlyDictionary<ChartInstrument, IReadOnlyList<Phrase>>? StarPower = null)
{
    /// <summary>Whether nothing was filled or generated.</summary>
    public bool IsEmpty => Parts.Values.All(static p => p.Drafts.Count == 0) && StarPower is not { Count: > 0 };

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
    /// <summary>
    /// Stages the chart, the removal of the one it replaces, and its <c>song.ini</c> changes in <paramref name="draft"/>,
    /// all as one add: reverting the chart brings back the one it replaced and puts the keys back.
    /// </summary>
    public void ApplyTo(MetadataDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        draft.AddFile(
            new AddedFile(ChartFiles.MidiName, StagedPath, Length),
            RemoveChart is { } removed ? [removed] : [],
            SongIniChanges.Select(static c => new MetadataChange(c.Key, c.Value)));
    }
}

/// <summary>
/// The library's Generate Lower Difficulties, and its quick fix for CHR066 (charter-spec C16 item 5): a song's missing difficulties filled from
/// those above them by the reduction rules (<see cref="DifficultyGenerator"/>), star power generated for its parts without
/// any (<see cref="StarPowerGenerator"/>), or both, and the chart written as a <c>notes.mid</c> and read back as a Chart
/// Builder save is, to be staged in the song's draft. Nothing in the song is written.
/// </summary>
/// <remarks>
/// <para>Star power is generated first, so the difficulties filled after it keep a note in each phrase, as the reducer
/// seeds them, and none is left empty on one (CHR065).</para>
/// <para>The chart is the one the song plays as drafted (<see cref="DraftedChart"/>), so a song filled once can be filled
/// again. A draft that changes the chart some other way, its offsets or its vocals, is refused: those changes are made to
/// the stored chart as it's saved, which the new chart would take the place of.</para>
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
        return await ReadAsync(files, pairs, draft, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// As <see cref="ReadAsync(SongEntry, IReadOnlyList{KeyValuePair{string, string}}, MetadataDraft?, CancellationToken)"/>,
    /// reading the song through <paramref name="files"/>, already open, so a song read for its metadata isn't opened again.
    /// </summary>
    /// <exception cref="InvalidOperationException">The draft changes the chart otherwise, or the song has no chart, or it's too large.</exception>
    /// <exception cref="IOException">The song or its chart can't be read, or the chart is malformed.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">Reading was cancelled.</exception>
    public static async Task<SongChartRead> ReadAsync(
        SongFileReader files,
        IReadOnlyList<KeyValuePair<string, string>> pairs,
        MetadataDraft? draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(pairs);
        if (!CanGenerate(draft))
            throw new InvalidOperationException("The song's chart has other changes to save first.");

        var chart = DraftedChart.Of(files.TopLevel.Select(static f => f.Name), draft)
            ?? throw new InvalidOperationException("The song has no chart.");
        return await ReadChartAsync(files, chart, pairs, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The song's chart is too large to read.");
    }

    /// <summary>
    /// Reads <paramref name="chart"/>, the chart a song open as <paramref name="files"/> plays as drafted, with
    /// <paramref name="pairs"/>, its <c>song.ini</c> as drafted. The draft isn't checked
    /// (<see cref="CanGenerate"/>): the caller has.
    /// </summary>
    /// <returns>The chart read; null when it's too large to read.</returns>
    /// <exception cref="InvalidOperationException">The chart is malformed.</exception>
    /// <exception cref="IOException">The song or its chart can't be read, or the chart is malformed.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">Reading was cancelled.</exception>
    public static async Task<SongChartRead?> ReadChartAsync(
        SongFileReader files,
        DraftedChart chart,
        IReadOnlyList<KeyValuePair<string, string>> pairs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(pairs);
        if (await chart.ReadAsync(files, ChartReader.MaxBytes, cancellationToken).ConfigureAwait(false) is not { } bytes)
            return null;
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
    public static SongGeneration Prepare(SongChartRead read, IReadOnlyList<KeyValuePair<string, string>> pairs, ReductionDefaults rules) =>
        Prepare(read, pairs, rules, SongGenerationWork.DifficultiesOnly);

    /// <summary>
    /// Does <paramref name="work"/> to <paramref name="read"/>'s chart: star power generated for each of its parts that
    /// still has none, with the defaults (<see cref="StarPowerOptions.Default"/>), then, when it asks, every missing
    /// difficulty in its scope filled (<see cref="DifficultyGenerator.TargetsFor"/>) by <paramref name="rules"/>. Nothing is written,
    /// and the chart read is left as it was. It can run off the UI thread.
    /// </summary>
    /// <param name="pairs">The song's <c>song.ini</c> as drafted.</param>
    /// <exception cref="InvalidOperationException">A draft can't be added to the chart.</exception>
    /// <exception cref="ArgumentException">A draft can't be added to the chart.</exception>
    public static SongGeneration Prepare(SongChartRead read, IReadOnlyList<KeyValuePair<string, string>> pairs, ReductionDefaults rules, SongGenerationWork work)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(work);
        var (load, options, _, import, chartName) = read;
        var chart = load.Chart;
        var starPower = new Dictionary<ChartInstrument, IReadOnlyList<Phrase>>();
        foreach (var part in work.StarPower.Distinct().Order())
        {
            if (chart.PartOf(part) is not { } given || StarPowerGenerator.HasStarPower(given))
                continue;
            var proposals = StarPowerGenerator.Propose(chart, part, StarPowerOptions.Default, options).Proposals;
            if (StarPowerGenerator.CommandOf(part, proposals) is not { } add)
                continue;

            // The chart read is left as it was, as the reductions after leave it.
            if (starPower.Count == 0)
                chart = chart.Clone();
            add.Apply(chart);
            starPower[part] = [.. proposals.Select(static p => p.Phrase)];
        }

        var variant = DifficultyGenerator.DrumsVariantOf(chart, options);
        var parts = new Dictionary<ChartInstrument, GeneratedDifficulties>();
        foreach (var (part, targets) in work.Difficulties ? DifficultyGenerator.TargetsFor(chart, work.Scope ?? LowerDifficultyScope.Missing) : new Dictionary<ChartInstrument, IReadOnlyList<ChartDifficulty>>())
        {
            var generated = DifficultyGenerator.Generate(chart, part, targets, rules, variant, options);
            parts[part] = generated;
            chart = generated.Drafted;
        }

        var loaded = chart.Parts.Select(static p => p.Instrument).ToHashSet();
        var changes = PendingSongIniChanges.Of(chart, new PendingSongIniInputs(loaded, pairs) { Import = import });
        var after = MidiChartWriter.SettingsAfterSave(chart, ChartReadOptions.From(PendingSongIniChanges.Apply(pairs, changes)));
        string? removeChart = chartName.Equals(ChartFiles.MidiName, StringComparison.OrdinalIgnoreCase) ? null : chartName;
        return new SongGeneration(parts, chart, after, changes, removeChart, starPower);
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
        CancellationToken cancellationToken = default) =>
        GenerateAsync(entry, pairs, draft, rules, SongGenerationWork.DifficultiesOnly, stash, cancellationToken);

    /// <summary>
    /// Reads <paramref name="entry"/>'s chart, does <paramref name="work"/> to it and keeps it in one go
    /// (<see cref="ReadAsync"/>, <see cref="Prepare(SongChartRead, IReadOnlyList{KeyValuePair{string, string}}, ReductionDefaults, SongGenerationWork)"/>,
    /// <see cref="StageAsync"/>), off the calling thread.
    /// </summary>
    /// <returns>The chart kept; null when the song has nothing to fill or generate.</returns>
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
        SongGenerationWork work,
        GeneratedChartStash stash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(stash);
        return Task.Run(async () =>
        {
            var read = await ReadAsync(entry, pairs, draft, cancellationToken).ConfigureAwait(false);
            var generation = Prepare(read, pairs, rules, work);
            return generation.IsEmpty ? null : await StageAsync(generation, stash, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }
}
