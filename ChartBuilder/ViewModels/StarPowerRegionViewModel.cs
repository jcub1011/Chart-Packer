using System;
using System.Globalization;
using ChartBuilder.Resources;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Editing;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChartBuilder.ViewModels
{
    /// <summary>A star power region of the star power window: its time, why it's there, and whether it's kept.</summary>
    public sealed partial class StarPowerRegionViewModel : ProposalRowViewModel
    {
        public StarPowerRegionViewModel(
            GeneratePhrasesViewModel owner,
            int index,
            StarPowerRegion region,
            double startSeconds,
            double endSeconds,
            double? spikeSeconds,
            int notes)
            : base(owner, index, region.Phrase, startSeconds, endSeconds, notes)
        {
            Region = region;
            SpikeSeconds = spikeSeconds;
            IsKept = region.Kept;
        }

        public StarPowerRegion Region { get; }

        /// <summary>For a lead-in, the hard passage it charges for; null otherwise.</summary>
        public double? SpikeSeconds { get; }

        public bool IsLeadIn => Region.IsSpikeLeadIn;

        public override string RoleText => SpikeSeconds is { } spike
            ? string.Format(CultureInfo.CurrentCulture, Strings.StarPowerRoleLeadInFormat, SongTime.Format(TimeSpan.FromSeconds(spike)))
            : Strings.StarPowerRoleFiller;

        public override bool HasChip => IsLeadIn;

        public override string? ChipText => IsLeadIn ? (Charged ? Strings.StarPowerCharged : Strings.StarPowerNotCharged) : null;

        public override bool ChipLow => IsLeadIn && !Charged;

        /// <summary>
        /// Whether the kept star power gives enough meter to activate going into its hard passage. It changes as other
        /// regions are kept or dropped, so a row shows it without being rebuilt.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ChipText), nameof(ChipLow))]
        public partial bool Charged { get; set; }
    }
}
