using System.Collections.Concurrent;
using System.Globalization;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Difficulty;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charting.Lint;
using ChartShared.Core.Charts;
using ChartShared.Core.Metadata;
using ChartShared.Core.Scanning;
using Serilog;

namespace ChartShared.Core.Validation;

/// <summary>Whether a song's chart was linted, or why it wasn't.</summary>
public enum SongChartLintStatus
{
    /// <summary>The chart was read and linted.</summary>
    Linted,

    /// <summary>The song has neither <c>notes.mid</c> nor <c>notes.chart</c>.</summary>
    NoChart,

    /// <summary>The chart is larger than <see cref="ChartReader.MaxBytes"/>.</summary>
    ChartTooLarge,

    /// <summary>The song or its chart couldn't be read.</summary>
    Unreadable,

    /// <summary>A rule failed while checking the chart; the log says why.</summary>
    Failed,
}

/// <summary>A chart lint finding, with where it is in measures and beats for showing it.</summary>
/// <param name="Place">Where its tick is in the song, or null for a finding about the song as a whole.</param>
/// <param name="EndPlace">Where its last tick is, for a finding about a stretch of the chart rather than a point; null otherwise.</param>
public sealed record SongChartFinding(Finding Finding, MeasureBeat? Place, MeasureBeat? EndPlace = null);

/// <summary>What linting a song's chart found (<see cref="SongChartLint"/>).</summary>
/// <param name="Detail">For <see cref="SongChartLintStatus.Unreadable"/>, what went wrong.</param>
public sealed record SongChartLintReport(SongChartLintStatus Status, IReadOnlyList<SongChartFinding> Findings, string? Detail = null)
{
    private static readonly IReadOnlyDictionary<string, int> NoRatings = new Dictionary<string, int>();

    /// <summary>
    /// The suggested tier for each <c>diff_*</c> key CHR083 found unset (charter-spec C18 item 3), which the library's quick
    /// fix sets; none for a part the estimator can't rate.
    /// </summary>
    public IReadOnlyDictionary<string, int> SuggestedRatings { get; init; } = NoRatings;

    /// <summary>
    /// The suggested tier of every <c>diff_*</c> key the chart can be rated for (<see cref="DifficultyEstimator.EstimateSong"/>),
    /// ignoring case, for the inspector's difficulty flyouts: only when asked for (<see cref="SongChartLint.LintAndRateAsync"/>),
    /// so a library's validation doesn't rate every part of every song; null otherwise, or when the chart wasn't linted.
    /// </summary>
    public IReadOnlyDictionary<string, int>? AllSuggestedRatings { get; init; }

    /// <summary>Whether any finding is a Warning or an Error.</summary>
    public bool HasProblems => Findings.Any(static f => f.Finding.Severity != Severity.Info);
}

/// <summary>
/// Chart lint for Chart Packer's library (charter-spec C16 item 5): the chart rules of §6, run on a song's chart as it's
/// stored, with its <c>song.ini</c> as drafted, so charts made outside Chart Builder get the same checks.
/// </summary>
/// <remarks>
/// <para>The chart is read as the games read it today, with the read options as loaded (not as a save would write them).
/// A <c>.chart</c> is converted in memory. The audio is measured for CHR003 and CHR084.</para>
/// <para>Lint never blocks packing in the library: the findings are shown, and nothing else (§15).</para>
/// <para>It runs on the caller's thread, so a batch validation lints on its own workers, and it skips a song whose chart,
/// <c>song.ini</c> and audio are unchanged since the cache last linted it.</para>
/// </remarks>
public static class SongChartLint
{
    private static readonly ChartLinter Linter = new();

    private static readonly IReadOnlyDictionary<string, int> NoRatings = new Dictionary<string, int>();

    private static ILogger Logger => Log.ForContext(typeof(SongChartLint));

    /// <summary>Lints a chart already loaded, with the song's <paramref name="pairs"/>.</summary>
    /// <param name="audioMs">How long the song's audio is, or null when it's not known, which leaves out CHR003 and CHR084.</param>
    /// <param name="rateAllKeys">Whether to rate every key, for <see cref="SongChartLintReport.AllSuggestedRatings"/>.</param>
    public static SongChartLintReport Lint(
        ChartLoadResult load, IReadOnlyList<KeyValuePair<string, string>> pairs, ChartReadOptions options, TimeSpan delay, long? audioMs, bool rateAllKeys = false)
    {
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(options);
        var input = new ChartLintInput(load.Chart, pairs, options, audioMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null, delay);
        var findings = ChartLoadFindings.Merge(Linter.LintAll(input).Findings, ChartLoadFindings.Of(load.Issues));
        var map = load.Chart.TempoMap.ToTempoMap();
        return new SongChartLintReport(
            SongChartLintStatus.Linted,
            [.. findings.Select(f => new SongChartFinding(
                f,
                (f.Location?.Tick ?? f.Tick) is { } tick ? MeasureBeat.At(map, tick) : null,
                f.Location is { } location && location.EndTick > location.Tick ? MeasureBeat.At(map, location.EndTick) : null))])
        {
            SuggestedRatings = SuggestedRatingsOf(findings, load.Chart, options),
            AllSuggestedRatings = rateAllKeys ? AllRatingsOf(load.Chart, options) : null,
        };
    }

