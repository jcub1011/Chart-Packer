using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia.Threading;
using ChartBuilder.Resources;
using ChartBuilder.Services;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Difficulty;
using ChartPacker.Core.Charting.Editing;
using ChartPacker.Core.Charting.Lint;
using ChartPacker.Core.Charts;
using ChartPacker.UI.Controls.Highway;
using ChartPacker.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>A part and difficulty the preview can show, or the lane in focus.</summary>
    public sealed record PreviewTrackChoice(ChartTrackKey? Track, string Label);

    /// <summary>
    /// The highway preview (charter-spec C18 items 1 and 2): a game-style view of one part and difficulty, played in sync
    /// with the audio and the editor, with its notes "hit" as they pass. It shows the lane in focus, or a part and
    /// difficulty picked, docked in the Editor's side panel or in a window of its own, which can go full screen.
    /// </summary>
    /// <remarks>
    /// <para>Its scene is built from the model's game view (§4.3), as the lanes draw it, so it shows what the games read:
    /// natural HOPOs, sustain cutoffs and star power. Nothing is written to disk. After an edit that touches the track, the
    /// scene is built again, at most once a frame, and only while it's shown. <i>Settled in CM5:</i> the whole track, not
    /// only the range the edit touched, as the game view is worked out a track at a time.</para>
    /// <para>It reads the same clock as the lanes (<see cref="PlaybackViewModel.Clock"/>), which allows for the latency and
    /// the speed, in the same frame, so it stays within one frame of them (AC C18.1).</para>
    /// <para>Practice plays the loop region over and over at the speed picked (C18 item 2).</para>
    /// </remarks>
    public sealed partial class PreviewViewModel : ViewModelBase, IDisposable
    {
        private readonly ChartEditorViewModel _editor;
        private readonly BuilderSettingsService _settings;
        private bool _rebuildPending;
        private int _shownCount;

        // The track the scene was last built for, so a change of focus elsewhere doesn't build it again.
        private ChartTrackKey? _built;

        public PreviewViewModel(ChartEditorViewModel editor, BuilderSettingsService settings)
        {
            _editor = editor;
            _settings = settings;
            LookAhead = Math.Clamp(settings.Settings.PreviewLookAhead, MinLookAhead, MaxLookAhead);
            RefreshChoices();
            SelectedTrack = TrackChoices[0];
            Session.ChartChanged += OnChartChanged;
            Session.StateChanged += OnSessionState;
            Session.SongSettingsChanged += OnSongSettingsChanged;
            Playback.PropertyChanged += OnPlaybackChanged;
            _editor.Song.Changed += OnSongChanged;
        }

        // A rating used, reverted or saved changes whether the suggestion is still to be used.
        private void OnSongChanged(object? sender, EventArgs e)
        {
            if (IsShown)
                RefreshSuggestion();
        }

        /// <summary>The shortest look-ahead the preview's slider gives.</summary>
        public const double MinLookAhead = 0.5;

        /// <summary>The longest look-ahead the preview's slider gives.</summary>
        public const double MaxLookAhead = 4;

        private EditorSession Session => _editor.Session;

        public PlaybackViewModel Playback => _editor.Playback;

        /// <summary>The time to draw at: the editor's clock, allowing for the latency and the speed.</summary>
        public Func<TimeSpan> Clock => Playback.Clock;

        /// <summary>The lane in focus, then each part and difficulty the chart has.</summary>
        [ObservableProperty]
        public partial IReadOnlyList<PreviewTrackChoice> TrackChoices { get; private set; } = [];

        /// <summary>What the preview shows: the lane in focus, or a part and difficulty picked.</summary>
        [ObservableProperty]
        public partial PreviewTrackChoice? SelectedTrack { get; set; }

        /// <summary>The part and difficulty shown, or null when there's none to show.</summary>
        public ChartTrackKey? Track => SelectedTrack?.Track ?? Session.Focused;

        /// <summary>What the preview draws.</summary>
        [ObservableProperty]
        public partial HighwayScene? Scene { get; private set; }

        public bool HasScene => Scene is not null;

        /// <summary>
        /// The part's suggested rating (charter-spec C18 item 3), "Suggested rating: 4", from its highest charted
        /// difficulty; null when there's nothing to rate. Only a suggestion: it's written only when the user uses it.
        /// </summary>
        [ObservableProperty]
        public partial string? SuggestionText { get; private set; }

        /// <summary>What the suggestion was worked out from, and each difficulty's own tier, for its tooltip.</summary>
        [ObservableProperty]
        public partial string? SuggestionBasis { get; private set; }

        /// <summary>What using the suggestion does: "Use as diff_guitar"; null when song.ini already has it.</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(UseSuggestionCommand))]
        public partial string? UseSuggestionText { get; private set; }

        public bool HasSuggestion => SuggestionText is not null;

        // The keys the suggestion would set, and its tier.
        private (IReadOnlyList<string> Keys, int Tier)? _suggestion;

        /// <summary>Uses the suggested rating for the part's <c>diff_*</c> keys, as pending changes Revert takes back.</summary>
        [RelayCommand(CanExecute = nameof(CanUseSuggestion))]
        private void UseSuggestion()
        {
            if (_suggestion is not { } suggestion)
                return;
            foreach (string key in suggestion.Keys)
                _editor.Song.SetRating(key, suggestion.Tier);
            _editor.Messages.Info(string.Format(CultureInfo.CurrentCulture, Strings.SuggestedRatingUsedFormat, suggestion.Tier, string.Join(", ", suggestion.Keys)));
            RefreshSuggestion();
        }

        private bool CanUseSuggestion() => UseSuggestionText is not null;

        partial void OnSuggestionTextChanged(string? value) => OnPropertyChanged(nameof(HasSuggestion));

        // The part's suggested rating, and each difficulty's, from the game view as the lanes draw it.
        private void RefreshSuggestion()
        {
            _suggestion = null;
            if (Track is not { } key || Session.Chart.PartOf(key.Instrument) is not { } part)
            {
                (SuggestionText, SuggestionBasis, UseSuggestionText) = (null, null, null);
                return;
            }

            var tempo = Session.TempoMap;
            var estimates = Enum.GetValues<ChartDifficulty>()
                .Where(d => part[d].Notes.Count > 0)
                .Select(d => (Difficulty: d, Estimate: DifficultyEstimator.Estimate(Session.GameTrack(new ChartTrackKey(key.Instrument, d)), tempo, part.Kind)))
                .Where(e => e.Estimate is not null)
                .ToList();
            if (estimates.Count == 0)
            {
                (SuggestionText, SuggestionBasis, UseSuggestionText) = (null, null, null);
                return;
            }

            var (top, estimate) = estimates[^1];
            var basis = estimate!.Basis;
            var lines = new List<string>
            {
                string.Format(CultureInfo.CurrentCulture, Strings.SuggestedRatingFromFormat, ChartLabels.DifficultyName(top)),
                string.Format(CultureInfo.CurrentCulture, Strings.SuggestedRatingNpsFormat, basis.PeakNps, basis.AverageNps),
                string.Format(CultureInfo.CurrentCulture, Strings.SuggestedRatingChordsFormat, basis.ChordShare),
                basis.KickIndependence is { } kick
                    ? string.Format(CultureInfo.CurrentCulture, Strings.SuggestedRatingKickFormat, kick)
                    : string.Format(CultureInfo.CurrentCulture, Strings.SuggestedRatingHopoFormat, basis.HopoTapShare),
            };
            lines.AddRange(estimates.OrderByDescending(e => e.Difficulty).Select(e =>
                string.Format(CultureInfo.CurrentCulture, Strings.SuggestedRatingDifficultyFormat, ChartLabels.DifficultyName(e.Difficulty), e.Estimate!.Tier)));
            lines.Add(Strings.SuggestedRatingOnlySuggestion);

            SuggestionText = string.Format(CultureInfo.CurrentCulture, Strings.SuggestedRatingFormat, estimate.Tier);
            SuggestionBasis = string.Join(Environment.NewLine, lines);
            var pairs = _editor.Song.EffectivePairs();
            var keys = PendingSongIniFixes.RatingKeysToSet(Session.Chart, key.Instrument, pairs, estimate.Tier);
            _suggestion = keys.Count > 0 ? (keys, estimate.Tier) : null;
            UseSuggestionText = keys.Count > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.SuggestedRatingUseFormat, string.Join(", ", keys)) : null;
        }

        /// <summary>How many seconds ahead of the strike line the highway shows: the further, the slower notes fall.</summary>
        [ObservableProperty]
        public partial double LookAhead { get; set; }

        /// <summary>Whether practice plays the loop region over and over (charter-spec C18 item 2).</summary>
        [ObservableProperty]
        public partial bool IsPractice { get; set; }

        /// <summary>Whether a docked tab or a window shows the preview, so its scene is kept up to date.</summary>
        public bool IsShown => _shownCount > 0;

        /// <summary>The preview's tab or window was shown (true) or hidden; the scene is built only while one shows it.</summary>
        public void SetShown(bool shown)
        {
            _shownCount = Math.Max(0, _shownCount + (shown ? 1 : -1));
            if (IsShown)
                ScheduleRebuild();
        }

        /// <summary>Opens the preview in a window of its own, which can go full screen (F11).</summary>
        [RelayCommand]
        private void OpenWindow() => _editor.ShowPreviewWindow(this);

        partial void OnSelectedTrackChanged(PreviewTrackChoice? value) => ScheduleRebuild();

        partial void OnLookAheadChanged(double value)
        {
            double clamped = Math.Clamp(value, MinLookAhead, MaxLookAhead);
            if (clamped != value)
            {
                LookAhead = clamped;
                return;
            }

            _settings.Settings.PreviewLookAhead = clamped;
        }

        partial void OnIsPracticeChanged(bool value)
        {
            if (!value)
            {
                Playback.IsLooping = false;
                return;
            }

            if (!Playback.HasLoop)
                _editor.SetLoopFromSelection();
            if (Playback.Loop is not { } loop)
            {
                IsPractice = false;
                return;
            }

            Playback.IsLooping = true;
            Playback.SeekTo(loop.Start);
            if (!Playback.IsPlaying)
                _editor.PlayPause();
        }

        partial void OnSceneChanged(HighwayScene? value) => OnPropertyChanged(nameof(HasScene));

        public void Dispose()
        {
            Session.ChartChanged -= OnChartChanged;
            Session.StateChanged -= OnSessionState;
            Session.SongSettingsChanged -= OnSongSettingsChanged;
            Playback.PropertyChanged -= OnPlaybackChanged;
            _editor.Song.Changed -= OnSongChanged;
        }

        private void OnChartChanged(object? sender, EditorChartChangedEventArgs e)
        {
            if (e.Affected.Song.HasFlag(ChartPacker.Core.Charting.Commands.SongChanges.Parts))
                RefreshChoices();
            if (Track is { } track && e.Affected.Touches(track))
                ScheduleRebuild();
        }

        // The lane in focus changed, which the preview follows unless a track is picked.
        private void OnSessionState(object? sender, EventArgs e)
        {
            if (SelectedTrack?.Track is null && Track != _built)
                ScheduleRebuild();
        }

        private void OnSongSettingsChanged(object? sender, EventArgs e) => ScheduleRebuild();

        // Practice ends when the loop does.
        private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PlaybackViewModel.IsLooping) && !Playback.IsLooping && IsPractice)
                IsPractice = false;
        }

        private void RefreshChoices()
        {
            var keep = SelectedTrack?.Track;
            var choices = new List<PreviewTrackChoice> { new(null, Strings.PreviewFollowLane) };
            foreach (var part in Session.Chart.Parts)
            {
                foreach (var difficulty in Enum.GetValues<ChartDifficulty>().OrderDescending())
                {
                    choices.Add(new PreviewTrackChoice(
                        new ChartTrackKey(part.Instrument, difficulty),
                        string.Format(CultureInfo.CurrentCulture, Strings.LaneTitleFormat, ChartLabels.InstrumentName(part.Instrument), ChartLabels.DifficultyName(difficulty))));
                }
            }

            TrackChoices = choices;
            SelectedTrack = choices.FirstOrDefault(c => c.Track == keep) ?? choices[0];
        }

        // Built once a frame at most, so a drag's previews, or a burst of edits, build it once.
        private void ScheduleRebuild()
        {
            if (_rebuildPending || !IsShown)
                return;
            _rebuildPending = true;
            Dispatcher.UIThread.Post(Rebuild, DispatcherPriority.Render);
        }

        private void Rebuild()
        {
            _rebuildPending = false;
            if (!IsShown)
                return;
            _built = Track;
            Scene = SceneOf(Session, Track);
            RefreshSuggestion();
        }

        /// <summary>
        /// The scene of <paramref name="track"/> as the games read it (the game view), timed by the chart's tempo map and the
        /// song's delay, as the lanes are; null when the chart hasn't the part.
        /// </summary>
        public static HighwayScene? SceneOf(EditorSession session, ChartTrackKey? track)
        {
            ArgumentNullException.ThrowIfNull(session);
            if (track is not { } key || session.TrackOf(key) is null)
                return null;
            var kind = ChartInstruments.KindOf(key.Instrument);
            var tempo = session.TempoMap;
            string label = string.Format(CultureInfo.CurrentCulture, Strings.LaneTitleFormat, ChartLabels.InstrumentName(key.Instrument), ChartLabels.DifficultyName(key.Difficulty));
            var lane = new HighwayLane(kind, HighwayLane.LaneCountOf(kind, session.IsFiveLaneDrums), label, session.GameTrack(key).Notes, tempo, session.Delay);
            return new HighwayScene([lane], tempo, session.Delay);
        }
    }
}
