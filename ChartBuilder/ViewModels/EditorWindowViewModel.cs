using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using ChartBuilder.Resources;
using ChartBuilder.Services;
using ChartPacker;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Layout;
using ChartPacker.Services;
using ChartPacker.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// The Editor window: a toolbar, the lanes in the middle with the Chart Inspector and Properties panels either side,
    /// and the timeline along the bottom. So far only the layout: the tools, undo, Chart settings and the lanes' bodies
    /// are placeholders.
    /// </summary>
    /// <remarks>
    /// The panels' sizes are view state, applied and captured by the window; they're kept here between the two so
    /// <see cref="SaveLayout"/> can persist them. So are the lanes' sizes, which the lane dock captures.
    /// </remarks>
    public sealed partial class EditorWindowViewModel : ViewModelBase
    {
        /// <summary>How long the placeholder timeline is, until a chart's audio sets it.</summary>
        private const double PlaceholderLengthSeconds = 180;

        private readonly BuilderSettingsService _settings;
        private readonly ISettingsWindowService _settingsWindow;

        /// <param name="settings">Where the layout is saved.</param>
        /// <param name="settingsWindow">Opens Program settings.</param>
        /// <param name="audioFiles">The audio files New chart was given, which the Editor can't use yet.</param>
        public EditorWindowViewModel(BuilderSettingsService settings, ISettingsWindowService settingsWindow, IReadOnlyList<string> audioFiles)
        {
            _settings = settings;
            _settingsWindow = settingsWindow;
            Notice = AudioNotAddedNotice(audioFiles);

            var saved = settings.Settings;
            LeftPanelWidth = saved.EditorLeftPanelWidth;
            RightPanelWidth = saved.EditorRightPanelWidth;
            TimelineHeight = saved.EditorTimelineHeight;
            IsLeftPanelCollapsed = saved.EditorLeftPanelCollapsed;
            IsRightPanelCollapsed = saved.EditorRightPanelCollapsed;
            Lanes = new LaneDockViewModel(LaneDockLayout.FromState(saved.EditorLanes) ?? LaneDockLayout.CreateDefault());
        }

        public string Title { get; } = string.Format(CultureInfo.CurrentCulture, Strings.EditorWindowTitleFormat, Strings.EditorUntitledChart);

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

        /// <summary>The playhead, in seconds from the start of the song.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PositionText))]
        public partial double TimelinePosition { get; set; }

        /// <summary>The song's length, in seconds: what the timeline spans.</summary>
        public double TimelineLength => PlaceholderLengthSeconds;

        /// <summary>The playhead and the song's length, e.g. "0:42.0 / 3:00.0".</summary>
        public string PositionText => string.Format(
            CultureInfo.CurrentCulture, Strings.TimelinePositionFormat, FormatTime(TimelinePosition), FormatTime(TimelineLength));

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

        private static bool IsImplemented() => false;

        /// <summary>That <paramref name="audioFiles"/> weren't added, or null when there are none.</summary>
        private static string? AudioNotAddedNotice(IReadOnlyList<string> audioFiles) => audioFiles.Count switch
        {
            0 => null,
            1 => string.Format(CultureInfo.CurrentCulture, Strings.EditorAudioFileNotAddedFormat, Path.GetFileName(audioFiles[0])),
            _ => string.Format(CultureInfo.CurrentCulture, Strings.EditorAudioFilesNotAddedFormat, audioFiles.Count),
        };

        /// <summary>A time as m:ss.f, or h:mm:ss.f from an hour, e.g. 3:05.2.</summary>
        private static string FormatTime(double seconds) =>
            SongTime.Format(TimeSpan.FromSeconds(Math.Max(0, seconds)), decimals: 1);
    }
}