    // Every key's suggested tier. An estimate that fails leaves the keys unrated rather than the lint failed.
    private static IReadOnlyDictionary<string, int> AllRatingsOf(EditableChart chart, ChartReadOptions options)
    {
        try
        {
            return DifficultyEstimator.EstimateSong(chart, options);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            Logger.Warning(ex, "Estimating the song's ratings failed");
            return NoRatings;
        }
    }

    // The tier each CHR083 finding's key is suggested, while the chart is in memory: one estimate per part and model, as
    // keys rated by one model share it (Pro drums' diff_drums_real has its own). An estimate that fails leaves its key
    // unrated rather than the lint failed.
    private static IReadOnlyDictionary<string, int> SuggestedRatingsOf(IReadOnlyList<Finding> findings, EditableChart chart, ChartReadOptions options)
    {
        var ratings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var tiers = new Dictionary<(ChartInstrument, DifficultyInstrument), int?>();
        foreach (var finding in findings)
        {
            if (finding.RuleId != ChrRules.Chr083 || finding.Key is not { } key || finding.Location is not { } location)
                continue;
            var model = (location.Part, DifficultyInstruments.OfKey(location.Part, key));
            if (!tiers.TryGetValue(model, out int? tier))
            {
                try
                {
                    tier = PendingSongIniFixes.SuggestedRating(finding, chart, options)?.Tier;
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    Logger.Warning(ex, "Estimating the rating of {Part} failed", location.Part);
                    tier = null;
                }

                tiers[model] = tier;
            }

            if (tier is { } suggested)
                ratings[key] = suggested;
        }

        return ratings;
    }

