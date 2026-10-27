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
    /// Generate star power's window (charter-spec C8 item 1): a highway preview of the result, a whole-song timeline of the
    /// proposed regions, and the regions kept, dropped, drawn or resized, until the kept ones are applied to the chart as
    /// one undoable step.
    /// </summary>
    public sealed partial class GenerateStarPowerViewModel : ViewModelBase
    {
        private readonly EditorSession _session;
        private readonly PlaybackViewModel _playback;
        private readonly (long From, long To)? _span;
        private readonly ChartTrackKey _key;
        private StarPowerPlan _plan;
        private StarPowerCoverage _coverage = StarPowerCoverage.Empty;
        private bool _dirty;
        private bool _ready;

        /// <param name="editor">The editor the window is over.</param>
        /// <param name="part">The part star power is proposed for.</param>
        /// <param name="start">The options the window opens with.</param>
        /// <param name="overSelection">Whether star power is proposed over the selection's span, rather than the whole part.</param>
        /// <param name="mode">What to do with the star power already there.</param>
        public GenerateStarPowerViewModel(ChartEditorViewModel editor, ChartInstrument part, StarPowerOptions start, bool overSelection, StarPowerMode mode)
        {
            _session = editor.Session;
            _playback = editor.Playback;
            Part = part;
            _span = overSelection ? _session.Selection.SpanIn(part) : null;
            Spacing = (decimal)start.SpacingSeconds;
            PlanForSpikes = start.PlanForSpikes;
            ModeChoices =
            [
                new StarPowerModeChoice(StarPowerMode.Replace, Strings.StarPowerModeReplace),
                new StarPowerModeChoice(StarPowerMode.Add, Strings.StarPowerModeAdd),
            ];
            SelectedMode = ModeChoices.First(c => c.Mode == mode);
            ScopeText = overSelection ? Strings.GenerateScopeSelection : Strings.GenerateScopeWholePart;
            _plan = GeneratePlan();
            _key = new ChartTrackKey(
                part,
                _session.Focused is { } focused
                    && focused.Instrument == part
                    && _session.TrackOf(focused) is { Notes.Count: > 0 }
                    ? focused.Difficulty
                    : _plan.Source ?? ChartDifficulty.Expert);
            _ready = true;
            RefreshAll();
            SelectFirst();
        }

        private void SelectFirst()
        {
            if (SelectedRegion is null && Regions.Count > 0)
                SelectRegionForEdit(Regions[0]);
        }

        public ChartInstrument Part { get; }

        public string Title => string.Format(CultureInfo.CurrentCulture, Strings.GenerateStarPowerTitleFormat, ChartLabels.InstrumentName(Part));

        public string PartLabel => ChartLabels.InstrumentName(Part);

        /// <summary>Where star power is proposed: over the selection, or the whole part.</summary>
        public string ScopeText { get; }

        public PlaybackViewModel Playback => _playback;

        public Func<TimeSpan> Clock => _playback.Clock;

        public double LengthSeconds => _playback.LengthSeconds;

        /// <summary>Whether the window applied the plan and closed.</summary>
        public bool Applied { get; private set; }

        /// <summary>About how many seconds of the part's playing apart generated star power phrases are.</summary>
        [ObservableProperty]
        public partial decimal? Spacing { get; set; }

        /// <summary>Whether generated star power is charged for the part's hard passages.</summary>
        [ObservableProperty]
        public partial bool PlanForSpikes { get; set; }

        public IReadOnlyList<StarPowerModeChoice> ModeChoices { get; }

        /// <summary>What Generate star power does with the star power already there.</summary>
        [ObservableProperty]
        public partial StarPowerModeChoice SelectedMode { get; set; }

        public StarPowerMode Mode => SelectedMode.Mode;

        /// <summary>The options as picked, an emptied box taking the default.</summary>
        public StarPowerOptions Options => new(
            (double)Math.Clamp(Spacing ?? (decimal)StarPowerOptions.Default.SpacingSeconds, (decimal)StarPowerOptions.MinSpacingSeconds, (decimal)StarPowerOptions.MaxSpacingSeconds),
            PlanForSpikes);

        /// <summary>How far ahead the preview highway shows, in seconds.</summary>
        [ObservableProperty]
        public partial double LookAhead { get; set; } = 4;

        /// <summary>The regions proposed or drawn, earliest first.</summary>
        public ObservableCollection<StarPowerRegionViewModel> Regions { get; } = [];

        /// <summary>The star power already in the chart, wholly inside the span.</summary>
        public ObservableCollection<StarPowerExistingItem> ExistingRegions { get; } = [];

        /// <summary>Whether the chart has star power wholly inside the span, to show as context.</summary>
        public bool HasExisting => ExistingRegions.Count > 0;

        [ObservableProperty]
        public partial StarPowerRegionViewModel? SelectedRegion { get; set; }

        [ObservableProperty]
        public partial int? SelectedIndex { get; set; }

        /// <summary>What the preview highway draws.</summary>
        [ObservableProperty]
        public partial HighwayScene? Scene { get; private set; }

        /// <summary>What the whole-song timeline draws.</summary>
        [ObservableProperty]
        public partial OverviewData? Overview { get; private set; }

        /// <summary>The meter ribbon and hard passages the timeline adds.</summary>
        [ObservableProperty]
        public partial StarPowerTimelineExtras? TimelineExtras { get; private set; }

        [ObservableProperty]
        public partial string? SummaryText { get; private set; }

        [ObservableProperty]
        public partial string? WarningText { get; private set; }

        [ObservableProperty]
        public partial bool HasWarnings { get; private set; }

        [ObservableProperty]
        public partial string? StatusText { get; private set; }

        /// <summary>Why there's nothing proposed, with what to try; null when there are regions.</summary>
        [ObservableProperty]
        public partial string? EmptyText { get; private set; }

        [ObservableProperty]
        public partial bool HasProposals { get; private set; }

        [ObservableProperty]
        public partial bool IsConfirmingRegenerate { get; private set; }

        public bool CanApply => HasProposals && _plan.AnyKept;

        [RelayCommand]
        public void SelectRegion(StarPowerRegionViewModel region)
        {
            SelectRegionForEdit(region);
            _playback.SeekTo(region.StartSeconds);
        }

        /// <summary>Selects a region without moving the audio, as a press on its edge on the highway does.</summary>
        public void SelectRegionForEdit(StarPowerRegionViewModel region)
        {
            SelectedRegion = region;
            SelectedIndex = region.Index;
            foreach (var item in Regions)
                item.IsSelected = item == region;
            TimelineExtras = TimelineExtras is { } extras ? extras with { SelectedProposal = SelectedIndex } : null;
        }

        [RelayCommand]
        public void ToggleRegion(StarPowerRegionViewModel region)
        {
            if (region.Index < 0 || region.Index >= Regions.Count)
                return;
            _plan.Toggle(region.Index);
            region.IsKept = _plan.Regions[region.Index].Kept;
            _dirty = true;
            RefreshAll();
        }

        [RelayCommand]
        public void KeepAll()
        {
            _plan.KeepAll();
            _dirty = true;
            RefreshAll();
        }

        [RelayCommand]
        public void DropAll()
        {
            _plan.DropAll();
            _dirty = true;
            RefreshAll();
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
        /// Works the layout out again with the options as they are now, in place of the regions as they are: the window
        /// asks first when that would lose kept, dropped, drawn or resized regions.
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

        /// <summary>Works the layout out again, losing the changes.</summary>
        [RelayCommand]
        public void ConfirmRegenerate()
        {
            IsConfirmingRegenerate = false;
            ResetPlan();
        }

        /// <summary>Keeps the regions as they are, rather than regenerating.</summary>
        [RelayCommand]
        public void CancelRegenerate() => IsConfirmingRegenerate = false;

        private void ResetPlan()
        {
            _plan = GeneratePlan();
            _dirty = false;
            IsConfirmingRegenerate = false;
            SelectedRegion = null;
            StatusText = null;
            RefreshAll();
            SelectFirst();
        }

        /// <summary>Applies the kept regions to the chart as one step; whether it did.</summary>
        public bool Apply()
        {
            if (!CanApply || _plan.BuildCommand() is not { } command)
                return false;
            if (!_session.Execute(command))
            {
                StatusText = _session.Status;
                return false;
            }
            Applied = true;
            return true;
        }

        /// <summary>Closes without applying anything.</summary>
        public void Cancel() => Applied = false;

        partial void OnSelectedModeChanged(StarPowerModeChoice value)
        {
            if (!_ready || _plan.Mode == value.Mode)
                return;
            // The mode changes what the generator works out, so the layout is proposed again.
            _plan = StarPowerPlan.Generate(_session.Chart, Part, Options, value.Mode, _span, _session.ReadOptions);
            _dirty = false;
            SelectedRegion = null;
            StatusText = null;
            RefreshAll();
            SelectFirst();
        }

        private StarPowerPlan GeneratePlan() =>
            StarPowerPlan.Generate(_session.Chart, Part, Options, Mode, _span, _session.ReadOptions);

        private void RefreshAll()
        {
            _coverage = _plan.Coverage(SampleCount());
            RefreshRegions();
            RefreshScene();
            RefreshOverview();
            RefreshSummary();
            OnPropertyChanged(nameof(CanApply));
        }

        private int SampleCount() => Math.Clamp((int)Math.Round(Math.Max(0, _playback.LengthSeconds) * 4), 64, 2048);

        private void RefreshRegions()
        {
            var selectedPhrase = SelectedRegion?.Region.Phrase;
            var charged = _coverage.Spikes.Where(static s => s.Charged).Select(static s => s.Spike.StartTick).ToHashSet();
            var noteTrack = _session.GameTrack(_key);

            Regions.Clear();
            for (int i = 0; i < _plan.Regions.Count; i++)
            {
                var region = _plan.Regions[i];
                var phrase = region.Phrase;
                int notes = noteTrack.Notes.Count(n => phrase.Covers(n.Tick));
                double? spike = region.SpikeTick is { } tick ? _session.AudioAt(tick) : null;
                bool isCharged = region.SpikeTick is { } spikeTick && charged.Contains(spikeTick);
                Regions.Add(new StarPowerRegionViewModel(
                    this,
                    i,
                    region,
                    _session.AudioAt(phrase.Tick),
                    _session.AudioAt(phrase.CoverEnd),
                    spike,
                    notes,
                    isCharged));
            }

            ExistingRegions.Clear();
            foreach (var phrase in _plan.Existing)
            {
                ExistingRegions.Add(new StarPowerExistingItem(
                    SongTime.Format(TimeSpan.FromSeconds(_session.AudioAt(phrase.Tick))),
                    string.Format(CultureInfo.CurrentCulture, "{0:0.0} s", _session.AudioAt(phrase.CoverEnd) - _session.AudioAt(phrase.Tick)),
                    _plan.Mode == StarPowerMode.Replace ? Strings.StarPowerExistingReplaced : Strings.StarPowerExistingKept));
            }

            OnPropertyChanged(nameof(HasExisting));

            SelectedRegion = selectedPhrase is { } wanted
                ? Regions.FirstOrDefault(r => r.Region.Phrase == wanted)
                : null;
            foreach (var item in Regions)
                item.IsSelected = item == SelectedRegion;
            SelectedIndex = SelectedRegion?.Index;
        }

        private void RefreshScene()
        {
            var playing = new List<Phrase>(_plan.SurvivingStarPower);
            playing.AddRange(_plan.Regions.Where(static r => r.Kept).Select(static r => r.Phrase));
            Scene = StarPowerPreviewScene.Of(_session, _key, playing);
        }

        private void RefreshOverview()
        {
            var proposals = Regions
                .Select(r => new OverviewProposal(r.StartSeconds, r.EndSeconds, r.IsKept))
                .ToList();
            Overview = OverviewData.Of(
                _session,
                _key,
                _playback.LengthSeconds,
                proposals,
                _plan.Mode == StarPowerMode.Replace,
                _plan.Mode == StarPowerMode.Replace ? _plan.Existing : null);
            TimelineExtras = new StarPowerTimelineExtras(
                [.. _coverage.Samples.Select(s => new StarPowerMeterPoint(_session.AudioAt(s.Tick), s.Meter, s.Active))],
                [.. _coverage.Spikes.Select(s => new StarPowerSpikeMark(_session.AudioAt(s.Spike.StartTick), s.Charged))],
                SelectedIndex);
        }

        private void RefreshSummary()
        {
            HasProposals = Regions.Count > 0;
            int kept = Regions.Count(static r => r.IsKept);
            double seconds = Regions.Where(static r => r.IsKept).Sum(static r => r.EndSeconds - r.StartSeconds);
            SummaryText = string.Format(
                CultureInfo.CurrentCulture,
                Strings.StarPowerPlanSummaryFormat,
                kept,
                Regions.Count,
                SongTime.Format(TimeSpan.FromSeconds(seconds)));

            int uncharged = _coverage.UnchargedSpikes;
            string? warning = null;
            if (HasProposals && kept == 0)
                warning = Strings.StarPowerNoRegionsKept;
            else if (HasProposals && uncharged > 0)
                warning = string.Format(CultureInfo.CurrentCulture, Strings.StarPowerUnchargedFormat, uncharged);
            WarningText = warning;
            HasWarnings = warning is not null;

            EmptyText = _plan.Reason switch
            {
                NoStarPowerReason.NotFiveFret => Strings.StarPowerNotFiveFret,
                NoStarPowerReason.NoNotes => Strings.StarPowerNoNotes,
                NoStarPowerReason.HasStarPower => Strings.StarPowerHasStarPower,
                NoStarPowerReason.NoSpotFits => $"{Strings.StarPowerNoSpot}\n{Strings.StarPowerEmptyHint}",
                _ => Strings.StarPowerNoRegions,
            };
            if (HasProposals)
                EmptyText = null;
        }
    }
}
