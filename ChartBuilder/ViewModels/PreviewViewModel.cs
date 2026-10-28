using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using ChartBuilder.Resources;
using ChartBuilder.Services;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Difficulty;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charting.Lint;
using ChartShared.Core.Charts;
using ChartShared.UI.Controls.Highway;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

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
            LookAhead = HighwayLookAhead.Clamp(settings.Settings.PreviewLookAhead, BuilderSettings.DefaultPreviewLookAhead);
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
                ShowSuggestion();
        }

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

        // The keys the suggestion would set, each with its tier.
        private IReadOnlyList<(string Key, int Tier)>? _suggestion;

        // The track last rated, each of its part's diff_* keys' rating, the part's own key first, and each charted
        // difficulty's estimate by the own key's model, lowest first; and the runs started, so only the latest is shown.
        private (ChartTrackKey Track, IReadOnlyList<KeyedEstimate> Keys, List<(ChartDifficulty Difficulty, DifficultyEstimate Estimate)> Own)? _estimated;
        private int _suggestions;

        private static ILogger Logger => Log.ForContext<PreviewViewModel>();

        /// <summary>Uses the suggested rating for the part's <c>diff_*</c> keys, as pending changes Revert takes back.</summary>
        [RelayCommand(CanExecute = nameof(CanUseSuggestion))]
        private void UseSuggestion()
        {
            if (_suggestion is not { Count: > 0 } suggestion)
                return;
            foreach (var (key, tier) in suggestion)
                _editor.Song.SetRating(key, tier);
            _editor.Messages.Info(suggestion.Select(static s => s.Tier).Distinct().Count() == 1
                ? string.Format(CultureInfo.CurrentCulture, Strings.SuggestedRatingUsedFormat, suggestion[0].Tier, string.Join(", ", suggestion.Select(static s => s.Key)))
                : string.Format(CultureInfo.CurrentCulture, Strings.SuggestedRatingsUsedFormat, string.Join(", ", suggestion.Select(static s => string.Format(CultureInfo.CurrentCulture, Strings.SuggestedRatingPairFormat, s.Key, s.Tier)))));
            ShowSuggestion();
        }

        private bool CanUseSuggestion() => UseSuggestionText is not null;

        partial void OnSuggestionTextChanged(string? value) => OnPropertyChanged(nameof(HasSuggestion));

        // The part's suggested rating, and each difficulty's, from the game view as the lanes draw it: the tracks read on
        // the UI thread, and rated off it, as the difficulty model takes a few milliseconds a track.
        private void RefreshSuggestion()
        {
            int run = ++_suggestions;
            if (Track is not { } key || Session.Chart.PartOf(key.Instrument) is not { } part)
            {
                _estimated = null;
                ShowSuggestion();
                return;
            }

            var tempo = Session.TempoMap;
            long? end = DifficultyNotes.EndTickOf(Session.Chart);
            var models = DifficultyEstimator.KeyModelsOf(Session.Chart, key.Instrument, Session.ReadOptions);
            var tracks = DifficultyEstimator.ChartedTracks(part, d => Session.GameTrack(new ChartTrackKey(key.Instrument, d)));
            _ = EstimateAsync(run, key, models, tracks, tempo, end);
        }

        // The own key's model rates every difficulty, which the tooltip lists; the other keys' only their highest.
        private async Task EstimateAsync(
            int run, ChartTrackKey key, IReadOnlyList<(string Key, DifficultyInstrument Instrument)> models,
            List<(ChartDifficulty Difficulty, NoteTrack Track, IReadOnlyList<Phrase> FreeLanes)> tracks, TempoMap tempo, long? end)
        {
            try
            {
                var (keyed, own) = await Task.Run(() =>
                {
                    var keyed = new List<KeyedEstimate>();
                    var own = new List<(ChartDifficulty Difficulty, DifficultyEstimate Estimate)>();
                    if (models.Count == 0)
                        return (keyed, own);
                    own.AddRange(tracks
                        .Select(t => (t.Difficulty, Estimate: DifficultyEstimator.Estimate(t.Track, tempo, models[0].Instrument, end, t.FreeLanes)))
                        .Where(e => e.Estimate is not null)
                        .Select(e => (e.Difficulty, e.Estimate!)));
                    if (own.Count == 0)
                        return (keyed, own);
                    keyed.Add(new KeyedEstimate(models[0].Key, models[0].Instrument, own[^1].Estimate));
                    foreach (var (other, instrument) in models.Skip(1))
                    {
                        if (DifficultyEstimator.EstimateHighest(tracks, tempo, instrument, end) is { } highest)
                            keyed.Add(new KeyedEstimate(other, instrument, highest.Estimate));
                    }

                    return (keyed, own);
                });
                if (run != _suggestions)
                    return;
                _estimated = (key, keyed, own);
                ShowSuggestion();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "The suggested rating of {Track} couldn't be worked out", key);
            }
        }

        // The suggestion last worked out, and the keys using it would set, which a rating used or reverted changes.
        private void ShowSuggestion()
        {
            _suggestion = null;
            if (_estimated is not { Keys.Count: > 0 } estimated || Track != estimated.Track || Session.Chart.PartOf(estimated.Track.Instrument) is null)
            {
                (SuggestionText, SuggestionBasis, UseSuggestionText) = (null, null, null);
                return;
            }

            var key = estimated.Track;
            var (top, estimate) = estimated.Own[^1];
            var basis = estimate.Basis;
            var rating = estimate.Rating;
            var a = rating.Attributes;
            var lines = new List<string>
            {
                string.Format(CultureInfo.CurrentCulture, Strings.SuggestedRatingFromFormat, ChartLabels.DifficultyName(top)),
                string.Format(CultureInfo.CurrentCulture, rating.IsOverchart ? Strings.SuggestedRatingOverchartFormat : Strings.SuggestedRatingRankFormat, rating.Rank),
                string.Format(CultureInfo.CurrentCulture, rating.Instrument.IsDrums() ? Strings.SuggestedRatingDrumAttributesFormat : Strings.SuggestedRatingAttributesFormat, a.Strum, a.Note, a.Rhythm, a.Pattern, a.Endurance),
            };

            // Pro drums' own rating, with the cymbals, for diff_drums_real.
            var others = estimated.Keys.Skip(1).ToList();
            foreach (var other in others)
                lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.SuggestedRatingProDrumsFormat, other.Estimate.Tier, other.Estimate.Rating.Rank));

            lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.SuggestedRatingNpsFormat, basis.PeakNps, basis.AverageNps));
            if (basis.KickIndependence is { } kick)
                lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.SuggestedRatingKickFormat, kick));
            lines.AddRange(estimated.Own.OrderByDescending(e => e.Difficulty).Select(e =>
                string.Format(CultureInfo.CurrentCulture, Strings.SuggestedRatingDifficultyFormat, ChartLabels.DifficultyName(e.Difficulty), e.Estimate.Tier)));
            lines.Add(Strings.SuggestedRatingModelSuggestion);

            int? proTier = others.Count > 0 ? others[0].Estimate.Tier : null;
            SuggestionText = proTier is { } pro && pro != estimate.Tier
                ? string.Format(CultureInfo.CurrentCulture, Strings.SuggestedRatingWithProFormat, estimate.Tier, pro)
                : string.Format(CultureInfo.CurrentCulture, Strings.SuggestedRatingFormat, estimate.Tier);
            SuggestionBasis = string.Join(Environment.NewLine, lines);
            var pairs = _editor.Song.EffectivePairs();
            var ratings = PendingSongIniFixes.RatingsToSet(key.Instrument, pairs, estimated.Keys);
            _suggestion = ratings.Count > 0 ? ratings : null;
            UseSuggestionText = ratings.Count > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.SuggestedRatingUseFormat, string.Join(", ", ratings.Select(static r => r.Key))) : null;
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

        // Already snapped by whatever sets it: the slider and the constructor.
        partial void OnLookAheadChanged(double value) => _settings.Settings.PreviewLookAhead = value;

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
            if (e.Affected.Song.HasFlag(ChartShared.Core.Charting.Commands.SongChanges.Parts))
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
