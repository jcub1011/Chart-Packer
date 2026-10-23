using System;
using System.Collections.Generic;
using System.Linq;
using ChartBuilder.Resources;
using ChartShared.Core.Charting;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChartBuilder.ViewModels
{
    /// <summary>What a generated fill must end on, as Generate fills' options offer it.</summary>
    public sealed record FillEndChoice(FillEnd End, string Label);

    /// <summary>
    /// Generate fills' options (charter-spec C6 item 5), as its dialog picks them from a drums lane's Edit menu, starting
    /// from the last run's.
    /// </summary>
    public sealed partial class GenerateFillsViewModel : ViewModelBase
    {
        /// <param name="start">The options the dialog opens with.</param>
        /// <param name="overSelection">Whether fills are proposed over the selection's span, rather than the whole part.</param>
        public GenerateFillsViewModel(FillOptions start, bool overSelection)
        {
            EndChoices = [new FillEndChoice(FillEnd.Crash, Strings.GenerateFillsEndCrash), new FillEndChoice(FillEnd.AnyNote, Strings.GenerateFillsEndAnyNote)];
            Spacing = start.SpacingMeasures;
            Length = start.LengthMeasures;
            SelectedEnd = EndChoices.FirstOrDefault(c => c.End == start.EndOn) ?? EndChoices[0];
            ScopeText = overSelection ? Strings.GenerateScopeSelection : Strings.GenerateScopeWholePart;
        }

        /// <summary>How many measures apart generated fills start, at the least.</summary>
        [ObservableProperty]
        public partial decimal? Spacing { get; set; }

        /// <summary>How many measures a generated fill lasts.</summary>
        [ObservableProperty]
        public partial decimal? Length { get; set; }

        public IReadOnlyList<FillEndChoice> EndChoices { get; }

        /// <summary>What a generated fill must end on.</summary>
        [ObservableProperty]
        public partial FillEndChoice? SelectedEnd { get; set; }

        /// <summary>Where fills are proposed: over the selection, or the whole part.</summary>
        public string ScopeText { get; }

        /// <summary>The options as picked, an emptied box taking the default.</summary>
        public FillOptions Options => new(
            (int)Math.Max(1, Spacing ?? FillOptions.Default.SpacingMeasures),
            (int)Math.Max(1, Length ?? FillOptions.Default.LengthMeasures),
            SelectedEnd?.End ?? FillEnd.Crash);
    }
}
