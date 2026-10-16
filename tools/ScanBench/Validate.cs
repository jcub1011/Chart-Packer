using System.Diagnostics;
using ChartShared.Core.Charting;
using ChartShared.Core.Charts;
using ChartShared.Core.Metadata;
using ChartShared.Core.Scanning;
using ChartShared.Core.Validation;

namespace ScanBench;

/// <summary>
/// Times the Validator's check of every song: end to end on as many workers as it uses and fewer, then one song at a
/// time on one thread, split between the steps of <see cref="SongMetadataCheck"/> and <see cref="SongChartLint"/>.
/// </summary>
internal static class Validate
{
    public static async Task RunAsync(string root, int depth)
    {
        var entries = SongLibraryScanner.Enumerate(root, depth).ToList();
        int workers = Math.Max(Environment.ProcessorCount - 1, 1);

        // A first, untimed run takes the JIT out of the times, and warms the file cache.
        await EndToEndAsync(entries, workers);

        Console.WriteLine($"{entries.Count} songs ({entries.Count(e => e.Kind == SongEntryKind.SngFile)} .sng), warm, no lint cache, {(System.Runtime.GCSettings.IsServerGC ? "server" : "workstation")} GC.");
        Console.WriteLine();
        Console.WriteLine("| Workers | Time (s) | Songs/s | GC paused (s) | Gen 0/1/2 GCs |");
        Console.WriteLine("|--:|--:|--:|--:|--:|");
        foreach (int count in new[] { 1, 2, 4, 8, workers }.Distinct())
        {
            var paused = GC.GetTotalPauseDuration();
            int[] collections = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
            var watch = Stopwatch.StartNew();
            await EndToEndAsync(entries, count);
            var elapsed = watch.Elapsed;
            Console.WriteLine(
                $"| {count} | {elapsed.TotalSeconds:F2} | {entries.Count / elapsed.TotalSeconds:F0} | {(GC.GetTotalPauseDuration() - paused).TotalSeconds:F2} | " +
                $"{GC.CollectionCount(0) - collections[0]}/{GC.CollectionCount(1) - collections[1]}/{GC.CollectionCount(2) - collections[2]} |");
        }

        Console.WriteLine();
        var steps = new Steps();
        foreach (var entry in entries)
            await steps.RunAsync(entry);
        steps.Print(entries.Count);
    }

    // What a Validator worker does for a song without a draft.
    private static Task EndToEndAsync(List<SongEntry> entries, int workers) =>
        Parallel.ForEachAsync(entries, new ParallelOptions { MaxDegreeOfParallelism = workers }, async (entry, token) =>
        {
            var (report, files) = await SongMetadataCheck.OpenAsync(entry, token);
            using (files)
            {
                if (files is not null && report.ReadError is null)
                    await SongChartLint.LintAsync(files, report.Pairs, null, null, token);
            }

            MetadataValidator.Validate(report.Pairs, report.DuplicateKeys);
        });

    /// <summary>The steps SongMetadataCheck.ReadAsync and SongChartLint.LintAsync take, each timed, with what it allocates.</summary>
    private sealed class Steps
    {
        private readonly Dictionary<string, TimeSpan> _times = [];
        private readonly Dictionary<string, long> _bytes = [];
        private readonly Stopwatch _watch = new();
        private long _mark;

        public async Task RunAsync(SongEntry entry)
        {
            _watch.Restart();
            _mark = GC.GetAllocatedBytesForCurrentThread();
            var report = SongMetadataCheck.ReadAsync(entry).GetAwaiter().GetResult();
            Add("metadata read");
            MetadataValidator.Validate(report.Pairs, report.DuplicateKeys);
            Add("metadata rules");
            if (report.ReadError is not null)
                return;

            try
            {
                IReadOnlyList<string> stored = [.. SongFile.ListTopLevel(entry).Select(static f => f.Name)];
                Add("list files");
                if (ChartReader.Pick(stored) is not { } chartName)
                    return;

                var tracks = SongTracks.Resolve(stored, null);
                _ = ChartFileStamp.Of(entry, chartName);
                foreach (var track in tracks)
                    _ = entry.Kind == SongEntryKind.SngFile ? 0 : new FileInfo(Path.Combine(entry.FullPath, track.StoredName)).Length;
                Add("stamps");

                byte[]? bytes = SongFile.ReadAsync(entry, chartName, ChartReader.MaxBytes).GetAwaiter().GetResult();
                Add("chart read");
                if (bytes is null)
                    return;

                var chart = SongOpener.LoadChart(chartName, bytes, report.Pairs);
                Add("chart parse");
                long? audioMs = SongAudioLength.LongestMs(entry, tracks);
                Add("audio length");
                try
                {
                    SongChartLint.Lint(chart.Load, report.Pairs, chart.Options, chart.Delay, audioMs);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                }

                Add("lint rules");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
            }

            await Task.CompletedTask;
        }

        public void Print(int songs)
        {
            var total = _times.Values.Aggregate(TimeSpan.Zero, (a, b) => a + b);
            Console.WriteLine("One at a time, on one thread:");
            Console.WriteLine();
            Console.WriteLine("| Step | Time (s) | Share | Allocated (MiB) | KiB/song |");
            Console.WriteLine("|---|--:|--:|--:|--:|");
            foreach (var (step, time) in _times.OrderByDescending(t => t.Value))
                Console.WriteLine($"| {step} | {time.TotalSeconds:F2} | {time / total:P0} | {_bytes[step] / (1024 * 1024)} | {_bytes[step] / 1024 / songs} |");
            Console.WriteLine($"| total | {total.TotalSeconds:F2} | | {_bytes.Values.Sum() / (1024 * 1024)} | |");
        }

        private void Add(string step)
        {
            _times[step] = _times.GetValueOrDefault(step) + _watch.Elapsed;
            long now = GC.GetAllocatedBytesForCurrentThread();
            _bytes[step] = _bytes.GetValueOrDefault(step) + now - _mark;
            _mark = now;
            _watch.Restart();
        }
    }
}
