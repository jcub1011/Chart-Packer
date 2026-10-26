using System;
using System.Collections.Generic;
using System.Linq;
using ChartBuilder.Services;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charts;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// The default reduction method: the rule-based reducer (charter-spec C9 item 2), as fields over a working copy
    /// of every set of rules. Future generation methods add their own view model and view plus a
    /// <see cref="ReduceMethodChoice"/>, with the Reduce dialog switching on the picked choice.
    /// </summary>
    public sealed partial class RulesReduceMethodViewModel : ViewModelBase
    {
        /// <summary>The method's id, as <see cref="BuilderSettings"/> persists it.</summary>
        public const string MethodId = BuilderSettings.DefaultReduceMethodId;

        private bool _loading;

        /// <param name="defaults">The rules to start from, which nothing here saves.</param>
        /// <param name="kind">The family shown first.</param>
        /// <param name="target">The target difficulty shown first: Hard, Medium or Easy.</param>
        public RulesReduceMethodViewModel(ReductionDefaults defaults, ChartInstrumentKind kind, ChartDifficulty target)
        {
            Rules = new ReductionRulesViewModel(defaults, kind, target);
            Rules.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
            _loading = true;
            try
            {
                SelectedRulesFor = RulesForChoices.First(c => c.Difficulty == target);
            }
            finally
            {
                _loading = false;
            }
        }

        /// <summary>Raised when a rule is changed in a field, not as another family or target is shown.</summary>
        public event EventHandler? Changed;

        /// <summary>The rules for the family and the difficulty picked, as fields; a run's changes are its own.</summary>
        public ReductionRulesViewModel Rules { get; }

        public IReadOnlyList<DifficultyChoice> RulesForChoices => ReductionRulesViewModel.TargetChoices;

        /// <summary>Which difficulty's rules are shown, in Reduce all.</summary>
        [ObservableProperty]
        public partial DifficultyChoice? SelectedRulesFor { get; set; }

        /// <summary>Whether the picker for which difficulty's rules are shown is visible (Reduce all).</summary>
        [ObservableProperty]
        public partial bool ShowRulesForPicker { get; set; }

        /// <summary>Every set of rules, with the fields' changes in it.</summary>
        public ReductionDefaults Defaults => Rules.Defaults;

        public bool IsDrums => Rules.IsDrums;

        public ChartInstrumentKind Kind => Rules.Kind;

        /// <summary>The fields, as rules for a 5-fret or GHL part.</summary>
        public FretReductionRules FretRules() => Rules.FretRules();

        /// <summary>The fields, as rules for drums.</summary>
        public DrumReductionRules DrumRules() => Rules.DrumRules();

        /// <summary>Shows the rules for <paramref name="kind"/> and <paramref name="target"/>.</summary>
        public void Show(ChartInstrumentKind kind, ChartDifficulty target) => Rules.Show(kind, target);

        /// <summary>Shows the rules for the family shown and <paramref name="target"/>.</summary>
        public void ShowForTarget(ChartDifficulty target) => Rules.Show(Rules.Kind, target);

        /// <summary>Puts back the shipped rules for the family and target shown.</summary>
        public void Reset() => Rules.Reset();

        partial void OnSelectedRulesForChanged(DifficultyChoice? value)
        {
            if (_loading || value is null)
                return;
            Rules.Show(Rules.Kind, value.Difficulty);
        }
    }
}
