using System;
using System.Globalization;
using ChartBuilder.Resources;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Editing;

namespace ChartBuilder.ViewModels
{
    /// <summary>A proposed fill of Generate fills: its time, what it ends on, and whether it's kept.</summary>
    public sealed class FillRegionViewModel : ProposalRowViewModel
    {
        public FillRegionViewModel(
            GeneratePhrasesViewModel owner,
            int index,
            FillRegion region,
            double startSeconds,
            double activationSeconds,
            int notes,
            string endOnLabel)
            : base(owner, index, region.Phrase, startSeconds, activationSeconds, notes)
        {
            Region = region;
            ActivationSeconds = activationSeconds;
            EndOnLabel = endOnLabel;
            IsKept = region.Kept;
        }

        public FillRegion Region { get; }

        /// <summary>Where the fill ends, on the measure's first beat star power activates on.</summary>
        public double ActivationSeconds { get; }

        /// <summary>What the fill ends on, as the options say: a crash, or any note.</summary>
        public string EndOnLabel { get; }

        public override bool ProposalAccent => true;

        public override string RoleText => string.Format(
            CultureInfo.CurrentCulture,
            Strings.GenerateFillsRoleFormat,
            EndOnLabel,
            SongTime.Format(TimeSpan.FromSeconds(ActivationSeconds)));
    }
}
