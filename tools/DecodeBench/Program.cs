using System.Globalization;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Filters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Order;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using DecodeBench;

// Times the app's audio decoders with BenchmarkDotNet, against FFmpeg's (see RESULTS.md):
//
//   dotnet run -c Release --project tools/DecodeBench -- [paths…] [--per-format K] [--iterations N] [--aot] [--list]
//
// A path is an audio file, a .sng (its audio is read in place) or a directory searched for both, which
// keeps its K longest stems of each format. With no paths it reads the test fixtures. --aot adds a
// Native AOT job beside the JIT one, as the app ships; --list only shows the stems found. Files are only read.

var paths = new List<string>();
int perFormat = 2;
int iterations = 10;
bool aot = false;
bool list = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--per-format":
            perFormat = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--iterations":
            iterations = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--aot":
            aot = true;
            break;
        case "--list":
            list = true;
            break;
        default:
            paths.Add(args[i]);
            break;
    }
}

if (paths.Count == 0)
    paths.Add(FindFixtures());

var stems = paths.SelectMany(path => StemLocation.Find(path, perFormat)).ToList();
if (stems.Count == 0)
{
    Console.Error.WriteLine("No audio found.");
    return 1;
}

// Numbered in the order found, so two stems of the same shape are told apart without naming either.
stems = stems.Select((s, i) => s with { Label = $"{s.Label} #{i + 1}" }).ToList();

string? ffmpegProblem = FfmpegReference.UnavailableReason;
PrintStems(stems, ffmpegProblem is null);
if (list)
    return 0;

// BenchmarkDotNet's benchmark processes read the stems from here; the host reads them too, for its parameters.
string serialized = StemLocation.Serialize(stems);
Environment.SetEnvironmentVariable(StemLocation.Variable, serialized);
var variable = new EnvironmentVariable(StemLocation.Variable, serialized);

var job = Job.Default.WithWarmupCount(2).WithIterationCount(iterations).WithLaunchCount(1).WithEnvironmentVariable(variable);
var config = ManualConfig.Create(DefaultConfig.Instance)
    .AddJob(job.WithId("JIT"))
    .AddColumn(StatisticColumn.Median)
    .AddColumn(DerivedColumn.All(stems))
    .WithSummaryStyle(SummaryStyle.Default.WithMaxParameterColumnWidth(80))
    .WithOrderer(new DefaultOrderer(SummaryOrderPolicy.Declared, MethodOrderPolicy.Declared))
    .AddLogicalGroupRules(BenchmarkLogicalGroupRule.ByParams)
    .WithArtifactsPath(Path.Combine(AppContext.BaseDirectory, "BenchmarkDotNet.Artifacts"));

if (aot)
    config = config.AddJob(job.WithRuntime(NativeAotRuntime.Net10_0).WithId("NativeAOT"));

if (ffmpegProblem is not null)
{
    Console.WriteLine($"FFmpeg can't be used, so it isn't timed: {ffmpegProblem}");
    config = config.AddFilter(new SimpleFilter(c => c.Descriptor.WorkloadMethod.Name != nameof(DecodeBenchmarks.Ffmpeg)));
}

BenchmarkRunner.Run<DecodeBenchmarks>(config);
return 0;

// Each stem's shape, and what FFmpeg makes of it: its decoder, and how many frames it gives against the app's.
static void PrintStems(List<StemLocation> stems, bool withFfmpeg)
{
    Console.WriteLine("| Stem | Rate | Frames | FFmpeg decoder | FFmpeg format | FFmpeg frames |");
    Console.WriteLine("|---|--:|--:|---|--:|--:|");
    foreach (var location in stems)
    {
        using var stem = location.Open();
        using var source = stem.OpenSource();
        var buffer = new float[4096 * source.Channels];
        long frames = 0;
        int read;
        while ((read = source.ReadFrames(buffer)) > 0)
            frames += read;

        string ffmpeg = "| - | - | - |";
        if (withFfmpeg)
        {
            try
            {
                using var decoder = FfmpegReference.Open(stem.OpenStream());
                var first = decoder.Run(source.Channels);

                // Each benchmark run rewinds; one that didn't land on the start would read less.
                var second = decoder.Run(source.Channels);
                string again = second.Frames == first.Frames ? "" : $" (rewound: {second.Frames})";
                ffmpeg = $"| {decoder.Name} | {first.SampleFormat} | {first.Frames}{again} |";
            }
            catch (InvalidDataException ex)
            {
                ffmpeg = $"| failed: {ex.Message} | | |";
            }
        }

        Console.WriteLine($"| {location.Label} | {source.SampleRate} | {frames} {ffmpeg}");
    }

    Console.WriteLine();
}

// The test fixtures, found from the working directory or the executable, whichever is in the repo.
static string FindFixtures()
{
    foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            string fixtures = Path.Combine(dir.FullName, "ChartPacker.Core.Tests", "Audio", "Fixtures");
            if (Directory.Exists(fixtures))
                return fixtures;
        }
    }

    throw new DirectoryNotFoundException("The test fixtures weren't found; pass the files to read.");
}
