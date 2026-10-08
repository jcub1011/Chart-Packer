using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Threading;
using ChartBuilder.Resources;
using ChartBuilder.Services;
using ChartPacker.Core.Charting.Authoring;
using ChartPacker.Core.Charting.Editing;
using ChartPacker.Core.Charts;
using ChartPacker.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>A choice of family in the Reduce dialog's Settings mode.</summary>
    public sealed record FamilyChoice(ChartInstrumentKind Kind, string Label);

    /// <summary>A thinning grid the Reduce dialog offers: a snap's division, or 0 for none.</summary>
    public sealed record DivisionChoice(int Division, string Label);

    /// <summary>The highest fret or column a reduced chord may use.</summary>
    public sealed record LaneLimitChoice(int Lane, string Label);

    /// <summary>
    /// The Reduce dialog (charter-spec C9 item 2). Run from a lane, it shows the rules for the part's family prefilled from
    /// the defaults in Settings, and each change redraws the draft over the target difficulty; Apply adds it as one undoable
    /// step, and the changes are for that run only. Opened from Settings, it edits the defaults themselves.
    /// </summary>
    public sealed partial class ReduceViewModel : ViewModelBase
    {
        // How long after a change of the rules the draft is worked out again, so typing a number does it once.
        private static readonly TimeSpan RedrawDelay = TimeSpan.FromMilliseconds(120);

        private readonly ChartEditorViewModel? _editor;
        private readonly BuilderSettingsService _settings;
        private readonly ChartInstrument _part;
        private readonly DispatcherTimer? _redraw;
        private ReductionDefaults _defaults;
        private bool _loading;
        private bool _done;

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
            if (editor is not null)
            {
                _redraw = new DispatcherTimer { Interval = RedrawDelay };
                _redraw.Tick += (_, _) => Redraw();
            }
        }

        /// <summary>Raised when the dialog's work is done: applied, saved or cancelled.</summary>
        public event EventHandler? Done;

        /// <summary>A run over <paramref name="lane"/>'s part, to the lane's difficulty, or the one below it when it's Expert.</summary>
        public static ReduceViewModel ForRun(ChartEditorViewModel editor, BuilderSettingsService settings, ChartTrackKey lane)
        {
            var vm = new ReduceViewModel(settings, editor, lane.Instrument, ChartInstruments.KindOf(lane.Instrument));
            var part = editor.Session.Chart.PartOf(lane.Instrument);
            var to = lane.Difficulty == ChartDifficulty.Expert ? ChartDifficulty.Hard : lane.Difficulty;
            vm.ToChoices = [.. new[] { ChartDifficulty.Hard, ChartDifficulty.Medium, ChartDifficulty.Easy }.Select(Choice)];
            vm._loading = true;
            vm.SelectedTo = vm.ToChoices.First(c => c.Difficulty == to);
            vm.RefreshFrom(part is null ? null : d => part[d].Notes.Count > 0);
            vm._loading = false;
            vm.LoadRules();
            vm.Redraw();
            return vm;
        }

        /// <summary>The defaults in Settings, which every run starts from.</summary>
        public static ReduceViewModel ForDefaults(BuilderSettingsService settings)
        {
            var vm = new ReduceViewModel(settings, null, ChartInstrument.Guitar, ChartInstrumentKind.FiveFret);
            vm.ToChoices = [.. new[] { ChartDifficulty.Hard, ChartDifficulty.Medium, ChartDifficulty.Easy }.Select(Choice)];
            vm._loading = true;
            vm.SelectedFamily = vm.FamilyChoices[0];
            vm.SelectedTo = vm.ToChoices[0];
            vm._loading = false;
            vm.LoadRules();
            return vm;
        }

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
        public partial IReadOnlyList<DifficultyChoice> ToChoices { get; private set; } = [];

        /// <summary>The difficulty the draft is for.</summary>
        [ObservableProperty]
        public partial DifficultyChoice? SelectedTo { get; set; }

        public IReadOnlyList<DivisionChoice> DivisionChoices { get; }

        public IReadOnlyList<DivisionChoice> KickChoices { get; }

        public IReadOnlyList<LaneLimitChoice> LaneLimitChoices => Kind == ChartInstrumentKind.SixFret
            ? [.. Enumerable.Range(0, 3).Select(c => new LaneLimitChoice(c, string.Format(CultureInfo.CurrentCulture, Strings.ReduceColumnsFormat, c + 1)))]
            : [.. Enumerable.Range(0, 5).Select(f => new LaneLimitChoice(f, string.Format(CultureInfo.CurrentCulture, Strings.ReduceFretsFormat, FretName(f))))];

        public string LaneLimitLabel => Kind == ChartInstrumentKind.SixFret ? Strings.ReduceColumnsLabel : Strings.ReduceFretsLabel;

        public decimal ChordSizeMaximum => Kind == ChartInstrumentKind.SixFret ? 3 : 5;

        [ObservableProperty]
        public partial decimal? MaxChordSize { get; set; }

        [ObservableProperty]
        public partial LaneLimitChoice? HighestLane { get; set; }

        [ObservableProperty]
        public partial DivisionChoice? Thinning { get; set; }

        [ObservableProperty]
        public partial bool KeepForcedHopoAndTap { get; set; }

        [ObservableProperty]
        public partial bool KeepShortSustains { get; set; }

        [ObservableProperty]
        public partial decimal? PadsPerHit { get; set; }

        [ObservableProperty]
        public partial DivisionChoice? KickThinning { get; set; }

        /// <summary>What the draft holds, or why there's none.</summary>
        [ObservableProperty]
        public partial string Summary { get; private set; } = "";

        /// <summary>Adds the draft (a run), or saves the defaults (Settings).</summary>
        [RelayCommand]
        private void Apply()
        {
            if (_editor is { } editor)
            {
                _redraw?.Stop();
                Redraw();
                if (!editor.Session.Reduce.Accept())
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
            if (SelectedTo is not { } to)
                return;
            var shipped = ReductionDefaults.Shipped;
            _defaults = Kind == ChartInstrumentKind.Drums
                ? _defaults.WithDrums(to.Difficulty, shipped.DrumsFor(to.Difficulty))
                : _defaults.WithFrets(Kind, to.Difficulty, shipped.FretsFor(Kind, to.Difficulty));
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

        partial void OnSelectedToChanged(DifficultyChoice? oldValue, DifficultyChoice? newValue)
        {
            if (_loading || newValue is null)
                return;
            if (IsDefaults && oldValue is not null)
                Store(Kind, oldValue.Difficulty);
            if (_editor?.Session.Chart.PartOf(_part) is { } part)
                RefreshFrom(d => part[d].Notes.Count > 0);
            LoadRules();
            ScheduleRedraw();
        }

        partial void OnSelectedFromChanged(DifficultyChoice? value) => ScheduleRedraw();

        partial void OnMaxChordSizeChanged(decimal? value) => ScheduleRedraw();

        partial void OnHighestLaneChanged(LaneLimitChoice? value) => ScheduleRedraw();

        partial void OnThinningChanged(DivisionChoice? value) => ScheduleRedraw();

        partial void OnKeepForcedHopoAndTapChanged(bool value) => ScheduleRedraw();

        partial void OnKeepShortSustainsChanged(bool value) => ScheduleRedraw();

        partial void OnPadsPerHitChanged(decimal? value) => ScheduleRedraw();

        partial void OnKickThinningChanged(DivisionChoice? value) => ScheduleRedraw();

        private static DifficultyChoice Choice(ChartDifficulty difficulty) => new(difficulty, ChartLabels.DifficultyName(difficulty));

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
            if (SelectedTo is not { } to)
                return;
            _loading = true;
            if (Kind == ChartInstrumentKind.Drums)
            {
                var rules = _defaults.DrumsFor(to.Difficulty);
                PadsPerHit = rules.PadsPerHit;
                Thinning = DivisionChoices.FirstOrDefault(c => c.Division == rules.ThinningDivision) ?? DivisionChoices[0];
                KickThinning = KickChoices.FirstOrDefault(c => c.Division == rules.KickDivision) ?? KickChoices[0];
            }
            else
            {
                var rules = _defaults.FretsFor(Kind, to.Difficulty);
                MaxChordSize = rules.MaxChordSize;
                HighestLane = LaneLimitChoices.FirstOrDefault(c => c.Lane == rules.HighestLane) ?? LaneLimitChoices[^1];
                Thinning = DivisionChoices.FirstOrDefault(c => c.Division == rules.ThinningDivision) ?? DivisionChoices[0];
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
            KeepForcedHopoAndTap = KeepForcedHopoAndTap,
            KeepShortSustains = KeepShortSustains,
        }.Normalized(Kind);

        private DrumReductionRules DrumRules() => new DrumReductionRules
        {
            PadsPerHit = (int)(PadsPerHit ?? 1),
            ThinningDivision = Thinning?.Division ?? 0,
            KickDivision = KickThinning?.Division ?? 0,
        }.Normalized();

        // The fields, into the defaults being edited, for the family and target they were shown for.
        private void Store() => Store(Kind, SelectedTo?.Difficulty);

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

        // The draft over the target, by the rules as they are now.
        private void Redraw()
        {
            _redraw?.Stop();
            if (_editor is not { } editor || SelectedFrom is not { } from || SelectedTo is not { } to)
            {
                Summary = Strings.ReduceNothingToReduce;
                return;
            }

            var reduce = editor.Session.Reduce;
            bool started = Kind == ChartInstrumentKind.Drums
                ? reduce.Start(from.Difficulty, to.Difficulty, DrumRules())
                : reduce.Start(_part, from.Difficulty, to.Difficulty, FretRules());
            if (!started || reduce.Result is not { } result)
            {
                Summary = editor.Session.Status ?? Strings.ReduceNothingToReduce;
                return;
            }

            Summary = string.Format(
                CultureInfo.CurrentCulture,
                Strings.ReduceSummaryFormat,
                ChartLabels.DifficultyName(from.Difficulty),
                ChartLabels.DifficultyName(to.Difficulty),
                result.Notes.Count,
                result.Removed);
        }

        private void Finish()
        {
            _done = true;
            _redraw?.Stop();
            Done?.Invoke(this, EventArgs.Empty);
        }
    }
}
