using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using ChartBuilder.Resources;
using ChartBuilder.Services;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using ChartShared.Core.Validation;
using ChartShared.UI.Services;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace ChartBuilder.ViewModels
{
    /// <summary>A choice of family in the Reduce dialog's Settings mode.</summary>
    public sealed record FamilyChoice(ChartInstrumentKind Kind, string Label);

    /// <summary>A thinning grid the Reduce dialog offers: a snap's division, or 0 for none.</summary>
    public sealed record DivisionChoice(int Division, string Label);

    /// <summary>The highest fret or column a reduced chord may use.</summary>
    public sealed record LaneLimitChoice(int Lane, string Label);

    /// <summary>What the Reduce dialog makes a draft of: one difficulty, or, for null, Hard, Medium and Easy together.</summary>
    public sealed record TargetChoice(ChartDifficulty? Difficulty, string Label);

    /// <summary>What a run of Reduce covers: the whole part when <paramref name="Range"/> is null, else those ticks.</summary>
    public sealed record ScopeChoice(string Label, (long From, long To)? Range);

    /// <summary>
    /// A difficulty in the Reduce dialog's ladder, the draft in place: its suggested rating, and its peak notes per second
    /// as a share of the part's highest.
    /// </summary>
    /// <param name="IsDraft">Whether this run drafts it.</param>
    /// <param name="IsOutOfOrder">Whether it's rated as hard as the difficulty above it, or harder.</param>
    public sealed record ReduceRatingRow(string Difficulty, string Tier, string Peak, double Share, bool IsDraft, bool IsOutOfOrder);

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

        private static readonly ChartDifficulty[] Lower = [ChartDifficulty.Hard, ChartDifficulty.Medium, ChartDifficulty.Easy];

        private readonly ChartEditorViewModel? _editor;
        private readonly BuilderSettingsService _settings;
        private readonly ChartInstrument _part;
        private readonly DispatcherTimer? _redraw;
        private ReductionDefaults _defaults;
        private bool _loading;
        private bool _done;
        private int _checks;

        private ReduceViewModel(BuilderSettingsService settings, ChartEditorViewModel? editor, ChartInstrument part, ChartInstrumentKind kind)
        {
            _settings = settings;
            _editor = editor;
            _part = part;
            _defaults = settings.Settings.ReductionRules;
            Kind = kind;
            DivisionChoices = [new DivisionChoice(0, Strings.ReduceNoThinning), .. SnapGrid.CommonDivisions.Select(d => new DivisionChoice(d, BuilderSettingsCatalog.SnapLabel(d)))];
            KickChoices = [new DivisionChoice(0, Strings.ReduceKickKept), .. SnapGrid.CommonDivisions.Select(d => new DivisionChoice(d, BuilderSettingsCatalog.SnapLabel(d)))];
            FamilyChoices =
            [
                new FamilyChoice(ChartInstrumentKind.FiveFret, Strings.ReduceFamilyFiveFret),
                new FamilyChoice(ChartInstrumentKind.SixFret, Strings.ReduceFamilyGhl),
                new FamilyChoice(ChartInstrumentKind.Drums, Strings.ReduceFamilyDrums),
            ];
            RulesForChoices = [.. Lower.Select(Choice)];
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
        /// selection when there's one.
        /// </summary>
        public static ReduceViewModel ForRun(ChartEditorViewModel editor, BuilderSettingsService settings, ChartTrackKey lane)
        {
            var vm = new ReduceViewModel(settings, editor, lane.Instrument, ChartInstruments.KindOf(lane.Instrument));
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
            vm.SelectedTo = vm.ToChoices.First(c => c.Difficulty == to);
            vm.SelectedRulesFor = vm.RulesForChoices.First(c => c.Difficulty == to);
            vm.SelectedScope = scopes.FirstOrDefault(s => s.Label == Strings.ReduceScopeSelection) ?? scopes[0];
            vm.RefreshFrom(part is null ? null : d => part[d].Notes.Count > 0);
            vm._loading = false;
            vm.LoadRules();

            // Before the first draft, so the playback is lent, and any pass of record mode ended, before it's made.
            vm.Preview = new ReducePreviewViewModel(editor, settings);
            vm.Redraw();
            return vm;
        }

        /// <summary>The defaults in Settings, which every run starts from.</summary>
        public static ReduceViewModel ForDefaults(BuilderSettingsService settings)
        {
            var vm = new ReduceViewModel(settings, null, ChartInstrument.Guitar, ChartInstrumentKind.FiveFret);
            vm.ToChoices = [.. Lower.Select(d => new TargetChoice(d, ChartLabels.DifficultyName(d)))];
            vm._loading = true;
            vm.SelectedFamily = vm.FamilyChoices[0];
            vm.SelectedTo = vm.ToChoices[0];
            vm._loading = false;
            vm.LoadRules();
            return vm;
        }

        /// <summary>Asks before Reduce all replaces a difficulty that has notes; the dialog's window sets it, to be its owner.</summary>
        public IConfirmationService? Confirmations { get; set; }

        /// <summary>The preview of the draft against its source, with the song; null on the defaults in Settings.</summary>
        public ReducePreviewViewModel? Preview { get; private set; }

        /// <summary>Whether this is a run over a lane, rather than the defaults in Settings.</summary>
        public bool IsRun => _editor is not null;

        public bool IsDefaults => !IsRun;

        public string Title => IsRun
            ? string.Format(CultureInfo.CurrentCulture, Strings.ReduceTitleFormat, ChartLabels.InstrumentName(_part))
            : Strings.ReduceDefaultsTitle;

        public string ApplyLabel => IsRun ? Strings.ReduceApplyButton : Strings.ReduceSaveButton;

        /// <summary>The family whose rules are shown.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsDrums), nameof(IsFrets), nameof(LaneLimitChoices), nameof(LaneLimitLabel), nameof(ChordSizeMaximum))]
        public partial ChartInstrumentKind Kind { get; private set; }

        public bool IsDrums => Kind == ChartInstrumentKind.Drums;

        public bool IsFrets => !IsDrums;

        public IReadOnlyList<FamilyChoice> FamilyChoices { get; }

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

        public IReadOnlyList<DifficultyChoice> RulesForChoices { get; }

        /// <summary>Which difficulty's rules are shown, in Reduce all.</summary>
        [ObservableProperty]
        public partial DifficultyChoice? SelectedRulesFor { get; set; }

        public IReadOnlyList<ScopeChoice> ScopeChoices { get; private set; } = [];

        /// <summary>What the run covers: the whole part, the selection or the section at the cursor.</summary>
        [ObservableProperty]
        public partial ScopeChoice? SelectedScope { get; set; }

        public IReadOnlyList<DivisionChoice> DivisionChoices { get; }

        public IReadOnlyList<DivisionChoice> KickChoices { get; }

        public IReadOnlyList<LaneLimitChoice> LaneLimitChoices => Kind == ChartInstrumentKind.SixFret
            ? [.. Enumerable.Range(0, 3).Select(c => new LaneLimitChoice(c, string.Format(CultureInfo.CurrentCulture, Strings.ReduceColumnsFormat, c + 1)))]
            : [.. Enumerable.Range(0, 5).Select(f => new LaneLimitChoice(f, string.Format(CultureInfo.CurrentCulture, Strings.ReduceFretsFormat, FretName(f))))];

        public string LaneLimitLabel => Kind == ChartInstrumentKind.SixFret ? Strings.ReduceColumnsLabel : Strings.ReduceFretsLabel;

        public decimal ChordSizeMaximum => Kind == ChartInstrumentKind.SixFret ? 3 : 5;

        public decimal MinGapMaximum => ReductionDefaults.MaxGapMs;

        [ObservableProperty]
        public partial decimal? MaxChordSize { get; set; }

        [ObservableProperty]
        public partial LaneLimitChoice? HighestLane { get; set; }

        [ObservableProperty]
        public partial DivisionChoice? Thinning { get; set; }

        /// <summary>The fewest milliseconds two chords are apart, beside the thinning grid.</summary>
        [ObservableProperty]
        public partial decimal? MinGapMs { get; set; }

        [ObservableProperty]
        public partial bool KeepForcedHopoAndTap { get; set; }

        [ObservableProperty]
        public partial bool KeepShortSustains { get; set; }

        [ObservableProperty]
        public partial decimal? PadsPerHit { get; set; }

        [ObservableProperty]
        public partial DivisionChoice? KickThinning { get; set; }

        /// <summary>What the draft holds, or why there's none, as one line per draft.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Summary))]
        public partial IReadOnlyList<string> SummaryLines { get; private set; } = [];

        /// <summary>What the draft holds, or why there's none, in one message.</summary>
        public string Summary => string.Join(" ", SummaryLines);

        /// <summary>Whether the draft's check has come in, so its ladder and findings are shown.</summary>
        [ObservableProperty]
        public partial bool HasCheck { get; private set; }

        /// <summary>Each difficulty of the part with notes, Expert first, the draft in place.</summary>
        [ObservableProperty]
        public partial IReadOnlyList<ReduceRatingRow> Ratings { get; private set; } = [];

        /// <summary>Where a lower difficulty isn't easier than the one above it; empty when they're in order.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasOrderWarning))]
        public partial string OrderWarning { get; private set; } = "";

        public bool HasOrderWarning => OrderWarning.Length > 0;

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
        private ChartDifficulty? RulesTarget => IsAll ? SelectedRulesFor?.Difficulty : SelectedTo?.Difficulty;

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
                Store();
                _settings.Settings.ReductionRules = _defaults;
                _settings.Save();
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
        private void Reset()
        {
            if (RulesTarget is not { } to)
                return;
            var shipped = ReductionDefaults.Shipped;
            _defaults = Kind == ChartInstrumentKind.Drums
                ? _defaults.WithDrums(to, shipped.DrumsFor(to))
                : _defaults.WithFrets(Kind, to, shipped.FretsFor(Kind, to));
            LoadRules();
            ScheduleRedraw();
        }

        partial void OnSelectedFamilyChanged(FamilyChoice? oldValue, FamilyChoice? newValue)
        {
            if (newValue is null || IsRun)
                return;
            if (!_loading && oldValue is not null)
                Store(oldValue.Kind, SelectedTo?.Difficulty);
            Kind = newValue.Kind;
            if (!_loading)
                LoadRules();
        }

        partial void OnSelectedToChanged(TargetChoice? oldValue, TargetChoice? newValue)
        {
            if (_loading || newValue is null)
                return;
            if (oldValue is not null)
                Store(Kind, oldValue.Difficulty ?? SelectedRulesFor?.Difficulty);
            if (_editor?.Session.Chart.PartOf(_part) is { } part)
                RefreshFrom(d => part[d].Notes.Count > 0);
            LoadRules();
            ScheduleRedraw();
        }

        partial void OnSelectedRulesForChanged(DifficultyChoice? oldValue, DifficultyChoice? newValue)
        {
            if (_loading || newValue is null)
                return;
            if (oldValue is not null)
                Store(Kind, oldValue.Difficulty);
            LoadRules();
        }

        partial void OnSelectedScopeChanged(ScopeChoice? value) => ScheduleRedraw();

        partial void OnSelectedFromChanged(DifficultyChoice? value) => ScheduleRedraw();

        partial void OnMaxChordSizeChanged(decimal? value) => ScheduleRedraw();

        partial void OnHighestLaneChanged(LaneLimitChoice? value) => ScheduleRedraw();

        partial void OnThinningChanged(DivisionChoice? value) => ScheduleRedraw();

        partial void OnMinGapMsChanged(decimal? value) => ScheduleRedraw();

        partial void OnKeepForcedHopoAndTapChanged(bool value) => ScheduleRedraw();

        partial void OnKeepShortSustainsChanged(bool value) => ScheduleRedraw();

        partial void OnPadsPerHitChanged(decimal? value) => ScheduleRedraw();

        partial void OnKickThinningChanged(DivisionChoice? value) => ScheduleRedraw();

        private void OnToggleChanged(object? sender, PropertyChangedEventArgs e) => ScheduleRedraw();

        private static DifficultyChoice Choice(ChartDifficulty difficulty) => new(difficulty, ChartLabels.DifficultyName(difficulty));

        private static TargetToggle Toggle(ChartDifficulty difficulty, bool charted) => new(
            difficulty,
            charted ? string.Format(CultureInfo.CurrentCulture, Strings.ReduceChartedFormat, ChartLabels.DifficultyName(difficulty)) : ChartLabels.DifficultyName(difficulty),
            !charted);

        private static string FretName(int fret) => fret switch
        {
            0 => Strings.FretGreen,
            1 => Strings.FretRed,
            2 => Strings.FretYellow,
            3 => Strings.FretBlue,
            _ => Strings.FretOrange,
        };

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

        // The rules for the family and target, into the fields.
        private void LoadRules()
        {
            if (RulesTarget is not { } to)
                return;
            _loading = true;
            if (Kind == ChartInstrumentKind.Drums)
            {
                var rules = _defaults.DrumsFor(to);
                PadsPerHit = rules.PadsPerHit;
                Thinning = DivisionChoices.FirstOrDefault(c => c.Division == rules.ThinningDivision) ?? DivisionChoices[0];
                MinGapMs = rules.MinGapMs ?? 0;
                KickThinning = KickChoices.FirstOrDefault(c => c.Division == rules.KickDivision) ?? KickChoices[0];
            }
            else
            {
                var rules = _defaults.FretsFor(Kind, to);
                MaxChordSize = rules.MaxChordSize;
                HighestLane = LaneLimitChoices.FirstOrDefault(c => c.Lane == rules.HighestLane) ?? LaneLimitChoices[^1];
                Thinning = DivisionChoices.FirstOrDefault(c => c.Division == rules.ThinningDivision) ?? DivisionChoices[0];
                MinGapMs = rules.MinGapMs ?? 0;
                KeepForcedHopoAndTap = rules.KeepForcedHopoAndTap;
                KeepShortSustains = rules.KeepShortSustains;
            }

            _loading = false;
        }

        private FretReductionRules FretRules() => new FretReductionRules
        {
            MaxChordSize = (int)(MaxChordSize ?? 1),
            HighestLane = HighestLane?.Lane ?? 4,
            ThinningDivision = Thinning?.Division ?? 0,
            MinGapMs = (int)(MinGapMs ?? 0),
            KeepForcedHopoAndTap = KeepForcedHopoAndTap,
            KeepShortSustains = KeepShortSustains,
        }.Normalized(Kind);

        private DrumReductionRules DrumRules() => new DrumReductionRules
        {
            PadsPerHit = (int)(PadsPerHit ?? 1),
            ThinningDivision = Thinning?.Division ?? 0,
            MinGapMs = (int)(MinGapMs ?? 0),
            KickDivision = KickThinning?.Division ?? 0,
        }.Normalized();

        // The fields, into the rules being edited, for the family and target they were shown for.
        private void Store() => Store(Kind, RulesTarget);

        private void Store(ChartInstrumentKind kind, ChartDifficulty? to)
        {
            if (to is not { } target)
                return;
            _defaults = kind == ChartInstrumentKind.Drums
                ? _defaults.WithDrums(target, DrumRules())
                : _defaults.WithFrets(kind, target, FretRules() with { });
        }

        private void ScheduleRedraw()
        {
            if (_loading || _redraw is null)
                return;
            _redraw.Stop();
            _redraw.Start();
        }

        // The draft over the target, or over each target ticked, by the rules as they are now.
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
            if (IsAll)
            {
                Store();
                var targets = Toggles.Where(static t => t.IsChecked).Select(static t => t.Difficulty).ToList();
                if (targets.Count == 0)
                {
                    reduce.Cancel();
                    ClearCheck();
                    SummaryLines = [Strings.ReduceNoTargets];
                    Preview?.Clear(Strings.ReduceNoTargets);
                    return;
                }

                started = Kind == ChartInstrumentKind.Drums
                    ? reduce.StartAll(targets.ToDictionary(t => t, _defaults.DrumsFor), range)
                    : reduce.StartAll(_part, targets.ToDictionary(t => t, t => _defaults.FretsFor(Kind, t)), range);
            }
            else if (SelectedFrom is { } from && to.Difficulty is { } target)
            {
                started = Kind == ChartInstrumentKind.Drums
                    ? reduce.Start(from.Difficulty, target, DrumRules(), range)
                    : reduce.Start(_part, from.Difficulty, target, FretRules(), range);
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
                d.Result.Removed))];
            Preview?.OnDrafted(reduce);
            _ = CheckAsync(editor, reduce);
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

            double highest = result.Estimates.Values.Select(static e => e.Basis.PeakNps).DefaultIfEmpty(0).Max();
            Ratings = [.. Enum.GetValues<ChartDifficulty>().OrderDescending()
                .Where(result.Estimates.ContainsKey)
                .Select(d => new ReduceRatingRow(
                    ChartLabels.DifficultyName(d),
                    result.Estimates[d].Tier.ToString(CultureInfo.CurrentCulture),
                    string.Format(CultureInfo.CurrentCulture, Strings.ReducePeakFormat, result.Estimates[d].Basis.PeakNps),
                    highest > 0 ? result.Estimates[d].Basis.PeakNps / highest : 0,
                    targets.Contains(d),
                    result.OutOfOrder.Contains(d)))];
            OrderWarning = result.OutOfOrder.Count == 0
                ? ""
                : string.Format(CultureInfo.CurrentCulture, Strings.ReduceOrderWarningFormat, string.Join(", ", result.OutOfOrder.Select(ChartLabels.DifficultyName)));
            var map = editor.Session.TempoMap;
            _allFindings = [.. result.Findings.Select(f => Row(f, map))];
            ListFindings();
            Preview?.OnFindings(result.Findings);
            HasCheck = true;
            OnPropertyChanged(nameof(HasNoFindings));
        }

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
            Ratings = [];
            OrderWarning = "";
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
