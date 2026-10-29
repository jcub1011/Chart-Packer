using System.Collections.Generic;
using Avalonia.Input;
using ChartBuilder.ViewModels;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;

namespace ChartBuilder.Controls.Editing
{
    /// <summary>
    /// The editor's keys (charter-spec §9), for the focused canvas. Letters, digits and brackets are matched by physical
    /// key, so they're in one place on every keyboard layout. Tab is never taken, so focus can always leave the canvas.
    /// Ctrl+Z, Ctrl+Y and Ctrl+S are the window's, and so are Alt+1 to Alt+6, which pick the tools (<see cref="ToolKeys"/>).
    /// </summary>
    internal static class EditorKeys
    {
        /// <summary>Does what <paramref name="key"/> does in <paramref name="lane"/>.</summary>
        /// <returns>Whether the key does something here.</returns>
        public static bool Handle(PhysicalKey key, KeyModifiers modifiers, ChartEditorViewModel editor, EditorLaneViewModel lane, EditorCanvas canvas)
        {
            var session = editor.Session;
            var tools = session.Tools;
            bool ctrl = (modifiers & KeyModifiers.Control) != 0;
            bool shift = (modifiers & KeyModifiers.Shift) != 0;
            bool alt = (modifiers & KeyModifiers.Alt) != 0;
            bool plain = (modifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Meta)) == 0;
            bool highway = canvas.CanvasView == LaneView.Highway;

            // Playtest (charter-spec C19 item 4): P turns it on or off, and N and Shift+N go to the problems it found. While the
            // song plays, the window takes the keys bound before they get here (EditorWindow).
            if (plain && key == PhysicalKey.P)
            {
                editor.TogglePlaytest();
                return true;
            }

            if ((plain || (shift && !ctrl && !alt)) && key == PhysicalKey.N && editor.Playtest.HasFeedback)
            {
                editor.Playtest.StepProblem(shift ? -1 : 1);
                return true;
            }

            // Record mode (charter-spec C15 item 5): number keys play notes, Space ends the pass and R turns record mode off,
            // Esc cancels the pass, and a difficulty key ends it before switching.
            if (session.Recording is not null)
            {
                if (plain && ShownLaneOf(key, lane, session) is { } shown)
                {
                    editor.RecordPress(shown);
                    return true;
                }

                if (shift && !ctrl && !alt && LaneKey(key) == 0)
                {
                    editor.RecordPress(0, doubleKick: true);
                    return true;
                }

                if (plain && key == PhysicalKey.Escape)
                {
                    editor.CancelRecording();
                    return true;
                }

                if (ctrl && !alt && DifficultyOf(key) is not null)
                    editor.FinishRecording();
            }

            // A vocal part isn't recorded into, and is only ever drawn along a timeline.
            if (plain && key == PhysicalKey.R && !lane.IsVocal)
            {
                editor.ToggleRecord();
                return true;
            }

            // Later and earlier: up and down a highway, right and left along a timeline.
            int later = (key, highway) switch
            {
                (PhysicalKey.ArrowUp, true) or (PhysicalKey.ArrowRight, false) => 1,
                (PhysicalKey.ArrowDown, true) or (PhysicalKey.ArrowLeft, false) => -1,
                _ => 0,
            };

            // Across: right across a highway's columns, down a timeline's rows.
            int across = (key, highway) switch
            {
                (PhysicalKey.ArrowRight, true) or (PhysicalKey.ArrowDown, false) => 1,
                (PhysicalKey.ArrowLeft, true) or (PhysicalKey.ArrowUp, false) => -1,
                _ => 0,
            };

            // A vocal part's lane (charter-spec C3 item 7): Alt and the arrows move the selected notes a grid step in time,
            // or a semitone in pitch (with Shift, an octave); Ctrl+A selects its notes, and Ctrl+V pastes into it. The keys
            // that change a highway's notes or its part do nothing here.
            if (lane.Vocal is { } vocal)
            {
                if (alt && !ctrl && (later != 0 || across != 0))
                {
                    tools.MoveVocalSelection(shift ? 0 : later, -across * (shift ? 12 : 1));
                    return true;
                }

                if (ctrl && !shift && !alt && key == PhysicalKey.A)
                {
                    tools.SelectAllVocals(vocal);
                    return true;
                }

                if (ctrl && !alt && key == PhysicalKey.V)
                {
                    _ = editor.PasteAsync(shift ? PasteMode.Merge : PasteMode.Overwrite, vocal);
                    return true;
                }

                if (IsHighwayOnly(key, plain, shift && !ctrl && !alt, ctrl && shift && !alt))
                    return false;
            }

