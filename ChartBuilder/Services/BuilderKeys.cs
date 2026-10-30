using System.Collections.Generic;
using ChartBuilder.Resources;
using ChartShared.Core.Keybinds;
using ChartShared.UI.Keybinds;
using ChartShared.UI.Resources;

namespace ChartBuilder.Services
{
    /// <summary>Chart Builder's actions with keys: the shared ones and its own (charter-spec §9).</summary>
    public static class BuilderKeys
    {
        /// <summary>The Editor window: its commands, wherever focus is.</summary>
        public static readonly KeyScope Editor = new("builder.editor");

        /// <summary>The editing tools' keys, which also work wherever focus is. By key position, so they're the number row on every layout.</summary>
        public static readonly KeyScope Tools = new("builder.tools", Editor, KeyMatch.Physical);

        /// <summary>A lane canvas's keys, live only while one has focus. By key position, so <c>[</c> and <c>]</c> work on any layout.</summary>
        public static readonly KeyScope Canvas = new("builder.canvas", Tools, KeyMatch.Physical);

        /// <summary>The Preview window.</summary>
        public static readonly KeyScope Preview = new("builder.preview");

        /// <summary>The window Generate star power and Generate fills share.</summary>
        public static readonly KeyScope GeneratePhrases = new("builder.generatePhrases");

        public const string NewChart = "builder.file.new";
        public const string OpenChart = "builder.file.open";
        public const string GoToMeasure = "builder.nav.goToMeasure";
        public const string Find = "builder.nav.find";
        public const string FindNext = "builder.nav.findNext";
        public const string FindPrevious = "builder.nav.findPrevious";
        public const string ToolSelect = "builder.tool.select";
        public const string ToolPlace = "builder.tool.place";
        public const string ToolErase = "builder.tool.erase";
        public const string ToolPhrase = "builder.tool.phrase";
        public const string ToolTempo = "builder.tool.tempo";
        public const string ToolSync = "builder.tool.sync";
        public const string LoopTool = "builder.tool.loopHold";
        public const string PlaytestToggle = "builder.playtest.toggle";
        public const string PlaytestNext = "builder.playtest.next";
        public const string PlaytestPrevious = "builder.playtest.previous";
        public const string RecordToggle = "builder.record.toggle";
        public const string CursorUp = "builder.cursor.up";
        public const string CursorDown = "builder.cursor.down";
        public const string CursorLeft = "builder.cursor.left";
        public const string CursorRight = "builder.cursor.right";
        public const string SectionNext = "builder.cursor.sectionNext";
        public const string SectionPrevious = "builder.cursor.sectionPrevious";
        public const string TempoChangeNext = "builder.cursor.tempoChangeNext";
        public const string TempoChangePrevious = "builder.cursor.tempoChangePrevious";
        public const string MeasureNext = "builder.cursor.measureNext";
        public const string MeasurePrevious = "builder.cursor.measurePrevious";
        public const string GoToStart = "builder.cursor.start";
        public const string GoToEnd = "builder.cursor.end";
        public const string SelectionMoveUp = "builder.selection.moveUp";
        public const string SelectionMoveDown = "builder.selection.moveDown";
        public const string SelectionMoveLeft = "builder.selection.moveLeft";
        public const string SelectionMoveRight = "builder.selection.moveRight";
        public const string SelectionShiftUp = "builder.selection.shiftUp";
        public const string SelectionShiftDown = "builder.selection.shiftDown";
        public const string SelectionShiftLeft = "builder.selection.shiftLeft";
        public const string SelectionShiftRight = "builder.selection.shiftRight";
        public const string SelectAll = "builder.selection.all";
        public const string SelectSection = "builder.selection.section";
        public const string Quantize = "builder.edit.quantize";
        public const string Cut = "builder.edit.cut";
        public const string Copy = "builder.edit.copy";
        public const string Paste = "builder.edit.paste";
        public const string PasteMerge = "builder.edit.pasteMerge";
        public const string Delete = "builder.edit.delete";
        public const string Cancel = "builder.edit.cancel";
        public const string SnapCoarser = "builder.lane.snapCoarser";
        public const string SnapFiner = "builder.lane.snapFiner";
        public const string LookAheadLess = "builder.lane.lookAheadLess";
        public const string LookAheadMore = "builder.lane.lookAheadMore";
        public const string AudioBackdrop = "builder.lane.audioBackdrop";
        public const string SnapToOnset = "builder.lane.snapToOnset";
        public const string NudgeToOnsets = "builder.lane.nudgeToOnsets";
        public const string ToggleView = "builder.lane.toggleView";
        public const string TapTempo = "builder.lane.tapTempo";
        public const string NoteLane1 = "builder.note.lane1";
        public const string NoteLane2 = "builder.note.lane2";
        public const string NoteLane3 = "builder.note.lane3";
        public const string NoteLane4 = "builder.note.lane4";
        public const string NoteLane5 = "builder.note.lane5";
        public const string NoteLane6 = "builder.note.lane6";
        public const string NoteOpen = "builder.note.open";
        public const string NoteDoubleKick = "builder.note.doubleKick";
        public const string NoteForceHopo = "builder.note.forceHopo";
        public const string NoteForceStrum = "builder.note.forceStrum";
        public const string NoteTap = "builder.note.tap";
        public const string NoteCymbal = "builder.note.cymbal";
        public const string NoteAccent = "builder.note.accent";
        public const string NoteGhost = "builder.note.ghost";
        public const string NoteStarPower = "builder.note.starPower";
        public const string DifficultyEasy = "builder.difficulty.easy";
        public const string DifficultyMedium = "builder.difficulty.medium";
        public const string DifficultyHard = "builder.difficulty.hard";
        public const string DifficultyExpert = "builder.difficulty.expert";
        public const string MouseScroll = "builder.mouse.scroll";
        public const string MouseFastScroll = "builder.mouse.fastScroll";
        public const string MouseZoom = "builder.mouse.zoom";
        public const string MouseSync = "builder.mouse.sync";
        public const string PreviewFullScreen = "builder.preview.fullScreen";
        public const string PreviewExitFullScreen = "builder.preview.exitFullScreen";
        public const string PreviousRegion = "builder.generatePhrases.previousRegion";
        public const string NextRegion = "builder.generatePhrases.nextRegion";
        public const string ApplyPhrases = "builder.generatePhrases.apply";

