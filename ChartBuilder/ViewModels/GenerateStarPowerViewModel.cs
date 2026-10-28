using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ChartBuilder.Controls;
using ChartBuilder.Resources;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using ChartShared.UI.Controls.Highway;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// Generate star power's window (charter-spec C8 item 1): the star power layout proposed over the selection or the
    /// whole part, kept, dropped or drawn, until the kept ones are applied to the chart as one undoable step.
    /// </summary>
    public sealed partial class GenerateStarPowerViewModel : GeneratePhrasesViewModel
    {
        private StarPowerPlan _plan = null!;
        private StarPowerCoverage _coverage = StarPowerCoverage.Empty;
        private ChartTrackKey _key;

        /// <param name="editor">The editor the window is over.</param>
        /// <param name="part">The part star power is proposed for.</param>
        /// <param name="start">The options the window opens with.</param>
        /// <param name="overSelection">Whether star power is proposed over the selection's span, rather than the whole part.</param>
        /// <param name="mode">What to do with the star power already there.</param>
        public GenerateStarPowerViewModel(ChartEditorViewModel editor, ChartInstrument part, StarPowerOptions start, bool overSelection, GenerationMode mode)
            : base(editor, part, overSelection, mode)
        {
            Spacing = (decimal)start.SpacingSeconds;
            PlanForSpikes = start.PlanForSpikes;
            Initialize();
        }

        /// <summary>About how many seconds of the part's playing apart generated star power phrases are.</summary>
        [ObservableProperty]
        public partial decimal? Spacing { get; set; }

        /// <summary>Whether generated star power is charged for the part's hard passages.</summary>
        [ObservableProperty]
        public partial bool PlanForSpikes { get; set; }

        /// <summary>The options as picked, an emptied box taking the default.</summary>
        public StarPowerOptions Options => new(
            (double)Math.Clamp(Spacing ?? (decimal)StarPowerOptions.Default.SpacingSeconds, (decimal)StarPowerOptions.MinSpacingSeconds, (decimal)StarPowerOptions.MaxSpacingSeconds),
            PlanForSpikes);

        protected override string TitleFormat => Strings.GenerateStarPowerTitleFormat;

        public override string ApplyText => Strings.StarPowerApply;

        public override string ApplyToolTip => Strings.StarPowerApplyToolTip;

        public override string ModeLabel => Strings.StarPowerModeLabel;

        public override string RegionsHeading => Strings.StarPowerRegionsHeading;

        public override string ExistingHeading => Strings.StarPowerExistingHeading;

        public override string PreviewAutomationName => Strings.GeneratePreviewAutomationName;

        public override string TimelineAutomationName => Strings.GenerateTimelineAutomationName;

        protected override bool PlanAnyKept => _plan.AnyKept;

        protected override int SurvivingBandCount => _plan.SurvivingStarPower.Count;

        protected override GenerationMode PlanMode => _plan.Mode;

        protected override void GeneratePlan()
        {
            _plan = StarPowerPlan.Generate(Session.Chart, Part, Options, Mode, Span, Session.ReadOptions);
            _key = KeyFor(_plan.Source);
        }

        protected override void RebuildRows()
        {
            var selectedPhrase = SelectedRegion?.Phrase;
            var noteTrack = Session.GameTrack(_key);

            Regions.Clear();
            for (int i = 0; i < _plan.Regions.Count; i++)
            {
                var region = _plan.Regions[i];
                var phrase = region.Phrase;
                int notes = noteTrack.Notes.Count(n => phrase.Covers(n.Tick));
                double? spike = region.SpikeTick is { } tick ? Session.AudioAt(tick) : null;
                Regions.Add(new StarPowerRegionViewModel(
                    this,
                    i,
                    region,
                    Session.AudioAt(phrase.Tick),
                    Session.AudioAt(phrase.CoverEnd),
                    spike,
                    notes));
            }

            ExistingRegions.Clear();
            foreach (var phrase in _plan.Existing)
            {
                ExistingRegions.Add(new ExistingProposalItem(
                    SongTime.Format(TimeSpan.FromSeconds(Session.AudioAt(phrase.Tick))),
                    string.Format(CultureInfo.CurrentCulture, "{0:0.0} s", Session.AudioAt(phrase.CoverEnd) - Session.AudioAt(phrase.Tick)),
                    _plan.Mode == GenerationMode.Replace ? Strings.GenerateExistingReplaced : Strings.GenerateExistingKept));
            }

            NotifyExistingChanged();
            RestoreSelection(selectedPhrase);
        }

        protected override void RefreshLive()
        {
            _coverage = _plan.Coverage(SampleCount());
            base.RefreshLive();
        }

        protected override void UpdateRowsLive()
        {
            var charged = ChargedSpikes();
            foreach (var region in Regions.OfType<StarPowerRegionViewModel>())
                region.Charged = region.Region.SpikeTick is { } tick && charged.Contains(tick);
        }

        protected override void RefreshPreview()
        {
            var playing = new List<Phrase>(_plan.SurvivingStarPower);
            playing.AddRange(_plan.Regions.Where(static r => r.Kept).Select(static r => r.Phrase));
            Scene = ProposalPreviewScene.Of(Session, _key, playing, starPower: true);

            var proposals = Regions
                .Select(r => new OverviewProposal(r.StartSeconds, r.EndSeconds, r.IsKept))
                .ToList();
            bool replacing = _plan.Mode == GenerationMode.Replace;
            Overview = OverviewData.Of(
                Session,
                _key,
                Playback.LengthSeconds,
                proposals,
                replacing,
                replacing ? _plan.Existing : null);
            TimelineExtras = new StarPowerTimelineExtras(
                [.. _coverage.Samples.Select(s => new StarPowerMeterPoint(Session.AudioAt(s.Tick), s.Meter, s.Active))],
                [.. _coverage.Spikes.Select(s => new StarPowerSpikeMark(Session.AudioAt(s.Spike.StartTick), s.Charged))],
                SelectedIndex);
        }

        protected override void UpdateSummary()
        {
            HasProposals = Regions.Count > 0;
            int kept = Regions.Count(static r => r.IsKept);
            double seconds = Regions.Where(static r => r.IsKept).Sum(static r => r.EndSeconds - r.StartSeconds);
            SummaryText = HasProposals
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.StarPowerPlanSummaryFormat,
                    kept,
                    Regions.Count,
                    SongTime.Format(TimeSpan.FromSeconds(seconds)))
                : Strings.StarPowerNothingToApply;

            int uncharged = _coverage.UnchargedSpikes;
            string? warning = null;
            if (HasProposals && kept == 0)
                warning = Strings.GenerateNoProposalsKept;
            else if (HasProposals && uncharged > 0)
                warning = uncharged == 1
                    ? Strings.StarPowerUnchargedOne
                    : string.Format(CultureInfo.CurrentCulture, Strings.StarPowerUnchargedFormat, uncharged);
            WarningText = warning;

            EmptyText = _plan.Reason switch
            {
                NoStarPowerReason.UnsupportedInstrument => Strings.StarPowerUnsupportedInstrument,
                NoStarPowerReason.NoNotes => Strings.StarPowerNoNotes,
                NoStarPowerReason.HasStarPower => Strings.StarPowerHasStarPower,
                NoStarPowerReason.NoSpotFits => $"{Strings.StarPowerNoSpot}\n{Strings.GenerateEmptyHint}",
                _ => Strings.StarPowerNoRegions,
            };
            if (HasProposals)
                EmptyText = null;
        }

        protected override void TogglePlan(int index) => _plan.Toggle(index);

        protected override bool IsPlanKept(int index) => _plan.Regions[index].Kept;

        protected override void KeepAllPlan() => _plan.KeepAll();

        protected override void DropAllPlan() => _plan.DropAll();

        public override bool Apply()
        {
            if (!CanApply || _plan.BuildCommand() is not { } command)
                return false;
            if (!Session.Execute(command))
            {
                StatusText = Session.Status;
                return false;
            }

            Applied = true;
            return true;
        }

        /// <summary>Whether each hard passage is charged for, by its tick.</summary>
        private HashSet<long> ChargedSpikes() =>
            _coverage.Spikes.Where(static s => s.Charged).Select(static s => s.Spike.StartTick).ToHashSet();

        private int SampleCount() => Math.Clamp((int)Math.Round(Math.Max(0, Playback.LengthSeconds) * 4), 64, 2048);
    }
}
