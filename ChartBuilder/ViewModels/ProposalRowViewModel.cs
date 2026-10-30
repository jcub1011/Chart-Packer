using System;
using System.Globalization;
using ChartBuilder.Resources;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charts;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>A proposed phrase of the generation window: its time, why it's there, and whether it's kept.</summary>
    public abstract partial class ProposalRowViewModel : ViewModelBase
    {
        private readonly GeneratePhrasesViewModel _owner;

        protected ProposalRowViewModel(
            GeneratePhrasesViewModel owner,
            int index,
            Phrase phrase,
            double startSeconds,
            double endSeconds,
            int notes)
        {
            _owner = owner;
            Index = index;
            Phrase = phrase;
            StartSeconds = startSeconds;
            EndSeconds = endSeconds;
            Notes = notes;
            IsKept = true;
            SelectCommand = new RelayCommand(() => _owner.SelectRegion(this));
            ToggleCommand = new RelayCommand(() => _owner.ToggleRegion(this));
        }

        public int Index { get; }

        /// <summary>The phrase proposed, which identifies its row as the rows are rebuilt.</summary>
        public Phrase Phrase { get; }

        public double StartSeconds { get; }

        public double EndSeconds { get; }

        public int Notes { get; }

        /// <summary>What the row says about why the phrase is there, e.g. "Filler".</summary>
        public abstract string RoleText { get; }

        /// <summary>The row's second line, e.g. "8 notes – 2.0 s".</summary>
        public string DetailText => string.Format(
            CultureInfo.CurrentCulture,
            Strings.GenerateRegionDetailFormat,
            Notes,
            string.Format(CultureInfo.CurrentCulture, "{0:0.0} s", EndSeconds - StartSeconds));

        /// <summary>Whether the row's accent is the proposal amber, as a fill's is, rather than star power's cyan.</summary>
        public virtual bool ProposalAccent => false;

        /// <summary>Whether the row shows a status chip, as a lead-in does.</summary>
        public virtual bool HasChip => false;

        /// <summary>What the row's status chip says.</summary>
        public virtual string? ChipText => null;

        /// <summary>Whether the row's status chip is in its warning colour.</summary>
        public virtual bool ChipLow => false;

        public string TimeText => string.Format(
            CultureInfo.CurrentCulture,
            "{0} – {1}",
            SongTime.Format(TimeSpan.FromSeconds(StartSeconds)),
            SongTime.Format(TimeSpan.FromSeconds(EndSeconds)));

        [ObservableProperty]
        public partial bool IsKept { get; set; }

        [ObservableProperty]
        public partial bool IsSelected { get; set; }

        public IRelayCommand SelectCommand { get; }

        public IRelayCommand ToggleCommand { get; }
    }

    /// <summary>A phrase already in the chart, as the window lists it.</summary>
    /// <param name="TimeText">Where it is, in the song.</param>
    /// <param name="LengthText">How long it lasts.</param>
    /// <param name="StatusText">What applying the plan does with it.</param>
    public sealed record ExistingProposalItem(string TimeText, string LengthText, string StatusText);

    /// <summary>The choice of what a generator does with the phrases already there.</summary>
    public sealed record GenerationModeChoice(GenerationMode Mode, string Label);
}
