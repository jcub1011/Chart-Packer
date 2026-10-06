using System;
using System.Globalization;
using System.Threading.Tasks;
using ChartBuilder.Resources;
using ChartBuilder.Services;
using ChartPacker;
using ChartPacker.Services;
using ChartPacker.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// The Editor window: a toolbar, the Chart Inspector and Properties panels either side, and the timeline along the
    /// bottom. So far only the layout: the tools, undo and Chart settings are placeholders.
    /// </summary>
    /// <remarks>
    /// The panels' sizes are view state, applied and captured by the window; they're kept here between the two so
    /// <see cref="SaveLayout"/> can persist them.
    /// </remarks>
    public sealed partial class EditorWindowViewModel : ViewModelBase
    {
        /// <summary>How long the placeholder timeline is, until a chart's audio sets it.</summary>
        private const double PlaceholderLengthSeconds = 180;

        private readonly BuilderSettingsService _settings;
        private readonly ISettingsWindowService _settingsWindow;

        public EditorWindowViewModel(BuilderSettingsService settings, ISettingsWindowService settingsWindow)
        {
            _settings = settings;
            _settingsWindow = settingsWindow;

            var saved = settings.Settings;
            LeftPanelWidth = saved.EditorLeftPanelWidth;
            RightPanelWidth = saved.EditorRightPanelWidth;
            TimelineHeight = saved.EditorTimelineHeight;
            IsLeftPanelCollapsed = saved.EditorLeftPanelCollapsed;
            IsRightPanelCollapsed = saved.EditorRightPanelCollapsed;
        }

        public string Title { get; } = string.Format(CultureInfo.CurrentCulture, Strings.EditorWindowTitleFormat, Strings.EditorUntitledChart);

        public string VersionText { get; } = string.Format(CultureInfo.CurrentCulture, Strings.VersionFormat, AppVersion.Semantic);

        /// <summary>The Chart Inspector panel's width while expanded.</summary>
        public double LeftPanelWidth { get; set; }

        /// <summary>The Properties panel's width while expanded.</summary>
        public double RightPanelWidth { get; set; }

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

        /// <summary>Saves the panels' sizes and whether they're collapsed, for the next Editor window.</summary>
        public void SaveLayout()
        {
            var saved = _settings.Settings;
            saved.EditorLeftPanelWidth = LeftPanelWidth;
            saved.EditorRightPanelWidth = RightPanelWidth;
            saved.EditorTimelineHeight = TimelineHeight;
            saved.EditorLeftPanelCollapsed = IsLeftPanelCollapsed;
            saved.EditorRightPanelCollapsed = IsRightPanelCollapsed;
            _settings.Save();
        }

        private static bool IsImplemented() => false;

        /// <summary>A time as m:ss.f, e.g. 3:05.2.</summary>
        private static string FormatTime(double seconds) =>
            TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"m\:ss\.f", CultureInfo.CurrentCulture);
    }
}
