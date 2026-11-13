using ChartShared.Core.Optimization;
using static ChartShared.Core.Tests.Optimization.OptimizationTestSupport;

namespace ChartShared.Core.Tests.Optimization;

/// <summary>
/// <see cref="OptimizeOptions.Parallelism"/>: charts planned, estimated and optimized at once, with the results, the
/// progress and the cleanup the same as one at a time.
/// </summary>
[TestClass]
public sealed class OptimizeParallelTests
{
    private DirectoryInfo _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = Directory.CreateTempSubdirectory("chartpacker-optpar-");

    [TestCleanup]
    public void DeleteRoot() => _root.Delete(recursive: true);

    public TestContext TestContext { get; set; }

    private static OptimizeOptions VideoOptions(int parallelism) => Only(OptimizeStrategy.Video) with { Parallelism = parallelism };

    private async Task<List<ChartAssets>> ChartsAsync(int count)
    {
        var charts = new List<ChartAssets>();
        for (int i = 0; i < count; i++)
        {
            var entry = Folder(_root.FullName, $"Song {i:D2}", null, ("guitar.ogg", Bytes(1000, 1)), ("video.mp4", Bytes(4000 + (i * 100), 3)));
            charts.Add(await AssetInspector.InspectAsync(entry, cancellationToken: TestContext.CancellationToken));
        }

        return charts;
    }

    /// <summary>A fake whose optimizing takes a moment and counts how many are at it at once.</summary>
    private sealed class Concurrency
    {
        private int _current;
        private int _max;

        public int Max => Volatile.Read(ref _max);

        public FakeOptimizer Optimizer(int milliseconds = 150) => new()
        {
            OnOptimize = (_, _) =>
            {
                int now = Interlocked.Increment(ref _current);
                int seen;
                while (now > (seen = Volatile.Read(ref _max)) && Interlocked.CompareExchange(ref _max, now, seen) != seen)
                {
                }

                Thread.Sleep(milliseconds);
                Interlocked.Decrement(ref _current);
            },
        };
    }

    private static OptimizerSet SetOf(FakeOptimizer optimizer) => new(optimizer, optimizer, optimizer);

    [TestMethod]
    public async Task OneChartAtATimeNeverOverlaps()
    {
        var charts = await ChartsAsync(4);
        var concurrency = new Concurrency();
        var optimizer = concurrency.Optimizer(50);
        var options = VideoOptions(1);
        var plans = await OptimizationPlanner.PlanAllAsync(charts, options, SetOf(optimizer), cancellationToken: TestContext.CancellationToken);

        var result = await OptimizeJob.RunAsync(plans, options, SetOf(optimizer), null, null, TestContext.CancellationToken);

        Assert.AreEqual(4, result.Succeeded);
        Assert.AreEqual(1, concurrency.Max);
    }

    [TestMethod]
    public async Task ChartsAreOptimizedAtOnceUpToTheParallelismWithResultsInPlanOrder()
    {
        var charts = await ChartsAsync(8);
        var concurrency = new Concurrency();
        var optimizer = concurrency.Optimizer();
        var options = VideoOptions(3);
        var plans = await OptimizationPlanner.PlanAllAsync(charts, options, SetOf(optimizer), cancellationToken: TestContext.CancellationToken);
        var progress = new SyncProgress<OptimizeProgress>();

        var result = await OptimizeJob.RunAsync(plans, options, SetOf(optimizer), null, progress, TestContext.CancellationToken);

        Assert.AreEqual(8, result.Succeeded, string.Join("; ", result.Items.Select(i => i.ErrorMessage)));
        Assert.IsGreaterThan(1, concurrency.Max, "Some were done at once.");
        Assert.IsLessThanOrEqualTo(3, concurrency.Max, "Never more than the parallelism.");
        Assert.AreSequenceEqual(charts.Select(c => c.Entry.FullPath).ToList(), result.Items.Select(i => i.Entry.FullPath).ToList());
        foreach (var chart in charts)
            Assert.AreSequenceEqual(["guitar.ogg", "song.ini", "video.webm"], Names(chart.Entry));
        Assert.AreEqual(0, Leftovers(_root.FullName).Count);

        // Each finished chart is reported once, and the count of finished charts rises by one each time.
        var completed = progress.Reports.Where(r => r.Completed is not null).ToList();
        Assert.HasCount(8, completed);
        Assert.AreSequenceEqual(Enumerable.Range(1, 8).ToList(), completed.Select(r => r.Done).ToList());
        Assert.IsTrue(progress.Reports.All(r => r.Total == 8));
    }

