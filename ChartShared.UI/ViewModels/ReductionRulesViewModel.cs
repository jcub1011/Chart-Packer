using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using ChartShared.UI.Resources;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChartShared.UI.ViewModels
{
    /// <summary>A family of parts whose reduction rules are shown: 5-fret, GHL or drums.</summary>
    public sealed record FamilyChoice(ChartInstrumentKind Kind, string Label);

    /// <summary>A difficulty to pick, with its name.</summary>
    public sealed record DifficultyChoice(ChartDifficulty Difficulty, string Label);

    /// <summary>A thinning grid the rules offer: a snap's division, or 0 for none.</summary>
    public sealed record DivisionChoice(int Division, string Label);

    /// <summary>The highest fret or column a reduced chord may use.</summary>
    public sealed record LaneLimitChoice(int Lane, string Label);

    /// <summary>
    /// The reduction rules (charter-spec C9 item 2) for one family and target difficulty, as fields to edit, over a working
    /// copy of every set (<see cref="Defaults"/>). Chart Builder's Reduce dialog and Chart Packer's Generate difficulties
    /// dialog each show it (<c>ReductionRulesView</c>) and pick which family and target it shows.
    /// </summary>
    public sealed partial class ReductionRulesViewModel : ViewModelBase
    {
        /// <summary>The difficulties a part can be reduced to, highest first.</summary>
        public static IReadOnlyList<ChartDifficulty> Targets { get; } = [ChartDifficulty.Hard, ChartDifficulty.Medium, ChartDifficulty.Easy];

        private readonly IReadOnlyList<DivisionChoice> _burstGrids;
        private ReductionDefaults _defaults;
        private bool _loading;

        /// <param name="defaults">The rules to start from, which nothing here saves.</param>
        /// <param name="kind">The family shown first.</param>
        /// <param name="target">The target difficulty shown first: Hard, Medium or Easy.</param>
        public ReductionRulesViewModel(ReductionDefaults defaults, ChartInstrumentKind kind, ChartDifficulty target)
        {
            ArgumentNullException.ThrowIfNull(defaults);
            _defaults = defaults;
            DivisionChoices = [new DivisionChoice(0, SharedStrings.ReductionNoThinning), .. SnapGrid.CommonDivisions.Select(d => new DivisionChoice(d, SnapLabel(d)))];
            KickChoices = [new DivisionChoice(0, SharedStrings.ReductionKickKept), .. SnapGrid.CommonDivisions.Select(d => new DivisionChoice(d, SnapLabel(d)))];
            _burstGrids = [.. SnapGrid.CommonDivisions.Select(d => new DivisionChoice(d, SnapLabel(d)))];
            Kind = kind;
            Target = target;
            LoadRules();
        }

        /// <summary>Raised when a rule is changed in a field, not as another family or target is shown.</summary>
        public event EventHandler? Changed;

        /// <summary>The families there are, for a host that lets one be picked.</summary>
        public static IReadOnlyList<FamilyChoice> FamilyChoices { get; } =
        [
            new FamilyChoice(ChartInstrumentKind.FiveFret, SharedStrings.ReductionFamilyFiveFret),
            new FamilyChoice(ChartInstrumentKind.SixFret, SharedStrings.ReductionFamilyGhl),
            new FamilyChoice(ChartInstrumentKind.Drums, SharedStrings.ReductionFamilyDrums),
        ];

        /// <summary>The target difficulties there are, highest first, for a host that lets one be picked.</summary>
        public static IReadOnlyList<DifficultyChoice> TargetChoices { get; } = [.. Targets.Select(d => new DifficultyChoice(d, ChartLabels.DifficultyName(d)))];

        /// <summary>Every set of rules, with the fields' changes in it.</summary>
        public ReductionDefaults Defaults
        {
            get
            {
                Store();
                return _defaults;
            }
        }

        /// <summary>The family whose rules are shown.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsDrums), nameof(IsFrets), nameof(LaneLimitChoices), nameof(LaneLimitLabel), nameof(ChordSizeMaximum))]
        public partial ChartInstrumentKind Kind { get; private set; }

        /// <summary>The target difficulty whose rules are shown.</summary>
        [ObservableProperty]
        public partial ChartDifficulty Target { get; private set; }

        public bool IsDrums => Kind == ChartInstrumentKind.Drums;

        public bool IsFrets => !IsDrums;

        public IReadOnlyList<DivisionChoice> DivisionChoices { get; }

        public IReadOnlyList<DivisionChoice> KickChoices { get; }

        /// <summary>The grids a burst can be as close as: a snap each, never none, and none coarser than the thinning grid.</summary>
        public IReadOnlyList<DivisionChoice> BurstChoices => Thinning is { Division: > 0 } thinning
            ? [.. _burstGrids.Where(c => c.Division >= thinning.Division)]
            : _burstGrids;

        public IReadOnlyList<LaneLimitChoice> LaneLimitChoices => Kind == ChartInstrumentKind.SixFret
            ? [.. Enumerable.Range(0, 3).Select(c => new LaneLimitChoice(c, string.Format(CultureInfo.CurrentCulture, SharedStrings.ReductionColumnsFormat, c + 1)))]
            : [.. Enumerable.Range(0, 5).Select(f => new LaneLimitChoice(f, string.Format(CultureInfo.CurrentCulture, SharedStrings.ReductionFretsFormat, FretName(f))))];

        public string LaneLimitLabel => Kind == ChartInstrumentKind.SixFret ? SharedStrings.ReductionColumnsLabel : SharedStrings.ReductionFretsLabel;

        public decimal ChordSizeMaximum => Kind == ChartInstrumentKind.SixFret ? 3 : 5;

        public decimal MaxBurstMaximum => ReductionDefaults.MaxBurstLimit;

        [ObservableProperty]
        public partial decimal? MaxChordSize { get; set; }

        [ObservableProperty]
        public partial LaneLimitChoice? HighestLane { get; set; }

        [ObservableProperty]
        public partial DivisionChoice? Thinning { get; set; }

        /// <summary>The grid a burst's chords can be as close as.</summary>
        [ObservableProperty]
        public partial DivisionChoice? BurstThinning { get; set; }

        /// <summary>The most chords in a burst.</summary>
        [ObservableProperty]
        public partial decimal? MaxBurst { get; set; }

        [ObservableProperty]
        public partial bool KeepForcedHopoAndTap { get; set; }

        [ObservableProperty]
        public partial bool KeepShortSustains { get; set; }

        /// <summary>Whether a sustain held while other notes are played is kept.</summary>
        [ObservableProperty]
        public partial bool KeepExtendedSustains { get; set; }

        [ObservableProperty]
        public partial decimal? PadsPerHit { get; set; }

        [ObservableProperty]
        public partial DivisionChoice? KickThinning { get; set; }

        /// <summary>A snap's name, e.g. "1/16".</summary>
        public static string SnapLabel(int division) => string.Format(CultureInfo.CurrentCulture, SharedStrings.ReductionSnapFormat, division);

        /// <summary>Shows the rules for <paramref name="kind"/> parts reduced to <paramref name="target"/>, keeping the fields' changes to those shown.</summary>
        public void Show(ChartInstrumentKind kind, ChartDifficulty target)
        {
            if (kind == Kind && target == Target)
                return;
            Store();

            // A field whose choices change with the family may be cleared by its control meanwhile, which is no edit.
            _loading = true;
            try
            {
                Kind = kind;
                Target = target;
            }
            finally
            {
                _loading = false;
            }

            LoadRules();
        }

        /// <summary>Puts every set in place of the working copy, and shows the rules for the family and target shown.</summary>
        public void Load(ReductionDefaults defaults)
        {
            ArgumentNullException.ThrowIfNull(defaults);
            _defaults = defaults;
            LoadRules();
        }

        /// <summary>Puts back the shipped rules for the family and target shown.</summary>
        public void Reset()
        {
            var shipped = ReductionDefaults.Shipped;
            _defaults = Kind == ChartInstrumentKind.Drums
                ? _defaults.WithDrums(Target, shipped.DrumsFor(Target))
                : _defaults.WithFrets(Kind, Target, shipped.FretsFor(Kind, Target));
            LoadRules();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>The fields, as rules for a 5-fret or GHL part.</summary>
        public FretReductionRules FretRules() => new FretReductionRules
        {
            MaxChordSize = (int)(MaxChordSize ?? 1),
            HighestLane = HighestLane?.Lane ?? 4,
            ThinningDivision = Thinning?.Division ?? 0,
            BurstDivision = BurstThinning?.Division,
            MaxBurst = (int)(MaxBurst ?? 1),
            KeepForcedHopoAndTap = KeepForcedHopoAndTap,
            KeepShortSustains = KeepShortSustains,
            KeepExtendedSustains = KeepExtendedSustains,
        }.Normalized(Kind);

        /// <summary>The fields, as rules for drums.</summary>
        public DrumReductionRules DrumRules() => new DrumReductionRules
        {
            PadsPerHit = (int)(PadsPerHit ?? 1),
            ThinningDivision = Thinning?.Division ?? 0,
            BurstDivision = BurstThinning?.Division,
            MaxBurst = (int)(MaxBurst ?? 1),
            KickDivision = KickThinning?.Division ?? 0,
        }.Normalized();

        partial void OnMaxChordSizeChanged(decimal? value) => OnFieldChanged();

        partial void OnHighestLaneChanged(LaneLimitChoice? value) => OnFieldChanged();

        partial void OnThinningChanged(DivisionChoice? value)
        {
            // The burst's choices go no coarser than the thinning; its control may clear it meanwhile, which is no edit.
            bool loading = _loading;
            _loading = true;
            try
            {
                int grid = BurstThinning?.Division ?? value?.Division ?? 0;
                OnPropertyChanged(nameof(BurstChoices));
                BurstThinning = PickBurst(grid);
            }
            finally
            {
                _loading = loading;
            }

            OnFieldChanged();
        }

        partial void OnBurstThinningChanged(DivisionChoice? value) => OnFieldChanged();

        partial void OnMaxBurstChanged(decimal? value) => OnFieldChanged();

        partial void OnKeepForcedHopoAndTapChanged(bool value) => OnFieldChanged();

        partial void OnKeepShortSustainsChanged(bool value) => OnFieldChanged();

        partial void OnKeepExtendedSustainsChanged(bool value) => OnFieldChanged();

        partial void OnPadsPerHitChanged(decimal? value) => OnFieldChanged();

        partial void OnKickThinningChanged(DivisionChoice? value) => OnFieldChanged();

        private void OnFieldChanged()
        {
            if (!_loading)
                Changed?.Invoke(this, EventArgs.Empty);
        }

        private static string FretName(int fret) => fret switch
        {
            0 => SharedStrings.ReductionFretGreen,
            1 => SharedStrings.ReductionFretRed,
            2 => SharedStrings.ReductionFretYellow,
            3 => SharedStrings.ReductionFretBlue,
            _ => SharedStrings.ReductionFretOrange,
        };

        // The rules for the family and target, into the fields.
        private void LoadRules()
        {
            _loading = true;
            try
            {
                if (Kind == ChartInstrumentKind.Drums)
                {
                    var rules = _defaults.DrumsFor(Target);
                    PadsPerHit = rules.PadsPerHit;
                    Thinning = DivisionChoices.FirstOrDefault(c => c.Division == rules.ThinningDivision) ?? DivisionChoices[0];
                    LoadBursts(rules.ThinningDivision, rules.BurstDivision, rules.MaxBurst);
                    KickThinning = KickChoices.FirstOrDefault(c => c.Division == rules.KickDivision) ?? KickChoices[0];
                }
                else
                {
                    var rules = _defaults.FretsFor(Kind, Target);
                    MaxChordSize = rules.MaxChordSize;
                    HighestLane = LaneLimitChoices.FirstOrDefault(c => c.Lane == rules.HighestLane) ?? LaneLimitChoices[^1];
                    Thinning = DivisionChoices.FirstOrDefault(c => c.Division == rules.ThinningDivision) ?? DivisionChoices[0];
                    LoadBursts(rules.ThinningDivision, rules.BurstDivision, rules.MaxBurst);
                    KeepForcedHopoAndTap = rules.KeepForcedHopoAndTap;
                    KeepShortSustains = rules.KeepShortSustains;
                    KeepExtendedSustains = rules.KeepExtendedSustains ?? true;
                }
            }
            finally
            {
                _loading = false;
            }
        }

        // A set's bursts, into the fields: where it has none set, as close as its thinning, one chord at a time.
        private void LoadBursts(int division, int? burstDivision, int? maxBurst)
        {
            BurstThinning = PickBurst(burstDivision ?? division);
            MaxBurst = maxBurst ?? 1;
        }

        // The burst's choice on grid, or the nearest there is: one coarser than the thinning is the thinning's.
        private DivisionChoice? PickBurst(int grid)
        {
            var choices = BurstChoices;
            return choices.FirstOrDefault(c => c.Division == grid) ?? choices.MinBy(c => Math.Abs(c.Division - grid));
        }

        // The fields, into the working copy, for the family and target they show.
        private void Store()
        {
            _defaults = Kind == ChartInstrumentKind.Drums
                ? _defaults.WithDrums(Target, DrumRules())
                : _defaults.WithFrets(Kind, Target, FretRules());
        }
    }
}
