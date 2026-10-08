using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ChartBuilder.Resources;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Analysis;
using ChartPacker.Core.Charting.Editing;
using ChartPacker.Core.Charts;
using ChartPacker.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>Finds the tempo in the song's audio, for Chart settings' Detect from audio (charter-spec C1).</summary>
    public interface ITempoDetector
    {
        /// <summary>Whether there's audio to find it in.</summary>
        bool CanDetect { get; }

        /// <summary>
        /// The tempo the song starts at, tidied, and the tempo map proposed for review; null when no steady beat was found.
        /// </summary>
        Task<double?> DetectAsync();
    }

    /// <summary>
    /// The editor's audio assist (charter-spec C14), from the toolbar and the keys: snap to onset (<c>O</c>), Nudge to
    /// onsets (<c>Shift</c>+<c>O</c>), a proposed tempo map to review, and Find first downbeat.
    /// </summary>
    public sealed partial class AudioAssistViewModel : ViewModelBase, ITempoDetector
    {
        private const string SnapMessageKey = "snap-to-onset";
        private const string ProposalMessageKey = "tempo-proposal";

        private readonly ChartEditorViewModel _editor;

        // The beats Find first downbeat chose among, for stepping its downbeat a beat at a time.
        private double[] _downbeatBeats = [];
        private int _downbeatIndex;

        public AudioAssistViewModel(ChartEditorViewModel editor)
        {
            _editor = editor;
        }

        /// <summary>Raised when a report has more than the message bar shows, and the Messages dialog should open on it.</summary>
        public event EventHandler? ShowMessagesRequested;

        private EditorSession Session => _editor.Session;

        private TempoProposals Proposal => Session.TempoProposal;

        public bool HasAudio => _editor.Audio.HasAudio;

        public bool CanDetect => HasAudio;

        // Snap and nudge.

        /// <summary>Whether notes placed and moved go to the nearest onset within half a grid step (charter-spec C14 item 2).</summary>
        public bool IsSnapToOnset
        {
            get => Session.SnapToOnset;
            set
            {
                if (value == Session.SnapToOnset)
                    return;
                Session.SnapToOnset = value;
                OnPropertyChanged();
                _editor.Messages.Info(value ? Strings.SnapToOnsetOnStatus : Strings.SnapToOnsetOffStatus, SnapMessageKey);
            }
        }

        /// <summary>Turns snap to onset on or off (<c>O</c>).</summary>
        public void ToggleSnapToOnset() => IsSnapToOnset = !IsSnapToOnset;

        /// <summary>
        /// Moves the selected notes to their nearest onsets, as one undoable step (charter-spec C14 item 3), and says how
        /// far each chord moved (<c>Shift</c>+<c>O</c>).
        /// </summary>
        [RelayCommand]
        public void Nudge()
        {
            if (Session.Selection.Notes.Count == 0)
            {
                _editor.Messages.Info(Strings.NudgeNothingSelected);
                return;
            }

            if (Session.Tools.NudgeSelectionToOnsets() is not { } plan)
            {
                _editor.Messages.Info(Strings.NudgeNoOnsets);
                return;
            }

            if (plan.Command is null)
            {
                _editor.Messages.Info(string.Format(CultureInfo.CurrentCulture, Strings.NudgeNothingMovedFormat, plan.Stayed));
                return;
            }

            ReportNudge(plan);
        }

        // The proposed tempo map.

        /// <summary>Whether the proposal listens to the whole mix, rather than the audio of the lane in focus.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ListenToLane))]
        public partial bool ListenToMix { get; set; } = true;

        /// <summary>Whether the proposal listens to the audio of the lane in focus.</summary>
        public bool ListenToLane
        {
            get => !ListenToMix;
            set => ListenToMix = !value;
        }

        /// <summary>An anchor every this many measures when the proposal is accepted; 0 for none.</summary>
        [ObservableProperty]
        public partial decimal? AnchorEvery { get; set; } = ProposalOptions.Default.AnchorEveryMeasures;

        /// <summary>How far, in milliseconds, a beat can be from its segment's tempo before it needs a marker of its own.</summary>
        [ObservableProperty]
        public partial decimal? MergeMs { get; set; } = (decimal)ProposalOptions.Default.MergeToleranceMs;

        /// <summary>Whether beats are being proposed or a downbeat found.</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ProposeTempoMapCommand), nameof(FindFirstDownbeatCommand))]
        public partial bool IsBusy { get; private set; }

        /// <summary>What the proposal covers: the selection, or the whole song.</summary>
        public string ProposalScopeText => Session.Selection.Range is { } range && range.To > range.From
            ? string.Format(CultureInfo.CurrentCulture, Strings.ProposalScopeSelectionFormat, PropertiesViewModel.MeasureBeat(Session.TempoMap, range.From), PropertiesViewModel.MeasureBeat(Session.TempoMap, range.To))
            : Strings.ProposalScopeWholeSong;

        public bool IsProposalActive => Proposal.IsActive;

        /// <summary>What the run bar says about the proposal.</summary>
        public string ProposalText
        {
            get
            {
                if (!Proposal.IsActive)
                    return "";
                string bpm = Proposal.Bpm is { } value ? value.ToString("0.0", CultureInfo.CurrentCulture) : "?";
                string text = string.Format(CultureInfo.CurrentCulture, Strings.ProposalRunFormat, Proposal.Beats.Count, bpm, Proposal.Unsure.Count, Proposal.Downbeat + 1);
                return Proposal.MayBeHalfTempo ? text + " " + Strings.ProposalMayBeHalf : text;
            }
        }

        /// <summary>
        /// Proposes a tempo map from the audio (charter-spec C14 item 4), over the selection or the whole song, and shows it
        /// over the real one for review.
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanAnalyse))]
        private async Task ProposeTempoMapAsync()
        {
            (long From, long To)? span = Session.Selection.Range is { } range && range.To > range.From ? (range.From, range.To) : null;
            (double, double)? audio = span is { } ticks ? (Session.AudioAt(ticks.From), Session.AudioAt(ticks.To)) : null;
            await ProposeAsync(audio, span);
        }

        /// <summary>Writes the proposed tempo map as one undoable step (charter-spec C4 item 7, C14 item 4).</summary>
        [RelayCommand]
        private void AcceptProposal()
        {
            if (!Proposal.Accept(Options()))
                return;
            _editor.Messages.Info(string.Format(CultureInfo.CurrentCulture, Strings.ProposalAcceptedFormat, Session.Chart.TempoMap.Tempos.Count, Session.Chart.TempoMap.Anchors.Count), ProposalMessageKey);
        }

        [RelayCommand]
        private void DiscardProposal() => Proposal.Discard();

        [RelayCommand]
        private void DoubleProposal() => Proposal.Double();

        [RelayCommand]
        private void HalveProposal() => Proposal.Halve();

        [RelayCommand]
        private void ProposalDownbeatEarlier() => Proposal.ShiftDownbeat(-1);

        [RelayCommand]
        private void ProposalDownbeatLater() => Proposal.ShiftDownbeat(1);

        [RelayCommand]
        private void ResetProposal() => Proposal.Reset();

        // The first downbeat.

        /// <summary>Where Find first downbeat thinks measure 1 starts, in audio seconds; null until it's found one.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasDownbeat), nameof(DownbeatText))]
        public partial double? Downbeat { get; private set; }

        public bool HasDownbeat => Downbeat is not null;

        public string DownbeatText => Downbeat is { } seconds
            ? string.Format(CultureInfo.CurrentCulture, Strings.DownbeatFoundFormat, SongTime.Format(TimeSpan.FromSeconds(Math.Max(0, seconds)), decimals: 3))
            : "";

        /// <summary>Finds where measure 1 most likely starts (charter-spec C14 item 5), to apply as a lead-in or a delay.</summary>
        [RelayCommand(CanExecute = nameof(CanAnalyse))]
        private async Task FindFirstDownbeatAsync()
        {
            if (_editor.Audio.Mix is not { } mix)
                return;
            IsBusy = true;
            try
            {
                if (await _editor.Audio.Analysis.ProposeAsync(mix, null, BeatsPerMeasure()) is not { Downbeat: { } found } result)
                {
                    _editor.Messages.Info(Strings.DownbeatNotFound, ProposalMessageKey);
                    return;
                }

                _downbeatBeats = [.. result.Proposal.Beats];
                _downbeatIndex = found.BeatIndex;
                Downbeat = found.Seconds;
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private void DownbeatEarlier() => StepDownbeat(-1);

        [RelayCommand]
        private void DownbeatLater() => StepDownbeat(1);

        [RelayCommand]
        private void DismissDownbeat() => Downbeat = null;

        /// <summary>
        /// Lead-in (charter-spec C14 item 5): the song's first tempo is set so measure 2 starts on the downbeat, as one
        /// undoable step; the delay is left alone.
        /// </summary>
        [RelayCommand]
        private async Task ApplyLeadInAsync()
        {
            if (Downbeat is not { } seconds)
                return;
            bool applied = await _editor.ApplyTempoEditAsync(blocked => TempoMapEdits.LeadIn(Session.Chart, seconds, Session.Delay, Session.KeepTimes, blocked));
            if (applied)
            {
                Proposal.SetDownbeatNear(seconds);
                Downbeat = null;
            }
        }

        /// <summary>
        /// Delay (charter-spec C14 item 5): the song's delay is set so tick 0 lands on the downbeat, as a pending change,
        /// which Revert takes back.
        /// </summary>
        [RelayCommand]
        private void ApplyDelay()
        {
            if (Downbeat is not { } seconds)
                return;
            var song = _editor.Song;
            song.Restore("delay");
            song.DelayMs = (long)Math.Round(Math.Max(0, seconds) * 1000, MidpointRounding.AwayFromZero);
            Proposal.SetDownbeatNear(seconds);
            Downbeat = null;
            _editor.Messages.Info(string.Format(CultureInfo.CurrentCulture, Strings.DownbeatDelaySetFormat, song.DelayMs), ProposalMessageKey);
        }

        // Detect from audio.

        /// <inheritdoc/>
        public async Task<double?> DetectAsync()
        {
            if (!CanDetect)
                return null;
            var proposal = await ProposeAsync(null, null);
            return proposal?.StartingBpm is { } bpm ? BpmTidy.Tidy(bpm) : null;
        }

        /// <summary>Takes in a change to the session's state: snap to onset, the selection, the proposal.</summary>
        internal void Refresh()
        {
            OnPropertyChanged(nameof(IsSnapToOnset));
            OnPropertyChanged(nameof(HasAudio));
            OnPropertyChanged(nameof(IsProposalActive));
            OnPropertyChanged(nameof(ProposalText));
            OnPropertyChanged(nameof(ProposalScopeText));
        }

        private bool CanAnalyse() => HasAudio && !IsBusy;

        /// <summary>Proposes beats over <paramref name="audio"/> (or the whole song), and starts reviewing them.</summary>
        private async Task<BeatProposal?> ProposeAsync((double From, double To)? audio, (long From, long To)? span)
        {
            var source = ListenToMix || Session.Focused is not { } focused
                ? _editor.Audio.Mix
                : _editor.Lanes.All.FirstOrDefault(l => l.Track == focused)?.Audio.Source ?? _editor.Audio.Mix;
            if (source is null)
                return null;

            IsBusy = true;
            try
            {
                _editor.Messages.Info(Strings.ProposalFinding, ProposalMessageKey);
                if (await _editor.Audio.Analysis.ProposeAsync(source, audio, BeatsPerMeasure()) is not { } result || result.Proposal.IsEmpty)
                {
                    _editor.Messages.Info(Strings.ProposalNoBeats, ProposalMessageKey);
                    return null;
                }

                Proposal.Start(result.Proposal, result.Downbeat?.BeatIndex ?? 0, span);
                _editor.Messages.Info(ProposalText, ProposalMessageKey);
                return result.Proposal;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private ProposalOptions Options() => new((int)Math.Clamp(AnchorEvery ?? 0, 0, 64), (double)Math.Clamp(MergeMs ?? 0, 0, 20));

        /// <summary>How many beats a measure has at the start of the song.</summary>
        private int BeatsPerMeasure() => Session.TempoMap.TimeSignatures[0].Numerator;

        private void StepDownbeat(int beats)
        {
            if (Downbeat is null || _downbeatBeats.Length == 0)
                return;
            _downbeatIndex = Math.Clamp(_downbeatIndex + beats, 0, _downbeatBeats.Length - 1);
            Downbeat = _downbeatBeats[_downbeatIndex];
        }

        /// <summary>
        /// Says what a nudge did: how many chords moved and the largest move, then each chord with how far it moved. When
        /// chords stayed, the Messages dialog opens on it, as nothing else lists them.
        /// </summary>
        private void ReportNudge(NudgePlan plan)
        {
            var map = Session.TempoMap;
            double largest = plan.Moved.Max(m => Math.Abs(m.Milliseconds));
            var text = new StringBuilder(string.Format(CultureInfo.CurrentCulture, Strings.NudgeReportFormat, plan.Moved.Count, largest, plan.Stayed));
            foreach (var move in plan.Moved)
            {
                text.AppendLine();
                text.Append(string.Format(CultureInfo.CurrentCulture, Strings.NudgeLineFormat, PropertiesViewModel.MeasureBeat(map, move.From), move.Milliseconds));
            }

            foreach (long tick in plan.NoOnset)
            {
                text.AppendLine();
                text.Append(string.Format(CultureInfo.CurrentCulture, Strings.NudgeNoOnsetLineFormat, PropertiesViewModel.MeasureBeat(map, tick)));
            }

            foreach (long tick in plan.Blocked)
            {
                text.AppendLine();
                text.Append(string.Format(CultureInfo.CurrentCulture, Strings.NudgeBlockedLineFormat, PropertiesViewModel.MeasureBeat(map, tick)));
            }

            if (plan.Stayed == 0)
            {
                _editor.Messages.Info(text.ToString());
                return;
            }

            _editor.Messages.Warning(text.ToString());
            ShowMessagesRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