    [TestMethod]
    public async Task ProgressReportsNeverOverlap()
    {
        var charts = await ChartsAsync(6);
        var optimizer = new Concurrency().Optimizer(30);
        var options = VideoOptions(4);
        var plans = await OptimizationPlanner.PlanAllAsync(charts, options, SetOf(optimizer), cancellationToken: TestContext.CancellationToken);
        var progress = new OverlapDetectingProgress();

        await OptimizeJob.RunAsync(plans, options, SetOf(optimizer), null, progress, TestContext.CancellationToken);

        Assert.AreEqual(0, progress.Overlaps, "Two reports were in flight at once.");
        Assert.IsGreaterThan(0, progress.Count);
    }

    private sealed class OverlapDetectingProgress : IProgress<OptimizeProgress>
    {
        private int _inside;
        private int _count;
        private int _overlaps;

        public int Overlaps => Volatile.Read(ref _overlaps);

        public int Count => Volatile.Read(ref _count);

        public void Report(OptimizeProgress value)
        {
            if (Interlocked.Increment(ref _inside) > 1)
                Interlocked.Increment(ref _overlaps);
            Interlocked.Increment(ref _count);
            Thread.Sleep(1);
            Interlocked.Decrement(ref _inside);
        }
    }

    [TestMethod]
    public async Task AChartThatFailsDoesNotStopTheOthersInParallel()
    {
        var charts = await ChartsAsync(5);
        var optimizer = new Concurrency().Optimizer(20);
        optimizer.FailWhen = files => files[0].Asset.Length == 4200;
        var options = VideoOptions(3);
        var plans = await OptimizationPlanner.PlanAllAsync(charts, options, SetOf(optimizer), cancellationToken: TestContext.CancellationToken);

        var result = await OptimizeJob.RunAsync(plans, options, SetOf(optimizer), null, null, TestContext.CancellationToken);

        Assert.AreEqual(4, result.Succeeded);
        Assert.AreEqual(1, result.Failed);
        Assert.AreEqual(OptimizeItemStatus.Failed, result.Items[2].Status);
        Assert.AreEqual(0, Leftovers(_root.FullName).Count);
    }

    [TestMethod]
    public async Task ACancelWithSeveralInFlightLeavesNoTempFiles()
    {
        var charts = await ChartsAsync(6);
        using var cancel = new CancellationTokenSource();
        var concurrency = new Concurrency();
        var optimizer = concurrency.Optimizer(200);
        var inner = optimizer.OnOptimize;
        int started = 0;
        optimizer.OnOptimize = (files, destination) =>
        {
            // Cancels once two are at it, so more than one is in flight when it lands.
            if (Interlocked.Increment(ref started) == 2)
                cancel.CancelAfter(50);
            inner!(files, destination);
        };
        var options = VideoOptions(3);
        var plans = await OptimizationPlanner.PlanAllAsync(charts, options, SetOf(optimizer), cancellationToken: TestContext.CancellationToken);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => OptimizeJob.RunAsync(plans, options, SetOf(optimizer), null, null, cancel.Token));

