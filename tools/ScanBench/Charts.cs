using System.Diagnostics;
using ChartShared.Core.Charts;
using ChartShared.Core.Library;
using ChartShared.Core.Scanning;

namespace ScanBench;

/// <summary>Times reading each song's chart the way the library list does, from memory, with what it allocates, and lists the slowest.</summary>
internal static class Charts
{
    private const int Runs = 5;

    public static void Run(string root, int depth)
    {
        var timed = new List<(string Song, string Name, int Bytes, double Ms, long Allocated)>();
        foreach (var entry in SongLibraryScanner.Enumerate(root, depth))
        {
            string? name;
            byte[]? bytes;
            try
            {
                name = ChartReader.Pick(SongFile.ListTopLevel(entry).Select(f => f.Name));
                bytes = name is null ? null : SongFile.ReadAsync(entry, name, ChartReader.MaxBytes).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (name is null || bytes is null)
                continue;

            // The best of a few runs, the first of which also takes the JIT.
            double best = double.MaxValue;
            long allocated = 0;
            for (int i = 0; i < Runs; i++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                var watch = Stopwatch.StartNew();
                _ = ChartContents.Read(bytes, name);
                best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            }

            timed.Add((entry.RelativePath, name, bytes.Length, best, allocated));
        }

        if (timed.Count == 0)
        {
            Console.WriteLine("No charts found.");
            return;
        }

        var ms = timed.Select(t => t.Ms).Order().ToList();
        Console.WriteLine($"{timed.Count} charts: {ms.Sum():F0} ms in all, median {ms[ms.Count / 2]:F2} ms, slowest {ms[^1]:F2} ms (best of {Runs}).");
        Console.WriteLine();
        Console.WriteLine("| Format | Charts | ms | MiB read | MiB allocated | Allocated per byte read |");
        Console.WriteLine("|---|--:|--:|--:|--:|--:|");
        foreach (var format in timed.GroupBy(t => Path.GetExtension(t.Name)))
        {
            double read = format.Sum(t => (double)t.Bytes), allocated = format.Sum(t => (double)t.Allocated);
            Console.WriteLine($"| {format.Key} | {format.Count()} | {format.Sum(t => t.Ms):F0} | {read / (1 << 20):F1} | {allocated / (1 << 20):F1} | {allocated / read:F1} |");
        }

        Console.WriteLine();
        Console.WriteLine("| Song | Chart | KiB | ms | KiB allocated |");
        Console.WriteLine("|---|---|--:|--:|--:|");
        foreach (var t in timed.OrderByDescending(t => t.Ms).Take(10))
            Console.WriteLine($"| {t.Song} | {t.Name} | {t.Bytes / 1024} | {t.Ms:F2} | {t.Allocated / 1024} |");
    }
}
