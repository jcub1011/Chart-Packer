using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using ChartBuilder.Resources;
using ChartBuilder.Services;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using ChartShared.Core.Generation;
using ChartShared.Core.Generation.Neural;
using ChartShared.Core.Validation;
using ChartShared.UI.Services;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace ChartBuilder.ViewModels
{
    /// <summary>What the Reduce dialog makes a draft of: one difficulty, or, for null, Hard, Medium and Easy together.</summary>
    public sealed record TargetChoice(ChartDifficulty? Difficulty, string Label);

    /// <summary>What a run of Reduce covers: the whole part when <paramref name="Range"/> is null, else those ticks.</summary>
    public sealed record ScopeChoice(string Label, (long From, long To)? Range);

    /// <summary>A lint finding on the Reduce dialog's draft: the rule, the difficulty it's on, and what it says.</summary>
    /// <param name="Tick">Where it is, which clicking it goes to in the preview.</param>
    /// <param name="Place">The difficulty and where in measures and beats, e.g. "Hard · m. 12.3".</param>
    public sealed record ReduceFindingRow(string RuleId, string Difficulty, string Message, long Tick, string Place);

    /// <summary>A difficulty Reduce all can fill, ticked to fill it.</summary>
    public sealed partial class TargetToggle(ChartDifficulty difficulty, string label, bool isChecked) : ObservableObject
    {
        public ChartDifficulty Difficulty { get; } = difficulty;

        public string Label { get; } = label;

        [ObservableProperty]
        public partial bool IsChecked { get; set; } = isChecked;
    }

    /// <summary>
    /// The Reduce dialog (charter-spec C9 item 2). Run from a lane, it shows the rules for the part's family prefilled from
    /// the defaults in Settings, and each change redraws the draft over the target difficulty, or over each of Hard, Medium
    /// and Easy for Reduce all, with the draft's lint findings and suggested ratings; Apply adds it as one undoable step,
    /// and the changes are for that run only. Opened from Settings, it edits the defaults themselves.
    /// </summary>
    public sealed partial class ReduceViewModel : ViewModelBase
    {
        // How long after a change of the rules the draft is worked out again, so typing a number does it once.
        private static readonly TimeSpan RedrawDelay = TimeSpan.FromMilliseconds(120);

        // The most findings listed; the rest are counted.
        private const int MostFindingsShown = 8;

        private static readonly IReadOnlyList<ChartDifficulty> Lower = ReductionRulesViewModel.Targets;

        private readonly ChartEditorViewModel? _editor;
        private readonly BuilderSettingsService _settings;
        private readonly ChartInstrument _part;
        private readonly DispatcherTimer? _redraw;
        private bool _loading;
        private bool _done;
        private int _checks;

        private ReduceViewModel(BuilderSettingsService settings, ChartEditorViewModel? editor, ChartInstrument part, ChartInstrumentKind kind, IGeneratorModelStore? models)
        {
            _settings = settings;
            _editor = editor;
            _part = part;
            RulesMethod = new RulesReduceMethodViewModel(settings.ReductionRules.Load(), kind, ChartDifficulty.Hard);
            RulesMethod.Changed += (_, _) => ScheduleRedraw();
            NeuralMethod = new NeuralReduceMethodViewModel(settings, models ?? editor?.GeneratorModels);
            NeuralMethod.Changed += (_, _) => ScheduleRedraw();
            if (editor is not null)
            {
                _redraw = new DispatcherTimer { Interval = RedrawDelay };
                _redraw.Tick += (_, _) => Redraw();
            }
        }

        /// <summary>Raised when the dialog's work is done: applied, saved or cancelled.</summary>
        public event EventHandler? Done;

        /// <summary>
        /// A run over <paramref name="lane"/>'s part, to the lane's difficulty, or the one below it when it's Expert; over the
        /// selection when there's one. For <paramref name="all"/>, Reduce all over the whole part instead, the difficulties
        /// without notes ticked and the lane's rules shown.
        /// </summary>
        public static ReduceViewModel ForRun(ChartEditorViewModel editor, BuilderSettingsService settings, ChartTrackKey lane, bool all = false)
        {
            var vm = new ReduceViewModel(settings, editor, lane.Instrument, ChartInstruments.KindOf(lane.Instrument), editor.GeneratorModels);
            var session = editor.Session;
            var part = session.Chart.PartOf(lane.Instrument);
            var to = lane.Difficulty == ChartDifficulty.Expert ? ChartDifficulty.Hard : lane.Difficulty;
            vm.ToChoices = [.. Lower.Select(d => new TargetChoice(d, ChartLabels.DifficultyName(d))), new TargetChoice(null, Strings.ReduceAllTargets)];
            vm.Toggles = [.. Lower.Select(d => Toggle(d, part is not null && part[d].Notes.Count > 0))];
            foreach (var toggle in vm.Toggles)
                toggle.PropertyChanged += vm.OnToggleChanged;

            var scopes = new List<ScopeChoice> { new(Strings.ReduceScopeWholePart, null) };
            if (session.Selection.Span is { } selection)
                scopes.Add(new ScopeChoice(Strings.ReduceScopeSelection, selection));
            if (session.Tools.SectionSpanAt(session.CursorTick) is { } section)
                scopes.Add(new ScopeChoice(Strings.ReduceScopeSection, section));
            vm.ScopeChoices = scopes;

            vm._loading = true;
            vm.SelectedMethod = vm.MethodChoices.FirstOrDefault(m => m.Id == settings.Settings.ReduceMethodId) ?? vm.MethodChoices[0];
            vm.SelectedTo = vm.ToChoices.First(c => c.Difficulty == (all ? null : to));
            vm.RulesMethod.SelectedRulesFor = vm.RulesMethod.RulesForChoices.First(c => c.Difficulty == to);
            vm.RulesMethod.ShowRulesForPicker = vm.IsAll;
            vm.SelectedScope = all ? scopes[0] : scopes.FirstOrDefault(s => s.Label == Strings.ReduceScopeSelection) ?? scopes[0];
            vm.RefreshFrom(part is null ? null : d => part[d].Notes.Count > 0);
            vm._loading = false;
            vm.ShowRules();

            // Before the first draft, so the playback is lent, and any pass of record mode ended, before it's made.
            vm.Preview = new ReductionPreviewViewModel(editor.Playback, new BuilderReductionPreviewSettings(settings.Settings), editor.WheelUpIsLater, editor.BorrowPlayback());
            vm.Redraw();
            return vm;
        }

        /// <summary>The defaults in Settings, which every run starts from.</summary>
        public static ReduceViewModel ForDefaults(BuilderSettingsService settings, IGeneratorModelStore? models = null)
        {
            var vm = new ReduceViewModel(settings, null, ChartInstrument.Guitar, ChartInstrumentKind.FiveFret, models);
            vm.ToChoices = [.. Lower.Select(d => new TargetChoice(d, ChartLabels.DifficultyName(d)))];
            vm._loading = true;
            vm.SelectedMethod = vm.MethodChoices.FirstOrDefault(m => m.Id == settings.Settings.ReduceMethodId) ?? vm.MethodChoices[0];
            vm.SelectedFamily = vm.FamilyChoices[0];
            vm.SelectedTo = vm.ToChoices[0];
            if (vm.SelectedTo.Difficulty is { } toDifficulty)
                vm.RulesMethod.SelectedRulesFor = vm.RulesMethod.RulesForChoices.First(c => c.Difficulty == toDifficulty);
            vm.RulesMethod.ShowRulesForPicker = vm.IsAll;
            vm._loading = false;
            vm.ShowRules();
            return vm;
        }

        /// <summary>Asks before Reduce all replaces a difficulty that has notes; the dialog's window sets it, to be its owner.</summary>
        public IConfirmationService? Confirmations { get; set; }

        /// <summary>The preview of the draft against its source, with the song; null on the defaults in Settings.</summary>
        public ReductionPreviewViewModel? Preview { get; private set; }

        /// <summary>Whether this is a run over a lane, rather than the defaults in Settings.</summary>
        public bool IsRun => _editor is not null;

        public bool IsDefaults => !IsRun;

        public string Title => IsRun
            ? string.Format(CultureInfo.CurrentCulture, Strings.ReduceTitleFormat, ChartLabels.InstrumentName(_part))
            : Strings.ReduceDefaultsTitle;

        public string ApplyLabel => IsRun ? Strings.ReduceApplyButton : Strings.ReduceSaveButton;

        /// <summary>The default reduction method's rules, as fields; a run's changes are its own.</summary>
        public RulesReduceMethodViewModel RulesMethod { get; }

        /// <summary>The neural method's options; a run's changes are its own.</summary>
        public NeuralReduceMethodViewModel NeuralMethod { get; }

        /// <summary>How the lower difficulties are made: the default reduction, and the neural network.</summary>
        public IReadOnlyList<ReduceMethodChoice> MethodChoices { get; } =
        [
            new ReduceMethodChoice(RulesReduceMethodViewModel.MethodId, Strings.ReduceMethodRules),
            new ReduceMethodChoice(NeuralReduceMethodViewModel.MethodId, Strings.ReduceMethodNeural),
        ];

        /// <summary>The method the draft is made by.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsRulesMethod))]
        [NotifyPropertyChangedFor(nameof(IsNeuralMethod))]
        public partial ReduceMethodChoice? SelectedMethod { get; set; }

        /// <summary>Whether the default reduction method is picked, so its rules are shown.</summary>
        public bool IsRulesMethod => SelectedMethod?.Id == RulesReduceMethodViewModel.MethodId;

        /// <summary>Whether the neural method is picked, so its pack and options are shown.</summary>
        public bool IsNeuralMethod => SelectedMethod?.Id == NeuralReduceMethodViewModel.MethodId;

        public IReadOnlyList<FamilyChoice> FamilyChoices => ReductionRulesViewModel.FamilyChoices;

        /// <summary>The family whose defaults are edited, in Settings mode.</summary>
        [ObservableProperty]
        public partial FamilyChoice? SelectedFamily { get; set; }

        [ObservableProperty]
        public partial IReadOnlyList<DifficultyChoice> FromChoices { get; private set; } = [];

        /// <summary>The difficulty reduced from.</summary>
        [ObservableProperty]
        public partial DifficultyChoice? SelectedFrom { get; set; }

        [ObservableProperty]
        public partial IReadOnlyList<TargetChoice> ToChoices { get; private set; } = [];

        /// <summary>The difficulty the draft is for, or Hard, Medium and Easy together.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsAll), nameof(IsOne))]
        public partial TargetChoice? SelectedTo { get; set; }

        /// <summary>Whether this is Reduce all: Hard, Medium and Easy, each from the one above it.</summary>
        public bool IsAll => IsRun && SelectedTo is { Difficulty: null };

        /// <summary>Whether this is a run to one difficulty, from the one picked.</summary>
        public bool IsOne => IsRun && !IsAll;

        /// <summary>The difficulties Reduce all can fill, those without notes ticked.</summary>
        public IReadOnlyList<TargetToggle> Toggles { get; private set; } = [];

        public IReadOnlyList<ScopeChoice> ScopeChoices { get; private set; } = [];

        /// <summary>What the run covers: the whole part, the selection or the section at the cursor.</summary>
        [ObservableProperty]
        public partial ScopeChoice? SelectedScope { get; set; }

        /// <summary>What the draft holds, or why there's none, as one line per draft.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Summary))]
        public partial IReadOnlyList<string> SummaryLines { get; private set; } = [];

        /// <summary>What the draft holds, or why there's none, in one message.</summary>
        public string Summary => string.Join(" ", SummaryLines);

        /// <summary>Whether the draft's check has come in, so its ladder and findings are shown.</summary>
        [ObservableProperty]
        public partial bool HasCheck { get; private set; }

        /// <summary>Each difficulty of the part with notes, Expert first, the draft in place, with its suggested rating.</summary>
        [ObservableProperty]
        public partial DifficultyLadder Ladder { get; private set; } = DifficultyLadder.Empty;

        /// <summary>The draft's lint findings, the first few of them until all are asked for.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasFindings), nameof(HasNoFindings))]
        public partial IReadOnlyList<ReduceFindingRow> Findings { get; private set; } = [];

        public bool HasFindings => Findings.Count > 0;

        /// <summary>Whether the check came in with no findings, which the dialog says.</summary>
        public bool HasNoFindings => HasCheck && Findings.Count == 0;

        /// <summary>How many findings there are past those listed; empty when they're all listed.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasMoreFindings))]
        public partial string MoreFindings { get; private set; } = "";

        public bool HasMoreFindings => MoreFindings.Length > 0;

        // Every finding of the last check, and whether they're all listed rather than the first few.
        private IReadOnlyList<ReduceFindingRow> _allFindings = [];
        private bool _showsAllFindings;

        /// <summary>Lists every finding, not only the first few.</summary>
        [RelayCommand]
        private void ShowAllFindings()
        {
            _showsAllFindings = true;
            ListFindings();
        }

        /// <summary>Shows the preview at <paramref name="row"/>'s finding.</summary>
        [RelayCommand]
        private void GoToFinding(ReduceFindingRow? row)
        {
            if (row is not null)
                Preview?.GoTo(row.Tick);
        }

        // The difficulty whose rules the fields show.
        private ChartDifficulty? RulesTarget => IsAll ? RulesMethod.SelectedRulesFor?.Difficulty : SelectedTo?.Difficulty;

        /// <summary>Adds the draft (a run), or saves the defaults (Settings).</summary>
        [RelayCommand]
        private async Task ApplyAsync()
        {
            if (_editor is { } editor)
            {
                // The draft as shown is the rules' as they are, unless a change is still to be drawn or the run was dropped.
                var reduce = editor.Session.Reduce;
                if (_redraw is { IsEnabled: true } || !reduce.IsActive)
                    Redraw();
                if (!reduce.IsActive)
                    return;
                if (IsAll && reduce.Overwrites is { Count: > 0 } replaced && Confirmations is { } confirmations
                    && !await confirmations.ConfirmAsync(
                        Strings.ReduceOverwriteTitle,
                        string.Format(CultureInfo.CurrentCulture, Strings.ReduceOverwriteMessageFormat, string.Join(", ", replaced.Select(ChartLabels.DifficultyName))),
                        Strings.ReduceOverwriteButton))
                    return;
                if (_done || !reduce.Accept())
                    return;
                editor.Messages.Info(Summary);
            }
            else
            {
                _settings.ReductionRules.Save(RulesMethod.Defaults);
            }

            Finish();
        }

        /// <summary>Ends a run, adding nothing; leaves the defaults as they were.</summary>
        [RelayCommand]
        public void Cancel()
        {
            if (_done)
                return;
            _editor?.Session.Reduce.Cancel();
            Finish();
        }

        /// <summary>The shipped rules for this family and target, in place of what's shown.</summary>
        [RelayCommand]
        private void Reset() => RulesMethod.Reset();

        partial void OnSelectedFamilyChanged(FamilyChoice? value)
        {
            if (value is null || IsRun)
                return;
            RulesMethod.Show(value.Kind, RulesTarget ?? ChartDifficulty.Hard);
        }

        partial void OnSelectedToChanged(TargetChoice? value)
        {
            if (_loading || value is null)
                return;
            if (_editor?.Session.Chart.PartOf(_part) is { } part)
                RefreshFrom(d => part[d].Notes.Count > 0);
            RulesMethod.ShowRulesForPicker = IsAll;
            ShowRules();
            ScheduleRedraw();
        }

        partial void OnSelectedMethodChanged(ReduceMethodChoice? value)
        {
            if (_loading || value is null)
                return;
            _settings.Settings.ReduceMethodId = value.Id;
            _settings.Save();
            ScheduleRedraw();
        }

        partial void OnSelectedScopeChanged(ScopeChoice? value) => ScheduleRedraw();

        partial void OnSelectedFromChanged(DifficultyChoice? value) => ScheduleRedraw();

        private void OnToggleChanged(object? sender, PropertyChangedEventArgs e) => ScheduleRedraw();

        private static DifficultyChoice Choice(ChartDifficulty difficulty) => new(difficulty, ChartLabels.DifficultyName(difficulty));

        // The rules for the family and the difficulty picked, into the fields; the fields' changes are kept for those shown.
        private void ShowRules()
        {
            if (RulesTarget is { } to)
            {
                RulesMethod.ShowForTarget(to);
                if (!IsAll)
                {
                    var match = RulesMethod.RulesForChoices.FirstOrDefault(c => c.Difficulty == to);
                    if (match is not null && !Equals(RulesMethod.SelectedRulesFor, match))
                        RulesMethod.SelectedRulesFor = match;
                }
            }
        }

        private static TargetToggle Toggle(ChartDifficulty difficulty, bool charted) => new(
            difficulty,
            charted ? string.Format(CultureInfo.CurrentCulture, Strings.ReduceChartedFormat, ChartLabels.DifficultyName(difficulty)) : ChartLabels.DifficultyName(difficulty),
            !charted);

        // The difficulties above the target to reduce from, those with notes first in mind: the highest with notes is picked.
        private void RefreshFrom(Func<ChartDifficulty, bool>? hasNotes)
        {
            var to = SelectedTo?.Difficulty ?? ChartDifficulty.Hard;
            var above = Enum.GetValues<ChartDifficulty>().Where(d => d > to).OrderDescending().ToList();
            bool loading = _loading;
            _loading = true;
            FromChoices = [.. above.Select(Choice)];
            var pick = above.FirstOrDefault(d => hasNotes?.Invoke(d) == true, above.FirstOrDefault());
            SelectedFrom = FromChoices.FirstOrDefault(c => c.Difficulty == pick);
            _loading = loading;
        }

        private void ScheduleRedraw()
        {
            if (_loading || _redraw is null)
                return;
            _redraw.Stop();
            _redraw.Start();
        }

        // The draft over the target, or over each target ticked, by the method picked. A learned
        // method drafts through the generator seam, falling back to the rules per part (§3.7).
        private void Redraw()
        {
            _redraw?.Stop();
            if (_editor is not { } editor || SelectedTo is not { } to)
            {
                ClearCheck();
                SummaryLines = [Strings.ReduceNothingToReduce];
                Preview?.Clear(Strings.ReduceNothingToReduce);
                return;
            }

            var reduce = editor.Session.Reduce;
            var range = SelectedScope?.Range;
            bool started;
            if (IsNeuralMethod && editor.GeneratorModels is { } models)
                started = StartNeural(editor, models, reduce, range, to);
            else if (IsAll)
            {
                var defaults = RulesMethod.Defaults;
                var targets = Toggles.Where(static t => t.IsChecked).Select(static t => t.Difficulty).ToList();
                if (targets.Count == 0)
                {
                    reduce.Cancel();
                    ClearCheck();
                    SummaryLines = [Strings.ReduceNoTargets];
                    Preview?.Clear(Strings.ReduceNoTargets);
                    return;
                }

                started = RulesMethod.IsDrums
                    ? reduce.StartAll(targets.ToDictionary(t => t, defaults.DrumsFor), range)
                    : reduce.StartAll(_part, targets.ToDictionary(t => t, t => defaults.FretsFor(RulesMethod.Kind, t)), range);
            }
            else if (SelectedFrom is { } from && to.Difficulty is { } target)
            {
                started = RulesMethod.IsDrums
                    ? reduce.Start(from.Difficulty, target, RulesMethod.DrumRules(), range)
                    : reduce.Start(_part, from.Difficulty, target, RulesMethod.FretRules(), range);
            }
            else
            {
                started = false;
            }

            if (!started || !reduce.IsActive)
            {
                ClearCheck();
                SummaryLines = [editor.Session.Status ?? Strings.ReduceNothingToReduce];
                Preview?.Clear(SummaryLines[0]);
                return;
            }

            SummaryLines = [.. reduce.Drafts.Select(d => string.Format(
                CultureInfo.CurrentCulture,
                Strings.ReduceSummaryFormat,
                ChartLabels.DifficultyName(d.Source),
                ChartLabels.DifficultyName(d.Target.Difficulty),
                d.Result.Notes.Count,
                d.Result.Removed)),
                .. reduce.FallbackReasons.Select(kv => $"{ChartLabels.DifficultyName(kv.Key)}: {kv.Value}")];
            Preview?.OnDrafted(RunOf(editor.Session, reduce));
            _ = CheckAsync(editor, reduce);
        }

        // A neural run: each target drafted by the pack, falling back to the rules per part (§3.7).
        // Reduce all chains from the difficulty above by default, or each from Expert when asked (§3.6).
        // The generator is kept for the dialog's life, so installed packs load once.
        private NeuralDifficultyGenerator? _neural;
        private IGeneratorModelStore? _neuralStore;

        private bool StartNeural(ChartEditorViewModel editor, IGeneratorModelStore models, ReduceProposals reduce, (long From, long To)? range, TargetChoice to)
        {
            var options = NeuralMethod.Options();
            if (_neural is null || !ReferenceEquals(_neuralStore, models))
            {
                _neural = new NeuralDifficultyGenerator(
                    models, target => RulesMethod.Defaults.FretsFor(RulesMethod.Kind, target));
                _neuralStore = models;
            }
            var readOptions = editor.Session.ReadOptions;
            var variant = editor.Session.DrumsVariantShown;
            ReductionResult Rules(EditableChart chart, ChartDifficulty from, ChartDifficulty target) =>
                RulesMethod.IsDrums
                    ? Reducer.Reduce(chart, from, target, RulesMethod.DrumRules(), variant, range)
                    : Reducer.Reduce(chart, _part, from, target, RulesMethod.Defaults.FretsFor(RulesMethod.Kind, target), range, readOptions);

            if (IsAll)
            {
                var targets = Toggles.Where(static t => t.IsChecked).Select(static t => t.Difficulty).ToList();
                if (targets.Count == 0)
                {
                    reduce.Cancel();
                    ClearCheck();
                    SummaryLines = [Strings.ReduceNoTargets];
                    Preview?.Clear(Strings.ReduceNoTargets);
                    return false;
                }

                bool expertCharted = editor.Session.Chart.PartOf(_part) is { } whole && whole[ChartDifficulty.Expert].Notes.Count > 0;
                List<(ChartDifficulty? From, ChartDifficulty To)> steps = options.Chained || !expertCharted
                    ? [.. targets.Select(static t => ((ChartDifficulty?)null, t))]
                    : [.. targets.Select(static t => ((ChartDifficulty?)ChartDifficulty.Expert, t))];
                return reduce.StartWithGenerator(_part, range, steps, Rules, _neural, options);
            }

            if (SelectedFrom is { } from && to.Difficulty is { } target)
                return reduce.StartWithGenerator(_part, range, [(from.Difficulty, target)], Rules, _neural, options);
            return false;
        }

        // The draft's findings and ratings, worked out off the UI thread; a newer draft's check replaces it.
        private async Task CheckAsync(ChartEditorViewModel editor, ReduceProposals reduce)
        {
            int check = ++_checks;
            var targets = reduce.Drafts.Select(static d => d.Target.Difficulty).ToList();
            ReductionCheck? result;
            try
            {
                result = await reduce.CheckAsync();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The check is a help, not the draft: a rule that fails leaves the draft to add as it is.
                Log.ForContext<ReduceViewModel>().Error(ex, "Checking the reduced draft failed");
                return;
            }

            if (result is null || check != _checks || _done)
                return;

            Ladder = DifficultyLadder.Of(result, targets);
            var map = editor.Session.TempoMap;
            _allFindings = [.. result.Findings.Select(f => Row(f, map))];
            ListFindings();
            Preview?.OnFindings(result.Findings);
            HasCheck = true;
            OnPropertyChanged(nameof(HasNoFindings));
        }

        // What the preview shows of the run: the part, its range and the chart's timing, and the drafts' diffs.
        private static ReductionPreviewRun RunOf(EditorSession session, ReduceProposals reduce) =>
            new(reduce.Part!.Value, reduce.Range, session.TempoMap, session.Delay, session.IsFiveLaneDrums, reduce.DiffAsync);

        private static ReduceFindingRow Row(Finding finding, TempoMap map)
        {
            var at = finding.Location!;
            string difficulty = ChartLabels.DifficultyName(at.Difficulty!.Value);
            string place = string.Format(CultureInfo.CurrentCulture, Strings.ReduceFindingPlaceFormat, difficulty, ChartLabels.PlaceText(MeasureBeat.At(map, at.Tick)));
            return new ReduceFindingRow(finding.RuleId, difficulty, finding.Message, at.Tick, place);
        }

        // The findings, the first few unless all are asked for, and how many more there are.
        private void ListFindings()
        {
            bool all = _showsAllFindings || _allFindings.Count <= MostFindingsShown;
            MoreFindings = all ? "" : string.Format(CultureInfo.CurrentCulture, Strings.ReduceMoreFindingsFormat, _allFindings.Count - MostFindingsShown);
            Findings = all ? _allFindings : [.. _allFindings.Take(MostFindingsShown)];
        }

        private void ClearCheck()
        {
            _checks++;
            HasCheck = false;
            Ladder = DifficultyLadder.Empty;
            MoreFindings = "";
            _allFindings = [];
            Findings = [];
        }

        private void Finish()
        {
            _done = true;
            _redraw?.Stop();
            Preview?.Dispose();
            Done?.Invoke(this, EventArgs.Empty);
        }
    }
}
