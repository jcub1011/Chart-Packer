using System.Collections.Generic;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Authoring;
using ChartPacker.Core.Charting.Editing;
using ChartPacker.Core.Charting.Layout;
using ChartPacker.Core.Launching;

namespace ChartBuilder.Services
{
    /// <summary>What notes do when a tempo marker changes (charter-spec C4 item 3).</summary>
    public enum TempoEditMode
    {
        /// <summary>Notes keep their ticks, so they move in time with the grid.</summary>
        KeepTicks,

        /// <summary>Notes keep their times, so they're re-ticked to stay where they were in the audio.</summary>
        KeepTimes,
    }

    /// <summary>Chart Builder's settings, persisted to <c>builder-settings.json</c> (charter-spec §10).</summary>
    /// <remarks>Chart Packer's are in its own file, so neither app overwrites the other's.</remarks>
    public sealed class BuilderSettings
    {
        public const double DefaultEditorLeftPanelWidth = 300;
        public const double DefaultEditorRightPanelWidth = 280;
        public const double DefaultEditorTimelineHeight = 160;

        /// <summary>The narrowest a side panel can be dragged, and the least a saved width is restored as.</summary>
        public const double MinEditorPanelWidth = 180;

        /// <summary>The shortest the timeline can be dragged, and the least a saved height is restored as.</summary>
        public const double MinEditorTimelineHeight = 80;

        /// <summary>The most a saved panel width or timeline height is restored as: wider than an 8K screen.</summary>
        public const double MaxEditorPanelSize = 8192;

        /// <summary>Whether Chart Builder writes log files (<see cref="ChartPacker.UI.AppLog"/>).</summary>
        public bool LoggingEnabled { get; set; } = true;

        /// <summary>The name a new chart's <c>charter</c> is filled in with. Empty for none.</summary>
        public string DefaultCharterName { get; set; } = "";

        /// <summary>The resolution, in ticks per beat, a new chart starts on: one of <see cref="NewChartSettings.Resolutions"/>.</summary>
        public int DefaultChartResolution { get; set; } = NewChartSettings.DefaultResolution;

        /// <summary>Whether notes keep their ticks or their times when a tempo changes.</summary>
        public TempoEditMode TempoEdits { get; set; } = TempoEditMode.KeepTicks;

        /// <summary>The fastest and slowest the editor's highway can scroll, as the inspector's highway allows.</summary>
        public const double MinEditorHighwaySpeed = 0.5;

        public const double MaxEditorHighwaySpeed = 3;

        /// <summary>
        /// How many seconds a lane shows beyond its strike line at a highway speed of 1: the more, the slower the notes go
        /// by. Each lane's own look-ahead, once set, takes its place.
        /// </summary>
        public const double LookAheadAtSpeedOne = 2;

        /// <summary>The snap the editor starts on, as steps in a whole note: one of <see cref="SnapGrid.CommonDivisions"/>.</summary>
        public int DefaultSnapDivision { get; set; } = SnapGrid.Default.Division;

        /// <summary>The view a lane opens in: a vertical highway or a horizontal timeline.</summary>
        public LaneView DefaultEditorView { get; set; } = LaneView.Highway;

        /// <summary>How fast notes go by in a lane whose look-ahead hasn't been set: 1 shows <see cref="LookAheadAtSpeedOne"/> seconds beyond the strike line.</summary>
        public double EditorHighwaySpeed { get; set; } = 1;

        /// <summary>What a lane draws its audio as, until it's switched (<c>W</c>): the waveform or the spectrogram (charter-spec C12, C13).</summary>
        public LaneBackdrop DefaultLaneBackdrop { get; set; } = LaneBackdrop.Waveform;

        /// <summary>The least the spectrogram memory budget can be, in MB.</summary>
        public const int MinSpectrogramBudgetMb = 64;

        /// <summary>The most the spectrogram memory budget can be, in MB.</summary>
        public const int MaxSpectrogramBudgetMb = 4096;

        /// <summary>How much memory every stem's spectrogram may take together, in MB (charter-spec C13 item 3).</summary>
        public int SpectrogramBudgetMb { get; set; } = 256;

        /// <summary>The metronome's volume, in percent of full.</summary>
        public int MetronomeVolume { get; set; } = 50;

        /// <summary>The hit sounds' volume, in percent of full.</summary>
        public int HitSoundVolume { get; set; } = 50;

        /// <summary>The editor's left panel (Chart Inspector) width, while expanded.</summary>
        public double EditorLeftPanelWidth { get; set; } = DefaultEditorLeftPanelWidth;

        /// <summary>The editor's right panel (Properties) width, while expanded.</summary>
        public double EditorRightPanelWidth { get; set; } = DefaultEditorRightPanelWidth;

        /// <summary>The height of the timeline along the bottom of the editor.</summary>
        public double EditorTimelineHeight { get; set; } = DefaultEditorTimelineHeight;

        /// <summary>Whether the editor's left panel is collapsed to a strip along the window's edge.</summary>
        public bool EditorLeftPanelCollapsed { get; set; }

        /// <summary>Whether the editor's right panel is collapsed to a strip along the window's edge.</summary>
        public bool EditorRightPanelCollapsed { get; set; }

        /// <summary>The editor's lanes, where each is docked and how big. Null for the default layout.</summary>
        public LaneDockState? EditorLanes { get; set; }

        /// <summary>Whether the editor was maximized when it last closed, so the next one opens maximized.</summary>
        public bool EditorMaximized { get; set; }

        /// <summary>
        /// Where the middle of the launcher was, in screen pixels, when Chart Builder last closed (or of the editor, when
        /// it closed with the app). The launcher opens there if that point is still on a screen. Null for where the OS puts it.
        /// </summary>
        public ScreenPoint? LauncherCenter { get; set; }

        /// <summary>The songs opened lately, the latest first, as the home screen lists them (<see cref="ChartPacker.Core.Launching.RecentSongs"/>).</summary>
        public List<RecentSong> RecentSongs { get; set; } = [];

        /// <summary>The fewest minutes between autosaves.</summary>
        public const int MinAutosaveMinutes = 1;

        /// <summary>The most minutes between autosaves.</summary>
        public const int MaxAutosaveMinutes = 60;

        /// <summary>How many minutes apart the chart open is autosaved for recovery (charter-spec C15 item 6, §10).</summary>
        public int AutosaveMinutes { get; set; } = 2;

        /// <summary>The parent folder a new chart's first save last made its song folder in (charter-spec C1, §10). Null for none yet.</summary>
        public string? LastNewChartFolder { get; set; }

        /// <summary>Whether the first song played on an output device that has no calibrated latency offers the calibration tool (charter-spec C11).</summary>
        public bool PromptForCalibration { get; set; } = true;

        /// <summary>
        /// The output devices already offered the calibration tool, by ID, or by name where they have none, so each is
        /// offered it once.
        /// </summary>
        public List<string> CalibrationPrompted { get; set; } = [];

        /// <summary>
        /// The default rules Reduce starts from, per family and target difficulty (charter-spec C9 item 2, §10). The Reduce
        /// dialog's changes apply to one run; its Settings mode changes these.
        /// </summary>
        public ReductionDefaults ReductionRules { get; set; } = ReductionDefaults.Shipped;

        /// <summary>How many seconds ahead the highway preview shows (charter-spec C18), as its slider last left it.</summary>
        public double PreviewLookAhead { get; set; } = 1.4;
    }

    /// <summary>A point on the screens, in screen pixels.</summary>
    public sealed class ScreenPoint
    {
        public int X { get; set; }

        public int Y { get; set; }
    }
}
