using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Input;
using ChartBuilder.Resources;
using ChartBuilder.Services;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charting.Playtest;
using ChartShared.Core.Charts;
using ChartShared.Core.Input;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>A problem the playtest found, as the Problems list shows it.</summary>
    public sealed record PlaytestProblemRow(PlaytestProblem Problem, string Where, string What);

    /// <summary>
    /// Playtest (charter-spec C19 item 4): with it on, while the song plays the focused lane's part and difficulty is played
    /// with the keyboard and the instrument devices, judged against its game view. While it's paused the editor works as it
    /// always does, so a missed note can be fixed and the passage played again. It and record mode are never on together
    /// (<see cref="ChartEditorViewModel.TogglePlaytest"/>).
    /// </summary>
    /// <remarks>
    /// Each lane played keeps what was found in it, marked over its notes, until it's cleared (<see cref="ClearCommand"/>),
    /// so a lane played, then another, then the first again, picks up where it was.
    /// </remarks>
    public sealed partial class PlaytestViewModel : ViewModelBase, IActionSink, IDisposable
    {
        private readonly ChartEditorViewModel _editor;
        private readonly BuilderSettingsService _settings;
        private readonly InputSettings? _bindings;
        private readonly Func<InputPlayer?> _devices;
        private readonly Dictionary<ChartTrackKey, PlaytestRun> _runs = [];
        private readonly Dictionary<int, double> _padHits = [];
        private double _lastSong;

        // What's pressed, for the strike line to show as the games show their frets and pads: the frets held, a bit each
        // from green; when each drum lane was last hit; and when the strings were last strummed, in audio seconds.
        private int _heldFrets;
        private double _strummed = double.NegativeInfinity;

        // The problem list the rows were made from, so they're made again only when it changes.
        private IReadOnlyList<PlaytestProblem>? _problemsFrom;

        /// <param name="bindings">The instrument bindings, for which drum colours the kit splits into tom and cymbal; null for none.</param>
        /// <param name="devices">The instrument devices, started the first time they're asked for; null where there are none.</param>
        internal PlaytestViewModel(ChartEditorViewModel editor, BuilderSettingsService settings, InputSettings? bindings, Func<InputPlayer?> devices)
        {
            _editor = editor;
            _settings = settings;
            _bindings = bindings;
            _devices = devices;
            Session.ChartChanged += OnChartChanged;
            Session.SongSettingsChanged += OnSongSettingsChanged;
        }

        /// <summary>Raised when what the lanes draw of the playtest changes: something judged, a lane played, or it all cleared.</summary>
        public event EventHandler? Changed;

        /// <summary>Whether playtest is on: playing the song playtests the focused lane.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ToolTip))]
        public partial bool IsArmed { get; internal set; }

        public string ToolTip => IsArmed ? Strings.PlaytestStopToolTip : Strings.PlaytestStartToolTip;

        /// <summary>Whether a lane is being playtested now: playtest is on and the song plays.</summary>
        [ObservableProperty]
        public partial bool IsActive { get; private set; }

        /// <summary>The part and difficulty playtested now, or last.</summary>
        public ChartTrackKey? Track { get; private set; }

        /// <summary>What's been found in <see cref="Track"/>; null before it's played, or once cleared.</summary>
        public PlaytestRun? Run => Track is { } key ? RunOf(key) : null;

        /// <summary>How long a strum or a pad hit stays lit at the strike line, in seconds of the song.</summary>
        public const double PressFlashSeconds = 0.12;

        /// <summary>The hit window judged with, from the settings.</summary>
        public HitWindow Window => new(_settings.Settings.PlaytestEarlyMs, _settings.Settings.PlaytestLateMs);

        /// <summary>Whether any lane has anything found in it to clear.</summary>
        [ObservableProperty]
        public partial bool HasFeedback { get; private set; }

        /// <summary>The pass's accuracy, combo and mean offset, for the transport bar.</summary>
        [ObservableProperty]
        public partial string StatsText { get; private set; } = "";

        /// <summary>Every problem found in <see cref="Track"/>, in the song's order, for the Problems list.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasProblems))]
        public partial IReadOnlyList<PlaytestProblemRow> Problems { get; private set; } = [];

        public bool HasProblems => Problems.Count > 0;

        private EditorSession Session => _editor.Session;

        private PlaybackViewModel Playback => _editor.Playback;

        /// <summary>What's been found in <paramref name="key"/>'s lane; null for a lane not played since it was last cleared.</summary>
        public PlaytestRun? RunOf(ChartTrackKey key) => _runs.GetValueOrDefault(key);

        /// <summary>
        /// The song started playing with playtest on: the focused lane is playtested, from where the song is. Nothing is, for
        /// a lane that can't be played (vocals, 6-fret), or with no devices.
        /// </summary>
        /// <returns>Whether a lane is playtested.</returns>
        internal bool Begin()
        {
            if (IsActive)
                return true;
            if (Session.Focused is not { } key || Session.TrackOf(key) is null
                || InstrumentActions.ProfileOf(ChartInstruments.KindOf(key.Instrument)) is not { } profile
                || _devices() is not { } player)
                return false;

            Track = key;
            var run = RunFor(key);
            player.Profile = profile;
            player.PlaysKeys = true;
            ResetPresses(player);
            IsActive = true;
            StartPass(player, run);
            Refresh();
            return true;
        }

        /// <summary>Turns playtest off: the lane isn't playtested any more, and what it found stays until it's cleared.</summary>
        internal void Disarm()
        {
            End();
            IsArmed = false;
        }

        /// <summary>The song stopped, or record mode took over: the lane isn't playtested, and what it found stays.</summary>
        internal void End()
        {
            if (!IsActive)
                return;
            if (_devices() is { } player)
            {
                player.ReleaseKeys();
                player.PlaysKeys = false;
            }

            IsActive = false;
            ResetPresses(null);
            Refresh();
        }

        /// <summary>
        /// How pressed the control for <paramref name="lane"/> of the lane played is at <paramref name="now"/>, from 0 to 1,
        /// as the strike line shows it (charter-spec C19 item 4): a fret, 1 while it's held; a drum pad or the kick, lit when
        /// it's hit and fading; on 5-fret, <see cref="ChartNote.OpenLane"/> is the strum, lit the same way.
        /// </summary>
        /// <param name="lane">The game view's lane: frets 0 to 4; drum pads from red at 0, the kick at −1.</param>
        public double PressOf(int lane, double now)
        {
            if (!IsActive || Run is not { } run)
                return 0;
            if (run.Chart.Kind != ChartInstrumentKind.Drums)
                return lane == ChartNote.OpenLane ? Flash(now - _strummed) : lane >= 0 && (_heldFrets & (1 << lane)) != 0 ? 1 : 0;
            return _padHits.TryGetValue(lane, out double hit) ? Flash(now - hit) : 0;
        }

        /// <summary>
        /// Plays <paramref name="key"/> while a lane is playtested: a key bound to the lane's instrument plays, and is taken
        /// from the editor. Space, Esc, P and N, and keys with Ctrl or Alt, are always the editor's.
        /// </summary>
        /// <returns>Whether the key is taken.</returns>
        public bool HandleKey(PhysicalKey key, KeyModifiers modifiers, bool down)
        {
            if (!IsActive || _devices() is not { } player || key is PhysicalKey.Space or PhysicalKey.Escape or PhysicalKey.P or PhysicalKey.N)
                return false;
            if (down && (modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0)
                return false;
            return player.Key(key, down);
        }

        /// <summary>Lets go of the keys held, as when the window loses focus.</summary>
        public void ReleaseKeys()
        {
            if (IsActive)
                _devices()?.ReleaseKeys();
        }

        /// <summary>
        /// Once a frame: starts a pass when the loop jumps back or the song is sought, and judges what time settles. The
        /// devices' clock is sampled by the editor first.
        /// </summary>
        internal void Frame()
        {
            if (!IsActive || _devices() is not { } player || Run is not { } run)
                return;
            var clock = player.Clock;
            if (!clock.IsPlaying || clock.Song is not { } song)
                return;
            if (clock.Jumped)
                run.Start(song, clock.Speed, player.IsDown, newPass: song < _lastSong);
            _lastSong = song;
            run.Advance(song, clock.Speed);
        }

        /// <inheritdoc/>
        public void Handle(in TimedAction action)
        {
            if (!IsActive || Run is not { } run)
                return;
            Press(run.Chart, action);
            run.Handle(action);
        }

        /// <summary>Moves the cursor to the next problem after it (N), or the one before (Shift+N), and selects its notes.</summary>
        public void StepProblem(int direction)
        {
            if (Run is not { } run || Track is not { } key)
                return;
            long cursor = Session.CursorTick;
            var problem = direction > 0
                ? run.Problems.FirstOrDefault(p => p.Tick > cursor)
                : run.Problems.LastOrDefault(p => p.Tick < cursor);
            if (problem is null)
            {
                Session.Status = run.Problems.Count == 0 ? Strings.PlaytestNoProblems : Strings.PlaytestNoMoreProblems;
                return;
            }

            GoTo(key, problem);
        }

        /// <summary>Takes up the playtest settings, as after the Settings window.</summary>
        public void ReloadSettings() => Rebuild();

        public void Dispose()
        {
            Session.ChartChanged -= OnChartChanged;
            Session.SongSettingsChanged -= OnSongSettingsChanged;
            foreach (var run in _runs.Values)
                run.Changed -= OnRunChanged;
            _runs.Clear();
        }

        /// <summary>
        /// Forgets everything found in every lane: the marks, the problems and the stats. A lane being played goes on from
        /// a new pass.
        /// </summary>
        [RelayCommand]
        private void Clear()
        {
            foreach (var run in _runs.Values)
                run.Changed -= OnRunChanged;
            _runs.Clear();
            if (IsActive && Track is { } key && _devices() is { } player)
                StartPass(player, RunFor(key));
            Refresh();
        }

        /// <summary>Goes to <paramref name="row"/>'s problem, from the Problems list.</summary>
        [RelayCommand]
        private void GoToProblem(PlaytestProblemRow row)
        {
            if (Track is { } key)
                GoTo(key, row.Problem);
        }

        // The lane's run, made on its first play.
        private PlaytestRun RunFor(ChartTrackKey key)
        {
            if (!_runs.TryGetValue(key, out var run))
            {
                run = new PlaytestRun(EngineFor(key));
                run.Changed += OnRunChanged;
                _runs[key] = run;
            }

            return run;
        }

        // Takes in what an action presses or lets go, for the strike line.
        private void Press(PlaytestChart chart, in TimedAction action)
        {
            if (chart.Kind == ChartInstrumentKind.Drums)
            {
                if (action.IsDown && DrumPads.Of(action.Action, chart.Drums) is { } pad)
                    _padHits[pad.Lane] = action.Song;
                return;
            }

            if (InstrumentActions.FretOf(action.Action) is { } fret)
                _heldFrets = action.IsDown ? _heldFrets | (1 << fret) : _heldFrets & ~(1 << fret);
            else if (InstrumentActions.IsStrum(action.Action) && action.IsDown)
                _strummed = action.Song;
        }

        // Nothing pressed but the frets held now: as a playtest starts or ends.
        private void ResetPresses(InputPlayer? player)
        {
            _heldFrets = 0;
            foreach (var action in InstrumentActions.FiveFret)
            {
                if (player is not null && InstrumentActions.FretOf(action) is { } fret && player.IsDown(action))
                    _heldFrets |= 1 << fret;
            }

            _padHits.Clear();
            _strummed = double.NegativeInfinity;
        }

        // Lit when struck, fading over PressFlashSeconds.
        private static double Flash(double since) => since < 0 ? 0 : Math.Max(0, 1 - (since / PressFlashSeconds));

        // A new pass from where the song is.
        private void StartPass(InputPlayer player, PlaytestRun run)
        {
            player.Sample();
            double song = player.Clock.Song ?? Playback.Clock().TotalSeconds;
            _lastSong = song;
            run.Start(song, player.Clock.Speed, player.IsDown, newPass: true);
        }

        // The engine for the track as it is now, with the settings as they are.
        private IPlaytestEngine EngineFor(ChartTrackKey key)
        {
            var settings = _settings.Settings;
            var game = Session.GameTrack(key);
            if (ChartInstruments.KindOf(key.Instrument) != ChartInstrumentKind.Drums)
                return new FiveFretEngine(PlaytestChart.Frets(game, Session.AudioAt, Session.TickAt), Window, new FretRules());

            var chart = PlaytestChart.DrumNotes(game, Session.AudioAt, Session.TickAt, Session.DrumsVariantShown, settings.PlaytestDoubleKick);
            var rules = new DrumRules(settings.PlaytestCymbalsMustMatch, settings.PlaytestDoubleKick, settings.PlaytestOverhitsBreakCombo, settings.PlaytestJudgeDynamics);
            var split = DrumPads.SplitOf(_bindings?.BindingsOf(InputProfileKind.Drums) ?? []);
            return new DrumsEngine(chart, Window, rules, split);
        }

        // The chart, the song's settings or the playtest's changed: every lane played is judged as it now is, from where the
        // song is; a lane whose track is gone is forgotten.
        private void Rebuild()
        {
            if (_runs.Count == 0)
                return;
            var player = _devices();
            double song = player?.Clock.Song ?? Playback.Clock().TotalSeconds;
            double speed = player?.Clock.Speed ?? 1;
            Func<InstrumentAction, bool> isDown = player is null ? static _ => false : player.IsDown;
            foreach (var (key, run) in _runs.ToList())
            {
                if (Session.TrackOf(key) is null)
                {
                    run.Changed -= OnRunChanged;
                    _runs.Remove(key);
                    continue;
                }

                run.Replace(EngineFor(key), song, speed, isDown);
            }

            if (IsActive && Track is { } track && !_runs.ContainsKey(track))
                End();
            Refresh();
        }

        private void OnChartChanged(object? sender, EditorChartChangedEventArgs e)
        {
            if (!e.IsPreview)
                Rebuild();
        }

        private void OnSongSettingsChanged(object? sender, EventArgs e) => Rebuild();

        private void OnRunChanged(object? sender, EventArgs e) => Refresh();

        private void Refresh()
        {
            HasFeedback = _runs.Count > 0;
            if (Run is { } run)
            {
                var pass = run.Pass;
                StatsText = pass.Hits + pass.Misses + pass.Overhits == 0
                    ? Strings.PlaytestStatsEmpty
                    : string.Format(CultureInfo.CurrentCulture, Strings.PlaytestStatsFormat, pass.Accuracy, pass.Combo, pass.MeanOffsetMs);
                if (!ReferenceEquals(_problemsFrom, run.Problems))
                {
                    _problemsFrom = run.Problems;
                    var map = Session.TempoMap;
                    Problems = [.. run.Problems.Select(p => new PlaytestProblemRow(p, PropertiesViewModel.MeasureBeat(map, p.Tick), WhatOf(p)))];
                }
            }
            else
            {
                StatsText = "";
                _problemsFrom = null;
                Problems = [];
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }

        private static string WhatOf(PlaytestProblem problem)
        {
            string what = problem.Kind switch
            {
                JudgeKind.Miss => Strings.PlaytestProblemMiss,
                JudgeKind.Overstrum => Strings.PlaytestProblemOverstrum,
                JudgeKind.Overhit => Strings.PlaytestProblemOverhit,
                _ => Strings.PlaytestProblemSustainDrop,
            };
            return problem.Count > 1 ? string.Format(CultureInfo.CurrentCulture, Strings.PlaytestProblemTimesFormat, what, problem.Count) : what;
        }

        // The cursor to the problem, and its notes selected: a chord's at its tick, a drum note's in its lane.
        private void GoTo(ChartTrackKey key, PlaytestProblem problem)
        {
            Session.MoveCursorTo(problem.Tick);
            if (Session.TrackOf(key) is not { } track)
                return;
            bool drums = ChartInstruments.KindOf(key.Instrument) == ChartInstrumentKind.Drums;

            // A game view drum lane as the editor stores it: from the kick at 0, unswapped from a disco flip.
            int? lane = drums ? Session.StoredLane(key, problem.Tick, problem.Lane + 1) : null;
            var notes = track.Notes.At(problem.Tick).ToArray().Where(n => lane is null || n.Lane == lane).ToList();
            if (notes.Count > 0)
                Session.Selection.Select(key, notes);
            else
                Session.Selection.Clear();
        }
    }
}