        /// <summary>The tool actions, in the toolbar's order.</summary>
        public static IReadOnlyList<string> ToolActions { get; } =
            [ToolSelect, ToolPlace, ToolErase, ToolPhrase, ToolTempo, ToolSync];

        /// <summary>The lane keys 1 to 6, in the order of a highway's columns.</summary>
        public static IReadOnlyList<string> LaneActions { get; } =
            [NoteLane1, NoteLane2, NoteLane3, NoteLane4, NoteLane5, NoteLane6];

        /// <summary>The difficulty keys, Easy to Expert.</summary>
        public static IReadOnlyList<string> DifficultyActions { get; } =
            [DifficultyEasy, DifficultyMedium, DifficultyHard, DifficultyExpert];

        public static KeybindCatalog Catalog() => new(Actions());

        /// <summary>The ids of the actions of <paramref name="catalog"/> in the <see cref="Canvas"/> scope that a window registers handlers for, in the catalog's order.</summary>
        public static IEnumerable<string> CanvasActions(KeybindCatalog catalog)
        {
            foreach (var action in catalog.InScope(Canvas))
            {
                if (action.DisplayOverride is null)
                    yield return action.Id;
            }
        }

        private static IEnumerable<KeyAction> Actions()
        {
            foreach (var shared in SharedKeys.Actions())
                yield return shared;

            string file = SharedStrings.KeyCategoryFile;
            string nav = Strings.KeyCategoryEditor + KeyAction.CategorySeparator + Strings.KeyCategoryNavigation;
            string tools = Strings.KeyCategoryEditor + KeyAction.CategorySeparator + Strings.KeyCategoryTools;
            string playtest = Strings.KeyCategoryEditor + KeyAction.CategorySeparator + Strings.KeyCategoryPlaytest;
            string cursor = Strings.KeyCategoryEditor + KeyAction.CategorySeparator + Strings.KeyCategoryCursor;
            string selection = Strings.KeyCategoryEditor + KeyAction.CategorySeparator + Strings.KeyCategorySelection;
            string notes = Strings.KeyCategoryEditor + KeyAction.CategorySeparator + Strings.KeyCategoryNotes;
            string clip = Strings.KeyCategoryEditor + KeyAction.CategorySeparator + Strings.KeyCategoryClipboard;
            string lane = Strings.KeyCategoryEditor + KeyAction.CategorySeparator + Strings.KeyCategoryLane;
            string mouse = Strings.KeyCategoryEditor + KeyAction.CategorySeparator + Strings.KeyCategoryMouse;
            string preview = Strings.KeyCategoryPreview;
            string gen = Strings.KeyCategoryGeneratePhrases;

            yield return Make(NewChart, Strings.KeyActionNewChart, file, Editor, "Ctrl+N");
            yield return Make(OpenChart, Strings.KeyActionOpenChart, file, Editor, "Ctrl+O");

            yield return Make(GoToMeasure, Strings.KeyActionGoToMeasure, nav, Editor, "Ctrl+G");
            yield return Make(Find, Strings.KeyActionFind, nav, Editor, "Ctrl+F");
            yield return Make(FindNext, Strings.KeyActionFindNext, nav, Editor, "F3", bare: true);
            yield return Make(FindPrevious, Strings.KeyActionFindPrevious, nav, Editor, "Shift+F3");

            yield return Make(ToolSelect, Strings.KeyActionToolSelect, tools, Tools, "Alt+Digit1", yields: true);
            yield return Make(ToolPlace, Strings.KeyActionToolPlace, tools, Tools, "Alt+Digit2", yields: true);
            yield return Make(ToolErase, Strings.KeyActionToolErase, tools, Tools, "Alt+Digit3", yields: true);
            yield return Make(ToolPhrase, Strings.KeyActionToolPhrase, tools, Tools, "Alt+Digit4", yields: true);
            yield return Make(ToolTempo, Strings.KeyActionToolTempo, tools, Tools, "Alt+Digit5", yields: true);
            yield return Make(ToolSync, Strings.KeyActionToolSync, tools, Tools, "Alt+Digit6", yields: true);
            yield return Make(LoopTool, Strings.KeyActionLoopTool, tools, Tools, "L", bare: true, hold: true);

            yield return Make(PlaytestToggle, Strings.KeyActionPlaytestToggle, playtest, Canvas, "P", bare: true, reserved: true);
            yield return Make(PlaytestNext, Strings.KeyActionPlaytestNext, playtest, Canvas, "N", bare: true, reserved: true);
            yield return Make(PlaytestPrevious, Strings.KeyActionPlaytestPrevious, playtest, Canvas, "Shift+N");
            yield return Make(RecordToggle, Strings.KeyActionRecordToggle, playtest, Canvas, "R", bare: true);

            yield return Make(CursorUp, Strings.KeyActionCursorUp, cursor, Canvas, "ArrowUp,Shift+ArrowUp", bare: true, repeatable: true);
            yield return Make(CursorDown, Strings.KeyActionCursorDown, cursor, Canvas, "ArrowDown,Shift+ArrowDown", bare: true, repeatable: true);
            yield return Make(CursorLeft, Strings.KeyActionCursorLeft, cursor, Canvas, "ArrowLeft,Shift+ArrowLeft", bare: true, repeatable: true);
            yield return Make(CursorRight, Strings.KeyActionCursorRight, cursor, Canvas, "ArrowRight,Shift+ArrowRight", bare: true, repeatable: true);
            yield return Make(SectionNext, Strings.KeyActionSectionNext, cursor, Canvas, "Ctrl+ArrowUp", repeatable: true);
            yield return Make(SectionPrevious, Strings.KeyActionSectionPrevious, cursor, Canvas, "Ctrl+ArrowDown", repeatable: true);
            yield return Make(TempoChangeNext, Strings.KeyActionTempoChangeNext, cursor, Canvas, "Ctrl+Shift+ArrowUp", repeatable: true);
            yield return Make(TempoChangePrevious, Strings.KeyActionTempoChangePrevious, cursor, Canvas, "Ctrl+Shift+ArrowDown", repeatable: true);
            yield return Make(MeasureNext, Strings.KeyActionMeasureNext, cursor, Canvas, "PageUp", bare: true, repeatable: true);
            yield return Make(MeasurePrevious, Strings.KeyActionMeasurePrevious, cursor, Canvas, "PageDown", bare: true, repeatable: true);
            yield return Make(GoToStart, Strings.KeyActionGoToStart, cursor, Canvas, "Home", bare: true);
            yield return Make(GoToEnd, Strings.KeyActionGoToEnd, cursor, Canvas, "End", bare: true);

            yield return Make(SelectionMoveUp, Strings.KeyActionSelectionMoveUp, selection, Canvas, "Alt+ArrowUp", repeatable: true);
            yield return Make(SelectionMoveDown, Strings.KeyActionSelectionMoveDown, selection, Canvas, "Alt+ArrowDown", repeatable: true);
            yield return Make(SelectionMoveLeft, Strings.KeyActionSelectionMoveLeft, selection, Canvas, "Alt+ArrowLeft", repeatable: true);
            yield return Make(SelectionMoveRight, Strings.KeyActionSelectionMoveRight, selection, Canvas, "Alt+ArrowRight", repeatable: true);
            yield return Make(SelectionShiftUp, Strings.KeyActionSelectionShiftUp, selection, Canvas, "Alt+Shift+ArrowUp", repeatable: true);
            yield return Make(SelectionShiftDown, Strings.KeyActionSelectionShiftDown, selection, Canvas, "Alt+Shift+ArrowDown", repeatable: true);
            yield return Make(SelectionShiftLeft, Strings.KeyActionSelectionShiftLeft, selection, Canvas, "Alt+Shift+ArrowLeft", repeatable: true);
            yield return Make(SelectionShiftRight, Strings.KeyActionSelectionShiftRight, selection, Canvas, "Alt+Shift+ArrowRight", repeatable: true);
            yield return Make(SelectAll, Strings.KeyActionSelectAll, selection, Canvas, "Ctrl+A", locked: true);
            yield return Make(SelectSection, Strings.KeyActionSelectSection, selection, Canvas, "Ctrl+Shift+A");

            yield return Make(Quantize, Strings.KeyActionQuantize, clip, Canvas, "Ctrl+Q");
            yield return Make(Cut, Strings.KeyActionCut, clip, Canvas, "Ctrl+X", locked: true);
            yield return Make(Copy, Strings.KeyActionCopy, clip, Canvas, "Ctrl+C", locked: true);
            yield return Make(Paste, Strings.KeyActionPaste, clip, Canvas, "Ctrl+V", locked: true);
            yield return Make(PasteMerge, Strings.KeyActionPasteMerge, clip, Canvas, "Ctrl+Shift+V");
            yield return Make(Delete, Strings.KeyActionDelete, clip, Canvas, "Delete,Backspace", bare: true, locked: true);
            yield return Make(Cancel, Strings.KeyActionCancel, clip, Canvas, "Escape", bare: true, locked: true, reserved: true);

            yield return Make(SnapCoarser, Strings.KeyActionSnapCoarser, lane, Canvas, "BracketLeft", bare: true, repeatable: true);
            yield return Make(SnapFiner, Strings.KeyActionSnapFiner, lane, Canvas, "BracketRight", bare: true, repeatable: true);
            yield return Make(LookAheadLess, Strings.KeyActionLookAheadLess, lane, Canvas, "Equal,Shift+Equal,NumPadAdd,Shift+NumPadAdd", bare: true, repeatable: true);
            yield return Make(LookAheadMore, Strings.KeyActionLookAheadMore, lane, Canvas, "Minus,NumPadSubtract", bare: true, repeatable: true);
            yield return Make(AudioBackdrop, Strings.KeyActionAudioBackdrop, lane, Canvas, "W", bare: true);
            yield return Make(SnapToOnset, Strings.KeyActionSnapToOnset, lane, Canvas, "O", bare: true);
            yield return Make(NudgeToOnsets, Strings.KeyActionNudgeToOnsets, lane, Canvas, "Shift+O");
            yield return Make(ToggleView, Strings.KeyActionToggleView, lane, Canvas, "V", bare: true);
            yield return Make(TapTempo, Strings.KeyActionTapTempo, lane, Canvas, "B", bare: true);

            yield return Make(NoteLane1, Strings.KeyActionNoteLane1, notes, Canvas, "Digit1,NumPad1", bare: true, hold: true);
            yield return Make(NoteLane2, Strings.KeyActionNoteLane2, notes, Canvas, "Digit2,NumPad2", bare: true, hold: true);
            yield return Make(NoteLane3, Strings.KeyActionNoteLane3, notes, Canvas, "Digit3,NumPad3", bare: true, hold: true);
            yield return Make(NoteLane4, Strings.KeyActionNoteLane4, notes, Canvas, "Digit4,NumPad4", bare: true, hold: true);
            yield return Make(NoteLane5, Strings.KeyActionNoteLane5, notes, Canvas, "Digit5,NumPad5", bare: true, hold: true);
            yield return Make(NoteLane6, Strings.KeyActionNoteLane6, notes, Canvas, "Digit6,NumPad6", bare: true, hold: true);
            yield return Make(NoteOpen, Strings.KeyActionNoteOpen, notes, Canvas, "Digit0,NumPad0", bare: true, hold: true);
            yield return Make(NoteDoubleKick, Strings.KeyActionNoteDoubleKick, notes, Canvas, "Shift+Digit0,Shift+NumPad0", hold: true);
            yield return Make(NoteForceHopo, Strings.KeyActionNoteForceHopo, notes, Canvas, "H", bare: true);
            yield return Make(NoteForceStrum, Strings.KeyActionNoteForceStrum, notes, Canvas, "G", bare: true);
            yield return Make(NoteTap, Strings.KeyActionNoteTap, notes, Canvas, "T", bare: true);
            yield return Make(NoteCymbal, Strings.KeyActionNoteCymbal, notes, Canvas, "C", bare: true);
            yield return Make(NoteAccent, Strings.KeyActionNoteAccent, notes, Canvas, "A", bare: true);
            yield return Make(NoteGhost, Strings.KeyActionNoteGhost, notes, Canvas, "Shift+A");
            yield return Make(NoteStarPower, Strings.KeyActionNoteStarPower, notes, Canvas, "S", bare: true);

            yield return Make(DifficultyEasy, Strings.KeyActionDifficultyEasy, lane, Canvas, "Ctrl+Digit1");
            yield return Make(DifficultyMedium, Strings.KeyActionDifficultyMedium, lane, Canvas, "Ctrl+Digit2");
            yield return Make(DifficultyHard, Strings.KeyActionDifficultyHard, lane, Canvas, "Ctrl+Digit3");
            yield return Make(DifficultyExpert, Strings.KeyActionDifficultyExpert, lane, Canvas, "Ctrl+Digit4");

            yield return Make(MouseScroll, Strings.KeyActionMouseScroll, mouse, Canvas, "", locked: true, display: "Wheel");
            yield return Make(MouseFastScroll, Strings.KeyActionMouseFastScroll, mouse, Canvas, "", locked: true, display: "Shift+Wheel");
            yield return Make(MouseZoom, Strings.KeyActionMouseZoom, mouse, Canvas, "", locked: true, display: "Ctrl+Wheel");
            yield return Make(MouseSync, Strings.KeyActionMouseSync, mouse, Canvas, "", locked: true, display: "Ctrl+Shift (held)");

            yield return Make(PreviewFullScreen, Strings.KeyActionPreviewFullScreen, preview, Preview, "F11", bare: true);
            yield return Make(PreviewExitFullScreen, Strings.KeyActionPreviewExitFullScreen, preview, Preview, "Escape", bare: true, locked: true);

            yield return Make(PreviousRegion, Strings.KeyActionPreviousRegion, gen, GeneratePhrases, "OemOpenBrackets", bare: true);
            yield return Make(NextRegion, Strings.KeyActionNextRegion, gen, GeneratePhrases, "OemCloseBrackets", bare: true);
            yield return Make(ApplyPhrases, Strings.KeyActionApplyPhrases, gen, GeneratePhrases, "Ctrl+Enter");
        }

        // Chords are written as keybinds.json writes them, several separated by commas.
        private static KeyAction Make(
            string id, string name, string category, KeyScope scope, string defaults,
            bool bare = false, bool repeatable = false, bool locked = false, bool hold = false, bool reserved = false, bool yields = false, string? display = null) => new()
        {
            Id = id,
            Name = name,
            Category = category,
            Scope = scope,
            Defaults = defaults.Length == 0 ? [] : KeyChord.List(defaults.Split(',')),
            AllowBare = bare,
            Repeatable = repeatable,
            Locked = locked,
            Kind = hold ? KeyActionKind.Hold : KeyActionKind.Press,
            ReservedDuringPlaytest = reserved,
            YieldsToTextInput = yields,
            DisplayOverride = display,
        };
    }
}
