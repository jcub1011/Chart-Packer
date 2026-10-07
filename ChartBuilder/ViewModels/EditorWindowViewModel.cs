using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using ChartBuilder.Resources;
using ChartBuilder.Services;
using ChartPacker;
using ChartPacker.Audio.Playback;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Layout;
using ChartPacker.Core.Scanning;
using ChartPacker.Services;
using ChartPacker.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// The Editor window: a toolbar, the lanes in the middle with the Chart Inspector and Properties panels either side,
    /// and the timeline along the bottom, where an open song plays. The tools, undo, Chart settings and the lanes'
    /// bodies are placeholders so far.
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

        /// <summary>A new chart, with nothing in it yet.</summary>
        /// <param name="settings">Where the layout is saved.</param>
        /// <param name="settingsWindow">Opens Program settings.</param>
        /// <param name="audioFiles">The audio files New chart was given, which the Editor can't use yet.</param>
        public EditorWindowViewModel(BuilderSettingsService settings, ISettingsWindowService settingsWindow, IReadOnlyList<string> audioFiles)
            : this(settings, settingsWindow, null, null, new PlaybackViewModel())
        {
            Notice = AudioNotAddedNotice(audioFiles);
        }

        /// <summary>A song opened from a folder or <c>.sng</c> (charter-spec C2).</summary>
        /// <param name="songLock">The song's lock, held until the editor is disposed.</param>
        /// <param name="openOutput">Opens the audio output, for playback.</param>
        /// <param name="latency">The audio/visual latency, which what's drawn against the audio allows for.</param>
        public EditorWindowViewModel(
            BuilderSettingsService settings,
            ISettingsWindowService settingsWindow,
            OpenedSong song,
            SongLock songLock,
            Func<int, IAudioOutput> openOutput,
            AudioLatencyService latency)
            : this(settings, settingsWindow, song, songLock, new PlaybackViewModel(song, openOutput, latency))
        {
            Notice = LoadIssuesNotice(song.Load.Issues.Count);
        }

        private EditorWindowViewModel(BuilderSettingsService settings, ISettingsWindowService settingsWindow, OpenedSong? song, SongLock? songLock, PlaybackViewModel playback)
        {
            _settings = settings;
            _settingsWindow = settingsWindow;
            _songLock = songLock;
            Song = song;
            Playback = playback;
            Title = string.Format(CultureInfo.CurrentCulture, Strings.EditorWindowTitleFormat, song?.Title ?? Strings.EditorUntitledChart);
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
            Lanes = new LaneDockViewModel(LaneDockLayout.FromState(saved.EditorLanes) ?? LaneDockLayout.CreateDefault());
        }

        public string Title { get; }

        /// <summary>The song open, or null for a new chart.</summary>
        public OpenedSong? Song { get; }

        /// <summary>The song's audio, and the timeline's playhead, waveform and beats.</summary>
        public PlaybackViewModel Playback { get; }

        public string VersionText { get; } = string.Format(CultureInfo.CurrentCulture, Strings.VersionFormat, AppVersion.Semantic);

        /// <summary>Something to tell the user, in a strip under the toolbar until they dismiss it. Null for none.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasNotice))]
        public partial string? Notice { get; set; }

        public bool HasNotice => Notice is not null;

        /// <summary>The lanes in the middle of the window: the parts' highways and the vocals.</summary>
        public LaneDockViewModel Lanes { get; }

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
        private Task OpenProgramSettingsAsync() => _settingsWindow.ShowAsync();

        /// <summary>The chart's own settings (song details, audio, resolution). Not built yet, so never enabled.</summary>
        [RelayCommand(CanExecute = nameof(IsImplemented))]
        private void OpenChartSettings()
        {
        }

        /// <summary>Not built yet, so never enabled.</summary>
        [RelayCommand(CanExecute = nameof(IsImplemented))]
        private void Undo()
        {
        }

        /// <summary>Not built yet, so never enabled.</summary>
        [RelayCommand(CanExecute = nameof(IsImplemented))]
        private void Redo()
        {
        }

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
            Playback.Dispose();
            _songLock?.Dispose();
        }

        private static bool IsImplemented() => false;

        /// <summary>That <paramref name="count"/> things in the chart were left out as it was read, or null when nothing was.</summary>
        private static string? LoadIssuesNotice(int count) => count switch
        {
            0 => null,
            1 => Strings.LoadIssueNotice,
            _ => string.Format(CultureInfo.CurrentCulture, Strings.LoadIssuesNoticeFormat, count),
        };

        /// <summary>That <paramref name="audioFiles"/> weren't added, or null when there are none.</summary>
        private static string? AudioNotAddedNotice(IReadOnlyList<string> audioFiles) => audioFiles.Count switch
        {
            0 => null,
            1 => string.Format(CultureInfo.CurrentCulture, Strings.EditorAudioFileNotAddedFormat, Path.GetFileName(audioFiles[0])),
            _ => string.Format(CultureInfo.CurrentCulture, Strings.EditorAudioFilesNotAddedFormat, audioFiles.Count),
        };
    }
}
