using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using ChartBuilder.Controls;
using ChartBuilder.Resources;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using ChartShared.UI.Controls.Highway;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// The window shared by Generate star power and Generate fills (charter-spec C6 item 5, C8 item 1): a highway preview
    /// of the result, a whole-song timeline of the proposed phrases, and the proposals kept, dropped or drawn, until the
    /// kept ones are applied to the chart as one undoable step.
    /// </summary>
    /// <remarks>
    /// A generator adds its own options and its own plan; this holds what the window does the same way for each: the
    /// ledger, the selection, the preview, the timeline, the footer and the commands. It changes nothing until Apply.
    /// </remarks>
    public abstract partial class GeneratePhrasesViewModel : ViewModelBase
    {
        private readonly EditorSession _session;
        private readonly PlaybackViewModel _playback;
        private readonly (long From, long To)? _span;
        private bool _dirty;
        private bool _ready;
        private GenerationMode? _pendingMode;
        private bool _revertingMode;

        protected GeneratePhrasesViewModel(ChartEditorViewModel editor, ChartInstrument part, bool overSelection, GenerationMode mode)
        {
            _session = editor.Session;
            _playback = editor.Playback;
            Part = part;
            _span = overSelection ? _session.Selection.SpanIn(part) : null;
            ScopeText = overSelection ? Strings.GenerateScopeSelection : Strings.GenerateScopeWholePart;
            ModeChoices =
            [
                new GenerationModeChoice(GenerationMode.Replace, Strings.GenerateModeReplace),
                new GenerationModeChoice(GenerationMode.Add, Strings.GenerateModeAdd),
            ];
            SelectedMode = ModeChoices.First(c => c.Mode == mode);
        }

        /// <summary>The editor the window is over.</summary>
        protected EditorSession Session => _session;

        /// <summary>The ticks the proposals are limited to; null for the whole part.</summary>
        protected (long From, long To)? Span => _span;

        /// <summary>The part the phrases are proposed for.</summary>
        public ChartInstrument Part { get; }

        /// <summary>The window's title, naming the part.</summary>
        public string Title => string.Format(CultureInfo.CurrentCulture, TitleFormat, ChartLabels.InstrumentName(Part));

        /// <summary>Where phrases are proposed: over the selection, or the whole part.</summary>
        public string ScopeText { get; }

        public PlaybackViewModel Playback => _playback;

        public Func<TimeSpan> Clock => _playback.Clock;

        public double LengthSeconds => _playback.LengthSeconds;

        /// <summary>Whether the window applied the plan and closed.</summary>
        public bool Applied { get; protected set; }

        /// <summary>How far ahead the preview highway shows, in seconds.</summary>
        [ObservableProperty]
        public partial double LookAhead { get; set; } = 4;

        /// <summary>The proposals, earliest first.</summary>
        public ObservableCollection<ProposalRowViewModel> Regions { get; } = [];

        /// <summary>The phrases already in the chart, wholly inside the span.</summary>
        public ObservableCollection<ExistingProposalItem> ExistingRegions { get; } = [];

        /// <summary>Whether the chart has phrases wholly inside the span, to show as context.</summary>
        public bool HasExisting => ExistingRegions.Count > 0;

        [ObservableProperty]
        public partial ProposalRowViewModel? SelectedRegion { get; set; }

        [ObservableProperty]
        public partial int? SelectedIndex { get; set; }

        /// <summary>What the preview highway draws.</summary>
        [ObservableProperty]
        public partial HighwayScene? Scene { get; protected set; }

        /// <summary>What the whole-song timeline draws.</summary>
        [ObservableProperty]
        public partial OverviewData? Overview { get; protected set; }

        /// <summary>The meter ribbon and hard passages the timeline adds, for star power; null for fills.</summary>
        [ObservableProperty]
        public partial StarPowerTimelineExtras? TimelineExtras { get; protected set; }

        [ObservableProperty]
        public partial string? SummaryText { get; protected set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasWarningOnly))]
        public partial string? WarningText { get; protected set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasStatus), nameof(HasWarningOnly))]
        public partial string? StatusText { get; protected set; }

        /// <summary>Whether applying failed, so the footer shows the error in place of the warning.</summary>
        public bool HasStatus => StatusText is not null;

        /// <summary>Whether the footer's notice line shows the warning, rather than an error standing in for it.</summary>
        public bool HasWarningOnly => WarningText is not null && StatusText is null;

        /// <summary>Why there's nothing proposed, with what to try; null when there are proposals.</summary>
        [ObservableProperty]
        public partial string? EmptyText { get; protected set; }

        [ObservableProperty]
        public partial bool HasProposals { get; protected set; }

        [ObservableProperty]
        public partial bool IsConfirmingRegenerate { get; private set; }

        public IReadOnlyList<GenerationModeChoice> ModeChoices { get; }

        /// <summary>What the generator does with the phrases already there.</summary>
        [ObservableProperty]
        public partial GenerationModeChoice SelectedMode { get; set; }

        public GenerationMode Mode => SelectedMode.Mode;

        /// <summary>Whether there's something to apply: proposals, and at least one kept.</summary>
        public bool CanApply => HasProposals && PlanAnyKept;

        /// <summary>
        /// The band of the preview highway the selected proposal draws, or null when none is selected or it's dropped:
        /// the phrases already there come first, then the kept proposals in order.
        /// </summary>
        public int? SelectedBand => SelectedRegion is { IsKept: true } selected
            ? SurvivingBandCount + Regions.Take(selected.Index).Count(static r => r.IsKept)
            : null;

        /// <summary>How many phrases already in the chart the preview draws before the kept proposals.</summary>
        protected abstract int SurvivingBandCount { get; }

        /// <summary>The title with the part in it, e.g. "Generate Star Power: {0}".</summary>
        protected abstract string TitleFormat { get; }

        /// <summary>What the apply button says.</summary>
        public abstract string ApplyText { get; }

        /// <summary>What the apply button does, for its tooltip.</summary>
        public abstract string ApplyToolTip { get; }

        /// <summary>What the mode picker is called, e.g. "Existing Star Power".</summary>
        public abstract string ModeLabel { get; }

        /// <summary>What the ledger's heading says, e.g. "Regions".</summary>
        public abstract string RegionsHeading { get; }

        /// <summary>What the existing phrases' panel is called, e.g. "Existing Star Power".</summary>
        public abstract string ExistingHeading { get; }

        /// <summary>What the preview highway is called for accessibility.</summary>
        public abstract string PreviewAutomationName { get; }

        /// <summary>What the timeline is called for accessibility.</summary>
        public abstract string TimelineAutomationName { get; }

        /// <summary>Whether the plan has a kept proposal.</summary>
        protected abstract bool PlanAnyKept { get; }

        /// <summary>What the plan does with the phrases already there.</summary>
        protected abstract GenerationMode PlanMode { get; }

        /// <summary>Works the plan out again from the options and mode as they are.</summary>
        protected abstract void GeneratePlan();

        /// <summary>Rebuilds <see cref="Regions"/> and <see cref="ExistingRegions"/> from the plan.</summary>
        protected abstract void RebuildRows();

        /// <summary>Updates what the plan's kept proposals decide, without rebuilding the rows.</summary>
        protected abstract void RefreshPreview();

        /// <summary>Updates the footer's summary, warning and empty text.</summary>
        protected abstract void UpdateSummary();

        /// <summary>Keeps or drops the plan's proposal at <paramref name="index"/>.</summary>
        protected abstract void TogglePlan(int index);

        /// <summary>Whether the plan keeps its proposal at <paramref name="index"/>.</summary>
        protected abstract bool IsPlanKept(int index);

        /// <summary>Keeps every proposal of the plan.</summary>
        protected abstract void KeepAllPlan();

        /// <summary>Drops every proposal of the plan.</summary>
        protected abstract void DropAllPlan();

        /// <summary>Applies the kept proposals to the chart as one step; whether it did.</summary>
        public abstract bool Apply();

        /// <summary>
        /// Called at the end of a concrete generator's constructor, once its options are set: works the plan out and
        /// fills the window.
        /// </summary>
        protected void Initialize()
        {
            _ready = true;
            GeneratePlan();
            RefreshAll();
            SelectFirst();
        }

        /// <summary>The track the preview and timeline show: the focused lane's, or the plan's source difficulty.</summary>
        protected ChartTrackKey KeyFor(ChartDifficulty? source) => new(
            Part,
            _session.Focused is { } focused
                && focused.Instrument == Part
                && _session.TrackOf(focused) is { Notes.Count: > 0 }
                ? focused.Difficulty
                : source ?? ChartDifficulty.Expert);

        /// <summary>Selects a proposal without moving the audio, as a press on its edge on the highway does.</summary>
        public void SelectRegionForEdit(ProposalRowViewModel region)
        {
            SelectedRegion = region;
            SelectedIndex = region.Index;
            foreach (var item in Regions)
                item.IsSelected = item == region;
            OnPropertyChanged(nameof(SelectedBand));
            TimelineExtras = TimelineExtras is { } extras ? extras with { SelectedProposal = SelectedIndex } : null;
        }

        [RelayCommand]
        public void SelectRegion(ProposalRowViewModel region)
        {
            SelectRegionForEdit(region);
            _playback.SeekTo(region.StartSeconds);
        }

        [RelayCommand]
        public void ToggleRegion(ProposalRowViewModel region)
        {
            if (region.Index < 0 || region.Index >= Regions.Count)
                return;
            TogglePlan(region.Index);
            region.IsKept = IsPlanKept(region.Index);
            _dirty = true;
            RefreshAfterKeepChange();
        }

        [RelayCommand]
        public void KeepAll()
        {
            KeepAllPlan();
            SyncKept();
            _dirty = true;
            RefreshAfterKeepChange();
        }

        [RelayCommand]
        public void DropAll()
        {
            DropAllPlan();
            SyncKept();
            _dirty = true;
            RefreshAfterKeepChange();
        }

        [RelayCommand]
        public void PreviousRegion()
        {
            if (Regions.Count == 0)
                return;
            int index = SelectedRegion is { } selected ? selected.Index - 1 : Regions.Count - 1;
            if (index < 0)
                index = Regions.Count - 1;
            SelectRegion(Regions[index]);
        }

        [RelayCommand]
        public void NextRegion()
        {
            if (Regions.Count == 0)
                return;
            int index = SelectedRegion is { } selected ? selected.Index + 1 : 0;
            if (index >= Regions.Count)
                index = 0;
            SelectRegion(Regions[index]);
        }

        /// <summary>
        /// Works the plan out again with the options as they are now, in place of the proposals as they are: the window
        /// asks first when that would lose kept or dropped proposals.
        /// </summary>
        [RelayCommand]
        public void Regenerate()
        {
            if (_dirty && !IsConfirmingRegenerate)
            {
                IsConfirmingRegenerate = true;
                return;
            }

            ResetPlan();
        }

        /// <summary>Works the plan out again, losing the changes.</summary>
        [RelayCommand]
        public void ConfirmRegenerate()
        {
            // Taken before the question closes, so closing it doesn't put the mode back.
            _pendingMode = null;
            IsConfirmingRegenerate = false;
            ResetPlan();
        }

        /// <summary>Keeps the proposals as they are, rather than regenerating.</summary>
        [RelayCommand]
        public void CancelRegenerate()
        {
            IsConfirmingRegenerate = false;
            if (_pendingMode is { } mode)
            {
                // A change of mode was asking; put the picker back where the plan still is.
                _pendingMode = null;
                _revertingMode = true;
                try
                {
                    SelectedMode = ModeChoices.First(c => c.Mode == PlanMode);
                }
                finally
                {
                    _revertingMode = false;
                }
            }
        }

        /// <summary>Closes without applying anything.</summary>
        public void Cancel() => Applied = false;

        partial void OnSelectedModeChanged(GenerationModeChoice value)
        {
            if (!_ready || _revertingMode || value is null)
                return;
            if (PlanMode == value.Mode)
            {
                // Back where the plan is, so there's nothing left to ask.
                if (_pendingMode is not null)
                {
                    _pendingMode = null;
                    IsConfirmingRegenerate = false;
                }

                return;
            }

            if (_dirty)
            {
                // Changing it works the plan out again, so ask first when that would lose kept or dropped proposals.
                _pendingMode = value.Mode;
                IsConfirmingRegenerate = true;
                return;
            }

            // The mode changes what the generator works out, so the plan is proposed again.
            ResetPlan();
        }

        /// <summary>Replaces the plan with a fresh one from the current options and mode.</summary>
        protected void ResetPlan()
        {
            GeneratePlan();
            _dirty = false;
            IsConfirmingRegenerate = false;
            SelectedRegion = null;
            StatusText = null;
            RefreshAll();
            SelectFirst();
        }

        /// <summary>Rebuilds the rows and updates everything the plan decides.</summary>
        protected void RefreshAll()
        {
            RebuildRows();
            RefreshLive();
        }

        /// <summary>
        /// Updates what the plan's kept proposals decide: the rows' chips, the highway, the timeline and the summary. It
        /// only updates the rows, never rebuilds them, so their order, the list's scroll position and a row's focus stay
        /// as they were; <see cref="RefreshAll"/> rebuilds them first, when the proposals themselves changed.
        /// </summary>
        protected virtual void RefreshLive()
        {
            UpdateRowsLive();
            RefreshPreview();
            UpdateSummary();
            OnPropertyChanged(nameof(CanApply));
            OnPropertyChanged(nameof(SelectedBand));
        }

        /// <summary>Updates the rows' own state, such as a lead-in's charged chip, without rebuilding them.</summary>
        protected virtual void UpdateRowsLive()
        {
        }

        /// <summary>
        /// Updates what keeping or dropping a proposal changes, without rebuilding the rows, so only colours and the
        /// preview change.
        /// </summary>
        private void RefreshAfterKeepChange()
        {
            // Changing the plan makes an earlier attempt's failure stale.
            StatusText = null;
            RefreshLive();
        }

        /// <summary>Takes the rows' kept state from the plan after Keep all or Drop all, without rebuilding them.</summary>
        private void SyncKept()
        {
            foreach (var region in Regions)
                region.IsKept = IsPlanKept(region.Index);
        }

        private void SelectFirst()
        {
            if (SelectedRegion is null && Regions.Count > 0)
                SelectRegionForEdit(Regions[0]);
        }

        /// <summary>Restores the selection after the rows are rebuilt, by the selected phrase's identity.</summary>
        protected void RestoreSelection(Phrase? selectedPhrase)
        {
            SelectedRegion = selectedPhrase is { } wanted
                ? Regions.FirstOrDefault(r => r.Phrase == wanted)
                : null;
            foreach (var item in Regions)
                item.IsSelected = item == SelectedRegion;
            SelectedIndex = SelectedRegion?.Index;
        }

        /// <summary>Tells the view whether the existing phrases' panel shows.</summary>
        protected void NotifyExistingChanged() => OnPropertyChanged(nameof(HasExisting));
    }
}
