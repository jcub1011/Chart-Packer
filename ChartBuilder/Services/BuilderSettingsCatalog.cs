using System;
using System.Globalization;
using System.Linq;
using ChartBuilder.Resources;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Authoring;
using ChartPacker.Core.Charting.Editing;
using ChartPacker.Core.Charting.Layout;
using ChartPacker.Core.Charting.Playtest;
using ChartPacker.Core.Settings;
using ChartPacker.Resources;
using ChartPacker.UI;

namespace ChartBuilder.Services
{
    /// <summary>
    /// Every setting Chart Builder's Settings window shows. To add a setting, add its definition (and its
    /// strings) here; to remove one, delete it.
    /// </summary>
    public static class BuilderSettingsCatalog
    {
        /// <param name="calibrate">Opens the calibration tool, which edits the audio latency (charter-spec C11).</param>
        /// <param name="editReductionRules">Opens the Reduce dialog's Settings mode, which edits the default rules (charter-spec C9).</param>
        /// <param name="openInputDevices">Opens the Input devices window, which edits the instrument bindings (charter-spec C19).</param>
        /// <param name="resetBindings">Puts every instrument profile's bindings back to the keyboard's defaults.</param>
        public static SettingsCatalog Create(BuilderSettingsService settings, Action calibrate, Action editReductionRules, Action openInputDevices, Action resetBindings)
        {
            var charter = new SettingCategory("charter", Strings.SettingsCategoryCharter);
            var newCharts = new SettingCategory("charter.newCharts", Strings.SettingsCategoryNewCharts, charter);
            var editing = new SettingCategory("charter.editing", Strings.SettingsCategoryEditing, charter);
            var audioAssist = new SettingCategory("charter.audioAssist", Strings.SettingsCategoryAudioAssist, charter);
            var audio = new SettingCategory("audio", Strings.SettingsCategoryAudio);
            var input = new SettingCategory("input", Strings.SettingsCategoryInput);
            var general = new SettingCategory("general", SharedStrings.SettingsCategoryGeneral);
            var logging = new SettingCategory("general.logging", SharedStrings.SettingsCategoryLogging, general);

            return new SettingsCatalog(
                [charter, newCharts, editing, audioAssist, audio, input, general, logging],
                [
                    // The bindings are per device and control, so they're set in the Input devices window (charter-spec C19 item 2).
                    new ActionSetting
                    {
                        Id = "input.devices",
                        Category = input,
                        Label = Strings.InputDevicesLabel,
                        Description = Strings.InputDevicesDescription,
                        Keywords = SettingKeywords.Split(Strings.InputDevicesKeywords),
                        Actions =
                        [
                            new SettingAction(Strings.InputDevicesButton, openInputDevices),
                            new SettingAction(Strings.InputResetBindingsButton, resetBindings),
                        ],
                    },
                    HitWindowSetting("input.playtestEarlyMs", input, Strings.PlaytestEarlyLabel, Strings.PlaytestEarlyDescription, Strings.PlaytestEarlyKeywords,
                        () => settings.Settings.PlaytestEarlyMs, ms => settings.Settings.PlaytestEarlyMs = ms),
                    HitWindowSetting("input.playtestLateMs", input, Strings.PlaytestLateLabel, Strings.PlaytestLateDescription, Strings.PlaytestLateKeywords,
                        () => settings.Settings.PlaytestLateMs, ms => settings.Settings.PlaytestLateMs = ms),
                    new ToggleSetting
                    {
                        Id = "input.cymbalsMustMatch",
                        Category = input,
                        Label = Strings.PlaytestCymbalsLabel,
                        Description = Strings.PlaytestCymbalsDescription,
                        Keywords = SettingKeywords.Split(Strings.PlaytestCymbalsKeywords),
                        DefaultValue = true,
                        Get = () => settings.Settings.PlaytestCymbalsMustMatch,
                        Set = on => settings.Settings.PlaytestCymbalsMustMatch = on,
                    },
                    new ToggleSetting
                    {
                        Id = "input.doubleKick",
                        Category = input,
                        Label = Strings.PlaytestDoubleKickLabel,
                        Description = Strings.PlaytestDoubleKickDescription,
                        Keywords = SettingKeywords.Split(Strings.PlaytestDoubleKickKeywords),
                        DefaultValue = true,
                        Get = () => settings.Settings.PlaytestDoubleKick,
                        Set = on => settings.Settings.PlaytestDoubleKick = on,
                    },
                    new ToggleSetting
                    {
                        Id = "input.overhitsBreakCombo",
                        Category = input,
                        Label = Strings.PlaytestOverhitsLabel,
                        Description = Strings.PlaytestOverhitsDescription,
                        Keywords = SettingKeywords.Split(Strings.PlaytestOverhitsKeywords),
                        DefaultValue = true,
                        Get = () => settings.Settings.PlaytestOverhitsBreakCombo,
                        Set = on => settings.Settings.PlaytestOverhitsBreakCombo = on,
                    },
                    new ToggleSetting
                    {
                        Id = "input.judgeDynamics",
                        Category = input,
                        Label = Strings.PlaytestDynamicsLabel,
                        Description = Strings.PlaytestDynamicsDescription,
                        Keywords = SettingKeywords.Split(Strings.PlaytestDynamicsKeywords),
                        DefaultValue = false,
                        Get = () => settings.Settings.PlaytestJudgeDynamics,
                        Set = on => settings.Settings.PlaytestJudgeDynamics = on,
                    },
                    new ChoiceSetting<GuitarRecordStyle>
                    {
                        Id = "input.guitarRecordStyle",
                        Category = input,
                        Label = Strings.GuitarRecordStyleLabel,
                        Description = Strings.GuitarRecordStyleDescription,
                        Keywords = SettingKeywords.Split(Strings.GuitarRecordStyleKeywords),
                        DefaultValue = GuitarRecordStyle.Strum,
                        Choices =
                        [
                            new SettingChoice<GuitarRecordStyle>(GuitarRecordStyle.Strum, Strings.GuitarRecordStyleStrum),
                            new SettingChoice<GuitarRecordStyle>(GuitarRecordStyle.Frets, Strings.GuitarRecordStyleFrets),
                        ],
                        Get = () => settings.Settings.GuitarRecordStyle,
                        Set = style => settings.Settings.GuitarRecordStyle = style,
                    },
                    new ToggleSetting
                    {
                        Id = "input.recordDynamics",
                        Category = input,
                        Label = Strings.RecordDynamicsLabel,
                        Description = Strings.RecordDynamicsDescription,
                        Keywords = SettingKeywords.Split(Strings.RecordDynamicsKeywords),
                        DefaultValue = false,
                        Get = () => settings.Settings.RecordDynamicsFromVelocity,
                        Set = on => settings.Settings.RecordDynamicsFromVelocity = on,
                    },
                    // The latency is per device, so it's edited in the calibration tool rather than here (charter-spec §10).
                    new ActionSetting
                    {
                        Id = "audio.latency",
                        Category = audio,
                        Label = Strings.AudioLatencyLabel,
                        Description = Strings.AudioLatencyDescription,
                        Keywords = SettingKeywords.Split(Strings.AudioLatencyKeywords),
                        Actions = [new SettingAction(Strings.CalibrateButton, calibrate)],
                    },
                    new ToggleSetting
                    {
                        Id = "audio.promptForCalibration",
                        Category = audio,
                        Label = Strings.PromptForCalibrationLabel,
                        Description = Strings.PromptForCalibrationDescription,
                        Keywords = SettingKeywords.Split(Strings.PromptForCalibrationKeywords),
                        DefaultValue = true,
                        Get = () => settings.Settings.PromptForCalibration,
                        Set = prompt => settings.Settings.PromptForCalibration = prompt,
                    },
                    new TextSetting
                    {
                        Id = "charter.defaultCharterName",
                        Category = newCharts,
                        Label = Strings.DefaultCharterNameLabel,
                        Description = Strings.DefaultCharterNameDescription,
                        Keywords = SettingKeywords.Split(Strings.DefaultCharterNameKeywords),
                        DefaultValue = "",
                        Placeholder = Strings.DefaultCharterNamePlaceholder,
                        ForbiddenCharacters = ";",
                        Get = () => settings.Settings.DefaultCharterName,
                        Set = name => settings.Settings.DefaultCharterName = name.Trim(),
                    },
                    new ChoiceSetting<int>
                    {
                        Id = "charter.defaultResolution",
                        Category = newCharts,
                        Label = Strings.DefaultChartResolutionLabel,
                        Description = Strings.DefaultChartResolutionDescription,
                        Keywords = SettingKeywords.Split(Strings.DefaultChartResolutionKeywords),
                        DefaultValue = NewChartSettings.DefaultResolution,
                        Choices = [.. NewChartSettings.Resolutions.Select(r => new SettingChoice<int>(r, ResolutionLabel(r)))],
                        Get = () => settings.Settings.DefaultChartResolution,
                        Set = resolution => settings.Settings.DefaultChartResolution = resolution,
                    },
                    new ChoiceSetting<TempoEditMode>
                    {
                        Id = "charter.tempoEdits",
                        Category = editing,
                        Label = Strings.TempoEditsLabel,
                        Description = Strings.TempoEditsDescription,
                        Keywords = SettingKeywords.Split(Strings.TempoEditsKeywords),
                        DefaultValue = TempoEditMode.KeepTicks,
                        Choices =
                        [
                            new SettingChoice<TempoEditMode>(TempoEditMode.KeepTicks, Strings.TempoEditsKeepTicks),
                            new SettingChoice<TempoEditMode>(TempoEditMode.KeepTimes, Strings.TempoEditsKeepTimes),
                        ],
                        Get = () => settings.Settings.TempoEdits,
                        Set = mode => settings.Settings.TempoEdits = mode,
                    },
                    // The rules are per family and difficulty, so they're edited in the Reduce dialog (charter-spec §10).
                    new ActionSetting
                    {
                        Id = "charter.reductionRules",
                        Category = editing,
                        Label = Strings.ReductionRulesLabel,
                        Description = Strings.ReductionRulesDescription,
                        Keywords = SettingKeywords.Split(Strings.ReductionRulesKeywords),
                        Actions =
                        [
                            new SettingAction(Strings.ReductionRulesEditButton, editReductionRules),
                            new SettingAction(Strings.ReductionRulesResetButton, () =>
                            {
                                settings.Settings.ReductionRules = ReductionDefaults.Shipped;
                                settings.Save();
                            }),
                        ],
                    },
                    new ChoiceSetting<int>
                    {
                        Id = "charter.defaultSnap",
                        Category = editing,
                        Label = Strings.DefaultSnapLabel,
                        Description = Strings.DefaultSnapDescription,
                        Keywords = SettingKeywords.Split(Strings.DefaultSnapKeywords),
                        DefaultValue = SnapGrid.Default.Division,
                        Choices = [.. SnapGrid.CommonDivisions.Select(d => new SettingChoice<int>(d, SnapLabel(d)))],
                        Get = () => settings.Settings.DefaultSnapDivision,
                        Set = division => settings.Settings.DefaultSnapDivision = division,
                    },
                    new ChoiceSetting<LaneView>
                    {
                        Id = "charter.defaultView",
                        Category = editing,
                        Label = Strings.DefaultEditorViewLabel,
                        Description = Strings.DefaultEditorViewDescription,
                        Keywords = SettingKeywords.Split(Strings.DefaultEditorViewKeywords),
                        DefaultValue = LaneView.Highway,
                        Choices =
                        [
                            new SettingChoice<LaneView>(LaneView.Highway, Strings.EditorViewHighway),
                            new SettingChoice<LaneView>(LaneView.Timeline, Strings.EditorViewTimeline),
                        ],
                        Get = () => settings.Settings.DefaultEditorView,
                        Set = view => settings.Settings.DefaultEditorView = view,
                    },
                    new IntegerSetting
                    {
                        Id = "charter.autosaveMinutes",
                        Category = editing,
                        Label = Strings.AutosaveMinutesLabel,
                        Description = Strings.AutosaveMinutesDescription,
                        Keywords = SettingKeywords.Split(Strings.AutosaveMinutesKeywords),
                        DefaultValue = 2,
                        Minimum = BuilderSettings.MinAutosaveMinutes,
                        Maximum = BuilderSettings.MaxAutosaveMinutes,
                        Get = () => settings.Settings.AutosaveMinutes,
                        Set = minutes => settings.Settings.AutosaveMinutes = minutes,
                    },
                    new FolderSetting
                    {
                        Id = "charter.packFolder",
                        Category = editing,
                        Label = Strings.PackFolderLabel,
                        Description = Strings.PackFolderDescription,
                        Keywords = SettingKeywords.Split(Strings.PackFolderKeywords),
                        DefaultValue = null,
                        EmptyLabel = Strings.PackFolderEmpty,
                        PickerTitle = Strings.SaveAndPackPickerTitle,
                        Get = () => settings.Settings.DefaultPackFolder,
                        Set = folder => settings.Settings.DefaultPackFolder = folder,
                    },
                    new RangeSetting
                    {
                        Id = "charter.highwaySpeed",
                        Category = editing,
                        Label = Strings.EditorHighwaySpeedLabel,
                        Description = Strings.EditorHighwaySpeedDescription,
                        Keywords = SettingKeywords.Split(Strings.EditorHighwaySpeedKeywords),
                        DefaultValue = 1,
                        Minimum = BuilderSettings.MinEditorHighwaySpeed,
                        Maximum = BuilderSettings.MaxEditorHighwaySpeed,
                        SmallChange = 0.1,
                        Format = speed => speed.ToString("0.0x", CultureInfo.CurrentCulture),
                        Get = () => settings.Settings.EditorHighwaySpeed,
                        Set = speed => settings.Settings.EditorHighwaySpeed = speed,
                    },
                    new ChoiceSetting<LaneBackdrop>
                    {
                        Id = "charter.laneBackdrop",
                        Category = audioAssist,
                        Label = Strings.LaneBackdropLabel,
                        Description = Strings.LaneBackdropDescription,
                        Keywords = SettingKeywords.Split(Strings.LaneBackdropKeywords),
                        DefaultValue = LaneBackdrop.Waveform,
                        Choices =
                        [
                            new SettingChoice<LaneBackdrop>(LaneBackdrop.Waveform, Strings.LaneShowWaveform),
                            new SettingChoice<LaneBackdrop>(LaneBackdrop.Spectrogram, Strings.LaneShowSpectrogram),
                        ],
                        Get = () => settings.Settings.DefaultLaneBackdrop,
                        Set = backdrop => settings.Settings.DefaultLaneBackdrop = backdrop,
                    },
                    new IntegerSetting
                    {
                        Id = "charter.spectrogramBudget",
                        Category = audioAssist,
                        Label = Strings.SpectrogramBudgetLabel,
                        Description = Strings.SpectrogramBudgetDescription,
                        Keywords = SettingKeywords.Split(Strings.SpectrogramBudgetKeywords),
                        DefaultValue = 256,
                        Minimum = BuilderSettings.MinSpectrogramBudgetMb,
                        Maximum = BuilderSettings.MaxSpectrogramBudgetMb,
                        Get = () => settings.Settings.SpectrogramBudgetMb,
                        Set = megabytes => settings.Settings.SpectrogramBudgetMb = megabytes,
                    },
                    Volume("audio.metronomeVolume", audio, Strings.MetronomeVolumeLabel, Strings.MetronomeVolumeDescription, Strings.MetronomeVolumeKeywords,
                        () => settings.Settings.MetronomeVolume, volume => settings.Settings.MetronomeVolume = volume),
                    Volume("audio.hitSoundVolume", audio, Strings.HitSoundVolumeLabel, Strings.HitSoundVolumeDescription, Strings.HitSoundVolumeKeywords,
                        () => settings.Settings.HitSoundVolume, volume => settings.Settings.HitSoundVolume = volume),
                    AppLog.CreateSetting(logging, () => settings.Settings.LoggingEnabled, enabled => settings.Settings.LoggingEnabled = enabled),
                    new ChoiceSetting<StartupAction>
                    {
                        Id = "general.startupAction",
                        Category = general,
                        Label = Strings.StartupActionLabel,
                        Description = Strings.StartupActionDescription,
                        Keywords = SettingKeywords.Split(Strings.StartupActionKeywords),
                        DefaultValue = StartupAction.BlankChart,
                        Choices =
                        [
                            new SettingChoice<StartupAction>(StartupAction.BlankChart, Strings.StartupBlankChart),
                            new SettingChoice<StartupAction>(StartupAction.ReopenRecent, Strings.StartupReopenRecent),
                        ],
                        Get = () => settings.Settings.StartupAction,
                        Set = action => settings.Settings.StartupAction = action,
                    },
                ]);
        }