            if (later != 0 && !alt && !ctrl)
            {
                tools.StepCursor(later);
                return true;
            }

            // Ctrl+↑ and Ctrl+↓: the next section's start and the previous one's, in either view; up is later, as it is
            // up a highway and with PgUp (§9).
            if (ctrl && !alt && !shift && key is PhysicalKey.ArrowUp or PhysicalKey.ArrowDown)
            {
                tools.StepCursorSections(key == PhysicalKey.ArrowUp ? 1 : -1);
                return true;
            }

            // Ctrl+Shift+↑ and Ctrl+Shift+↓: the next tempo or time signature change and the previous one, in either view.
            if (ctrl && shift && !alt && key is PhysicalKey.ArrowUp or PhysicalKey.ArrowDown)
            {
                tools.StepCursorTempoChanges(key == PhysicalKey.ArrowUp ? 1 : -1);
                return true;
            }

            // Alt+Shift across shifts the selected notes a lane, each stopping at the edge (charter-spec C15 item 4).
            if (alt && shift && !ctrl && across != 0)
            {
                tools.ShiftLanes(across);
                return true;
            }

            if (alt && !ctrl && (later != 0 || across != 0) && canvas.Slots is { } slots)
            {
                tools.MoveSelection(later, across, slots);
                return true;
            }

            if (ctrl && !alt && DifficultyOf(key) is { } difficulty)
            {
                lane.ShowDifficulty(difficulty);
                return true;
            }

            // 1 to 5 are the lanes in a highway's order (GHL's 1 to 6 white 1–3, then black 1–3), and 0 the open note or the
            // kick; Shift+0 is the 2× kick (§9). Only while the song is paused: while it plays, only record mode writes notes
            // (charter-spec C15 item 5), so a key that placed one would read as recording with it off. The key does nothing.
            if ((plain || shift) && !ctrl && !alt && LaneKey(key) is not null && editor.Playback.IsPlaying)
                return true;

            if (shift && !ctrl && !alt && LaneKey(key) == 0 && lane.Track is { Instrument: var instrument } && ChartInstruments.KindOf(instrument) == ChartInstrumentKind.Drums)
            {
                tools.ToggleDoubleKickAtCursor();
                return true;
            }

            if (plain && LaneKey(key) is { } number && lane.Track is not null)
            {
                var placeable = LaneSlots.Of(ChartInstruments.KindOf(lane.Track!.Value.Instrument), LaneView.Highway, session.IsFiveLaneDrums);
                if (number == 0)
                    tools.ToggleNoteAtCursor(EditorTools.ApartLane(placeable));
                else if (number <= placeable.KeyLanes.Count)
                    tools.ToggleNoteAtCursor(placeable.KeyLanes[number - 1]);
                return true;
            }

