using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using ChartBuilder.Resources;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Layout;
using ChartShared.Core.Charting.Lint;
using ChartShared.Core.Charts;
using ChartShared.Core.Scanning;
using ChartShared.Core.Validation;
using ChartShared.Core.Validation.QuickFixes;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using Serilog;

namespace ChartBuilder.ViewModels
{
    /// <summary>A mark on the bottom timeline for a finding: where it is in audio time, and how serious.</summary>
    public readonly record struct FindingMark(double Seconds, Severity Severity);

    /// <summary>
    /// The findings panel (charter-spec C3 item 5, C16): the chart linted as it's edited, off the UI thread on a snapshot,
    /// each finding linked to its place in the chart, with its quick fix where it has one. The bottom timeline marks them.
    /// </summary>
    /// <remarks>
    /// <para>Edits are linted a moment after they stop, so typing isn't held up, and never in the middle of a drag, whose
    /// preview isn't on the undo stack. Only what each edit touched is checked again (<see cref="ChartLintScheduler"/>).</para>
    /// <para>The findings are listed by <see cref="Issues"/>, which keeps the rows of those still found, so a run of lint
    /// only makes rows for what changed, and the lists showing them only make the rows on screen.</para>
    /// </remarks>
    public sealed partial class FindingsViewModel : ViewModelBase, IDisposable
    {
        private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(150);

        private readonly EditorSong _song;
        private readonly ChartUndoStack _undo;
        private readonly ChartEditorViewModel _editor;
        private readonly ChartLintScheduler _scheduler = new(new ChartLinter(ChartLintRules.ForEditor));
        private readonly DispatcherTimer _timer;
        private readonly List<TaskCompletionSource> _settling = [];
        private ChartTrackKey? _focused;
        private TempoMap? _shownMap;
        private bool _disposed;

        public FindingsViewModel(EditorSong song, ChartUndoStack undo, ChartEditorViewModel editor)
        {
            _song = song;
            _undo = undo;
            _editor = editor;
            Issues.FileName = ChartFiles.MidiName;
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = Delay };
            _timer.Tick += (_, _) => _ = RunAsync();
            _undo.Changed += OnChartChanged;
            _song.Changed += OnSettingsChanged;
            _editor.Session.SongSettingsChanged += OnSettingsChanged;
            _editor.Session.StateChanged += OnSessionState;
            _timer.Start();
        }

        private static ILogger Logger => Log.ForContext<FindingsViewModel>();

        /// <summary>
        /// The findings, grouped as the Findings tab and the chart issues window list them; in order within each group:
        /// by part, difficulty and tick.
        /// </summary>
        public ChartIssuesViewModel Issues { get; } = new();

        /// <summary>The marks for the bottom timeline: the lane in focus's findings, and the song-wide ones.</summary>
        [ObservableProperty]
        public partial IReadOnlyList<FindingMark> Marks { get; private set; } = [];

        public int ErrorCount => Issues.ErrorCount;

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
            foreach (var item in Issues.Items)
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

            // Only when they've changed, as the timeline is drawn again for new marks.
            if (!marks.SequenceEqual(Marks))
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
            // Where a finding is follows the tempo map, so after it changes no row is kept.
            var map = _editor.Session.TempoMap;
            bool reuse = ReferenceEquals(map, _shownMap);
            _shownMap = map;

            // What the load left out of the chart file is listed too, until a save writes the file without it.
            var findings = ChartLoadFindings.Merge(result.Findings, ChartLoadFindings.Of(_song.Opened?.Load.Issues ?? []));
            Issues.Show(findings, static f => f, f => new FindingItem(f, finding => WhereOf(finding, map), GoTo, FixesOf), reuse);
            OnPropertyChanged(nameof(ErrorCount));
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

