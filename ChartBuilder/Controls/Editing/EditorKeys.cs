using System.Collections.Generic;
using ChartBuilder.Services;
using ChartBuilder.ViewModels;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;

namespace ChartBuilder.Controls.Editing
{
    /// <summary>
    /// What the editor's keys do (charter-spec §9), for the focused canvas. Which key does what is the key map's: the window's
    /// <see cref="ChartShared.UI.Keybinds.KeyDispatcher"/> turns a press into one of <see cref="BuilderKeys"/>' actions, and
    /// <see cref="Run"/> does it. Tab is never taken, so focus can always leave the canvas. Ctrl+Z, Ctrl+Y and Ctrl+S are
    /// the window's, and so are the tool keys (<see cref="ToolKeys"/>).
    /// </summary>
    internal static class EditorKeys
    {
        /// <summary>The arrow an arrow action is for.</summary>
        private enum Arrow
        {
            Up,
            Down,
            Left,
            Right,
        }

        /// <summary>Does what the action <paramref name="id"/> does in <paramref name="lane"/>.</summary>
        /// <returns>Whether the action did something here; when not, its key is left to whatever else would take it.</returns>
        public static bool Run(string id, ChartEditorViewModel editor, EditorLaneViewModel lane, EditorCanvas canvas)
        {
            var session = editor.Session;
            var tools = session.Tools;
            bool highway = canvas.CanvasView == LaneView.Highway;

            switch (id)
            {
                // Playtest (charter-spec C19 item 4): P turns it on or off, and N and Shift+N go to the problems it found. While
                // the song plays, the window takes the keys bound before they get here (EditorWindow).
                case BuilderKeys.PlaytestToggle:
                    editor.TogglePlaytest();
                    return true;
                case BuilderKeys.PlaytestNext or BuilderKeys.PlaytestPrevious:
                    if (!editor.Playtest.HasFeedback)
                        return false;
                    editor.Playtest.StepProblem(id == BuilderKeys.PlaytestPrevious ? -1 : 1);
                    return true;

                // A vocal part isn't recorded into, and is only ever drawn along a timeline.
                case BuilderKeys.RecordToggle when !lane.IsVocal:
                    editor.ToggleRecord();
                    return true;

                case BuilderKeys.CursorUp or BuilderKeys.CursorDown or BuilderKeys.CursorLeft or BuilderKeys.CursorRight:
                {
                    int later = Later(ArrowOf(id), highway);
                    if (later == 0)
                        return false;
                    tools.StepCursor(later);
                    return true;
                }

                // Ctrl+↑ and Ctrl+↓: the next section's start and the previous one's, in either view; up is later, as it is
                // up a highway and with PgUp (§9).
                case BuilderKeys.SectionNext or BuilderKeys.SectionPrevious:
                    tools.StepCursorSections(id == BuilderKeys.SectionNext ? 1 : -1);
                    return true;

                // Ctrl+Shift+↑ and Ctrl+Shift+↓: the next tempo or time signature change and the previous one, in either view.
                case BuilderKeys.TempoChangeNext or BuilderKeys.TempoChangePrevious:
                    tools.StepCursorTempoChanges(id == BuilderKeys.TempoChangeNext ? 1 : -1);
                    return true;

                // A vocal part's lane (charter-spec C3 item 7): Alt and the arrows move the selected notes a grid step in time,
                // or a semitone in pitch (with Shift, an octave). Otherwise, Alt and the arrows move the selected notes a
                // grid step in time, or a lane across.
                case BuilderKeys.SelectionMoveUp or BuilderKeys.SelectionMoveDown or BuilderKeys.SelectionMoveLeft or BuilderKeys.SelectionMoveRight:
                {
                    var arrow = ArrowOf(id);
                    int later = Later(arrow, highway), across = Across(arrow, highway);
                    if (lane.Vocal is not null)
                    {
                        tools.MoveVocalSelection(later, -across);
                        return true;
                    }

                    if (canvas.Slots is not { } slots)
                        return false;
                    tools.MoveSelection(later, across, slots);
                    return true;
                }

                // Alt+Shift across shifts the selected notes a lane, each stopping at the edge (charter-spec C15 item 4); along,
                // it moves them as Alt does.
                case BuilderKeys.SelectionShiftUp or BuilderKeys.SelectionShiftDown or BuilderKeys.SelectionShiftLeft or BuilderKeys.SelectionShiftRight:
                {
                    var arrow = ArrowOf(id);
                    int later = Later(arrow, highway), across = Across(arrow, highway);
                    if (lane.Vocal is not null)
                    {
                        tools.MoveVocalSelection(0, -across * 12);
                        return true;
                    }

                    if (across != 0)
                    {
                        tools.ShiftLanes(across);
                        return true;
                    }

                    if (canvas.Slots is not { } slots)
                        return false;
                    tools.MoveSelection(later, across, slots);
                    return true;
                }

                case BuilderKeys.MeasureNext:
                    tools.StepCursorMeasures(1);
                    return true;
                case BuilderKeys.MeasurePrevious:
                    tools.StepCursorMeasures(-1);
                    return true;
                case BuilderKeys.GoToStart:
                    tools.GoToStart();
                    return true;
                case BuilderKeys.GoToEnd:
                    tools.GoToEnd();
                    return true;
                case BuilderKeys.SnapCoarser:
                    session.Snap = session.Snap.Coarser();
                    return true;
                case BuilderKeys.SnapFiner:
                    session.Snap = session.Snap.Finer();
                    return true;

                // The lane's look-ahead: + looks less far, so the notes spread out; − looks further. Half a second a press.
                case BuilderKeys.LookAheadLess:
                    lane.StepLookAhead(-1);
                    return true;
                case BuilderKeys.LookAheadMore:
                    lane.StepLookAhead(1);
                    return true;

                // Ctrl and a number picks the difficulty shown; a record pass ends first.
                case BuilderKeys.DifficultyEasy or BuilderKeys.DifficultyMedium or BuilderKeys.DifficultyHard or BuilderKeys.DifficultyExpert:
                    if (session.Recording is not null)
                        editor.FinishRecording();
                    lane.ShowDifficulty(DifficultyOf(id));
                    return true;

                // The lane's audio as a waveform or a spectrogram (charter-spec C13 item 5).
                case BuilderKeys.AudioBackdrop when !lane.IsVocal:
                    lane.Audio.ToggleBackdrop();
                    return true;

                // Snap to onset, and Nudge to onsets (charter-spec C14 items 2 and 3).
                case BuilderKeys.SnapToOnset when !lane.IsVocal:
                    editor.AudioAssist.ToggleSnapToOnset();
                    return true;
                case BuilderKeys.NudgeToOnsets when !lane.IsVocal:
                    editor.AudioAssist.Nudge();
                    return true;
                case BuilderKeys.ToggleView when !lane.IsVocal:
                    lane.ToggleView();
                    canvas.FocusSibling();
                    return true;

                case BuilderKeys.NoteForceHopo when !lane.IsVocal:
                    tools.ToggleChordFlag(ChordFlags.ForceHopo);
                    return true;
                case BuilderKeys.NoteForceStrum when !lane.IsVocal:
                    tools.ToggleChordFlag(ChordFlags.ForceStrum);
                    return true;
                case BuilderKeys.NoteTap when !lane.IsVocal:
                    tools.ToggleChordFlag(ChordFlags.Tap);
                    return true;
                case BuilderKeys.NoteCymbal when !lane.IsVocal:
                    tools.ToggleCymbal();
                    return true;
                case BuilderKeys.NoteAccent when !lane.IsVocal:
                    tools.ToggleDynamics(NoteDynamics.Accent);
                    return true;
                case BuilderKeys.NoteGhost when !lane.IsVocal:
                    tools.ToggleDynamics(NoteDynamics.Ghost);
                    return true;
                case BuilderKeys.NoteStarPower when !lane.IsVocal:
                    tools.ToggleStarPowerOverSelection();
                    return true;
                case BuilderKeys.TapTempo:
                    editor.Tap();
                    return true;

                case BuilderKeys.SelectAll:
                    if (lane.Vocal is { } vocal)
                        tools.SelectAllVocals(vocal);
                    else
                        tools.SelectAll();
                    return true;
                case BuilderKeys.SelectSection when !lane.IsVocal:
                    tools.SelectSectionAtCursor();
                    return true;

                // The selected notes onto the quantize grid, as the Edit menu's Quantize does. With Ctrl, as it rewrites the
                // notes' times, which a stray plain press must not.
                case BuilderKeys.Quantize:
                    editor.QuantizeSelection();
                    return true;

                // The clipboard (charter-spec C15 item 3); Ctrl+Shift+V merges rather than overwrites. A vocal part's lane
                // pastes into it.
                case BuilderKeys.Cut:
                    editor.Cut();
                    return true;
                case BuilderKeys.Copy:
                    editor.Copy();
                    return true;
                case BuilderKeys.Paste or BuilderKeys.PasteMerge:
                {
                    var mode = id == BuilderKeys.PasteMerge ? PasteMode.Merge : PasteMode.Overwrite;
                    _ = lane.Vocal is { } part ? editor.PasteAsync(mode, part) : editor.PasteAsync(mode);
                    return true;
                }

                case BuilderKeys.Delete:
                    tools.DeleteSelection();
                    return true;

                // Esc ends the countdown to the song starting, else cancels a record pass; else takes back a gesture, else closes the Go to / Find flyout, else clears the selection.
                case BuilderKeys.Cancel:
                    if (editor.IsCountingDown)
                        editor.CancelCountdown();
                    else if (session.Recording is not null)
                        editor.CancelRecording();
                    else if (tools.IsActive)
                        tools.Cancel();
                    else if (editor.Navigation.IsOpen)
                        editor.Navigation.CloseCommand.Execute(null);
                    else
                        session.Selection.Clear();
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// The lane keys 1 to 6 and 0, pressed (charter-spec §9): in record mode they play a note, while the song plays with it off
        /// they do nothing, and otherwise they place or remove a note on that lane at the cursor. Shift+0 is the 2× kick.
        /// </summary>
        /// <param name="number">The digit: 1 to 6 a column of a highway, 0 the open note or the kick.</param>
        /// <returns>Whether the key does something here.</returns>
        public static bool PressLane(int number, bool doubleKick, ChartEditorViewModel editor, EditorLaneViewModel lane)
        {
            var session = editor.Session;
            var tools = session.Tools;

            // Record mode (charter-spec C15 item 5): number keys play notes, Space ends the pass and R turns record mode off,
            // Esc cancels the pass, and a difficulty key ends it before switching.
            if (session.Recording is not null)
            {
                if (doubleKick)
                {
                    editor.RecordPress(0, doubleKick: true);
                    return true;
                }

                if (ShownLaneOf(number, lane, session) is { } shown)
                {
                    editor.RecordPress(shown);
                    return true;
                }
            }

            // 1 to 5 are the lanes in a highway's order (GHL's 1 to 6 white 1–3, then black 1–3), and 0 the open note or the
            // kick; Shift+0 is the 2× kick (§9). Only while the song is paused: while it plays, only record mode writes notes
            // (charter-spec C15 item 5), so a key that placed one would read as recording with it off. The key does nothing.
            if (editor.Playback.IsPlaying)
                return true;

            if (doubleKick)
            {
                if (lane.Track is not { Instrument: var instrument } || ChartInstruments.KindOf(instrument) != ChartInstrumentKind.Drums)
                    return false;
                tools.ToggleDoubleKickAtCursor();
                return true;
            }

            if (lane.Track is null)
                return false;

            var placeable = LaneSlots.Of(ChartInstruments.KindOf(lane.Track!.Value.Instrument), LaneView.Highway, session.IsFiveLaneDrums);
            if (number == 0)
                tools.ToggleNoteAtCursor(EditorTools.ApartLane(placeable));
            else if (number <= placeable.KeyLanes.Count)
                tools.ToggleNoteAtCursor(placeable.KeyLanes[number - 1]);
            return true;
        }

        /// <summary>
        /// Lets go of what a lane key held in record mode (charter-spec C15 item 5): the 2× kick for its own key, else the lane
        /// the number stands for, so letting go of 0 doesn't end a 2× kick that Shift+0 has since started.
        /// </summary>
        public static void ReleaseLane(int number, bool doubleKick, ChartEditorViewModel editor, EditorLaneViewModel lane)
        {
            if (editor.Session.Recording is null)
                return;
            if (doubleKick)
                editor.RecordRelease(0, doubleKick: true);
            else if (ShownLaneOf(number, lane, editor.Session) is { } shown)
                editor.RecordRelease(shown);
        }

        /// <summary>The digit a lane action stands for (1 to 6, 0 the open note or the kick); null for another action.</summary>
        public static int? LaneNumberOf(string id)
        {
            if (id is BuilderKeys.NoteOpen or BuilderKeys.NoteDoubleKick)
                return 0;
            int index = IndexOf(BuilderKeys.LaneActions, id);
            return index >= 0 ? index + 1 : null;
        }

        private static int IndexOf<T>(IReadOnlyList<T> list, T item)
        {
            var comparer = EqualityComparer<T>.Default;
            for (int i = 0; i < list.Count; i++)
            {
                if (comparer.Equals(list[i], item))
                    return i;
            }

            return -1;
        }

        /// <summary>
        /// The lane drawn in the column a number key places in (§9): 1 to 5 in a highway's order (GHL's 1 to 6), 0 the open
        /// note or the kick. Null for a number the lane has no column for.
        /// </summary>
        private static int? ShownLaneOf(int number, EditorLaneViewModel lane, EditorSession session)
        {
            if (lane.Track is not { } track)
                return null;
            var placeable = LaneSlots.Of(ChartInstruments.KindOf(track.Instrument), LaneView.Highway, session.IsFiveLaneDrums);
            if (number == 0)
                return EditorTools.ApartLane(placeable);
            return number <= placeable.KeyLanes.Count ? placeable.KeyLanes[number - 1] : null;
        }

        private static Arrow ArrowOf(string id) => id switch
        {
            BuilderKeys.CursorUp or BuilderKeys.SelectionMoveUp or BuilderKeys.SelectionShiftUp => Arrow.Up,
            BuilderKeys.CursorDown or BuilderKeys.SelectionMoveDown or BuilderKeys.SelectionShiftDown => Arrow.Down,
            BuilderKeys.CursorLeft or BuilderKeys.SelectionMoveLeft or BuilderKeys.SelectionShiftLeft => Arrow.Left,
            _ => Arrow.Right,
        };

        // Later and earlier: up and down a highway, right and left along a timeline.
        private static int Later(Arrow arrow, bool highway) => (arrow, highway) switch
        {
            (Arrow.Up, true) or (Arrow.Right, false) => 1,
            (Arrow.Down, true) or (Arrow.Left, false) => -1,
            _ => 0,
        };

        // Across: right across a highway's columns, down a timeline's rows.
        private static int Across(Arrow arrow, bool highway) => (arrow, highway) switch
        {
            (Arrow.Right, true) or (Arrow.Down, false) => 1,
            (Arrow.Left, true) or (Arrow.Up, false) => -1,
            _ => 0,
        };

        /// <summary>
        /// The tools the tool keys pick, in the toolbar's order, so the tool at index <c>n</c> is <c>BuilderKeys.ToolActions[n]</c>
        /// (Alt and <c>n + 1</c> by default). The table <see cref="ToolOf"/> reads.
        /// </summary>
        public static IReadOnlyList<EditorTool> ToolKeys { get; } =
            [EditorTool.Select, EditorTool.Place, EditorTool.Erase, EditorTool.Phrase, EditorTool.Tempo, EditorTool.Sync];

        /// <summary>The tool the action <paramref name="id"/> picks, or <see langword="null"/> when it isn't a tool action.</summary>
        public static EditorTool? ToolOf(string id)
        {
            int index = IndexOf(BuilderKeys.ToolActions, id);
            return index >= 0 ? ToolKeys[index] : null;
        }

        /// <summary>The difficulty a difficulty action picks: Easy to Expert.</summary>
        private static ChartDifficulty DifficultyOf(string id) => IndexOf(BuilderKeys.DifficultyActions, id) switch
        {
            0 => ChartDifficulty.Easy,
            1 => ChartDifficulty.Medium,
            2 => ChartDifficulty.Hard,
            _ => ChartDifficulty.Expert,
        };
    }
}
