using ChartBuilder.Services;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charting.Layout;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// What the Edit menu's items are enabled for: the same things the canvas's keys act on, so an item is dimmed when its
    /// key would do nothing. Kept apart from the window's view model so it can be checked on a session alone.
    /// </summary>
    public static class EditMenuState
    {
        /// <summary>Whether Copy and Cut have notes, or a range across the lanes, to put on the clipboard.</summary>
        public static bool CanCopy(EditorSelection selection) =>
            selection.HasNotes || selection.HasRangeAcrossLanes || selection.HasVocalNotes;

        /// <summary>
        /// Whether Delete has something to take out, as <see cref="EditorTools.DeleteSelection"/> takes it: notes, a range
        /// across the lanes, vocal notes and phrases, phrases, disco flips and tempo markers. A range of no lanes isn't one.
        /// </summary>
        public static bool CanDelete(EditorSelection selection) =>
            selection.HasNotes || selection.HasRangeAcrossLanes || selection.HasVocalNotes || selection.VocalPhrases.Count > 0
            || selection.Phrases.Count > 0 || selection.DiscoFlips.Count > 0 || selection.TempoTicks.Count > 0;

        /// <summary>Whether Quantize has notes selected in one track; vocal notes aren't quantized.</summary>
        public static bool CanQuantize(EditorSelection selection) => selection.Track is not null && selection.HasNotes;

        /// <summary>Whether Next and Previous match have passages found to go to.</summary>
        public static bool CanStepFind(EditorSession session) => session.Find.HasMatches;

        /// <summary>Whether Select all has a lane to select in: the focused highway or vocal part, or the vocal part being edited.</summary>
        public static bool CanSelectAll(EditorSession session) =>
            session.Focused is not null || session.FocusedVocal is not null || session.Selection.Vocal is not null;

        /// <summary>
        /// The vocal part Select all selects the notes of, as the lane's Ctrl+A does: the part whose lane was last focused,
        /// else the one being edited; null to select across the focused highway.
        /// </summary>
        public static VocalTrack? SelectAllVocalTarget(EditorSession session) => session.FocusedVocal ?? session.Selection.Vocal;

        /// <summary>
        /// Whether Paste has a clip to paste where <see cref="PasteTarget"/> puts it, by the same checks the paste itself makes,
        /// so an item is dimmed when it would be refused.
        /// </summary>
        public static bool CanPaste(EditorSession session, ChartClipboard clipboard, VocalTrack? laneVocal = null) =>
            clipboard.Current is { } clip && ClipboardEdits.CanPaste(session, clip, PasteTarget(session, clip, laneVocal), out _) is null;

        /// <summary>
        /// The vocal part a paste goes into: for a clip of vocal notes, the part whose lane was last focused, else the part being
        /// edited, else the part they were copied from (a cut leaves nothing selected); null for a highway's notes, which paste
        /// into the focused lane.
        /// </summary>
        public static VocalTrack? PasteVocalTarget(EditorSession session, ChartClip clip) =>
            clip.Vocals is { } vocals ? session.FocusedVocal ?? session.Selection.Vocal ?? vocals.Source : null;

        /// <summary>
        /// Where a paste started in a lane puts <paramref name="clip"/>: a vocal part's own lane pastes into its part, and any
        /// other pastes as the Edit menu does (<see cref="PasteVocalTarget"/>).
        /// </summary>
        public static VocalTrack? PasteTarget(EditorSession session, ChartClip clip, VocalTrack? laneVocal) =>
            laneVocal ?? PasteVocalTarget(session, clip);
    }
}
