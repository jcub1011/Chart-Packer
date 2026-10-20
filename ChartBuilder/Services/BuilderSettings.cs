using System.Collections.Generic;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charting.Layout;
using ChartShared.Core.Charting.Playtest;
using ChartShared.Core.Jobs;
using ChartShared.Core.Launching;
using ChartShared.UI.Services;

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

    /// <summary>Which way the mouse wheel moves through the song, along the lanes in either view and the timeline alike.</summary>
    public enum WheelScrollDirection
    {
        /// <summary>The wheel turned up is later, as up the highway.</summary>
        UpIsLater,

        /// <summary>The wheel turned down is later.</summary>
        DownIsLater,
    }

    /// <summary>What Chart Builder opens with when it starts with nothing on its command line.</summary>
    public enum StartupAction
    {
        /// <summary>A blank new chart.</summary>
        BlankChart,

        /// <summary>The song opened most recently.</summary>
        ReopenRecent,
    }

    /// <summary>Chart Builder's settings, persisted to <c>builder-settings.json</c> (charter-spec §10).</summary>
    /// <remarks>Chart Packer's are in its own file, so neither app overwrites the other's.</remarks>
    public sealed class BuilderSettings : ISettingsWindowSize
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

        /// <summary>Whether Chart Builder writes log files (<see cref="ChartShared.UI.AppLog"/>).</summary>
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

        /// <summary>Which way the mouse wheel moves the cursor, in both lane views and on the timeline along the bottom.</summary>
        public WheelScrollDirection WheelScrollDirection { get; set; } = WheelScrollDirection.UpIsLater;

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

        /// <summary>Whether the editor was maximized or fullscreen when it last closed, so the next one opens that way.</summary>
        /// <remarks>Kept for files saved before fullscreen was told apart from maximized; use with <see cref="EditorFullscreen"/>.</remarks>
        public bool EditorMaximized { get; set; }

        /// <summary>Whether the editor was fullscreen when it last closed, so the next one opens fullscreen.</summary>
        public bool EditorFullscreen { get; set; }

        /// <summary>The editor's normal bounds when it last closed, in screen pixels (position) and device-independent pixels (size). Null for none yet.</summary>
        public int? EditorX { get; set; }

        public int? EditorY { get; set; }

        public double? EditorWidth { get; set; }

        public double? EditorHeight { get; set; }

        /// <summary>The Settings window's normal size when it last closed, in device-independent pixels. Null for none yet.</summary>
        public double? SettingsWindowWidth { get; set; }

        public double? SettingsWindowHeight { get; set; }

        /// <summary>What Chart Builder opens with when it starts with nothing on its command line.</summary>
        public StartupAction StartupAction { get; set; } = StartupAction.BlankChart;

        /// <summary>The songs opened lately, the latest first, as the File menu's Open recent lists them (<see cref="ChartShared.Core.Launching.RecentSongs"/>).</summary>
        public List<RecentSong> RecentSongs { get; set; } = [];

        /// <summary>The fewest minutes between autosaves.</summary>
        public const int MinAutosaveMinutes = 1;

        /// <summary>The most minutes between autosaves.</summary>
        public const int MaxAutosaveMinutes = 60;

        /// <summary>How many minutes apart the chart open is autosaved for recovery (charter-spec C15 item 6, §10).</summary>
        public int AutosaveMinutes { get; set; } = 2;

        /// <summary>The parent folder a new chart's first save last made its song folder in (charter-spec C1, §10). Null for none yet.</summary>
        public string? LastNewChartFolder { get; set; }

        /// <summary>Where Save and pack writes the .sng. Null for asking every time, starting at the song's parent folder.</summary>
        public string? DefaultPackFolder { get; set; }

        /// <summary>The export dialog's location the last time it was confirmed. Null for none yet.</summary>
        public string? LastExportFolder { get; set; }

        /// <summary>The export dialog's if-exists choice the last time it was confirmed. Null for Chart Packer's policy.</summary>
        public OverwritePolicy? LastExportPolicy { get; set; }

        /// <summary>Whether the export dialog's auto-transcode to Opus was on the last time it was confirmed. Off by default.</summary>
        public bool LastTranscodeToOpus { get; set; }

        /// <summary>The export dialog's Opus quality the last time it was confirmed, in kbps per stereo stem.</summary>
        public int LastTranscodeBitrateKbps { get; set; } = 128;

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

        /// <summary>How long before a note a playtest takes a hit for it, in real milliseconds (charter-spec C19).</summary>
        public int PlaytestEarlyMs { get; set; } = (int)HitWindow.DefaultMs;

        /// <summary>How long after a note a playtest takes a hit for it, in real milliseconds (charter-spec C19).</summary>
        public int PlaytestLateMs { get; set; } = (int)HitWindow.DefaultMs;

        /// <summary>Whether a Pro drums cymbal note needs a cymbal, and a tom note a tom, for the colours the kit splits.</summary>
        public bool PlaytestCymbalsMustMatch { get; set; } = true;

        /// <summary>Whether a playtest judges Expert's 2× kick notes.</summary>
        public bool PlaytestDoubleKick { get; set; } = true;

        /// <summary>Whether a drum hit with no note to hit breaks the combo.</summary>
        public bool PlaytestOverhitsBreakCombo { get; set; } = true;

        /// <summary>Whether a playtest counts accents hit softly and ghosts hit hard.</summary>
        public bool PlaytestJudgeDynamics { get; set; }

        /// <summary>How a guitar controller records (charter-spec C19 item 7).</summary>
        public GuitarRecordStyle GuitarRecordStyle { get; set; } = GuitarRecordStyle.Strum;

        /// <summary>Whether a MIDI kit's hard hits record as accents and its soft ones as ghosts.</summary>
        public bool RecordDynamicsFromVelocity { get; set; }

        /// <summary>Whether record mode puts what's played on the quantize grid as it's recorded. Off: it's kept as played.</summary>
        public bool RecordQuantize { get; set; }

        /// <summary>
        /// The grid Quantize puts notes on, and record mode with it on, as steps in a whole note: one of
        /// <see cref="SnapGrid.CommonDivisions"/>, the last one picked.
        /// </summary>
        public int QuantizeDivision { get; set; } = SnapGrid.Default.Division;
    }
}
