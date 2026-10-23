using System;
using System.Globalization;
using ChartBuilder.Resources;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charts;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// Generate star power's options (charter-spec C8 item 1), as its dialog picks them from the Edit menu of a lane of
    /// <see cref="Part"/>, starting from the last run's.
    /// </summary>
    public sealed partial class GenerateStarPowerViewModel : ViewModelBase
    {
        /// <param name="part">The part star power is proposed for.</param>
        /// <param name="start">The options the dialog opens with.</param>
        /// <param name="overSelection">Whether star power is proposed over the selection's span, rather than the whole part.</param>
        public GenerateStarPowerViewModel(ChartInstrument part, StarPowerOptions start, bool overSelection)
        {
            Part = part;
            Spacing = (decimal)start.SpacingSeconds;
            PlanForSpikes = start.PlanForSpikes;
            ScopeText = overSelection ? Strings.GenerateScopeSelection : Strings.GenerateScopeWholePart;
        }

        public ChartInstrument Part { get; }

        /// <summary>The dialog's title, which names the part.</summary>
        public string Title => string.Format(CultureInfo.CurrentCulture, Strings.GenerateStarPowerTitleFormat, ChartLabels.InstrumentName(Part));

        /// <summary>About how many seconds of the part's playing apart generated star power phrases are.</summary>
        [ObservableProperty]
        public partial decimal? Spacing { get; set; }

        /// <summary>Whether generated star power is charged for the part's hard passages, rather than only spread evenly.</summary>
        [ObservableProperty]
        public partial bool PlanForSpikes { get; set; }

        /// <summary>Where star power is proposed: over the selection, or the whole part.</summary>
        public string ScopeText { get; }

        /// <summary>The options as picked, an emptied box taking the default.</summary>
        public StarPowerOptions Options => new(
            (double)Math.Clamp(Spacing ?? (decimal)StarPowerOptions.Default.SpacingSeconds, (decimal)StarPowerOptions.MinSpacingSeconds, (decimal)StarPowerOptions.MaxSpacingSeconds),
            PlanForSpikes);
    }
}