        /// <summary>A snap as it's shown, e.g. "1/16".</summary>
        public static string SnapLabel(int division) =>
            string.Format(CultureInfo.CurrentCulture, Strings.SnapFormat, division);

        /// <summary>One side of the playtest's hit window, in milliseconds (charter-spec C19).</summary>
        private static IntegerSetting HitWindowSetting(string id, SettingCategory category, string label, string description, string keywords, Func<int> get, Action<int> set) => new()
        {
            Id = id,
            Category = category,
            Label = label,
            Description = description,
            Keywords = SettingKeywords.Split(keywords),
            DefaultValue = (int)HitWindow.DefaultMs,
            Minimum = (int)HitWindow.MinMs,
            Maximum = (int)HitWindow.MaxMs,
            Get = get,
            Set = set,
        };

        /// <summary>A volume from 0 to 100%, edited with a slider.</summary>
        private static RangeSetting Volume(string id, SettingCategory category, string label, string description, string keywords, Func<int> get, Action<int> set) => new()
        {
            Id = id,
            Category = category,
            Label = label,
            Description = description,
            Keywords = SettingKeywords.Split(keywords),
            DefaultValue = 50,
            Minimum = 0,
            Maximum = 100,
            SmallChange = 5,
            Format = volume => string.Format(CultureInfo.CurrentCulture, Strings.SpeedPercentFormat, (int)Math.Round(volume)),
            Get = () => get(),
            Set = volume => set((int)Math.Round(volume)),
        };

        /// <summary>A resolution as Settings shows it, e.g. "480 ticks per beat".</summary>
        public static string ResolutionLabel(int resolution) =>
            string.Format(CultureInfo.CurrentCulture, Strings.ChartResolutionFormat, resolution);
    }
}
