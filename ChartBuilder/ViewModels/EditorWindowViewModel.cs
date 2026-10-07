using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ChartBuilder.Resources;
using ChartBuilder.Services;
using ChartPacker;
using ChartPacker.Audio.Playback;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charting.Layout;
using ChartPacker.Core.Charts;
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
    /// <see cref="SaveLayout"/> can persist them. So are the lanes' sizes, which the lane dock captures.
    /// </remarks>
    public sealed partial class EditorWindowViewModel : ViewModelBase, IDisposable
    {
        private readonly BuilderSettingsService _settings;
        private readonly ISettingsWindowService _settingsWindow;
        private readonly SongLock? _songLock;
        private readonly ChartUndoStack _undo;

        private EditorWindowViewModel(
            BuilderSettingsService settings,
            ISettingsWindowService settingsWindow,
            IConfirmationService confirmations,
            EditorSong song,
            SongLock? songLock,
            Func<int, IAudioOutput> openOutput,
            AudioLatencyService latency,
            Action calibrate)
        {
            _settings = settings;
            _settingsWindow = settingsWindow;
            _songLock = songLock;
            Song = song;
            Playback = new PlaybackViewModel(song, openOutput, latency, () => (settings.Settings.MetronomeVolume, settings.Settings.HitSoundVolume), calibrate);
            _undo = new ChartUndoStack(song.Chart);
            _undo.Changed += OnChartChanged;
            ChartSettings = new ChartSettingsViewModel(settings, confirmations, song.Chart, ApplyChartSettings);
            PendingChanges = PendingChangesOf();
            Title = string.Format(CultureInfo.CurrentCulture, Strings.EditorWindowTitleFormat, song.Title ?? Strings.EditorUntitledChart);
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
            Editor = new ChartEditorViewModel(song, _undo, Playback, settings, confirmations, LaneDockLayout.FromState(saved.EditorLanes) ?? LaneDockLayout.CreateDefault());
        }

        /// <summary>A song opened from a folder or <c>.sng</c> (charter-spec C2).</summary>
        /// <param name="settings">Where the layout is saved.</param>
        /// <param name="settingsWindow">Opens Program settings.</param>
        /// <param name="confirmations">Asks before Chart settings makes a change that loses something.</param>
        /// <param name="songLock">The song's lock, held until the editor is disposed.</param>
        /// <param name="openOutput">Opens the audio output, for playback.</param>
        /// <param name="latency">The audio/visual latency, which what's drawn against the audio allows for.</param>
        /// <param name="calibrate">Opens the calibration tool, from the transport bar.</param>
        public static EditorWindowViewModel ForSong(
            BuilderSettingsService settings,
            ISettingsWindowService settingsWindow,
            IConfirmationService confirmations,
            OpenedSong song,
            SongLock songLock,
            Func<int, IAudioOutput> openOutput,
            AudioLatencyService latency,
            Action calibrate) =>
            new(settings, settingsWindow, confirmations, EditorSong.Of(song), songLock, openOutput, latency, calibrate)
            {
                Notice = LoadIssuesNotice(song.Load.Issues.Count),
            };

        /// <summary>
        /// A new chart (charter-spec C1), with <paramref name="audio"/>'s tracks as its stems. It has no folder, and so no
        /// lock, until it's first saved.
        /// </summary>
        public static EditorWindowViewModel ForNewChart(
            BuilderSettingsService settings,
            ISettingsWindowService settingsWindow,
            IConfirmationService confirmations,
            EditableChart chart,
            NewChartAudio audio,
            Func<int, IAudioOutput> openOutput,
            AudioLatencyService latency,
            Action calibrate) =>
            new(settings, settingsWindow, confirmations, EditorSong.New(chart, audio.Tracks), null, openOutput, latency, calibrate)
            {
                Notice = NotAddedNotice(audio),
            };

        public string Title { get; }

        /// <summary>The song or new chart open.</summary>
        public EditorSong Song { get; }

        /// <summary>
        /// The <c>song.ini</c> changes the chart calls for, as <c>key = value</c>, shown until they're saved
        /// (charter-spec C17 item 2). Saving them comes with Chart Builder's save.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasPendingChanges))]
        public partial IReadOnlyList<string> PendingChanges { get; private set; }

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
            _undo.Changed -= OnChartChanged;
            Editor.Dispose();
            Playback.Dispose();
            _songLock?.Dispose();
        }

        /// <summary>
        /// Follows an edit, its undo or its redo: the beats when the tempo map changed, what's pending, and Chart settings.
        /// </summary>
        private void OnChartChanged(object? sender, ChartCommandEventArgs e)
        {
            if (e.Affected.TempoMapChanged)
                Playback.RefreshTempoMap();
            PendingChanges = PendingChangesOf();
            ChartSettings.Reload();
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
        }

        private IReadOnlyList<string> PendingChangesOf() =>
            [.. PendingSongIniChanges.Of(Song.Chart, Song.LoadedParts, Song.Pairs).Select(c => string.Format(CultureInfo.CurrentCulture, Strings.PendingChangeFormat, c.Key, c.Value))];

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
