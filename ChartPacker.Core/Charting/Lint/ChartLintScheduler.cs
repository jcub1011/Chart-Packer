using ChartPacker.Core.Charting.Commands;

namespace ChartPacker.Core.Charting.Lint;

/// <summary>One lint run a <see cref="ChartLintScheduler"/> started: what it lints, and from what.</summary>
/// <param name="Previous">The result the run goes on from.</param>
/// <param name="Input">The snapshot and the song's settings it lints.</param>
/// <param name="Change">What changed since <paramref name="Previous"/>, when it isn't a full run.</param>
/// <param name="IsFull">Whether every rule is run over the whole chart.</param>
public sealed record LintJob(ChartLinter Linter, ChartLintResult Previous, ChartLintInput Input, ChartChange Change, bool IsFull);

/// <summary>
/// Keeps an open chart linted as it's edited (charter-spec C16 item 1), one run at a time, off the editing thread: the
/// editor tells it what each edit touched, and starts a run on a snapshot when it's ready for one.
/// </summary>
/// <remarks>
/// <para>It belongs to the editing thread, but for <see cref="Run"/>, which can run on any. A run started on a snapshot
/// gives the findings for the chart as it was then; the edits made while it ran are linted against its result by the next
/// run, which is what <see cref="ChartLinter.Relint"/> needs.</para>
/// <para>The first run, and the first after the song's settings change or a run fails, is a full one.</para>
/// </remarks>
public sealed class ChartLintScheduler(ChartLinter linter)
{
    private ChartChange _pending = ChartChange.None;
    private bool _full = true;
    private LintJob? _running;

    /// <summary>The findings of the last run that finished.</summary>
    public ChartLintResult Current { get; private set; } = ChartLintResult.Empty;

    /// <summary>Whether the chart has changed since the last run started, so there's another to run.</summary>
    public bool HasWork => _full || !_pending.IsEmpty;

    /// <summary>Whether a run has started and not finished.</summary>
    public bool IsRunning => _running is not null;

    /// <summary>Notes what an edit, its undo or its redo touched, for the next run.</summary>
    public void Changed(ChartChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        _pending = _pending.Merge(change);
    }

    /// <summary>Notes that the song's settings changed (song.ini, the read options, the audio's length or the delay), so the next run is a full one.</summary>
    public void SettingsChanged() => _full = true;

    /// <summary>
    /// Starts a run on <paramref name="input"/>, a snapshot of the chart as it is now, with everything changed since the
    /// last; null when one is running, or there's nothing to do. Pass it to <see cref="Run"/>, then to <see cref="Finish"/>.
    /// </summary>
    public LintJob? Start(ChartLintInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (_running is not null || !HasWork)
            return null;

        var job = new LintJob(linter, Current, input, _pending, _full);
        _pending = ChartChange.None;
        _full = false;
        _running = job;
        return job;
    }

    /// <summary>Lints what <paramref name="job"/> says; on any thread.</summary>
    public static ChartLintResult Run(LintJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return job.IsFull ? job.Linter.LintAll(job.Input) : job.Linter.Relint(job.Previous, job.Input, job.Change);
    }

    /// <summary>
    /// Takes <paramref name="job"/>'s result as the current findings; or, when it failed (null), keeps the last and makes
    /// the next run a full one.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="job"/> isn't the run that's running.</exception>
    public void Finish(LintJob job, ChartLintResult? result)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!ReferenceEquals(job, _running))
            throw new InvalidOperationException("That lint run isn't the one running.");

        _running = null;
        if (result is null)
            _full = true;
        else
            Current = result;
    }
}
