using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ChartBuilder.Resources;
using ChartBuilder.Services;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Layout;
using ChartShared.Core.Charting.Saving;
using ChartShared.Core.Charts;
using ChartShared.Core.Metadata;
using ChartShared.Core.Scanning;
using ChartShared.UI.Services;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// The Editor window: a toolbar, the lanes in the middle with Chart settings and Properties panels either side, and
    /// the transport bar and the timeline along the bottom, where the song plays. It has a song opened, or a new chart that
    /// has no folder until it's first saved. The lanes' canvases edit the chart, and Chart settings and the Art tab the
    /// chart and the song around it; undo and redo step through every edit, the song's as well as the chart's.
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
        private readonly DispatcherTimer _tracksTimer;
        private SongLock? _songLock;

        // The copies of the files saves replace or remove, kept for undo; made by the first save that needs one.
        private MediaStash? _stash;

        // How long the tracks must be still before the audio is opened again, so a burst of undos opens it once. Its timer
        // isn't at background priority, which the lanes' redraws as their audio is read could hold off for seconds.
        private static readonly TimeSpan TracksDelay = TimeSpan.FromMilliseconds(250);

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
            Audio = new EditorAudio(song, settings.Settings, Messages);
            Playback = new PlaybackViewModel(song, services.OpenOutput, services.Latency, () => (settings.Settings.MetronomeVolume, settings.Settings.HitSoundVolume), services.Calibrate, Audio);
            _undo = new ChartUndoStack(song.Chart, startsSaved);
            _undo.Changed += OnChartChanged;
            ChartSettings = new ChartSettingsViewModel(settings, services.Confirmations, song, ApplyChartSettings, () => Editor?.AudioAssist);
            Metadata = new ChartMetadataViewModel(song, ApplyChartSettings);
            Tracks = new EditorTracksViewModel(song, Playback, services.FilePicker, ApplyChartSettings);
            Art = new ArtViewModel(song, services.ImagePicker, services.FilePicker, ApplyChartSettings, Playback.Clock);
            _tracksTimer = new DispatcherTimer(TracksDelay, DispatcherPriority.Normal, (_, _) => _ = ReloadAudioAsync());
            _tracksTimer.Stop();
            PendingChanges = PendingChangesOf();
            RefreshRecent();
            Title = TitleOf();
            Playback.PropertyChanged += (_, e) =>
            {
                // Audio that can't be played is said on the message bar, as nothing else would say it.
                if (e.PropertyName == nameof(PlaybackViewModel.Message) && Playback.Message is { } message)
                    Messages.Error(message);
            };

            var saved = settings.Settings;
            LeftPanelWidth = saved.EditorLeftPanelWidth;
            RightPanelWidth = saved.EditorRightPanelWidth;
            TimelineHeight = saved.EditorTimelineHeight;
            IsLeftPanelCollapsed = saved.EditorLeftPanelCollapsed;
            IsRightPanelCollapsed = saved.EditorRightPanelCollapsed;
            Editor = new ChartEditorViewModel(song, _undo, Playback, Audio, settings, services.Confirmations, Messages, LaneDockLayout.FromState(saved.EditorLanes) ?? LaneDockLayout.CreateDefault(), services.Authoring, services.Input);
            Audio.AudibleChanged += OnAudibleChanged;
            Audio.Start();
            Findings = new FindingsViewModel(song, _undo, Editor);
            Sections = new SectionsViewModel(Editor);
            TempoMap = new TempoMapViewModel(Editor, ChartSettings);
            Findings.Changed += (_, _) => RefreshSaveState();
            Editor.AudioAssist.ShowMessagesRequested += (_, _) => _ = ShowMessagesAsync();
            Song.Changed += OnSongChanged;
            Song.TracksChanged += OnTracksChanged;
            _ = MeasureAudioAsync();
            StartAutosave();
        }

        /// <summary>A song opened from a folder or <c>.sng</c> (charter-spec C2).</summary>
        /// <param name="songLock">The song's lock, held until the editor is disposed.</param>
        public static EditorWindowViewModel ForSong(EditorServices services, OpenedSong song, SongLock songLock)
        {
            var viewModel = new EditorWindowViewModel(services, EditorSong.Of(song), songLock);
            if (LoadIssuesNotice(song.Load.Issues.Count) is { } issues)
                viewModel.Messages.Warning(issues);
            return viewModel;
        }

        /// <summary>
        /// A new chart (charter-spec C1), with <paramref name="audio"/>'s tracks as its stems. It has no folder, and so no
        /// lock, until it's first saved.
        /// </summary>
        public static EditorWindowViewModel ForNewChart(EditorServices services, EditableChart chart, NewChartAudio audio)
        {
            var viewModel = new EditorWindowViewModel(services, EditorSong.New(chart, audio.Tracks), null);
            if (NotAddedNotice(audio) is { } notAdded)
                viewModel.Messages.Warning(notAdded);
            return viewModel;
        }

        /// <summary>The window's title: the song's, or Untitled chart, marked while there are unsaved edits.</summary>
        [ObservableProperty]
        public partial string Title { get; private set; }

        /// <summary>The song or new chart open.</summary>
        public EditorSong Song { get; }

        /// <summary>
        /// The title's tooltip: that clicking it shows the song in the file manager, and where it is. None for a new chart,
        /// which has no folder until it's first saved.
        /// </summary>
        public string? ShowSongToolTip => Song.Entry is { } entry
            ? string.Format(CultureInfo.CurrentCulture, Strings.ShowSongToolTipFormat, FileRevealService.RevealLabel, entry.FullPath)
            : null;

        /// <summary>
        /// The <c>song.ini</c> changes the chart and the song call for, shown from as soon as they're called for until
        /// they're saved, the reverted ones among them (charter-spec C17 item 2).
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasPendingChanges))]
        public partial IReadOnlyList<PendingChangeItem> PendingChanges { get; private set; }

        public bool HasPendingChanges => PendingChanges.Count > 0;

        /// <summary>
        /// The chart's resolution, the delay, and the parts and their ratings, in the Chart settings tab, and the starting tempo
        /// and time signature, in the Tempo map tab (charter-spec C1).
        /// </summary>
        public ChartSettingsViewModel ChartSettings { get; }

        /// <summary>The song's metadata, in the Chart settings tab.</summary>
        public ChartMetadataViewModel Metadata { get; }

        /// <summary>The song's audio tracks and their mix, in the Chart settings tab.</summary>
        public EditorTracksViewModel Tracks { get; }

        /// <summary>The Art tab: the album art, background image and background video.</summary>
        public ArtViewModel Art { get; }

        /// <summary>The song's audio, and the timeline's playhead, waveform and beats.</summary>
        public PlaybackViewModel Playback { get; }

        /// <summary>The song's audio as the lanes draw and analyse it (charter-spec C12–C14).</summary>
        public EditorAudio Audio { get; }

        /// <summary>What the Editor has had to say, the latest on the message bar along the bottom of the window.</summary>
        public EditorMessages Messages { get; } = new();

        /// <summary>The chart's editor: the lanes and their canvases, the tools and the snap, and Properties.</summary>
        public ChartEditorViewModel Editor { get; }

        /// <summary>The findings panel: the chart's lint findings as it's edited.</summary>
        public FindingsViewModel Findings { get; }

        /// <summary>The Sections tab: the chart's sections and other text events.</summary>
        public SectionsViewModel Sections { get; }

        /// <summary>The Tempo map tab: the chart's tempo and time signature changes.</summary>
        public TempoMapViewModel TempoMap { get; }

        /// <summary>The lanes in the middle of the window: the parts' highways and the vocals.</summary>
        public LaneDockViewModel Lanes => Editor.Lanes;

        /// <summary>The left panel's width while expanded, as the user last set it.</summary>
        public double LeftPanelWidth { get; set; }

        /// <summary>The Properties panel's width while expanded, as the user last set it.</summary>
        public double RightPanelWidth { get; set; }

        /// <summary>The timeline's height, as the user last set it.</summary>
        public double TimelineHeight { get; set; }

        [ObservableProperty]
        public partial bool IsLeftPanelCollapsed { get; set; }

        /// <summary>The left panel's tab shown: Chart settings (<see cref="ChartSettingsTab"/>), Art, Sections or Tempo map.</summary>
        [ObservableProperty]
        public partial int LeftPanelTab { get; set; }

        /// <summary>The left panel's tab index of Chart settings.</summary>
        public const int ChartSettingsTab = 0;

        /// <summary>The left panel's tab index of Art.</summary>
        public const int ArtTab = 1;

        partial void OnLeftPanelTabChanged(int value) => Art.IsShown = value == ArtTab && !IsLeftPanelCollapsed;

        partial void OnIsLeftPanelCollapsedChanged(bool value) => Art.IsShown = LeftPanelTab == ArtTab && !value;

        [ObservableProperty]
        public partial bool IsRightPanelCollapsed { get; set; }

        [RelayCommand]
        private void ToggleLeftPanel() => IsLeftPanelCollapsed = !IsLeftPanelCollapsed;

        [RelayCommand]
        private void ToggleRightPanel() => IsRightPanelCollapsed = !IsRightPanelCollapsed;

        /// <summary>Shows every message said this session, from the message bar.</summary>
        [RelayCommand]
        private Task ShowMessagesAsync() => _services.MessagesDialog.ShowAsync(new MessagesDialogViewModel(Messages));

        private bool CanShowSong() => Song.Entry is not null;

        /// <summary>Opens the song's folder in the file manager, or for a <c>.sng</c>, the folder it's in with it selected.</summary>
        [RelayCommand(CanExecute = nameof(CanShowSong))]
        private void ShowSong()
        {
            if (Song.Entry is not { } entry)
                return;

            if (entry.Kind == SongEntryKind.SngFile)
                _services.Reveal.Reveal([entry.FullPath]);
            else
                _services.Reveal.OpenFolder(entry.FullPath);
        }

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
        /// A change from Chart settings or the Art tab (charter-spec C1), as one undoable step. Playback goes on, so the
        /// tempo can be found by ear; the beats follow the change.
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
                Messages.Error(string.Format(CultureInfo.CurrentCulture, Strings.ChartSettingsNotAppliedFormat, ex.Message));
                ChartSettings.Reload();
                return;
            }

            if (DrumConversionText.ConversionIn(command) is { Report: { } report } conversion)
                ReportConversion(conversion, report);
        }

        /// <summary>
        /// Says what a drum conversion did (charter-spec C6 item 8). When it left notes out or changed them, the Messages
        /// dialog opens on the report, as nothing else lists notes no longer in the chart.
        /// </summary>
        private void ReportConversion(ConvertDrums conversion, DrumConversionReport report)
        {
            string text = DrumConversionText.Format(conversion, report, tick => Editor.Session.AudioAt(tick));
            if (!DrumConversionText.NeedsReading(report))
            {
                Messages.Info(text);
                return;
            }

            Messages.Warning(text);
            _ = ShowMessagesAsync();
        }

        [RelayCommand(CanExecute = nameof(CanUndo))]
        private void Undo() => Editor.Session.Undo();

        [RelayCommand(CanExecute = nameof(CanRedo))]
        private void Redo() => Editor.Session.Redo();

        // An edit of the song's own isn't undone or redone while a save runs, as the save moves the song on to what it wrote.
        private bool CanUndo() => _undo.CanUndo && !(IsSaving && IsSongEdit(_undo.NextUndo));

        private bool CanRedo() => _undo.CanRedo && !(IsSaving && IsSongEdit(_undo.NextRedo));

        private static bool IsSongEdit(IChartCommand? command) => command is SongValueCommand or MediaCommand;

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
            Song.TracksChanged -= OnTracksChanged;
            _tracksTimer.Stop();
            Findings.Dispose();
            Sections.Dispose();
            TempoMap.Dispose();
            ChartSettings.Dispose();
            Metadata.Dispose();
            Tracks.Dispose();
            Art.Dispose();
            Editor.Dispose();
            Audio.Dispose();
            Playback.Dispose();
            _songLock?.Dispose();
            _songLock = null;

            // Nothing can be undone once the window has closed, so the copies kept for undo go.
            _stash?.Dispose();
            _stash = null;
        }

        /// <summary>
        /// Follows an edit, its undo or its redo: the beats when the tempo map changed, what's pending, Chart settings,
        /// and the title's unsaved mark.
        /// </summary>
        private void OnChartChanged(object? sender, ChartCommandEventArgs e)
        {
            if (e.Affected.TempoMapChanged)
                Playback.RefreshTempoMap();
            RefreshPendingChanges();
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
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
            RefreshPendingChanges();
            Editor.Session.SetSongSettings(Song.EffectivePairs(), Song.Delay);
            Playback.RefreshTempoMap();
            CountChange();
            RefreshSaveState();
        }

        // Where the stems are heard, once they're read, places [music_start], [music_end] and [end] (charter-spec C8 item 5).
        private void OnAudibleChanged(object? sender, EventArgs e)
        {
            if (Audio.Audible is (true, var range))
                Editor.Session.SetAudible(range);
        }

        // A track added, replaced or removed, or a save: the audio is opened again once the tracks are still.
        private void OnTracksChanged(object? sender, EventArgs e)
        {
            _tracksTimer.Stop();
            _tracksTimer.Start();
        }

        /// <summary>
        /// Plays and draws the song's tracks as they are now: the waveforms, analysis and playback start again on them,
        /// playing on from where they were, and <c>song_length</c> is measured again.
        /// </summary>
        private async Task ReloadAudioAsync()
        {
            _tracksTimer.Stop();
            if (_closing.IsCancellationRequested)
                return;
            Audio.SetTracks(Song.Entry, Song.Tracks);
            foreach (var lane in Lanes.All)
                lane.Audio.Refresh();
            await Playback.ReloadAsync(Song.Tracks);
            _ = MeasureAudioAsync();
        }

        /// <summary>Lists what's pending again, only when it's changed, so an edit that changes none of it doesn't remake the list.</summary>
        private void RefreshPendingChanges()
        {
            var changes = Song.AllPendingChanges();
            if (changes.Count == PendingChanges.Count)
            {
                bool same = true;
                for (int i = 0; i < changes.Count && same; i++)
                {
                    var shown = PendingChanges[i];
                    same = shown.Change == changes[i] && shown.IsReverted == (changes[i].CanRevert && Song.Reverted.Contains(changes[i].Key));
                }

                if (same)
                    return;
            }

            PendingChanges = PendingChangesOf();
        }

        private IReadOnlyList<PendingChangeItem> PendingChangesOf() =>
            [.. Song.AllPendingChanges().Select(c => new PendingChangeItem(c, c.CanRevert && Song.Reverted.Contains(c.Key), Song.Revert, Song.Restore))];

        private string TitleOf() => string.Format(
            CultureInfo.CurrentCulture,
            HasUnsavedChanges ? Strings.EditorWindowTitleUnsavedFormat : Strings.EditorWindowTitleFormat,
            Song.Title ?? Strings.EditorUntitledChart);

        /// <summary>
        /// Measures the song's audio for <c>song_length</c>, off the UI thread, and for a new chart reads its first stem's
        /// tags, which the Chart Inspector starts its name, artist, album, genre and year from (charter-spec C1).
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

        // The first stem's tags, as song.ini's name, artist, album, genre and year.
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
                    foreach (var (key, value) in (ReadOnlySpan<(string, string?)>)[("name", info.Title), ("artist", info.Artist), ("album", info.Album), ("genre", info.Genre), ("year", info.Year)])
                    {
                        if (!string.IsNullOrWhiteSpace(value))
                            tags.Add(KeyValuePair.Create(key, value.Trim()));
                    }

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
