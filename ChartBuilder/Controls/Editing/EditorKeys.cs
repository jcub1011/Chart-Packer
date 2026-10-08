using Avalonia.Input;
using ChartBuilder.ViewModels;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Editing;
using ChartPacker.Core.Charts;

namespace ChartBuilder.Controls.Editing
{
    /// <summary>
    /// The editor's keys (charter-spec §9), for the focused canvas. Letters, digits and brackets are matched by physical
    /// key, so they're in one place on every keyboard layout. Tab is never taken, so focus can always leave the canvas.
    /// Ctrl+Z, Ctrl+Y and Ctrl+S are the window's.
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
            // kick; Shift+0 is the 2× kick (§9).
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
                case PhysicalKey.Space when shift:
                    editor.PlaySelection();
                    return true;
                case PhysicalKey.PageUp when plain:
                    tools.StepCursorMeasures(1);
                    return true;
                case PhysicalKey.PageDown when plain:
                    tools.StepCursorMeasures(-1);
                    return true;
                case PhysicalKey.BracketLeft when plain:
                    session.Snap = session.Snap.Coarser();
                    return true;
                case PhysicalKey.BracketRight when plain:
                    session.Snap = session.Snap.Finer();
                    return true;
                // The lane's look-ahead: + looks less far, so the notes spread out; − looks further.
                case PhysicalKey.Equal or PhysicalKey.NumPadAdd when plain || shift:
                    lane.ZoomBy(0.8);
                    return true;
                case PhysicalKey.Minus or PhysicalKey.NumPadSubtract when plain:
                    lane.ZoomBy(1.25);
                    return true;
                case PhysicalKey.V when plain:
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
                case PhysicalKey.L when plain:
                    editor.SetLoopFromSelection();
                    return true;
                case PhysicalKey.A when ctrl && !shift && !alt:
                    tools.SelectAll();
                    return true;
                case PhysicalKey.Delete or PhysicalKey.Backspace when plain:
                    tools.DeleteSelection();
                    return true;
                // Esc takes back a gesture, else ends a run of Generate fills, else clears the selection.
                case PhysicalKey.Escape when plain:
                    if (tools.IsActive)
                        tools.Cancel();
                    else if (session.Fills.IsActive)
                        session.Fills.Cancel();
                    else
                        session.Selection.Clear();
                    return true;
                default:
                    return false;
            }
        }

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
