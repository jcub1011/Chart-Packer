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
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChartBuilder.ViewModels
{
    /// <summary>What a generated fill must end on, as Generate fills' options offer it.</summary>
    public sealed record FillEndChoice(FillEnd End, string Label);

    /// <summary>
    /// Generate fills' window (charter-spec C6 item 5): the fills proposed over the selection or the whole part, kept,
    /// dropped or drawn, until the kept ones are applied to the chart as one undoable step.
    /// </summary>
    public sealed partial class GenerateFillsViewModel : GeneratePhrasesViewModel
    {
        private FillPlan _plan = null!;
        private ChartTrackKey _key;

        /// <param name="editor">The editor the window is over.</param>
        /// <param name="part">The drums the fills are proposed for.</param>
        /// <param name="start">The options the window opens with.</param>
        /// <param name="overSelection">Whether fills are proposed over the selection's span, rather than the whole part.</param>
        /// <param name="mode">What to do with the fills already there.</param>
        public GenerateFillsViewModel(ChartEditorViewModel editor, ChartInstrument part, FillOptions start, bool overSelection, GenerationMode mode)
            : base(editor, part, overSelection, mode)
        {
            EndChoices =
            [
                new FillEndChoice(FillEnd.Crash, Strings.GenerateFillsEndCrash),
                new FillEndChoice(FillEnd.AnyNote, Strings.GenerateFillsEndAnyNote),
            ];
            Spacing = start.SpacingMeasures;
            Length = start.LengthMeasures;
            SelectedEnd = EndChoices.FirstOrDefault(c => c.End == start.EndOn) ?? EndChoices[0];
            Initialize();
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

        /// <summary>The options as picked, an emptied box taking the default.</summary>
        public FillOptions Options => new(
            (int)Math.Clamp(Spacing ?? FillOptions.Default.SpacingMeasures, FillOptions.MinSpacingMeasures, FillOptions.MaxSpacingMeasures),
            (int)Math.Clamp(Length ?? FillOptions.Default.LengthMeasures, FillOptions.MinLengthMeasures, FillOptions.MaxLengthMeasures),
            SelectedEnd?.End ?? FillEnd.Crash);

        /// <summary>The fewest measures apart the spacing box offers, as the options define it.</summary>
        public decimal MinSpacing => FillOptions.MinSpacingMeasures;

        /// <summary>The most measures apart the spacing box offers.</summary>
        public decimal MaxSpacing => FillOptions.MaxSpacingMeasures;

        /// <summary>The fewest measures a fill can last.</summary>
        public decimal MinLength => FillOptions.MinLengthMeasures;

        /// <summary>The most measures a fill can last.</summary>
        public decimal MaxLength => FillOptions.MaxLengthMeasures;

        protected override string TitleFormat => Strings.GenerateFillsTitleFormat;

        public override string ApplyText => Strings.GenerateFillsApply;

        public override string ApplyToolTip => Strings.GenerateFillsApplyToolTip;

        public override string ModeLabel => Strings.GenerateFillsModeLabel;

        public override string RegionsHeading => Strings.GenerateFillsRegionsHeading;

        public override string ExistingHeading => Strings.GenerateFillsExistingHeading;

        public override string PreviewAutomationName => Strings.GeneratePreviewAutomationName;

        public override string TimelineAutomationName => Strings.GenerateTimelineAutomationName;

        protected override bool PlanAnyKept => _plan.AnyKept;

        protected override int SurvivingBandCount => _plan.SurvivingFills.Count;

        protected override GenerationMode PlanMode => _plan.Mode;

        protected override void GeneratePlan()
        {
            _plan = FillPlan.Generate(Session.Chart, Session.DrumsVariantShown, Options, Mode, Span);
            _key = KeyFor(_plan.Source);
        }

        protected override void RebuildRows()
        {
            var selectedPhrase = SelectedRegion?.Phrase;
            var noteTrack = Session.GameTrack(_key);
            string endOn = Options.EndOn == FillEnd.Crash ? Strings.GenerateFillsEndCrash : Strings.GenerateFillsEndAnyNote;

            Regions.Clear();
            for (int i = 0; i < _plan.Regions.Count; i++)
            {
                var region = _plan.Regions[i];
                var phrase = region.Phrase;
                int notes = noteTrack.Notes.Count(n => phrase.Covers(n.Tick));
                Regions.Add(new FillRegionViewModel(
                    this,
                    i,
                    region,
                    Session.AudioAt(phrase.Tick),
                    Session.AudioAt(region.ActivationTick),
                    notes,
                    endOn));
            }

            ExistingRegions.Clear();
            foreach (var phrase in _plan.Existing)
            {
                double start = Session.AudioAt(phrase.Tick);
                double activation = Session.AudioAt(phrase.CoverEnd - 1);
                ExistingRegions.Add(new ExistingProposalItem(
                    SongTime.Format(TimeSpan.FromSeconds(start)),
                    string.Format(CultureInfo.CurrentCulture, "{0:0.0} s", activation - start),
                    _plan.Mode == GenerationMode.Replace ? Strings.GenerateExistingReplaced : Strings.GenerateExistingKept));
            }

            NotifyExistingChanged();
            RestoreSelection(selectedPhrase);
        }

        protected override void RefreshPreview()
        {
            var playing = new List<Phrase>(_plan.SurvivingFills);
            playing.AddRange(_plan.Regions.Where(static r => r.Kept).Select(static r => r.Phrase));
            Scene = ProposalPreviewScene.Of(Session, _key, playing, starPower: false);

            var proposals = Regions
                .Select(r => new OverviewProposal(r.StartSeconds, r.EndSeconds, r.IsKept))
                .ToList();
            bool replacing = _plan.Mode == GenerationMode.Replace;
            Overview = OverviewData.Of(
                Session,
                _key,
                Playback.LengthSeconds,
                proposals,
                replaceStarPower: false,
                replacedPhrases: replacing ? _plan.Existing : null);
            // The extra is what outlines the selected proposal on the timeline; fills have no meter or hard passages.
            TimelineExtras = new StarPowerTimelineExtras([], [], SelectedIndex);
        }

        protected override void UpdateSummary()
        {
            HasProposals = Regions.Count > 0;
            int kept = Regions.Count(static r => r.IsKept);
            double seconds = Regions.Where(static r => r.IsKept).Sum(static r => r.EndSeconds - r.StartSeconds);
            SummaryText = HasProposals
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.GenerateFillsPlanSummaryFormat,
                    kept,
                    Regions.Count,
                    SongTime.Format(TimeSpan.FromSeconds(seconds)))
                : Strings.GenerateFillsNothingToApply;

            WarningText = HasProposals && kept == 0 ? Strings.GenerateNoProposalsKept : null;

            EmptyText = _plan.Reason switch
            {
                NoFillsReason.NoDrumNotes => Strings.GenerateFillsNoDrumNotes,
                NoFillsReason.TooFewStarPower => Strings.GenerateFillsTooFewStarPower,
                NoFillsReason.NoSpotFits => Options.EndOn == FillEnd.Crash
                    ? $"{Strings.GenerateFillsNoCrashSpot}\n{Strings.GenerateEmptyHint}"
                    : $"{Strings.GenerateFillsNoNoteSpot}\n{Strings.GenerateEmptyHint}",
                _ => Strings.GenerateFillsNoRegions,
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
    }
}