        Assert.AreEqual(0, Leftovers(_root.FullName).Count);
        foreach (var chart in charts)
        {
            var names = Names(chart.Entry);
            Assert.IsTrue(names.SequenceEqual(["guitar.ogg", "song.ini", "video.mp4"]) || names.SequenceEqual(["guitar.ogg", "song.ini", "video.webm"]),
                "Each chart is as it was or optimized, never half-way: " + string.Join(", ", names));
        }
    }

    [TestMethod]
    public async Task PlanningInParallelGivesTheSamePlansInTheSameOrder()
    {
        var charts = await ChartsAsync(7);
        var sequential = await OptimizationPlanner.PlanAllAsync(charts, VideoOptions(1), SetOf(new FakeOptimizer()), cancellationToken: TestContext.CancellationToken);
        var optimizer = new Concurrency().Optimizer(0);
        var progress = new SyncProgress<(int Done, int Total)>();

        var parallel = await OptimizationPlanner.PlanAllAsync(charts, VideoOptions(4), SetOf(optimizer), progress, TestContext.CancellationToken);

        Assert.HasCount(7, parallel);
        for (int i = 0; i < 7; i++)
        {
            Assert.AreEqual(charts[i].Entry.FullPath, parallel[i].Chart.Entry.FullPath);
            Assert.AreSequenceEqual(
                sequential[i].Actions.Select(a => (a.Strategy, a.Kind, a.OutputName, a.InputBytes, a.EstimatedOutputBytes, a.Reason)).ToList(),
                parallel[i].Actions.Select(a => (a.Strategy, a.Kind, a.OutputName, a.InputBytes, a.EstimatedOutputBytes, a.Reason)).ToList());
        }

        Assert.AreSequenceEqual(Enumerable.Range(1, 7).Select(n => (n, 7)).ToList(), progress.Reports);
    }

    [TestMethod]
    public async Task MeasuringInParallelGivesTheSameSavings()
    {
        var charts = await ChartsAsync(9);
        var options = VideoOptions(1);

        async Task<(Dictionary<string, long> Savings, List<ChartEstimate> Reports)> MeasureAsync(int parallelism)
        {
            var set = new OptimizerSet(null, new FakeOptimizer());
            var progress = new SyncProgress<ChartEstimate>();
            await new LibraryEstimator(charts, options, set, parallelism: parallelism).RunAsync(progress, TestContext.CancellationToken);
            return (progress.Reports.ToDictionary(r => r.Chart.Entry.FullPath, r => SavingsModel.Measured(r.Plan!).TotalBytes), progress.Reports);
        }

        var one = await MeasureAsync(1);
        var several = await MeasureAsync(4);

        Assert.HasCount(9, several.Reports);
        Assert.AreSequenceEqual(Enumerable.Range(1, 9).ToList(), several.Reports.Select(r => r.Done).ToList());
        foreach (var (path, saving) in one.Savings)
            Assert.AreEqual(saving, several.Savings[path], path);
    }

    [TestMethod]
    public void ParallelismBelowOneCountsAsOneAndTheRecommendationIsUsable()
    {
        Assert.AreEqual(1, OptimizeOptions.Default.Parallelism);
        Assert.IsGreaterThanOrEqualTo(1, OptimizeOptions.RecommendedParallelism);
        Assert.IsLessThanOrEqualTo(Math.Max(1, Environment.ProcessorCount), OptimizeOptions.RecommendedParallelism);
    }

    [TestMethod]
    public async Task ZeroParallelismStillRuns()
    {
        var charts = await ChartsAsync(2);
        var optimizer = new FakeOptimizer();
        var options = VideoOptions(0);
        var plans = await OptimizationPlanner.PlanAllAsync(charts, options, SetOf(optimizer), cancellationToken: TestContext.CancellationToken);

        var result = await OptimizeJob.RunAsync(plans, options, SetOf(optimizer), null, null, TestContext.CancellationToken);

        Assert.AreEqual(2, result.Succeeded);
    }
}
