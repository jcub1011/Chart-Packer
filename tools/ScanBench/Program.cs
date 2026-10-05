using System.Diagnostics;
using System.Globalization;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Scanning;
using ScanBench;

// Times the library scan on a real library (see README.md):
//
//   dotnet run -c Release --project tools/ScanBench -- <folder> [--parallelism 1,4,16] [--depth N]
//   dotnet run -c Release --project tools/ScanBench -- <folder> --phases [--warm] [--depth N]
//   dotnet run -c Release --project tools/ScanBench -- <folder> --charts [--depth N]
//
// Each parallelism runs SongLibraryLoader.LoadAsync once, as the app's scan does. --phases instead reads the songs
// one at a time on one thread, timing each step of a song's read and counting what ATL reads to measure audio;
// --warm reads them all once first, untimed, so the times leave out the JIT and the disk. --charts times reading each
// song's chart for the library list, warm, and lists the slowest.
// Only the first run after emptying the file cache is cold. Files are only read.

string? root = null;
int[] parallelisms = [4];
int depth = 2;
bool phases = false;
bool warm = false;
bool charts = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--parallelism":
            parallelisms = [.. args[++i].Split(',').Select(p => int.Parse(p, CultureInfo.InvariantCulture))];
            break;
        case "--depth":
            depth = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--phases":
            phases = true;
            break;
        case "--warm":
            warm = true;
            break;
        case "--charts":
            charts = true;
            break;
        default:
            root = args[i];
            break;
    }
}

if (root is null)
{
    Console.Error.WriteLine("Pass the library folder to scan.");
    return 1;
}

if (phases)
{
    Phases.Run(root, depth, warm);
    return 0;
}

if (charts)
{
    Charts.Run(root, depth);
    return 0;
}

Console.WriteLine($"{(System.Runtime.GCSettings.IsServerGC ? "Server" : "Workstation")} GC, {Environment.ProcessorCount} processors.");
Console.WriteLine();
Console.WriteLine("| Parallelism | Found | Read | Unreadable | Time (s) | Songs/s | GC paused (s) | Gen 0/1/2 GCs | Allocated (MiB) |");
Console.WriteLine("|--:|--:|--:|--:|--:|--:|--:|--:|--:|");
foreach (int parallelism in parallelisms)
{
    var progress = new Tally();
    var paused = GC.GetTotalPauseDuration();
    int[] collections = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];
    long allocated = GC.GetTotalAllocatedBytes();
    var watch = Stopwatch.StartNew();
    int found = await SongLibraryLoader.LoadAsync(root, depth, parallelism, progress);
    watch.Stop();

    double seconds = watch.Elapsed.TotalSeconds;
    string gcs = string.Join("/", Enumerable.Range(0, 3).Select(g => GC.CollectionCount(g) - collections[g]));
    Console.WriteLine(
        $"| {parallelism} | {found} | {progress.Read} | {progress.Unreadable} | {seconds:F2} | {progress.Read / seconds:F0} | " +
        $"{(GC.GetTotalPauseDuration() - paused).TotalSeconds:F2} | {gcs} | {(GC.GetTotalAllocatedBytes() - allocated) / (1 << 20)} |");
}

return 0;

/// <summary>Counts what the loader reports. Reports come one at a time, so no locking.</summary>
internal sealed class Tally : IProgress<LibraryLoadProgress>
{
    public int Read { get; private set; }

    public int Unreadable { get; private set; }

    public void Report(LibraryLoadProgress value)
    {
        Read += value.Songs.Count;
        Unreadable += value.Songs.Count(s => s.Summary is null);
    }
}
