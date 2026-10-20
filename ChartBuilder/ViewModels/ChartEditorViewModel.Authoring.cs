using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using ChartBuilder.Resources;
using ChartBuilder.Services;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charting.Layout;
using ChartShared.Core.Charts;
using ChartShared.Core.Input;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>How record mode puts a pass in, as the transport bar offers it.</summary>
    public sealed record RecordModeChoice(RecordMode Mode, string Label);

    /// <summary>The authoring tools (charter-spec C9, C15): Copy difficulty, Reduce, the clipboard, the transforms and record mode.</summary>
    public sealed partial class ChartEditorViewModel
    {
        // Record mode's message, which each pass updates.
        private const string RecordMessageKey = "record";

        // The instrument devices, started the first time the song plays, and where what they play goes in a pass of record
        // mode.
        private InputPlayer? _devices;
        private RecordInput? _recordSink;

        // How many dialogs have the playback lent to them (BorrowPlayback), and whether it looped before the first.
        private int _playbackBorrows;
        private bool _loopedBeforeBorrow;

        /// <summary>The ways record mode can put a pass in: merged with what's there, or over it.</summary>
        public IReadOnlyList<RecordModeChoice> RecordModeChoices { get; } =
        [
            new RecordModeChoice(RecordMode.Merge, Strings.RecordModeMerge),
            new RecordModeChoice(RecordMode.Overwrite, Strings.RecordModeOverwrite),
        ];

        /// <summary>How the next pass of record mode goes in; kept while the editor is open.</summary>
        [ObservableProperty]
        public partial RecordModeChoice? SelectedRecordMode { get; set; }

        /// <summary>
        /// Whether record mode puts what's played on the quantize grid (<see cref="SelectedQuantize"/>) as it's recorded,
        /// from the next press on; off, it's kept as played. Remembered from the last time it was set.
        /// </summary>
        [ObservableProperty]
        public partial bool IsRecordQuantized { get; set; }

        /// <summary>
        /// The quantize grid: what record mode quantizes to, and what Quantize in the note menu puts notes on. Remembered from
        /// the last one picked.
        /// </summary>
        [ObservableProperty]
        public partial SnapChoice? SelectedQuantize { get; set; }

        /// <summary>The quantize grid, as <see cref="SelectedQuantize"/> picks it.</summary>
        public SnapGrid QuantizeGrid => SnapGrid.Of(SelectedQuantize?.Division ?? SnapGrid.Default.Division);

        partial void OnIsRecordQuantizedChanged(bool value)
        {
            if (Session.Recording is { } pass)
                pass.Quantize = value ? QuantizeGrid : null;
            if (_settings.Settings.RecordQuantize == value)
                return;
            _settings.Settings.RecordQuantize = value;
            _settings.Save();
        }

        partial void OnSelectedQuantizeChanged(SnapChoice? value)
        {
            if (value is null)
                return;
            if (Session.Recording is { Quantize: not null } pass)
                pass.Quantize = QuantizeGrid;
            if (_settings.Settings.QuantizeDivision == value.Division)
                return;
            _settings.Settings.QuantizeDivision = value.Division;
            _settings.Save();
        }

        /// <summary>
        /// Puts the selected notes, and their sustains' ends, on the quantize grid, chord by chord, as one step (the note
        /// menu's Quantize); chords that would land on notes that aren't selected stay.
        /// </summary>
        public void QuantizeSelection()
        {
            var grid = QuantizeGrid;
            if (Session.Tools.QuantizeSelection(grid) is not { } plan)
                return;
            string label = BuilderSettingsCatalog.SnapLabel(grid.Division);
            if (plan.Blocked.Count > 0)
                Messages.Info(string.Format(CultureInfo.CurrentCulture, Strings.QuantizedBlockedFormat, plan.Moved, label, plan.Blocked.Count));
            else if (plan.Command is null)
                Messages.Info(string.Format(CultureInfo.CurrentCulture, Strings.QuantizeNothingFormat, label));
            else
                Messages.Info(string.Format(CultureInfo.CurrentCulture, Strings.QuantizedFormat, plan.Moved, label));
        }

        /// <summary>
        /// Whether record mode is on (charter-spec C15 item 5): playing the song records a pass into the focused lane. It and
        /// playtest (C19 item 4) are never on together; with both off, playing just plays.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(RecordToolTip))]
        public partial bool IsRecordArmed { get; private set; }

        /// <summary>Whether a pass of record mode is going on.</summary>
        public bool IsRecording => Session.Recording is not null;

        public string RecordToolTip => IsRecordArmed ? Strings.RecordStopToolTip : Strings.RecordStartToolTip;

        /// <summary>
        /// Turns record mode on or off (R), and playtest off. Turned on while the song plays, a pass starts there and then;
        /// turned off, the pass going on ends, added as one step.
        /// </summary>
        [RelayCommand]
        public void ToggleRecord()
        {
            if (IsRecordArmed)
            {
                IsRecordArmed = false;
                FinishRecording();
                Messages.Info(Strings.RecordOff, RecordMessageKey);
                return;
            }

            if (Session.Focused is not { } key || Session.TrackOf(key) is null)
            {
                Session.Status = Strings.RecordNoLane;
                return;
            }

            Playtest.Disarm();
            IsRecordArmed = true;
            if (Playback.IsPlaying)
            {
                StartRecordingPass();
                return;
            }

            Messages.Info(string.Format(
                CultureInfo.CurrentCulture, Strings.RecordArmedFormat, ChartLabels.InstrumentName(key.Instrument), ChartLabels.DifficultyName(key.Difficulty)),
                RecordMessageKey);
        }

        /// <summary>
        /// Turns playtest on or off (P), and record mode off (charter-spec C19 item 4). Turned on while the song plays, the
        /// focused lane is playtested from there; turned off, it's not, and what it found stays until it's cleared.
        /// </summary>
        [RelayCommand]
        public void TogglePlaytest()
        {
            if (Playtest.IsArmed)
            {
                Playtest.Disarm();
                Messages.Info(Strings.PlaytestOff, RecordMessageKey);
                return;
            }

            if (IsRecordArmed)
            {
                IsRecordArmed = false;
                FinishRecording();
            }

            Playtest.IsArmed = true;
            if (Playback.IsPlaying)
                Playtest.Begin();
            Messages.Info(Session.Focused is { } key
                ? string.Format(CultureInfo.CurrentCulture, Strings.PlaytestOnFormat, ChartLabels.InstrumentName(key.Instrument), ChartLabels.DifficultyName(key.Difficulty))
                : Strings.PlaytestOnNoLane, RecordMessageKey);
        }

        /// <summary>
        /// Lends the song's playback to a dialog over the Editor, as the Reduce dialog's preview has it: until it's given back,
        /// playing starts no pass of record mode or playtest, which would change the chart under the dialog, the loop is off
        /// and the hit sounds are quiet. Given back, it's paused and they're as they were.
        /// </summary>
        public IDisposable BorrowPlayback()
        {
            if (_playbackBorrows++ == 0)
            {
                Playback.Pause();
                _loopedBeforeBorrow = Playback.IsLooping;
                Playback.IsLooping = false;
                Playback.SetHits([]);
            }

            return new PlaybackBorrow(this);
        }

        private void GiveBackPlayback()
        {
            if (--_playbackBorrows > 0)
                return;
            Playback.Pause();
            Playback.IsLooping = _loopedBeforeBorrow;
            ScheduleRefresh();
        }

        /// <summary>The song started playing: a pass of record mode, or a playtest of the focused lane, if either is on.</summary>
        private void StartPlaying()
        {
            if (_playbackBorrows > 0)
                return;
            if (IsRecordArmed)
                StartRecordingPass();
            else if (Playtest.IsArmed && !Playtest.Begin())
                Session.Status = Strings.PlaytestNotPlayable;
        }

        /// <summary>The song stopped: the pass going on ends, added as one step, and so does the playtest.</summary>
        private void StopPlaying()
        {
            if (IsRecording)
                FinishRecording();
            Playtest.End();
        }

        /// <summary>
        /// Starts a pass of record mode into the focused lane from where the song is, over the selection's range when there's
        /// one. The instrument devices record into it too (charter-spec C19 item 7), each press timed by its own event; the
        /// keyboard records by the number keys, as it always has.
        /// </summary>
        private void StartRecordingPass()
        {
            if (Session.Recording is not null)
                return;
            Playtest.End();
            if (Session.Focused is not { } key || Session.TrackOf(key) is null)
            {
                Session.Status = Strings.RecordNoLane;
                return;
            }

            var mode = SelectedRecordMode?.Mode ?? RecordMode.Merge;
            var range = Session.Selection.Range is { } r && r.To > r.From ? r : ((long, long)?)null;
            var pass = Session.StartRecording(key, mode, Playback.Clock().TotalSeconds, range, IsRecordQuantized ? QuantizeGrid : null);
            if (InstrumentActions.ProfileOf(ChartInstruments.KindOf(key.Instrument)) is { } profile && Devices() is { } devices)
            {
                var settings = _settings.Settings;
                _recordSink = new RecordInput(Session, pass, settings.GuitarRecordStyle, settings.RecordDynamicsFromVelocity);
                devices.Profile = profile;
                devices.PlaysKeys = false;
            }

            Messages.Info(string.Format(
                CultureInfo.CurrentCulture,
                mode == RecordMode.Overwrite ? Strings.RecordingOverwriteFormat : Strings.RecordingMergeFormat,
                ChartLabels.InstrumentName(key.Instrument),
                ChartLabels.DifficultyName(key.Difficulty)),
                RecordMessageKey);
        }

        /// <summary>A key pressed for the lane drawn in <paramref name="shownLane"/>, in record mode, at the moment it's handled.</summary>
        public void RecordPress(int shownLane, bool doubleKick = false) =>
            Session.Recording?.Press(shownLane, Playback.Clock().TotalSeconds, doubleKick);

        /// <summary>The key for the lane drawn in <paramref name="shownLane"/> let go, in record mode.</summary>
        public void RecordRelease(int shownLane, bool doubleKick = false) =>
            Session.Recording?.Release(shownLane, Playback.Clock().TotalSeconds, doubleKick);

        /// <summary>Lets go of every key held in record mode, as when the window loses focus.</summary>
        public void RecordReleaseAll() => Session.Recording?.ReleaseAll(Playback.Clock().TotalSeconds);

        /// <summary>The instrument devices, started the first time they're asked for; null where there are none.</summary>
        private InputPlayer? Devices() => _devices ??= _input is null ? null : new InputPlayer(_input, Playback, new DeviceSink(this));

        /// <summary>Stops the devices recording, letting go of what they held first, so a held fret ends its sustain.</summary>
        private void StopRecordInput()
        {
            if (_recordSink is { } sink)
            {
                _devices?.ReleaseKeys();
                sink.ReleaseAll(Playback.Clock().TotalSeconds);
            }

            _recordSink = null;
        }

        /// <summary>Where what the devices play goes: the pass of record mode going on, else the playtest.</summary>
        private sealed class DeviceSink(ChartEditorViewModel editor) : IActionSink
        {
            public void Handle(in TimedAction action)
            {
                if (editor._recordSink is { } record)
                    record.Handle(action);
                else
                    editor.Playtest.Handle(action);
            }
        }

        /// <summary>Ends the pass of record mode going on, adding what it recorded as one step.</summary>
        public void FinishRecording()
        {
            if (Session.Recording is not { } pass)
                return;
            StopRecordInput();
            int count = pass.Count;
            bool added = pass.Finish(Playback.Clock().TotalSeconds);
            Messages.Info(added
                ? string.Format(CultureInfo.CurrentCulture, Strings.RecordedFormat, count)
                : Strings.RecordedNothing, RecordMessageKey);
        }

        /// <summary>Ends the pass of record mode going on, adding nothing (Esc).</summary>
        public void CancelRecording()
        {
            if (Session.Recording is not { } pass)
                return;
            StopRecordInput();
            pass.Cancel();

            // Record mode goes off, so the rest of the song just plays rather than recording nothing.
            IsRecordArmed = false;
            Messages.Info(Strings.RecordCancelled, RecordMessageKey);
        }

        /// <summary>
        /// Copies <paramref name="from"/> to <paramref name="to"/> of the same part (charter-spec C9 item 1), in place of
        /// what it has, asking first when it has notes. One undoable step.
        /// </summary>
        public async Task CopyDifficultyAsync(ChartTrackKey from, ChartDifficulty to)
        {
            if (from.Difficulty == to || Session.Chart.PartOf(from.Instrument) is not { } part)
                return;
            string source = ChartLabels.DifficultyName(from.Difficulty);
            string target = ChartLabels.DifficultyName(to);
            if (part[to].Notes.Count > 0 && !await _confirmations.ConfirmAsync(
                    Strings.CopyDifficultyTitle,
                    string.Format(CultureInfo.CurrentCulture, Strings.CopyDifficultyConfirmFormat, target, part[to].Notes.Count, source),
                    Strings.CopyDifficultyConfirmButton))
                return;

            Session.Tools.Cancel();
            var copy = new CopyDifficulty(from.Instrument, from.Difficulty, to);
            if (!Session.Execute(copy))
                return;
            Messages.Info(copy.DroppedDoubleKicks > 0
                ? string.Format(CultureInfo.CurrentCulture, Strings.CopyDifficultyDroppedKicksFormat, source, target, copy.DroppedDoubleKicks)
                : string.Format(CultureInfo.CurrentCulture, Strings.CopyDifficultyDoneFormat, source, target));
        }

        /// <summary>
        /// Opens the Reduce dialog over <paramref name="lane"/>'s part (charter-spec C9 item 2): its draft is drawn over the
        /// target difficulty as the rules change, and Apply adds it as one undoable step.
        /// </summary>
        public async Task ReduceAsync(ChartTrackKey lane)
        {
            if (_dialogs is null || Session.Chart.PartOf(lane.Instrument) is null)
                return;
            Session.Tools.Cancel();
            var reduce = ReduceViewModel.ForRun(this, _settings, lane);
            await _dialogs.ShowReduceAsync(reduce);
        }

        /// <summary>Opens the highway preview in a window of its own (charter-spec C18).</summary>
        public void ShowPreviewWindow(PreviewViewModel preview) => _dialogs?.ShowPreview(preview);

        /// <summary>Opens the difficulty analysis in a window of its own.</summary>
        public void ShowDifficultyWindow(DifficultyViewModel difficulty) => _dialogs?.ShowDifficulty(difficulty);

        /// <summary>Copies the selected notes, or the range selected across lanes, to the clipboard (Ctrl+C, charter-spec C15 item 3).</summary>
        public void Copy()
        {
            Session.Tools.Cancel();
            if (ClipboardEdits.Copy(Session) is not { } clip)
                return;
            ChartClipboard.Shared.Current = clip;
            Messages.Info(string.Format(CultureInfo.CurrentCulture, Strings.CopiedFormat, clip.NoteCount));
        }

        /// <summary>Copies the selection to the clipboard, then deletes it, as one step (Ctrl+X).</summary>
        public void Cut()
        {
            Session.Tools.Cancel();
            if (ClipboardEdits.Copy(Session) is not { } clip)
                return;
            ChartClipboard.Shared.Current = clip;
            Session.Tools.DeleteSelection();
            Messages.Info(string.Format(CultureInfo.CurrentCulture, Strings.CutFormat, clip.NoteCount));
        }

        /// <summary>
        /// Pastes the clipboard at the cursor (Ctrl+V, or Ctrl+Shift+V to merge), keeping its timing in ticks, as one step,
        /// and selects what it put in. Between 5-fret and GHL it asks for the lane mapping first. In a vocal part's lane,
        /// <paramref name="into"/> is its part, which only a vocal part's notes paste into (charter-spec C3 item 7).
        /// </summary>
        public async Task PasteAsync(PasteMode mode, VocalTrack? into = null)
        {
            Session.Tools.Cancel();
            var clipboard = ChartClipboard.Shared;
            if (clipboard.Current is not { } clip)
            {
                Session.Status = Strings.ClipboardEmpty;
                return;
            }

            if (ClipboardEdits.CanPaste(Session, clip, into, out var needed) is { } refusal)
            {
                Session.Status = refusal;
                return;
            }

            if (into is { } vocal)
            {
                var vocals = ClipboardEdits.PasteVocals(Session, clip, vocal, Session.CursorTick, mode);
                if (vocals.Refusal is { } vocalRefusal)
                    Session.Status = vocalRefusal;
                else if (vocals.Command is { } vocalCommand && Session.Execute(vocalCommand))
                    Session.Selection.SelectVocalNotes(vocal, vocals.Placed);
                return;
            }

            LaneMapping? mapping = null;
            if (needed is { } kinds)
            {
                if (_dialogs is null)
                    return;
                mapping = await _dialogs.AskLaneMappingAsync(new LaneMappingViewModel(clipboard.MappingFor(kinds.From, kinds.To)));
                if (mapping is null)
                    return;
                clipboard.Remember(mapping);
            }

            long at = Session.CursorTick;
            var paste = ClipboardEdits.Paste(Session, clip, at, mode, mapping);
            if (paste.Refusal is { } why)
            {
                Session.Status = why;
                return;
            }

            if (paste.Command is not { } command || !Session.Execute(command))
                return;
            if (paste.Placed.Count == 1)
                Session.Selection.Select(paste.Placed[0].Track, paste.Placed[0].Notes);
            else
                Session.Selection.SelectRange(at, at + paste.Placed.Max(p => p.Notes.Count == 0 ? 0 : p.Notes.Max(n => n.Tick + n.Length) - at), paste.Placed.Select(p => p.Track));
            if (paste.DroppedDoubleKicks > 0)
                Messages.Info(string.Format(CultureInfo.CurrentCulture, Strings.PasteDroppedKicksFormat, paste.DroppedDoubleKicks));
        }

        /// <summary>The playback lent by <see cref="BorrowPlayback"/>, given back once when disposed.</summary>
        private sealed class PlaybackBorrow(ChartEditorViewModel owner) : IDisposable
        {
            private ChartEditorViewModel? _owner = owner;

            public void Dispose()
            {
                _owner?.GiveBackPlayback();
                _owner = null;
            }
        }
    }
}
