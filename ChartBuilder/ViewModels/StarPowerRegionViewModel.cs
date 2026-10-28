using System;
using System.Globalization;
using ChartBuilder.Resources;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charting.Editing;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>A star power region of the star power window: its time, why it's there, and whether it's kept.</summary>
    public sealed partial class StarPowerRegionViewModel : ViewModelBase
    {
        private readonly GenerateStarPowerViewModel _owner;

        public StarPowerRegionViewModel(
            GenerateStarPowerViewModel owner,
            int index,
            StarPowerRegion region,
            double startSeconds,
            double endSeconds,
            double? spikeSeconds,
            int notes)
        {
            _owner = owner;
            Index = index;
            Region = region;
            StartSeconds = startSeconds;
            EndSeconds = endSeconds;
            SpikeSeconds = spikeSeconds;
            Notes = notes;
            IsKept = region.Kept;
            SelectCommand = new RelayCommand(() => _owner.SelectRegion(this));
            ToggleCommand = new RelayCommand(() => _owner.ToggleRegion(this));
        }

        public int Index { get; }

        public StarPowerRegion Region { get; }

        public double StartSeconds { get; }

        public double EndSeconds { get; }

        /// <summary>For a lead-in, the hard passage it charges for; null otherwise.</summary>
        public double? SpikeSeconds { get; }

        public int Notes { get; }

        /// <summary>
        /// Whether the kept star power gives enough meter to activate going into its hard passage. It changes as other
        /// regions are kept or dropped, so a row shows it without being rebuilt.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ChargedText))]
        public partial bool Charged { get; set; }

        public bool IsLeadIn => Region.IsSpikeLeadIn;

        public string TimeText => string.Format(
            CultureInfo.CurrentCulture,
            "{0} – {1}",
            SongTime.Format(TimeSpan.FromSeconds(StartSeconds)),
            SongTime.Format(TimeSpan.FromSeconds(EndSeconds)));

        public string RoleText => SpikeSeconds is { } spike
            ? string.Format(CultureInfo.CurrentCulture, Strings.StarPowerRoleLeadInFormat, SongTime.Format(TimeSpan.FromSeconds(spike)))
            : Strings.StarPowerRoleFiller;

        public string? ChargedText => IsLeadIn ? (Charged ? Strings.StarPowerCharged : Strings.StarPowerNotCharged) : null;

        public string DetailText => string.Format(
            CultureInfo.CurrentCulture,
            Strings.StarPowerRegionDetailFormat,
            Notes,
            string.Format(CultureInfo.CurrentCulture, "{0:0.0} s", EndSeconds - StartSeconds));

        [ObservableProperty]
        public partial bool IsKept { get; set; }

        [ObservableProperty]
        public partial bool IsSelected { get; set; }

        public IRelayCommand SelectCommand { get; }

        public IRelayCommand ToggleCommand { get; }
    }

    /// <summary>Star power already in the chart, as the window lists it.</summary>
    /// <param name="TimeText">Where it is, in the song.</param>
    /// <param name="LengthText">How long it lasts.</param>
    /// <param name="StatusText">What applying the plan does with it.</param>
    public sealed record StarPowerExistingItem(string TimeText, string LengthText, string StatusText);

    /// <summary>The choice of what Generate star power does with the star power already there.</summary>
    public sealed record StarPowerModeChoice(StarPowerMode Mode, string Label);
}