    /// <summary>
    /// Lints <paramref name="entry"/>'s chart as drafted, with <paramref name="pairs"/>, its <c>song.ini</c> as drafted, or
    /// gives the cached result when nothing it depends on has changed.
    /// </summary>
    /// <param name="draft">
    /// The song's draft, whose staged chart (<see cref="DraftedChart"/>) is linted and whose staged audio is measured in
    /// place of the song's; null for none.
    /// </param>
    /// <param name="cache">Where results are kept for the session; null to lint every time.</param>
    /// <exception cref="OperationCanceledException">Linting was cancelled.</exception>
    public static Task<SongChartLintReport> LintAsync(
        SongEntry entry,
        IReadOnlyList<KeyValuePair<string, string>> pairs,
        MetadataDraft? draft,
        SongChartLintCache? cache,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(pairs);
        return OrUnreadableAsync(async () =>
        {
            using var files = SongFileReader.Open(entry, cancellationToken);
            return await LintOpenAsync(files, pairs, draft, cache, rateAllKeys: false, cancellationToken).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// As <see cref="LintAsync(SongEntry, IReadOnlyList{KeyValuePair{string, string}}, MetadataDraft?, SongChartLintCache?, CancellationToken)"/>,
    /// also rating every <c>diff_*</c> key the chart can be rated for (<see cref="SongChartLintReport.AllSuggestedRatings"/>),
    /// for the inspector's difficulty flyouts, from the one read of the chart. A cached result without them is linted again.
    /// </summary>
    /// <exception cref="OperationCanceledException">Linting was cancelled.</exception>
    public static Task<SongChartLintReport> LintAndRateAsync(
        SongEntry entry,
        IReadOnlyList<KeyValuePair<string, string>> pairs,
        MetadataDraft? draft,
        SongChartLintCache? cache,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(pairs);
        return OrUnreadableAsync(async () =>
        {
            using var files = SongFileReader.Open(entry, cancellationToken);
            return await LintOpenAsync(files, pairs, draft, cache, rateAllKeys: true, cancellationToken).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// As <see cref="LintAsync(SongEntry, IReadOnlyList{KeyValuePair{string, string}}, MetadataDraft?, SongChartLintCache?, CancellationToken)"/>,
    /// reading the song through <paramref name="files"/>, already open, so a <c>.sng</c> read for its metadata isn't
    /// opened and its index read again for its chart and each of its audio files.
    /// </summary>
    /// <exception cref="OperationCanceledException">Linting was cancelled.</exception>
    public static Task<SongChartLintReport> LintAsync(
        SongFileReader files,
        IReadOnlyList<KeyValuePair<string, string>> pairs,
        MetadataDraft? draft,
        SongChartLintCache? cache,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(pairs);
        return OrUnreadableAsync(() => LintOpenAsync(files, pairs, draft, cache, rateAllKeys: false, cancellationToken));
    }

    // A song or chart that can't be read is reported as unreadable, not thrown. A malformed chart is a
    // ChartFormatException, which is an IOException, so it's reported so too.
    private static async Task<SongChartLintReport> OrUnreadableAsync(Func<Task<SongChartLintReport>> lint)
    {
        try
        {
            return await lint().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new SongChartLintReport(SongChartLintStatus.Unreadable, [], ex.Message);
        }
    }

    private static async Task<SongChartLintReport> LintOpenAsync(
        SongFileReader files,
        IReadOnlyList<KeyValuePair<string, string>> pairs,
        MetadataDraft? draft,
        SongChartLintCache? cache,
        bool rateAllKeys,
        CancellationToken cancellationToken)
    {
        var entry = files.Entry;
        IReadOnlyList<string> stored = [.. files.TopLevel.Select(static f => f.Name)];
        if (DraftedChart.Of(stored, draft) is not { } chart)
            return new SongChartLintReport(SongChartLintStatus.NoChart, []);

        var tracks = SongTracks.Resolve(stored, draft);
        var audio = AudioStamps(entry, tracks);
        var key = new SongChartLintKey(entry.FullPath, StampOf(entry, chart), pairs, audio, CultureInfo.CurrentUICulture.Name);
        if (cache?.TryGet(key, rateAllKeys) is { } known)
            return known;

        var report = await LintStoredAsync(files, chart, pairs, tracks, audio, cache, rateAllKeys, cancellationToken).ConfigureAwait(false);
        cache?.Add(key, report);
        return report;
    }

    private static async Task<SongChartLintReport> LintStoredAsync(
        SongFileReader files,
        DraftedChart drafted,
        IReadOnlyList<KeyValuePair<string, string>> pairs,
        IReadOnlyList<SongTrack> tracks,
        IReadOnlyList<AudioStamp> audio,
        SongChartLintCache? cache,
        bool rateAllKeys,
        CancellationToken cancellationToken)
    {
        var entry = files.Entry;
        byte[]? bytes = await drafted.ReadAsync(files, ChartReader.MaxBytes, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
            return new SongChartLintReport(SongChartLintStatus.ChartTooLarge, []);
        var chart = SongOpener.LoadChart(drafted.StoredName, bytes, pairs);
        long? audioMs = cache is not null && cache.TryGetAudioLength(entry.FullPath, audio, out long? measured)
            ? measured
            : SongAudioLength.LongestMs(files, tracks, cancellationToken);
        cache?.AddAudioLength(entry.FullPath, audio, audioMs);

        try
        {
            return Lint(chart.Load, pairs, chart.Options, chart.Delay, audioMs, rateAllKeys);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warning(ex, "Chart lint failed on {Song}", entry.FullPath);
            return new SongChartLintReport(SongChartLintStatus.Failed, [], ex.Message);
        }
    }

    // The chart file as it is, so a changed one is linted again: the song's, or the staged one, named by its path so it's
    // never taken for the song's own.
    private static ChartFileStamp? StampOf(SongEntry entry, DraftedChart chart)
    {
        if (chart.StagedPath is not { } path)
            return ChartFileStamp.Of(entry, chart.StoredName);
        var file = new FileInfo(path);
        return file.Exists ? new ChartFileStamp(path, file.Length, file.LastWriteTimeUtc) : null;
    }

    // The song's audio files as they are, so a changed one is measured again; none for a .sng, whose own stamp covers them.
    private static IReadOnlyList<AudioStamp> AudioStamps(SongEntry entry, IReadOnlyList<SongTrack> tracks)
    {
        var stamps = new List<AudioStamp>(tracks.Count);
        foreach (var track in tracks)
        {
            string? path = track.StagedSourcePath ?? (entry.Kind == SongEntryKind.SngFile ? null : Path.Combine(entry.FullPath, track.StoredName));
            if (path is null)
            {
                stamps.Add(new AudioStamp(track.StoredName, 0, default));
                continue;
            }

            var file = new FileInfo(path);
            stamps.Add(file.Exists ? new AudioStamp(path, file.Length, file.LastWriteTimeUtc) : new AudioStamp(path, -1, default));
        }

        return stamps;
    }
}

/// <summary>An audio file as it was when measured.</summary>
public readonly record struct AudioStamp(string Path, long Length, DateTime LastWriteTimeUtc);

/// <summary>What a song's chart lint result depends on: its chart file, its <c>song.ini</c> as drafted, its audio, and the language the messages are in.</summary>
public sealed class SongChartLintKey : IEquatable<SongChartLintKey>
{
    private readonly KeyValuePair<string, string>[] _pairs;
    private readonly AudioStamp[] _audio;
    private readonly int _hash;

    public SongChartLintKey(string path, ChartFileStamp? chart, IReadOnlyList<KeyValuePair<string, string>> pairs, IReadOnlyList<AudioStamp> audio, string culture)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(audio);
        Path = path;
        Chart = chart;
        _pairs = [.. pairs];
        _audio = [.. audio];
        Culture = culture;
        var hash = new HashCode();
        hash.Add(path, StringComparer.Ordinal);
        hash.Add(chart);
        foreach (var pair in _pairs)
        {
            hash.Add(pair.Key, StringComparer.Ordinal);
            hash.Add(pair.Value, StringComparer.Ordinal);
        }

        foreach (var stamp in _audio)
            hash.Add(stamp);
        hash.Add(culture, StringComparer.Ordinal);
        _hash = hash.ToHashCode();
    }

    public string Path { get; }

    public ChartFileStamp? Chart { get; }

    public string Culture { get; }

    public bool Equals(SongChartLintKey? other) =>
        other is not null
        && _hash == other._hash
        && Path.Equals(other.Path, StringComparison.Ordinal)
        && Equals(Chart, other.Chart)
        && Culture.Equals(other.Culture, StringComparison.Ordinal)
        && PairsEqual(_pairs, other._pairs)
        && _audio.AsSpan().SequenceEqual(other._audio);

    public override bool Equals(object? obj) => Equals(obj as SongChartLintKey);

    public override int GetHashCode() => _hash;

    private static bool PairsEqual(KeyValuePair<string, string>[] a, KeyValuePair<string, string>[] b)
    {
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (!string.Equals(a[i].Key, b[i].Key, StringComparison.Ordinal) || !string.Equals(a[i].Value, b[i].Value, StringComparison.Ordinal))
                return false;
        }

        return true;
    }
}

/// <summary>
/// Chart lint results kept for the session (charter-spec C16 item 5), so a song whose chart, <c>song.ini</c> and audio
/// haven't changed isn't linted again, and the audio isn't measured again when only <c>song.ini</c> changed. Nothing is
/// written to disk. Safe to use from many threads.
/// </summary>
public sealed class SongChartLintCache
{
    /// <summary>How many results it keeps; the oldest goes first.</summary>
    public const int Capacity = 4096;

    private readonly ConcurrentDictionary<SongChartLintKey, SongChartLintReport> _reports = new();
    private readonly ConcurrentQueue<SongChartLintKey> _order = new();
    private readonly ConcurrentDictionary<string, (AudioStamp[] Audio, long? Ms)> _audio = new(StringComparer.Ordinal);
    private int _hits;
    private int _measures;

    /// <summary>How many lints the cache answered.</summary>
    public int Hits => Volatile.Read(ref _hits);

    /// <summary>How many times a song's audio was measured.</summary>
    public int AudioMeasures => Volatile.Read(ref _measures);

    /// <summary>How many results it holds.</summary>
    public int Count => _reports.Count;

    // A linted result without every key rated doesn't answer a lint that rates them; one that rates them answers any.
    internal SongChartLintReport? TryGet(SongChartLintKey key, bool rateAllKeys = false)
    {
        if (!_reports.TryGetValue(key, out var report)
            || (rateAllKeys && report is { Status: SongChartLintStatus.Linted, AllSuggestedRatings: null }))
            return null;
        Interlocked.Increment(ref _hits);
        return report;
    }

    internal void Add(SongChartLintKey key, SongChartLintReport report)
    {
        if (_reports.TryAdd(key, report))
            _order.Enqueue(key);
        else
            _reports[key] = report;
        while (_reports.Count > Capacity && _order.TryDequeue(out var oldest))
            _reports.TryRemove(oldest, out _);
    }

    internal bool TryGetAudioLength(string song, IReadOnlyList<AudioStamp> audio, out long? ms)
    {
        if (_audio.TryGetValue(song, out var known) && known.Audio.AsSpan().SequenceEqual([.. audio]))
        {
            ms = known.Ms;
            return true;
        }

        ms = null;
        return false;
    }

    internal void AddAudioLength(string song, IReadOnlyList<AudioStamp> audio, long? ms)
    {
        if (_audio.TryGetValue(song, out var known) && known.Audio.AsSpan().SequenceEqual([.. audio]))
            return;
        Interlocked.Increment(ref _measures);
        _audio[song] = ([.. audio], ms);
    }
}
