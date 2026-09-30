using System.Text;
using ChartPacker.Core.Scanning;

namespace ChartPacker.Core.Dedup;

/// <summary>Finds duplicate songs in a library: exact copies, copies of one chart, and different charts of one song.</summary>
/// <remarks>
/// <para>
/// Songs are first grouped by what can be read without their contents: metadata, and every file's
/// name and size. Only songs that match that way are hashed, one file at a time from the smallest,
/// and a group stops being hashed once it's down to one song, so the chart and images usually
/// tell songs apart before any audio is read.
/// </para>
/// <para>
/// A folder is compared as the <c>.sng</c> packing it would make, so a folder and the <c>.sng</c>
/// packed from it are exact copies. A folder holding files a <c>.sng</c> can't (see
/// <see cref="SongFingerprint.Extras"/>) is never an exact copy of anything, because deleting it
/// would lose them.
/// </para>
/// </remarks>
public static class DedupAnalyzer
{
    /// <summary>How many songs are read at once.</summary>
    public const int ReadParallelism = 4;

    /// <summary>How many candidate groups are hashed at once.</summary>
    public const int HashParallelism = 2;

    /// <summary>Compares <paramref name="entries"/>.</summary>
    /// <param name="entries">The songs to compare.</param>
    /// <param name="hasDrafts">Whether the song at a path has unsaved metadata edits.</param>
    /// <param name="progress">Told how far the comparison has got.</param>
    /// <param name="cancellationToken">Cancels the comparison.</param>
    /// <exception cref="OperationCanceledException">The comparison was cancelled.</exception>
    public static async Task<DedupAnalysis> AnalyzeAsync(
        IReadOnlyList<SongEntry> entries,
        Func<string, bool>? hasDrafts = null,
        IProgress<DedupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        hasDrafts ??= _ => false;

        var hasher = new ContentHasher();
        var skipped = new Dictionary<string, DedupSkipped>(StringComparer.Ordinal);
        var songs = await ReadAllAsync(entries, hasDrafts, skipped, progress, cancellationToken).ConfigureAwait(false);

        var run = new Run(hasher, skipped, progress);
        var variants = await run.FindVariantsAsync(songs, cancellationToken).ConfigureAwait(false);
        var charts = await run.FindChartsAsync(variants, cancellationToken).ConfigureAwait(false);

        // A song found to have changed while its chart was hashed may already sit in a variant.
        variants = [.. variants.Select(v => run.WithoutSkipped(v)).OfType<DedupVariant>()];
        charts = [.. charts.Select(c => run.WithoutSkipped(c)).OfType<DedupChart>()];

        return new DedupAnalysis(
            [.. variants.Where(v => v.Entries.Count > 1).OrderBy(v => SortKey(v.Entries[0]), StringComparer.OrdinalIgnoreCase)],
            [.. charts.Where(c => c.Variants.Count > 1).OrderBy(c => SortKey(c.Variants[0].Entries[0]), StringComparer.OrdinalIgnoreCase)],
            FindSongs(charts),
            [.. skipped.Values.OrderBy(s => s.Entry.RelativePath, StringComparer.OrdinalIgnoreCase)],
            hasher);
    }