            switch (key)
            {
                case PhysicalKey.Space when plain:
                    editor.PlayPause();
                    return true;
                case PhysicalKey.PageUp when plain:
                    tools.StepCursorMeasures(1);
                    return true;
                case PhysicalKey.PageDown when plain:
                    tools.StepCursorMeasures(-1);
                    return true;
                case PhysicalKey.Home when plain:
                    tools.GoToStart();
                    return true;
                case PhysicalKey.End when plain:
                    tools.GoToEnd();
                    return true;
                case PhysicalKey.BracketLeft when plain:
                    session.Snap = session.Snap.Coarser();
                    return true;
                case PhysicalKey.BracketRight when plain:
                    session.Snap = session.Snap.Finer();
                    return true;
                // The lane's look-ahead: + looks less far, so the notes spread out; − looks further. Half a second a press.
                case PhysicalKey.Equal or PhysicalKey.NumPadAdd when plain || shift:
                    lane.StepLookAhead(-1);
                    return true;
                case PhysicalKey.Minus or PhysicalKey.NumPadSubtract when plain:
                    lane.StepLookAhead(1);
                    return true;
                // The lane's audio as a waveform or a spectrogram (charter-spec C13 item 5).
                case PhysicalKey.W when plain:
                    lane.Audio.ToggleBackdrop();
                    return true;
                // Snap to onset, and Nudge to onsets (charter-spec C14 items 2 and 3).
                case PhysicalKey.O when plain:
                    editor.AudioAssist.ToggleSnapToOnset();
                    return true;
                case PhysicalKey.O when shift && !ctrl && !alt:
                    editor.AudioAssist.Nudge();
                    return true;
                case PhysicalKey.V when plain && !lane.IsVocal:
                    lane.ToggleView();
                    canvas.FocusSibling();
                    return true;
                case PhysicalKey.H when plain:
                    tools.ToggleChordFlag(ChordFlags.ForceHopo);
                    return true;
                case PhysicalKey.G when plain:
                    tools.ToggleChordFlag(ChordFlags.ForceStrum);
                    return true;
                case PhysicalKey.T when plain:
                    tools.ToggleChordFlag(ChordFlags.Tap);
                    return true;
                case PhysicalKey.C when plain:
                    tools.ToggleCymbal();
                    return true;
                case PhysicalKey.A when plain:
                    tools.ToggleDynamics(NoteDynamics.Accent);
                    return true;
                case PhysicalKey.A when shift && !ctrl && !alt:
                    tools.ToggleDynamics(NoteDynamics.Ghost);
                    return true;
                case PhysicalKey.S when plain:
                    tools.ToggleStarPowerOverSelection();
                    return true;
                case PhysicalKey.B when plain:
                    editor.Tap();
                    return true;
                case PhysicalKey.A when ctrl && !shift && !alt:
                    tools.SelectAll();
                    return true;
                // The selected notes onto the quantize grid, as the Edit menu's Quantize does. With Ctrl, as it rewrites the
                // notes' times, which a stray plain press must not.
                case PhysicalKey.Q when ctrl && !shift && !alt:
                    editor.QuantizeSelection();
                    return true;
                // The section the cursor is in, across the lanes shown (charter-spec C15 item 2).
                case PhysicalKey.A when ctrl && shift && !alt:
                    tools.SelectSectionAtCursor();
                    return true;
                // The clipboard (charter-spec C15 item 3); Ctrl+Shift+V merges rather than overwrites.
                case PhysicalKey.X when ctrl && !shift && !alt:
                    editor.Cut();
                    return true;
                case PhysicalKey.C when ctrl && !shift && !alt:
                    editor.Copy();
                    return true;
                case PhysicalKey.V when ctrl && !alt:
                    _ = editor.PasteAsync(shift ? PasteMode.Merge : PasteMode.Overwrite);
                    return true;
                case PhysicalKey.Delete or PhysicalKey.Backspace when plain:
                    tools.DeleteSelection();
                    return true;
                // Esc takes back a gesture, else closes the Go to / Find flyout, else clears the selection.
                case PhysicalKey.Escape when plain:
                    if (tools.IsActive)
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

        /// <summary>Lets go of what <paramref name="key"/> held in record mode (charter-spec C15 item 5).</summary>
        /// <returns>Whether the key does something here.</returns>
        public static bool HandleUp(PhysicalKey key, ChartEditorViewModel editor, EditorLaneViewModel lane)
        {
            if (editor.Session.Recording is null)
                return false;
            if (LaneKey(key) == 0)
                editor.RecordRelease(0, doubleKick: true);
            if (ShownLaneOf(key, lane, editor.Session) is not { } shown)
                return false;
            editor.RecordRelease(shown);
            return true;
        }

        /// <summary>
        /// The lane drawn in the column a number key places in (§9): 1 to 5 in a highway's order (GHL's 1 to 6), 0 the open
        /// note or the kick. Null for another key.
        /// </summary>
        private static int? ShownLaneOf(PhysicalKey key, EditorLaneViewModel lane, EditorSession session)
        {
            if (LaneKey(key) is not { } number || lane.Track is not { } track)
                return null;
            var placeable = LaneSlots.Of(ChartInstruments.KindOf(track.Instrument), LaneView.Highway, session.IsFiveLaneDrums);
            if (number == 0)
                return EditorTools.ApartLane(placeable);
            return number <= placeable.KeyLanes.Count ? placeable.KeyLanes[number - 1] : null;
        }

        /// <summary>
        /// Whether <paramref name="key"/>, plain, with Shift alone or with Ctrl and Shift, changes a highway's notes, its
        /// part's audio or the lanes across, which a vocal part's lane hasn't.
        /// </summary>
        private static bool IsHighwayOnly(PhysicalKey key, bool plain, bool shiftOnly, bool ctrlShift) => key switch
        {
            PhysicalKey.H or PhysicalKey.G or PhysicalKey.T or PhysicalKey.C or PhysicalKey.S or PhysicalKey.W => plain,
            PhysicalKey.O => plain || shiftOnly,
            PhysicalKey.A => plain || shiftOnly || ctrlShift,
            _ => false,
        };

        /// <summary>The digit of a number key, or null for another key.</summary>
        private static int? LaneKey(PhysicalKey key) => key switch
        {
            PhysicalKey.Digit0 or PhysicalKey.NumPad0 => 0,
            PhysicalKey.Digit1 or PhysicalKey.NumPad1 => 1,
            PhysicalKey.Digit2 or PhysicalKey.NumPad2 => 2,
            PhysicalKey.Digit3 or PhysicalKey.NumPad3 => 3,
            PhysicalKey.Digit4 or PhysicalKey.NumPad4 => 4,
            PhysicalKey.Digit5 or PhysicalKey.NumPad5 => 5,
            PhysicalKey.Digit6 or PhysicalKey.NumPad6 => 6,
            _ => null,
        };

        /// <summary>
        /// The tools Alt and the number row's 1 to 6 pick, in the toolbar's order, so the tool at index <c>n</c> is Alt and
        /// <c>n + 1</c>. The one table <see cref="ToolOf"/> and <see cref="ShortcutOf"/> both read.
        /// </summary>
        public static IReadOnlyList<EditorTool> ToolKeys { get; } =
            [EditorTool.Select, EditorTool.Place, EditorTool.Erase, EditorTool.Phrase, EditorTool.Tempo, EditorTool.Sync];

        /// <summary>
        /// The tool Alt and a number-row digit picks (<see cref="ToolKeys"/>). The window takes it wherever focus is; plain
        /// digits are the lanes' and Ctrl's are the difficulties'. Not the number pad's, whose digits with Alt are Windows'
        /// Alt codes.
        /// </summary>
        /// <returns>The tool, or <see langword="null"/> when the key isn't one.</returns>
        public static EditorTool? ToolOf(PhysicalKey key, KeyModifiers modifiers)
        {
            if ((modifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Meta)) != KeyModifiers.Alt)
                return null;

            int index = key - PhysicalKey.Digit1;
            return index >= 0 && index < ToolKeys.Count ? ToolKeys[index] : null;
        }

        /// <summary>The key that picks <paramref name="tool"/> as shown to people, such as <c>Alt+3</c>, or <see langword="null"/> when none does.</summary>
        public static string? ShortcutOf(EditorTool tool)
        {
            for (int i = 0; i < ToolKeys.Count; i++)
            {
                if (ToolKeys[i] == tool)
                    return $"Alt+{i + 1}";
            }

            return null;
        }

        /// <summary>The difficulty Ctrl and a digit picks: 1 Easy to 4 Expert.</summary>
        private static ChartDifficulty? DifficultyOf(PhysicalKey key) => key switch
        {
            PhysicalKey.Digit1 => ChartDifficulty.Easy,
            PhysicalKey.Digit2 => ChartDifficulty.Medium,
            PhysicalKey.Digit3 => ChartDifficulty.Hard,
            PhysicalKey.Digit4 => ChartDifficulty.Expert,
            _ => null,
        };
    }
}
