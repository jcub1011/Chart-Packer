using System.Collections.Generic;
using System.Text.Json.Serialization;
using ChartShared.Audio.Waveforms;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charting.Layout;
using ChartShared.Core.Charting.Playtest;
using ChartShared.Core.Jobs;
using ChartShared.Core.Launching;
using ChartShared.UI.Services;
using ChartShared.UI.ViewModels;

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

    /// <summary>The Reduce dialog's preview's settings, as Chart Builder keeps them in <see cref="BuilderSettings"/>.</summary>
    public sealed class BuilderReductionPreviewSettings(BuilderSettings settings) : IReductionPreviewSettings
    {
        public bool IsShown
        {
            get => settings.ReducePreviewShown;
            set => settings.ReducePreviewShown = value;
        }

        public double LookAhead
        {
            get => settings.ReducePreviewLookAhead;
            set => settings.ReducePreviewLookAhead = value;
        }

        public ReductionChangeCategories Categories
        {
            get => settings.ReducePreviewCategories;
            set => settings.ReducePreviewCategories = value;
        }
    }

    /// <summary>Chart Builder's settings, persisted to <c>builder-settings.json</c> (charter-spec §10).</summary>
    /// <remarks>Chart Packer's are in its own file, so neither app overwrites the other's.</remarks>
    public sealed class BuilderSettings : IRememberedWindows
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

        /// <summary>
        /// How finely each track's waveform is read. Finer detail shows note starts more sharply
        /// in the lanes but takes longer to read and more memory.
        /// </summary>
        public const WaveformFidelity DefaultWaveformFidelity = WaveformFidelity.High;

        /// <summary>What a lane draws its audio as, until it's switched (<c>W</c>): the waveform or the spectrogram (charter-spec C12, C13).</summary>
        public LaneBackdrop DefaultLaneBackdrop { get; set; } = LaneBackdrop.Waveform;

        /// <summary>How finely each track's waveform is read.</summary>
        [JsonConverter(typeof(JsonStringEnumConverter<WaveformFidelity>))]
        public WaveformFidelity WaveformFidelity { get; set; } = DefaultWaveformFidelity;

        /// <summary>The least the spectrogram memory budget can be, in MB.</summary>
        public const int MinSpectrogramBudgetMb = 64;

        /// <summary>The most the spectrogram memory budget can be, in MB.</summary>
        public const int MaxSpectrogramBudgetMb = 4096;

        /// <summary>How much memory every stem's spectrogram may take together, in MB (charter-spec C13 item 3).</summary>
        public int SpectrogramBudgetMb { get; set; } = 256;

        /// <summary>The master volume of fresh settings: full.</summary>
        public const double DefaultMasterVolume = 1;

        /// <summary>The master volume, from silence to full.</summary>
        public double MasterVolume { get; set; } = DefaultMasterVolume;

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

        /// <summary>Whether the editor's left panel was collapsed to a strip, before panels could auto-hide.</summary>
        /// <remarks>Only read, for <see cref="EditorLeftPanelAutoHide"/> in files saved before it.</remarks>
        public bool EditorLeftPanelCollapsed { get; set; }

        /// <summary>Whether the editor's right panel was collapsed to a strip, before panels could auto-hide.</summary>
        /// <remarks>Only read, for <see cref="EditorRightPanelAutoHide"/> in files saved before it.</remarks>
        public bool EditorRightPanelCollapsed { get; set; }

        /// <summary>
        /// Whether the editor's left panel auto-hides: a strip along the window's edge that it flies out from, over the chart,
        /// rather than pinned beside it. Null in files saved before it, which <see cref="EditorLeftPanelCollapsed"/> stands in for.
        /// </summary>
        public bool? EditorLeftPanelAutoHide { get; set; }

        /// <summary>Whether the editor's right panel auto-hides, as <see cref="EditorLeftPanelAutoHide"/>.</summary>
        public bool? EditorRightPanelAutoHide { get; set; }

        /// <summary>The order of the left panel's tabs, by key, as the user dragged them. Null for the default order.</summary>
        public List<string>? EditorLeftTabs { get; set; }

        /// <summary>The order of the right panel's tabs, by key. Null for the default order.</summary>
        public List<string>? EditorRightTabs { get; set; }

        /// <summary>The order of each side-panel tab's sections, by the tab's key and then the sections'. Null for the defaults.</summary>
        public Dictionary<string, List<string>>? EditorSectionOrder { get; set; }

        /// <summary>The side-panel sections that are collapsed, each as <c>tab/section</c>. Null for none.</summary>
        public List<string>? EditorCollapsedSections { get; set; }

        /// <summary>The editor's lanes, where each is docked and how big. Null for the default layout.</summary>
        public LaneDockState? EditorLanes { get; set; }

        /// <summary>How each window was shown when it last closed, by its type's name (<see cref="ChartShared.UI.Views.WindowMemory"/>). Null for none yet.</summary>
        public Dictionary<string, SavedWindow>? Windows { get; set; }

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

        /// <summary>Where Pack writes the .sng. Null for asking every time, starting at the song's parent folder.</summary>
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
        /// The default rules Reduce started from before both apps shared them in <c>reduction-rules.json</c>
        /// (<see cref="BuilderSettingsService.ReductionRules"/>), which reads them from here until it's first saved. Never
        /// written now; kept so a file an older Chart Builder saved still has them.
        /// </summary>
        public ReductionDefaults? ReductionRules { get; set; }

        /// <summary>The look-ahead the highway preview starts with, in seconds.</summary>
        public const double DefaultPreviewLookAhead = HighwayLookAhead.Default;

        /// <summary>How many seconds ahead the highway preview shows (charter-spec C18), as its slider last left it.</summary>
        public double PreviewLookAhead { get; set; } = DefaultPreviewLookAhead;

        /// <summary>The look-ahead the Generate fills and star power dialog starts with, in seconds.</summary>
        public const double DefaultGeneratePhrasesLookAhead = 4;

        /// <summary>How many seconds ahead the Generate fills and star power dialog's highway shows, as its slider last left it.</summary>
        public double GeneratePhrasesLookAhead { get; set; } = DefaultGeneratePhrasesLookAhead;

        /// <summary>Whether the Reduce dialog shows its preview, as it was last left.</summary>
        public bool ReducePreviewShown { get; set; }

        /// <summary>
        /// How many seconds ahead the Reduce dialog's preview shows, as its slider last left it: its own, as it goes further
        /// than the highway preview's, to see a stretch of changes at once.
        /// </summary>
        public double ReducePreviewLookAhead { get; set; } = ReductionPreviewViewModel.DefaultLookAhead;

        /// <summary>
        /// Which kinds of change the Reduce dialog's preview marks and stops at, as its filters were last left: all but the
        /// removals at first, as a reduction is mostly those.
        /// </summary>
        public ReductionChangeCategories ReducePreviewCategories { get; set; } = ReductionChangeCategories.Default;

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
        public int QuantizeDivision { get; set; } = DefaultQuantizeDivision;

        /// <summary>1/32, the quantize grid before one is picked: finer than the snap, so a quick part keeps its notes apart.</summary>
        public const int DefaultQuantizeDivision = 32;
    }
}