    private static async Task<List<SongFingerprint>> ReadAllAsync(
        IReadOnlyList<SongEntry> entries,
        Func<string, bool> hasDrafts,
        Dictionary<string, DedupSkipped> skipped,
        IProgress<DedupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var results = new SongFingerprint?[entries.Count];
        var failures = new DedupSkipped?[entries.Count];
        int done = 0;
        progress?.Report(new DedupProgress(DedupPhase.Reading, 0, entries.Count, 0));

        var options = new ParallelOptions { MaxDegreeOfParallelism = ReadParallelism, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(Enumerable.Range(0, entries.Count), options, async (i, ct) =>
        {
            var entry = entries[i];
            try
            {
                results[i] = await SongFingerprintReader.ReadAsync(entry, hasDrafts(entry.FullPath), ct).ConfigureAwait(false);
            }
            catch (FingerprintSkippedException ex)
            {
                failures[i] = new DedupSkipped(entry, ex.Reason, ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures[i] = new DedupSkipped(entry, DedupSkipReason.Unreadable, ex.Message);
            }

            int count = Interlocked.Increment(ref done);
            if (count % 25 == 0 || count == entries.Count)
                progress?.Report(new DedupProgress(DedupPhase.Reading, count, entries.Count, 0));
        }).ConfigureAwait(false);

        foreach (var failure in failures)
        {
            if (failure is not null)
                skipped[failure.Entry.FullPath] = failure;
        }

        return [.. results.OfType<SongFingerprint>()];
    }

    private static IReadOnlyList<DedupSong> FindSongs(IReadOnlyList<DedupChart> charts)
    {
        // Charts are joined when any of their songs share a key, since copies of one chart can
        // carry slightly different titles.
        var parent = Enumerable.Range(0, charts.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);

        var firstWithKey = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < charts.Count; i++)
        {
            foreach (string key in charts[i].Entries.Select(e => e.SongKey).OfType<string>().Distinct())
            {
                if (firstWithKey.TryGetValue(key, out int other))
                    parent[Find(i)] = Find(other);
                else
                    firstWithKey[key] = i;
            }
        }

        return
        [
            .. Enumerable.Range(0, charts.Count)
                .Where(i => charts[i].Entries.Any(e => e.SongKey is not null))
                .GroupBy(Find)
                .Where(g => g.Count() > 1)
                .Select(g => g.Select(i => charts[i]).ToList())
                .Select(group => new DedupSong(group.SelectMany(c => c.Entries).Select(e => e.SongKey).OfType<string>().First(), group))
                .OrderBy(s => s.SongKey, StringComparer.Ordinal),
        ];
    }

    private static string SortKey(SongFingerprint song) => song.Entry.RelativePath;

    /// <summary>The state one comparison shares across its stages.</summary>
    private sealed class Run(ContentHasher hasher, Dictionary<string, DedupSkipped> skipped, IProgress<DedupProgress>? progress)
    {
        /// <summary>The shortest time between two progress reports, in milliseconds.</summary>
        private const long ReportInterval = 100;

        private readonly Lock _gate = new();
        private volatile DedupPhase _phase = DedupPhase.Comparing;
        private int _stepsDone;
        private int _stepsTotal;
        private long _lastReport = long.MinValue / 2;

        /// <summary>Groups <paramref name="songs"/> into variants of identical songs.</summary>
        public async Task<List<DedupVariant>> FindVariantsAsync(List<SongFingerprint> songs, CancellationToken cancellationToken)
        {
            var buckets = songs
                .GroupBy(s => s.Extras.Count > 0 ? "\0" + s.Entry.FullPath : Shape(s), StringComparer.Ordinal)
                .Select(g => g.ToList())
                .ToList();

            var candidates = buckets.Where(b => b.Count > 1).ToList();
            var variants = buckets.Where(b => b.Count == 1).Select(b => new DedupVariant(b)).ToList();
            BeginStage(DedupPhase.Comparing, candidates.Count);

            var found = new List<DedupVariant>[candidates.Count];
            var options = new ParallelOptions { MaxDegreeOfParallelism = HashParallelism, CancellationToken = cancellationToken };
            await Parallel.ForEachAsync(Enumerable.Range(0, candidates.Count), options, async (i, ct) =>
            {
                found[i] = await RefineAsync(candidates[i], ct).ConfigureAwait(false);
                StepDone();
            }).ConfigureAwait(false);

            foreach (var list in found)
                variants.AddRange(list);
            return variants;
        }

        /// <summary>Groups <paramref name="variants"/> by their chart, hashing only charts another variant could share.</summary>
        public async Task<List<DedupChart>> FindChartsAsync(List<DedupVariant> variants, CancellationToken cancellationToken)
        {
            var withChart = variants.Where(v => v.Entries[0].ChartName is not null).ToList();
            var bySize = withChart
                .GroupBy(v => (Name: v.Entries[0].ChartName!.ToLowerInvariant(), Length: v.Entries[0].ChartLength!.Value))
                .ToList();

            var charts = bySize.Where(g => g.Count() == 1).Select(g => new DedupChart(g.First().Entries[0].ChartName!, null, [g.First()])).ToList();
            var candidates = bySize.Where(g => g.Count() > 1).ToList();
            BeginStage(DedupPhase.MatchingCharts, candidates.Count);

            foreach (var group in candidates)
            {
                var hashed = new List<(DedupVariant Variant, string? Hash)>();
                foreach (var variant in group)
                    hashed.Add((variant, await ChartHashAsync(variant, cancellationToken).ConfigureAwait(false)));

                foreach (var same in hashed.GroupBy(h => h.Hash, StringComparer.Ordinal))
                {
                    var members = same.Select(h => h.Variant).ToList();
                    if (same.Key is null)
                        charts.AddRange(members.Select(v => new DedupChart(v.Entries[0].ChartName!, null, [v])));
                    else
                        charts.Add(new DedupChart(members[0].Entries[0].ChartName!, same.Key, members));
                }

                StepDone();
            }

            return charts;
        }

        public DedupVariant? WithoutSkipped(DedupVariant variant)
        {
            lock (_gate)
            {
                if (!variant.Entries.Any(e => skipped.ContainsKey(e.Entry.FullPath)))
                    return variant;
                var left = variant.Entries.Where(e => !skipped.ContainsKey(e.Entry.FullPath)).ToList();
                return left.Count == 0 ? null : new DedupVariant(left);
            }
        }

        public DedupChart? WithoutSkipped(DedupChart chart)
        {
            var variants = chart.Variants.Select(WithoutSkipped).OfType<DedupVariant>().ToList();
            if (variants.Count == chart.Variants.Count && variants.Zip(chart.Variants).All(p => ReferenceEquals(p.First, p.Second)))
                return chart;
            return variants.Count == 0 ? null : new DedupChart(chart.ChartName, chart.NotesHash, variants);
        }

        /// <summary>Splits a bucket of same-shaped songs by one file's hash at a time, smallest first.</summary>
        private async Task<List<DedupVariant>> RefineAsync(List<SongFingerprint> bucket, CancellationToken cancellationToken)
        {
            var files = bucket[0].Files.OrderBy(f => f.Length).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var groups = new List<List<SongFingerprint>> { bucket };
            var singles = new List<SongFingerprint>();

            foreach (var file in files)
            {
                var next = new List<List<SongFingerprint>>();
                foreach (var group in groups)
                {
                    var hashed = new List<(SongFingerprint Song, string Hash)>();
                    foreach (var song in group)
                    {
                        if (await TryHashAsync(song, file.Name, cancellationToken).ConfigureAwait(false) is { } hash)
                            hashed.Add((song, hash));
                    }

                    foreach (var same in hashed.GroupBy(h => h.Hash, StringComparer.Ordinal))
                    {
                        var members = same.Select(h => h.Song).ToList();
                        if (members.Count > 1)
                            next.Add(members);
                        else
                            singles.Add(members[0]);
                    }
                }

                groups = next;
                if (groups.Count == 0)
                    break;
            }

            // A song with no files at all matches on metadata alone.
            return [.. groups.Select(g => new DedupVariant(g)), .. singles.Select(s => new DedupVariant([s]))];
        }

        private async Task<string?> ChartHashAsync(DedupVariant variant, CancellationToken cancellationToken)
        {
            foreach (var song in variant.Entries)
            {
                if (await TryHashAsync(song, song.ChartName!, cancellationToken).ConfigureAwait(false) is { } hash)
                    return hash;
            }

            return null;
        }

        /// <summary>Hashes a file, or records the song as skipped and returns <see langword="null"/> when it can't be read.</summary>
        private async Task<string?> TryHashAsync(SongFingerprint song, string storedName, CancellationToken cancellationToken)
        {
            try
            {
                string hash = await hasher.HashAsync(song, storedName, cancellationToken).ConfigureAwait(false);
                ReportThrottled(Volatile.Read(ref _stepsDone));
                return hash;
            }
            catch (SongChangedException ex)
            {
                Skip(song, DedupSkipReason.ChangedDuringAnalysis, ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Skip(song, DedupSkipReason.Unreadable, ex.Message);
            }

            return null;
        }

        private void Skip(SongFingerprint song, DedupSkipReason reason, string detail)
        {
            lock (_gate)
                skipped.TryAdd(song.Entry.FullPath, new DedupSkipped(song.Entry, reason, detail));
        }

        /// <summary>Starts a stage of <paramref name="total"/> steps. Stages run one after another.</summary>
        private void BeginStage(DedupPhase phase, int total)
        {
            _phase = phase;
            Volatile.Write(ref _stepsDone, 0);
            Volatile.Write(ref _stepsTotal, total);
            Report(0);
        }

        private void StepDone()
        {
            int done = Interlocked.Increment(ref _stepsDone);
            if (done == Volatile.Read(ref _stepsTotal))
                Report(done);
            else
                ReportThrottled(done);
        }

        /// <summary>Reports at most once every <see cref="ReportInterval"/>, since every hash and step on every worker would otherwise post one.</summary>
        private void ReportThrottled(int done)
        {
            long now = Environment.TickCount64;
            long last = Interlocked.Read(ref _lastReport);
            if (now - last >= ReportInterval && Interlocked.CompareExchange(ref _lastReport, now, last) == last)
                Report(done);
        }

        private void Report(int done)
        {
            Interlocked.Exchange(ref _lastReport, Environment.TickCount64);
            progress?.Report(new DedupProgress(_phase, done, Volatile.Read(ref _stepsTotal), hasher.BytesHashed));
        }

        /// <summary>Everything about a song that can be compared without reading its files.</summary>
        private static string Shape(SongFingerprint song)
        {
            var shape = new StringBuilder(song.MetadataKey);
            foreach (var file in song.Files)
                shape.Append('\n').Append(file.Name.ToLowerInvariant()).Append('\u001F').Append(file.Length);
            return shape.ToString();
        }
    }
}
