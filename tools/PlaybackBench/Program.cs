using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;

// Times what playback runs every frame or audio chunk, and what each call allocates (see README.md):
//
//   dotnet run -c Release --project tools/PlaybackBench -- [--filter *Name*] [BenchmarkDotNet arguments]
//
// Results go to BenchmarkDotNet.Artifacts/results as JSON too, for compare.py.

var config = DefaultConfig.Instance
    .AddJob(Job.Default.WithWarmupCount(5).WithIterationCount(25).WithLaunchCount(2))
    .AddDiagnoser(MemoryDiagnoser.Default)
    .AddExporter(JsonExporter.Full);

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
