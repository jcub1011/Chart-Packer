using ChartShared.Core.Jobs;
using ChartShared.Core.Scanning;

namespace ChartShared.Core.Optimization;

/// <summary>One chart's estimate, as <see cref="LibraryEstimator"/> reports it.</summary>
/// <param name="Chart">The chart.</param>
/// <param name="Plan">What optimizing it would do, which is its estimate; null when it couldn't be planned.</param>
/// <param name="Error">Why it couldn't be planned.</param>
/// <param name="Done">How many charts have been measured, this one included.</param>
/// <param name="Total">How many charts there are to measure.</param>
/// <param name="SampleComplete">Whether every chart of the sample has been measured, so every type that can be predicted is.</param>
public sealed record ChartEstimate(ChartAssets Chart, ChartPlan? Plan, string? Error, int Done, int Total, bool SampleComplete);

/// <summary>
/// Measures what optimizing a library would save, a chart at a time, by planning each one: so a chart's estimate is the
/// plan a run would carry out, with every rule the run applies in it.
/// </summary>
/// <remarks>
/// <para>The charts of <see cref="SampleSelector.Cover"/> are measured first, which gives <see cref="Model"/> every media
/// type to predict the rest from; then the rest, the ones with the most to measure first. <see cref="Prioritize"/> moves a
/// chart to the front.</para>
/// <para>Estimates are taken from, and kept in, the <see cref="PlanServices.Cache"/>, so a chart measured before costs
/// nothing to measure again, and the run that follows doesn't measure at all.</para>
/// <para>Charts are measured several at once. A chart that can't be planned is reported with its error and doesn't stop
/// the rest; only cancellation does.</para>
/// </remarks>
public sealed class LibraryEstimator
{
    private readonly object _sync = new();
    private readonly OptimizeOptions _options;
    private readonly OptimizerSet _optimizers;
    private readonly PlanServices _services;
    private readonly int _parallelism;
    private readonly Dictionary<string, ChartAssets> _pending = new(OutputPaths.PathComparer);
    private readonly Queue<ChartAssets> _sample;
    private readonly Queue<ChartAssets> _rest;
    private readonly Stack<string> _priority = new();
    private readonly HashSet<string> _samplePaths = new(OutputPaths.PathComparer);
    private readonly int _total;
    private int _done;
    private TaskCompletionSource _running = Completed();

    /// <param name="charts">The charts to measure.</param>
    /// <param name="options">The options to plan them with.</param>
    /// <param name="optimizers">The optimizers that measure.</param>
    /// <param name="services">The cache and video gate; none when null.</param>
    /// <param name="model">What's learned as charts are measured; a new one when null.</param>
    /// <param name="parallelism">How many charts are measured at once; below 1 counts as 1.</param>
    /// <param name="quotaPerType">How many files of each media type the sample measures.</param>
    public LibraryEstimator(
        IReadOnlyList<ChartAssets> charts,
        OptimizeOptions options,
        OptimizerSet optimizers,
        PlanServices? services = null,
        SavingsModel? model = null,
        int parallelism = 1,
        int quotaPerType = SampleSelector.DefaultQuotaPerType)
    {
        ArgumentNullException.ThrowIfNull(charts);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(optimizers);

        _options = options;
        _optimizers = optimizers;
        _services = services ?? PlanServices.None;
        _parallelism = Math.Max(1, parallelism);
        Model = model ?? new SavingsModel(options);

        foreach (var chart in charts)
            _pending.TryAdd(chart.Entry.FullPath, chart);
        _total = _pending.Count;

        var sample = SampleSelector.Cover([.. _pending.Values], options, quotaPerType);
        _sample = new Queue<ChartAssets>(sample);
        foreach (var chart in sample)
            _samplePaths.Add(chart.Entry.FullPath);
        Sample = sample;

        _rest = new Queue<ChartAssets>(_pending.Values
            .Where(c => !_samplePaths.Contains(c.Entry.FullPath))
            .Select(c => (Chart: c, Bytes: OptimizationPlanner.UnitsOf(c, options).Where(u => u.NeedsMeasuring).Sum(u => u.InputBytes)))
            .OrderByDescending(c => c.Bytes)
            .ThenBy(c => c.Chart.Entry.FullPath, StringComparer.Ordinal)
            .Select(c => c.Chart));
    }

    /// <summary>What's been learned from the charts measured so far.</summary>
    public SavingsModel Model { get; }

    /// <summary>The charts measured first, in order.</summary>
    public IReadOnlyList<ChartAssets> Sample { get; }

    /// <summary>Whether measuring is held by <see cref="Pause"/>.</summary>
    public bool IsPaused
    {
        get
        {
            lock (_sync)
                return !_running.Task.IsCompleted;
        }
    }

    /// <summary>Has <paramref name="entry"/> measured next, ahead of the sample, when it's still waiting.</summary>
    public void Prioritize(SongEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_sync)
        {
            if (_pending.ContainsKey(entry.FullPath))
                _priority.Push(entry.FullPath);
        }
    }

    /// <summary>Holds measuring once the charts in hand are finished, until <see cref="Resume"/>.</summary>
    public void Pause()
    {
        lock (_sync)
        {
            if (_running.Task.IsCompleted)
                _running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Carries on after <see cref="Pause"/>.</summary>
    public void Resume()
    {
        lock (_sync)
            _running.TrySetResult();
    }

    /// <summary>Measures every chart. Returns when all are measured.</summary>
    /// <param name="progress">Reports each chart as it's measured, one report at a time.</param>
    /// <exception cref="OperationCanceledException">It was cancelled.</exception>
    public async Task RunAsync(IProgress<ChartEstimate>? progress = null, CancellationToken cancellationToken = default)
    {
        var workers = new Task[Math.Min(_parallelism, Math.Max(1, _total))];
        for (int i = 0; i < workers.Length; i++)
            workers[i] = Task.Run(() => WorkAsync(progress, cancellationToken), cancellationToken);
        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    private async Task WorkAsync(IProgress<ChartEstimate>? progress, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task running;
            lock (_sync)
                running = _running.Task;
            await running.WaitAsync(cancellationToken).ConfigureAwait(false);

            if (Take() is not { } chart)
                return;

            ChartPlan? plan = null;
            string? error = null;
            try
            {
                plan = await OptimizationPlanner.PlanAsync(chart, _options, _optimizers, _services, cancellationToken).ConfigureAwait(false);
                Model.Add(plan);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = ex.Message;
            }

            // Reports are made one at a time, so a host's progress never has two calls in flight.
            lock (_sync)
            {
                _samplePaths.Remove(chart.Entry.FullPath);
                progress?.Report(new ChartEstimate(chart, plan, error, ++_done, _total, _samplePaths.Count == 0));
            }
        }
    }

    private ChartAssets? Take()
    {
        lock (_sync)
        {
            while (_priority.TryPop(out string? path))
            {
                if (_pending.Remove(path, out var wanted))
                    return wanted;
            }

            // A chart taken ahead of its turn is still in its queue, and passed over here.
            foreach (var queue in (Queue<ChartAssets>[])[_sample, _rest])
            {
                while (queue.TryDequeue(out var next))
                {
                    if (_pending.Remove(next.Entry.FullPath))
                        return next;
                }
            }

            return null;
        }
    }

    private static TaskCompletionSource Completed()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}
