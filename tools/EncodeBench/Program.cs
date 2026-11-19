using System.Diagnostics;
using System.Globalization;
using ChartShared.Core.Optimization;
using ChartShared.Video;

// Times the Library Optimization Tool's VP8 encodes and estimates on real videos. See README.md.
if (args.Length < 2 || args[0] is not ("speed" or "parallel" or "estimate"))
{
    Console.Error.WriteLine("usage: EncodeBench speed|parallel|estimate <video>... [--frames N] [--target sd|hd|fhd|qhd|uhd] [--cq N] [--threads N]");
    return 2;
}

if (!VideoReencoder.IsAvailable)
{
    Console.Error.WriteLine(VideoReencoder.UnavailableReason);
    return 1;
}

var videos = new List<string>();
int frames = 600;
int cq = VideoReencodeSettings.DefaultCq;
int threads = 4;
var target = VideoTarget.Fhd;
for (int i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--frames": frames = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--cq": cq = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--threads": threads = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--target": target = Enum.Parse<VideoTarget>(args[++i], ignoreCase: true); break;
        default: videos.Add(args[i]); break;
    }
}

var baseline = new VideoReencodeSettings(target, cq, Threads: threads);
switch (args[0])
{
    case "speed": Speed(); break;
    case "parallel": Parallelism(); break;
    default: Estimate(); break;
}

return 0;

// One window of `frames` frames from the middle of the video: how long it takes, and what the whole video would weigh at
// that rate.
(double Fps, long Bytes) Sample(string video, VideoReencodeSettings settings)
{
    long started = Stopwatch.GetTimestamp();
    long bytes = VideoReencoder.EstimateSize(video, settings, 1, frames);
    return (frames / Stopwatch.GetElapsedTime(started).TotalSeconds, bytes);
}

// What each encoder setting costs and saves, one encode at a time.
void Speed()
{
    var configs = new List<(string Name, VideoReencodeSettings Settings)>();
    for (int cpu = 0; cpu <= 5; cpu++)
        configs.Add(($"good cpu-used {cpu}", baseline with { CpuUsedOverride = cpu }));
    foreach (int cpu in new[] { 4, 8, 12, 16 })
        configs.Add(($"realtime cpu-used {cpu}", baseline with { CpuUsedOverride = cpu, Realtime = true }));
    foreach (int count in new[] { 1, 2, 8 })
        configs.Add(($"good cpu-used 2, {count} threads", baseline with { Threads = count }));
    foreach (int slices in new[] { 2, 4, 8 })
        configs.Add(($"good cpu-used 2, {slices} slices", baseline with { Slices = slices }));
    configs.Add(("good cpu-used 2, 8 threads, 8 slices", baseline with { Threads = 8, Slices = 8 }));
    configs.Add(("good cpu-used 2, scaler 4 threads", baseline with { ScalerThreads = 4 }));
    configs.Add(("good cpu-used 2, decoder 2 threads", baseline with { DecoderThreads = 2 }));
    configs.Add(("good cpu-used 2, decoder 1 thread", baseline with { DecoderThreads = 1 }));

    foreach (string video in videos)
    {
        Console.WriteLine($"== {Path.GetFileName(video)} ({frames} frames, {target}, CQ {cq}, {threads} threads unless said)");
        var reference = Sample(video, baseline);
        Console.WriteLine($"{"setting",-40} {"fps",8} {"speed",7} {"size",7}");
        foreach (var (name, settings) in configs)
        {
            var (fps, bytes) = Sample(video, settings);
            Console.WriteLine($"{name,-40} {fps,8:F1} {fps / reference.Fps,6:F2}x {(double)bytes / reference.Bytes,6:F2}x");
        }
    }
}