            if (VocalTracks.Of(finding.Track) is { } vocal)
            {
                string part = LaneMenuItem.TitleOf(LanePart.Vocals(vocal));
                return finding.Tick is { } at ? string.Format(CultureInfo.CurrentCulture, Strings.FindingWhereFormat, part, PropertiesViewModel.MeasureBeat(map, at)) : part;
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
            else if (VocalTracks.Of(finding.Track) is { } vocal)
            {
                // A vocal part's lane is opened if it isn't, and the notes starting on the tick selected (charter-spec C3 item 7).
                _editor.Lanes.Layout.Add(LanePart.Vocals(vocal));
                if (finding.Tick is { } at)
                {
                    session.MoveCursorTo(at);
                    if (session.VocalContentOf(vocal) is { } content)
                    {
                        var places = content.Notes.Where(n => n.Tick == at).Select(n => n.Place).ToList();
                        if (places.Count > 0)
                            session.Selection.SelectVocalNotes(vocal, places);
                    }
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

        /// <summary>
        /// The ways to fix <paramref name="finding"/> now, for its menu: CHR084's reverted change restored, CHR083's suggested
        /// rating used (charter-spec C18 item 3), or the chart's fixes, each of which Fix All can apply to every finding of
        /// its rule, in its part or everywhere.
        /// </summary>
        private IReadOnlyList<FindingFix> FixesOf(Finding finding)
        {
            if (PendingSongIniFixes.KeyToRestore(finding, _song) is { } key)
                return [new FindingFix(null, () => _song.Restore(key), null, null)];

            var session = _editor.Session;
            if (finding.RuleId == ChrRules.Chr083)
            {
                return PendingSongIniFixes.SuggestedRating(finding, _song.Chart, session.ReadOptions) is { } rating
                    ? [new FindingFix(null, () => UseRating(rating.Key, rating.Tier), null, null)]
                    : [];
            }

            return [.. QuickFixRegistry.ProposeChartFixes(finding, session.Chart, session.ReadOptions).Select(option => new FindingFix(
                option.Label,
                () => Fix(finding, option.Id),
                finding.Location is { } location ? () => FixAll(finding, option.Id, location.Part) : null,
                () => FixAll(finding, option.Id, null)))];
        }

        private void UseRating(string key, int tier)
        {
            _song.SetRating(key, tier);
            _editor.Messages.Info(string.Format(CultureInfo.CurrentCulture, Strings.SuggestedRatingUsedFormat, tier, key));
        }

        /// <summary>Applies <paramref name="finding"/>'s fix <paramref name="optionId"/> as one undo step, worked out again from the chart as it is.</summary>
        private void Fix(Finding finding, string optionId)
        {
            var session = _editor.Session;
            if (QuickFixRegistry.ProposeChartFix(finding, session.Chart, session.ReadOptions, optionId) is { } command)
                session.Execute(command);
            else
                session.Status = Strings.FindingNoFixStatus;
        }

        /// <summary>
        /// Applies the fix <paramref name="optionId"/> of every finding of <paramref name="finding"/>'s rule as one undo step:
        /// in <paramref name="part"/>, every difficulty, or with none given, everywhere.
        /// </summary>
        private async void FixAll(Finding finding, string optionId, ChartInstrument? part)
        {
            // Brought up to date first, so the fixes are of the findings of the chart as it is.
            await SettleAsync();
            var findings = Issues.Items
                .Select(i => i.Finding)
                .Where(f => f.RuleId == finding.RuleId && (part is null || f.Location?.Part == part))
                .ToList();
            var session = _editor.Session;
            switch (session.FixAll(findings, optionId))
            {
                case null:
                    // Refused: the session's status says why.
                    break;
                case 0:
                    session.Status = Strings.FindingNoFixStatus;
                    break;
                case int fixes when part is { } instrument:
                    _editor.Messages.Info(string.Format(CultureInfo.CurrentCulture, Strings.FindingsFixedAllFormat, fixes, finding.RuleId, ChartLabels.InstrumentName(instrument)));
                    break;
                case int fixes:
                    _editor.Messages.Info(string.Format(CultureInfo.CurrentCulture, Strings.FindingsFixedEverywhereFormat, fixes, finding.RuleId));
                    break;
            }
        }
    }
}
