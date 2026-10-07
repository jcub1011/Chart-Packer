using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using ChartBuilder.Resources;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charting.Lint;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Validation;
using ChartPacker.Core.Validation.QuickFixes;
using ChartPacker.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace ChartBuilder.ViewModels
{
    /// <summary>A mark on the bottom timeline for a finding: where it is in audio time, and how serious.</summary>
    public readonly record struct FindingMark(double Seconds, Severity Severity);

    /// <summary>A row of the findings panel: a lint finding, where it is, and how to go there or fix it.</summary>
    public sealed class FindingItem(Finding finding, string where, bool canFix, Action<Finding> goTo, Action<Finding> fix)
    {
        public Finding Finding => finding;

        public string RuleId => finding.RuleId;

        public string Message => finding.Message;

        /// <summary>Where it is: the part and difficulty, and the measure and beat.</summary>
        public string Where => where;

        public bool IsError => finding.Severity == Severity.Error;

        public bool IsWarning => finding.Severity == Severity.Warning;

        public bool IsInfo => finding.Severity == Severity.Info;

        /// <summary>Whether it has a quick fix to offer: a chart command, or a pending change to restore.</summary>
        public bool CanFix => canFix;

        public IRelayCommand GoToCommand { get; } = new RelayCommand(() => goTo(finding));

        public IRelayCommand FixCommand { get; } = new RelayCommand(() => fix(finding));
    }

    /// <summary>
    /// The findings panel (charter-spec C3 item 5, C16): the chart linted as it's edited, off the UI thread on a snapshot,
    /// each finding linked to its place in the chart, with its quick fix where it has one. The bottom timeline marks them.
    /// </summary>
    /// <remarks>
    /// Edits are linted a moment after they stop, so typing isn't held up, and never in the middle of a drag, whose
    /// preview isn't on the undo stack. Only what each edit touched is checked again (<see cref="ChartLintScheduler"/>).
    /// </remarks>
    public sealed partial class FindingsViewModel : ViewModelBase, IDisposable
    {
        private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(150);

        private readonly EditorSong _song;
        private readonly ChartUndoStack _undo;
        private readonly ChartEditorViewModel _editor;
        private readonly ChartLintScheduler _scheduler = new(new ChartLinter());
        private readonly DispatcherTimer _timer;
        private readonly List<TaskCompletionSource> _settling = [];
        private ChartTrackKey? _focused;
        private bool _disposed;

        public FindingsViewModel(EditorSong song, ChartUndoStack undo, ChartEditorViewModel editor)
        {
            _song = song;
            _undo = undo;
            _editor = editor;
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Delay };
            _timer.Tick += (_, _) => _ = RunAsync();
            _undo.Changed += OnChartChanged;
            _song.Changed += OnSettingsChanged;
            _editor.Session.SongSettingsChanged += OnSettingsChanged;
            _editor.Session.StateChanged += OnSessionState;
            _timer.Start();
        }

        private static ILogger Logger => Log.ForContext<FindingsViewModel>();

        /// <summary>The findings, in order: song-wide first, then by part, difficulty and tick.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasFindings), nameof(ErrorCount), nameof(WarningCount), nameof(InfoCount), nameof(Summary))]
        public partial IReadOnlyList<FindingItem> Items { get; private set; } = [];

        /// <summary>The marks for the bottom timeline: the lane in focus's findings, and the song-wide ones.</summary>
        [ObservableProperty]
        public partial IReadOnlyList<FindingMark> Marks { get; private set; } = [];

        public bool HasFindings => Items.Count > 0;

        public int ErrorCount => Items.Count(i => i.IsError);

        public int WarningCount => Items.Count(i => i.IsWarning);

        public int InfoCount => Items.Count(i => i.IsInfo);

        /// <summary>The counts by severity, as the panel's summary.</summary>
        public string Summary => string.Format(CultureInfo.CurrentCulture, Strings.FindingsSummaryFormat, ErrorCount, WarningCount, InfoCount);

        /// <summary>Raised when the findings change.</summary>
        public event EventHandler? Changed;

        /// <summary>
        /// Lints whatever is left to lint now, and finishes once the findings are those of the chart as it is: for Save and
        /// pack, which an Error stops.
        /// </summary>
        public Task SettleAsync()
        {
            if (!_scheduler.HasWork && !_scheduler.IsRunning)
                return Task.CompletedTask;
            var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _settling.Add(settled);
            if (!_scheduler.IsRunning)
                _ = RunAsync();
            return settled.Task;
        }

        /// <summary>The marks again, for the lane now in focus.</summary>
        public void RefreshMarks()
        {
            var focused = _editor.Session.Focused;
            var marks = new List<FindingMark>();
            foreach (var item in Items)
            {
                var finding = item.Finding;
                if (finding.Location is { } location)
                {
                    if (focused is { } key && key.Instrument == location.Part && (location.Difficulty is null || location.Difficulty == key.Difficulty))
                        marks.Add(new FindingMark(_editor.Session.AudioAt(location.Tick), finding.Severity));
                }
                else if (finding.Tick is { } tick)
                {
                    marks.Add(new FindingMark(_editor.Session.AudioAt(tick), finding.Severity));
                }
            }

            Marks = marks;
        }

        public void Dispose()
        {
            _disposed = true;
            _timer.Stop();
            _undo.Changed -= OnChartChanged;
            _song.Changed -= OnSettingsChanged;
            _editor.Session.SongSettingsChanged -= OnSettingsChanged;
            _editor.Session.StateChanged -= OnSessionState;
            foreach (var settled in _settling)
                settled.TrySetResult();
            _settling.Clear();
        }

        private void OnChartChanged(object? sender, ChartCommandEventArgs e)
        {
            _scheduler.Changed(e.Affected);
            Schedule();
        }

        private void OnSettingsChanged(object? sender, EventArgs e)
        {
            _scheduler.SettingsChanged();
            Schedule();
        }

        // The marks follow the lane in focus.
        private void OnSessionState(object? sender, EventArgs e)
        {
            if (_editor.Session.Focused == _focused)
                return;
            _focused = _editor.Session.Focused;
            RefreshMarks();
        }

        // Lints a moment after the last change, so a run of edits is linted once.
        private void Schedule()
        {
            _timer.Stop();
            _timer.Start();
        }

        private async Task RunAsync()
        {
            _timer.Stop();
            if (_disposed || _scheduler.IsRunning)
                return;

            // A drag's preview is on the chart but not the undo stack, so it's waited out.
            if (_editor.Session.HasPreview)
            {
                _timer.Start();
                return;
            }

            var input = new ChartLintInput(
                _song.Chart.Clone(),
                _song.EffectivePairs(),
                _editor.Session.ReadOptions,
                _song.AudioLengthMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null,
                _song.Delay);
            if (_scheduler.Start(input) is not { } job)
            {
                Settled();
                return;
            }

            ChartLintResult? result = null;
            try
            {
                result = await Task.Run(() => ChartLintScheduler.Run(job));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A rule that fails shouldn't take the editor with it; the next run is a full one.
                Logger.Error(ex, "Linting the chart failed");
            }

            if (_disposed)
                return;
            _scheduler.Finish(job, result);
            if (result is null)
            {
                // Not tried again until the chart or the song changes, so a rule that fails on this chart doesn't loop.
                Settled();
                return;
            }

            Show(result);
            if (_scheduler.HasWork)
                _ = RunAsync();
            else
                Settled();
        }

        private void Settled()
        {
            foreach (var settled in _settling)
                settled.TrySetResult();
            _settling.Clear();
        }

        private void Show(ChartLintResult result)
        {
            var map = _song.Chart.TempoMap.ToTempoMap();
            var fixable = QuickFixRegistry.ChartStrategies.Select(s => s.RuleId).Append(ChrRules.Chr084).ToHashSet(StringComparer.Ordinal);

            // What the load left out of the chart file is listed too, until a save writes the file without it.
            var findings = ChartLoadFindings.Merge(result.Findings, ChartLoadFindings.Of(_song.Opened?.Load.Issues ?? []));
            Items = [.. findings.Select(f => new FindingItem(f, WhereOf(f, map), fixable.Contains(f.RuleId), GoTo, Fix))];
            RefreshMarks();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private static string WhereOf(Finding finding, TempoMap map)
        {
            if (finding.Location is { } location)
            {
                string part = location.Difficulty is { } difficulty
                    ? string.Format(CultureInfo.CurrentCulture, Strings.FindingPartDifficultyFormat, ChartLabels.InstrumentName(location.Part), ChartLabels.DifficultyName(difficulty))
                    : ChartLabels.InstrumentName(location.Part);
                return string.Format(CultureInfo.CurrentCulture, Strings.FindingWhereFormat, part, PropertiesViewModel.MeasureBeat(map, location.Tick));
            }

            if (finding.Tick is { } tick)
                return PropertiesViewModel.MeasureBeat(map, tick);
            return finding.Key ?? Strings.FindingWhereSong;
        }

        /// <summary>Goes to where <paramref name="finding"/> is: its lane in focus, the cursor on it, and its notes selected.</summary>
        private void GoTo(Finding finding)
        {
            var session = _editor.Session;
            if (finding.Location is { } location)
            {
                var difficulty = location.Difficulty ?? (session.Focused is { } focused && focused.Instrument == location.Part ? focused.Difficulty : ChartDifficulty.Expert);
                var key = new ChartTrackKey(location.Part, difficulty);
                session.Focused = key;
                session.MoveCursorTo(location.Tick);
                if (_song.Chart.PartOf(location.Part) is { } part)
                {
                    var notes = part[difficulty].Notes.Between(location.Tick, location.EndTick).ToArray();
                    if (notes.Length > 0)
                        session.Selection.Select(key, notes);
                }
            }
            else if (finding.Tick is { } tick)
            {
                session.MoveCursorTo(tick);
                if (finding.RuleId is ChrRules.Chr002 or ChrRules.Chr005)
                    session.Selection.SelectTempos([tick]);
            }

            RefreshMarks();
        }

        /// <summary>Applies <paramref name="finding"/>'s fix: a chart command as one undo step, or a reverted change restored.</summary>
        private void Fix(Finding finding)
        {
            if (PendingSongIniFixes.KeyToRestore(finding, _song) is { } key)
            {
                _song.Restore(key);
                return;
            }

            var session = _editor.Session;
            if (QuickFixRegistry.ProposeChartFix(finding, session.Chart, session.ReadOptions) is { } command)
                session.Execute(command);
            else
                session.Status = Strings.FindingNoFixStatus;
        }
    }
}