// How many frames a second the machine encodes in all, by how many videos run at once and how many threads each has.
void Parallelism()
{
    int cores = Environment.ProcessorCount;
    var grid = new List<(int Jobs, int Threads, int Decoder)>();
    foreach (int jobs in new[] { 1, 2, 4, 6, 8, 12, 16 }.Where(j => j <= cores))
    {
        foreach (int count in new[] { 1, 2, 4, 8 })
        {
            // Nothing that leaves most of the machine idle, or asks for several times what it has.
            if (jobs * count >= cores / 2 && jobs * count <= cores * 2)
                grid.Add((jobs, count, 0));
        }
    }

    // The same again with the decoder held to its encode's share, for the shapes that are in the running.
    grid.AddRange(grid.Where(g => g.Jobs >= 4).Select(g => (g.Jobs, g.Threads, Math.Max(1, g.Threads))).ToList());

    Console.WriteLine($"{cores} cores; {frames} frames a job, {target}, CQ {cq}, cpu-used {baseline.CpuUsed}; {videos.Count} videos in turn");
    Console.WriteLine($"{"jobs",5} {"threads",8} {"decoder",8} {"total fps",10}");
    foreach (var (jobs, count, decoder) in grid)
    {
        int work = Math.Max(jobs * 2, videos.Count);
        var settings = baseline with { Threads = count, DecoderThreads = decoder };
        long started = Stopwatch.GetTimestamp();
        Parallel.For(0, work, new ParallelOptions { MaxDegreeOfParallelism = jobs }, i => VideoReencoder.EstimateSize(videos[i % videos.Count], settings, 1, frames));
        double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        Console.WriteLine($"{jobs,5} {count,8} {(decoder == 0 ? "auto" : decoder.ToString(CultureInfo.InvariantCulture)),8} {work * frames / seconds,10:F1}");
    }
}

// How close an estimate made at a faster setting comes to the size a whole encode writes, once it's scaled by the ratio
// the two have on average.
void Estimate()
{
    var candidates = new List<(string Name, VideoReencodeSettings Settings)>
    {
        ("as the encode", baseline),
        ("good cpu-used 4", baseline with { CpuUsedOverride = 4 }),
        ("good cpu-used 5", baseline with { CpuUsedOverride = 5 }),
        ("realtime cpu-used 4", baseline with { CpuUsedOverride = 4, Realtime = true }),
        ("realtime cpu-used 8", baseline with { CpuUsedOverride = 8, Realtime = true }),
        ("realtime cpu-used 12", baseline with { CpuUsedOverride = 12, Realtime = true }),
        ("realtime cpu-used 16", baseline with { CpuUsedOverride = 16, Realtime = true }),
    };

    // The whole encodes, a few at a time; what they write is deleted.
    var actual = new long[videos.Count];
    var encodeSeconds = new double[videos.Count];
    string folder = Directory.CreateTempSubdirectory("encodebench-").FullName;
    try
    {
        Parallel.For(0, videos.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / Math.Max(1, threads)) }, i =>
        {
            string output = Path.Combine(folder, $"{i}.webm");
            long started = Stopwatch.GetTimestamp();
            VideoReencoder.Reencode(videos[i], output, baseline);
            encodeSeconds[i] = Stopwatch.GetElapsedTime(started).TotalSeconds;
            actual[i] = new FileInfo(output).Length;
            File.Delete(output);
        });
    }
    finally
    {
        Directory.Delete(folder, recursive: true);
    }

    var ratios = candidates.Select(_ => new double[videos.Count]).ToArray();
    var seconds = candidates.Select(_ => new double[videos.Count]).ToArray();
    for (int v = 0; v < videos.Count; v++)
    {
        Console.WriteLine($"== {Path.GetFileName(videos[v])}: {actual[v] / 1e6:F1} MB written in {encodeSeconds[v]:F0} s ({new FileInfo(videos[v]).Length / 1e6:F1} MB source)");
        for (int c = 0; c < candidates.Count; c++)
        {
            long started = Stopwatch.GetTimestamp();
            long estimate = VideoReencoder.EstimateSize(videos[v], candidates[c].Settings);
            seconds[c][v] = Stopwatch.GetElapsedTime(started).TotalSeconds;
            ratios[c][v] = (double)estimate / actual[v];
            Console.WriteLine($"   {candidates[c].Name,-22} {seconds[c][v],6:F1} s   estimate/actual {ratios[c][v]:F3}");
        }
    }

    // Scaled by the mean ratio, how far off each video's estimate would be.
    Console.WriteLine();
    Console.WriteLine($"{"estimate made at",-22} {"time",7} {"factor",7} {"mean err",9} {"worst err",10}");
    for (int c = 0; c < candidates.Count; c++)
    {
        double mean = ratios[c].Average();
        var errors = ratios[c].Select(r => Math.Abs((r / mean) - 1)).ToArray();
        Console.WriteLine($"{candidates[c].Name,-22} {seconds[c].Sum() / seconds[0].Sum(),6:F2}x {1 / mean,7:F3} {errors.Average(),9:P1} {errors.Max(),10:P1}");
    }
}
