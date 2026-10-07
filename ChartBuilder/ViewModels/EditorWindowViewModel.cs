using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChartBuilder.Resources;
using ChartBuilder.Services;
using ChartPacker;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charting.Layout;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Scanning;
using ChartPacker.Services;
using ChartPacker.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// The Editor window: a toolbar, the lanes in the middle with the Chart Inspector and Properties panels either side,
    /// and the transport bar and the timeline along the bottom, where the song plays. It has a song opened, or a new chart
    /// that has no folder until it's first saved. The lanes' canvases and Chart settings, a tab beside the Chart Inspector,
    /// edit the chart, and undo and redo step through its edits.
    /// </summary>
    /// <remarks>
    /// The panels' sizes are view state, applied and captured by the window; they're kept here between the two so
    /// <see cref="SaveLayout"/> can persist them. So are the lanes' sizes, which the lane dock captures. Saving is in
    /// the other part of the class.
    /// </remarks>
    public sealed partial class EditorWindowViewModel : ViewModelBase, IDisposable
    {
        private readonly EditorServices _services;
        private readonly BuilderSettingsService _settings;
        private readonly ISettingsWindowService _settingsWindow;
        private readonly IConfirmationService _confirmations;
        private readonly ChartUndoStack _undo;
        private readonly CancellationTokenSource _closing = new();
        private SongLock? _songLock;

        private EditorWindowViewModel(EditorServices services, EditorSong song, SongLock? songLock, bool startsSaved = true)
        {
            _services = services;
            _settings = services.Settings;
            _settingsWindow = services.SettingsWindow;
            _confirmations = services.Confirmations;
            _songLock = songLock;
            var settings = services.Settings;
            Song = song;
            Song.DefaultCharter = settings.Settings.DefaultCharterName;
            Playback = new PlaybackViewModel(song, services.OpenOutput, services.Latency, () => (settings.Settings.MetronomeVolume, settings.Settings.HitSoundVolume), services.Calibrate);
            _undo = new ChartUndoStack(song.Chart, startsSaved);
            _undo.Changed += OnChartChanged;
            ChartSettings = new ChartSettingsViewModel(settings, services.Confirmations, song.Chart, ApplyChartSettings);
            PendingChanges = PendingChangesOf();
            Title = TitleOf();
            Playback.PropertyChanged += (_, e) =>
            {
                // Audio that can't be played is said in the notice, as nothing else would say it.
                if (e.PropertyName == nameof(PlaybackViewModel.Message) && Playback.Message is { } message)
                    Notice = message;
            };

            var saved = settings.Settings;
            LeftPanelWidth = saved.EditorLeftPanelWidth;
            RightPanelWidth = saved.EditorRightPanelWidth;
            TimelineHeight = saved.EditorTimelineHeight;
            IsLeftPanelCollapsed = saved.EditorLeftPanelCollapsed;
            IsRightPanelCollapsed = saved.EditorRightPanelCollapsed;
            Editor = new ChartEditorViewModel(song, _undo, Playback, settings, services.Confirmations, LaneDockLayout.FromState(saved.EditorLanes) ?? LaneDockLayout.CreateDefault());
            Findings = new FindingsViewModel(song, _undo, Editor);
            Findings.Changed += (_, _) => RefreshSaveState();
            Song.Changed += OnSongChanged;
            _ = MeasureAudioAsync();
            StartAutosave();
        }

        /// <summary>A song opened from a folder or <c>.sng</c> (charter-spec C2).</summary>
        /// <param name="songLock">The song's lock, held until the editor is disposed.</param>
        public static EditorWindowViewModel ForSong(EditorServices services, OpenedSong song, SongLock songLock) =>
            new(services, EditorSong.Of(song), songLock)
            {
                Notice = LoadIssuesNotice(song.Load.Issues.Count),
            };

        /// <summary>
        /// A new chart (charter-spec C1), with <paramref name="audio"/>'s tracks as its stems. It has no folder, and so no
        /// lock, until it's first saved.
        /// </summary>
        public static EditorWindowViewModel ForNewChart(EditorServices services, EditableChart chart, NewChartAudio audio) =>
            new(services, EditorSong.New(chart, audio.Tracks), null)
            {
                Notice = NotAddedNotice(audio),
            };

        /// <summary>The window's title: the song's, or Untitled chart, marked while there are unsaved edits.</summary>
        [ObservableProperty]
        public partial string Title { get; private set; }

        /// <summary>The song or new chart open.</summary>
        public EditorSong Song { get; }

        /// <summary>
        /// The <c>song.ini</c> changes the chart and the song call for, shown from as soon as they're called for until
        /// they're saved, the reverted ones among them (charter-spec C17 item 2).
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasPendingChanges))]
        public partial IReadOnlyList<PendingChangeItem> PendingChanges { get; private set; }

        public bool HasPendingChanges => PendingChanges.Count > 0;

        /// <summary>The chart's timing and parts, in the Chart settings tab (charter-spec C1).</summary>
        public ChartSettingsViewModel ChartSettings { get; }

        /// <summary>The song's audio, and the timeline's playhead, waveform and beats.</summary>
        public PlaybackViewModel Playback { get; }

        public string VersionText { get; } = string.Format(CultureInfo.CurrentCulture, Strings.VersionFormat, AppVersion.Semantic);

        /// <summary>Something to tell the user, in a strip under the toolbar until they dismiss it. Null for none.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasNotice))]
        public partial string? Notice { get; set; }

        public bool HasNotice => Notice is not null;

        /// <summary>The chart's editor: the lanes and their canvases, the tools and the snap, and Properties.</summary>
        public ChartEditorViewModel Editor { get; }

        /// <summary>The findings panel: the chart's lint findings as it's edited.</summary>
        public FindingsViewModel Findings { get; }

        /// <summary>The lanes in the middle of the window: the parts' highways and the vocals.</summary>
        public LaneDockViewModel Lanes => Editor.Lanes;

        /// <summary>The Chart Inspector panel's width while expanded, as the user last set it.</summary>
        public double LeftPanelWidth { get; set; }

        /// <summary>The Properties panel's width while expanded, as the user last set it.</summary>
        public double RightPanelWidth { get; set; }

        /// <summary>The timeline's height, as the user last set it.</summary>
        public double TimelineHeight { get; set; }

        [ObservableProperty]
        public partial bool IsLeftPanelCollapsed { get; set; }

        [ObservableProperty]
        public partial bool IsRightPanelCollapsed { get; set; }

        [RelayCommand]
        private void ToggleLeftPanel() => IsLeftPanelCollapsed = !IsLeftPanelCollapsed;

        [RelayCommand]
        private void ToggleRightPanel() => IsRightPanelCollapsed = !IsRightPanelCollapsed;

        [RelayCommand]
        private void DismissNotice() => Notice = null;

        [RelayCommand]
        private async Task OpenProgramSettingsAsync()
        {
            await _settingsWindow.ShowAsync();
            Editor.ReloadSettings();
            Song.DefaultCharter = _settings.Settings.DefaultCharterName;
            if (_autosave is { } autosave)
                autosave.Interval = TimeSpan.FromMinutes(_settings.Settings.AutosaveMinutes);
        }

        /// <summary>
        /// A change from Chart settings (charter-spec C1), as one undoable step. Playback goes on, so the tempo can be
        /// found by ear; the beats follow the change.
        /// </summary>
        private void ApplyChartSettings(IChartCommand command)
        {
            try
            {
                _undo.Execute(command);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or ChartFormatException)
            {
                // Refused before anything changed, as every command checks first, so Chart settings shows the chart again.
                Notice = string.Format(CultureInfo.CurrentCulture, Strings.ChartSettingsNotAppliedFormat, ex.Message);
                ChartSettings.Reload();
            }
        }

        [RelayCommand(CanExecute = nameof(CanUndo))]
        private void Undo() => Editor.Session.Undo();

        [RelayCommand(CanExecute = nameof(CanRedo))]
        private void Redo() => Editor.Session.Redo();

        private bool CanUndo() => _undo.CanUndo;

        private bool CanRedo() => _undo.CanRedo;

        /// <summary>Saves the panels' sizes and whether they're collapsed, and the lanes, for the next Editor window.</summary>
        public void SaveLayout()
        {
            var saved = _settings.Settings;
            saved.EditorLeftPanelWidth = LeftPanelWidth;
            saved.EditorRightPanelWidth = RightPanelWidth;
            saved.EditorTimelineHeight = TimelineHeight;
            saved.EditorLeftPanelCollapsed = IsLeftPanelCollapsed;
            saved.EditorRightPanelCollapsed = IsRightPanelCollapsed;
            saved.EditorLanes = Lanes.Layout.ToState();
            _settings.Save();
        }

        /// <summary>Stops the song and lets go of its lock, as the window closes.</summary>
        public void Dispose()
        {
            StopAutosave();

            // Closed with nothing unsaved, or with its edits discarded: there's nothing left to recover.
            if (!HasUnsavedChanges)
                DeleteRecovery();
            _closing.Cancel();
            _undo.Changed -= OnChartChanged;
            Song.Changed -= OnSongChanged;
            Findings.Dispose();
            Editor.Dispose();
            Playback.Dispose();
            _songLock?.Dispose();
            _songLock = null;
        }

        /// <summary>
        /// Follows an edit, its undo or its redo: the beats when the tempo map changed, what's pending, Chart settings,
        /// and the title's unsaved mark.
        /// </summary>
        private void OnChartChanged(object? sender, ChartCommandEventArgs e)
        {
            if (e.Affected.TempoMapChanged)
                Playback.RefreshTempoMap();
            PendingChanges = PendingChangesOf();
            ChartSettings.Reload();
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
            CountChange();
            RefreshSaveState();
        }

        /// <summary>
        /// Follows a change to the song other than the chart: a save, a pending change reverted or restored, the default
        /// charter or the audio's length. The delay and the song's settings may have moved, which the editor and the
        /// beats follow.
        /// </summary>
        private void OnSongChanged(object? sender, EventArgs e)
        {
            PendingChanges = PendingChangesOf();
            Editor.Session.SetSongSettings(Song.EffectivePairs(), Song.Delay);
            Playback.RefreshTempoMap();
            CountChange();
            RefreshSaveState();
        }

        private IReadOnlyList<PendingChangeItem> PendingChangesOf() =>
            [.. Song.AllPendingChanges().Select(c => new PendingChangeItem(c, c.CanRevert && Song.Reverted.Contains(c.Key), Song.Revert, Song.Restore))];

        private string TitleOf() => string.Format(
            CultureInfo.CurrentCulture,
            HasUnsavedChanges ? Strings.EditorWindowTitleUnsavedFormat : Strings.EditorWindowTitleFormat,
            Song.Title ?? Strings.EditorUntitledChart);

        /// <summary>
        /// Measures the song's audio for <c>song_length</c>, off the UI thread, and for a new chart reads its first stem's
        /// title and artist, which its first save starts from (charter-spec C1).
        /// </summary>
        private async Task MeasureAudioAsync()
        {
            var entry = Song.Entry;
            var tracks = Song.Tracks;
            bool isNew = Song.IsNew;
            var token = _closing.Token;
            try
            {
                var (length, tags) = await Task.Run(() => (SongAudioLength.LongestMs(entry, tracks, token), isNew ? TagsOf(tracks) : []), token);
                if (token.IsCancellationRequested)
                    return;
                if (tags.Count > 0 && Song.IsNew && Song.Values.Count == 0)
                    Song.Values = tags;
                Song.AudioLengthMs = length;
            }
            catch (OperationCanceledException)
            {
            }
        }

        // The first stem's title and artist tags, as song.ini's name and artist.
        private static IReadOnlyList<KeyValuePair<string, string>> TagsOf(IReadOnlyList<SongTrack> tracks)
        {
            foreach (var track in tracks)
            {
                if (track.StagedSourcePath is not { } path)
                    continue;
                try
                {
                    if (AudioFileInfo.Read(path) is not { } info)
                        continue;
                    var tags = new List<KeyValuePair<string, string>>();
                    if (!string.IsNullOrWhiteSpace(info.Title))
                        tags.Add(KeyValuePair.Create("name", info.Title.Trim()));
                    if (!string.IsNullOrWhiteSpace(info.Artist))
                        tags.Add(KeyValuePair.Create("artist", info.Artist.Trim()));
                    return tags;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            return [];
        }

        /// <summary>Which of a new chart's audio files weren't used, and why, or null when all were.</summary>
        private static string? NotAddedNotice(NewChartAudio audio)
        {
            var files = audio.NotAudio.Select(f => string.Format(CultureInfo.CurrentCulture, Strings.NewChartFileNotAudioFormat, Path.GetFileName(f)))
                .Concat(audio.NoStem.Select(f => string.Format(CultureInfo.CurrentCulture, Strings.NewChartFileNoStemFormat, Path.GetFileName(f))))
                .ToList();
            return files.Count == 0 ? null : string.Format(CultureInfo.CurrentCulture, Strings.NewChartFilesNotAddedFormat, string.Join("; ", files));
        }

        /// <summary>That <paramref name="count"/> things in the chart were left out as it was read, or null when nothing was.</summary>
        private static string? LoadIssuesNotice(int count) => count switch
        {
            0 => null,
            1 => Strings.LoadIssueNotice,
            _ => string.Format(CultureInfo.CurrentCulture, Strings.LoadIssuesNoticeFormat, count),
        };
    }
}
