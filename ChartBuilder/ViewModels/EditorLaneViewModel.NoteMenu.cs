using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ChartBuilder.Resources;
using ChartBuilder.Services;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charting.Layout;
using ChartShared.Core.Charts;

namespace ChartBuilder.ViewModels
{
    public sealed partial class EditorLaneViewModel
    {
        /// <summary>
        /// The menu a right click in the lane opens, for the selected notes: Quantize, then a chord's type and open note on a
        /// fret part; cymbal or tom, the dynamics and 2× kick on drums; sung or spoken and slide on a vocal part. Each item
        /// ticks what every selected note is, as Properties does. Empty when none of the lane's notes is selected.
        /// </summary>
        public IReadOnlyList<LaneMenuItem> BuildNoteMenu()
        {
            if (Editor is not { } editor)
                return [];
            var session = editor.Session;
            var selection = session.Selection;
            if (Vocal is { } vocal)
                return selection.Vocal == vocal && selection.HasVocalNotes ? VocalNoteMenu(session, vocal) : [];
            if (Track is not { } track || selection.Track != track || selection.Notes.Count == 0 || session.TrackOf(track) is not { } edited)
                return [];
            var items = edited.Kind == ChartInstrumentKind.Drums ? DrumNoteMenu(session, track) : FretNoteMenu(session.Tools);
            var quantize = LaneMenuItem.Action(
                string.Format(CultureInfo.CurrentCulture, Strings.NoteMenuQuantizeFormat, BuilderSettingsCatalog.SnapLabel(editor.QuantizeGrid.Division)),
                isEnabled: true,
                editor.QuantizeSelection);
            return items.Count == 0 ? [quantize] : [quantize, LaneMenuItem.Separator, .. items];
        }

        private static List<LaneMenuItem> FretNoteMenu(EditorTools tools)
        {
            var type = tools.SelectedChordType();
            bool open = tools.IsSelectionOpen();
            return
            [
                LaneMenuItem.Choice(Strings.NoteMenuNatural, type == ChordFlags.None, () => tools.SetChordType(ChordFlags.None)),
                LaneMenuItem.Choice(Strings.NoteMenuForceHopo, type == ChordFlags.ForceHopo, () => tools.SetChordType(ChordFlags.ForceHopo)),
                LaneMenuItem.Choice(Strings.NoteMenuForceStrum, type == ChordFlags.ForceStrum, () => tools.SetChordType(ChordFlags.ForceStrum)),
                LaneMenuItem.Choice(Strings.PropertiesTap, type == ChordFlags.Tap, () => tools.SetChordType(ChordFlags.Tap)),
                LaneMenuItem.Separator,
                LaneMenuItem.Choice(Strings.NoteMenuOpen, open, () => tools.SetOpen(!open)),
            ];
        }

        private static List<LaneMenuItem> DrumNoteMenu(EditorSession session, ChartTrackKey track)
        {
            var tools = session.Tools;
            var part = session.Chart.PartOf(track.Instrument)!;
            var notes = session.Selection.Notes;
            var pads = notes.Where(static n => n.Lane > 0).ToList();
            var kicks = notes.Where(static n => n.Lane == 0).ToList();

            // Not a yellow inside a disco flip section, which the games play as red (charter-spec C6 item 7).
            var cymbals = session.Chart.DrumsVariant == DrumsVariant.Pro
                ? pads.Where(n => tools.CanToggleCymbal(track, n)).ToList()
                : [];

            var groups = new List<List<LaneMenuItem>>();
            if (cymbals.Count > 0)
            {
                bool allToms = cymbals.All(n => DrumCymbals.IsTom(part, n.Lane, n.Tick));
                bool allCymbals = cymbals.All(n => !DrumCymbals.IsTom(part, n.Lane, n.Tick));
                groups.Add(
                [
                    LaneMenuItem.Choice(Strings.PropertiesCymbal, allCymbals, () => tools.SetTom(false)),
                    LaneMenuItem.Choice(Strings.NoteMenuTom, allToms, () => tools.SetTom(true)),
                ]);
            }

            if (pads.Count > 0)
            {
                groups.Add(
                [
                    LaneMenuItem.Choice(Strings.NoteMenuNormalHit, pads.All(static n => n.Dynamics == NoteDynamics.None), () => tools.SetDynamics(NoteDynamics.None)),
                    LaneMenuItem.Choice(Strings.PropertiesAccent, pads.All(static n => n.Dynamics == NoteDynamics.Accent), () => tools.SetDynamics(NoteDynamics.Accent)),
                    LaneMenuItem.Choice(Strings.PropertiesGhost, pads.All(static n => n.Dynamics == NoteDynamics.Ghost), () => tools.SetDynamics(NoteDynamics.Ghost)),
                ]);
            }

            if (kicks.Count > 0 && track.Difficulty == ChartDifficulty.Expert)
            {
                bool doubled = kicks.All(static k => k.DoubleKick);
                groups.Add([LaneMenuItem.Choice(Strings.PropertiesDoubleKick, doubled, () => tools.SetDoubleKick(!doubled))]);
            }

            return Joined(groups);
        }

        private static List<LaneMenuItem> VocalNoteMenu(EditorSession session, VocalTrack vocal)
        {
            if (session.VocalContentOf(vocal) is not { } content)
                return [];
            var tools = session.Tools;
            var ticks = session.Selection.VocalNotes.Select(static n => n.Tick).ToList();
            bool spoken = ticks.All(t => VocalEdits.IsSpoken(content, t));
            bool sung = ticks.All(t => !VocalEdits.IsSpoken(content, t));
            bool slide = ticks.All(t => VocalEdits.IsSlide(content, t));
            return
            [
                LaneMenuItem.Choice(Strings.NoteMenuSung, sung, () => tools.SetVocalSpoken(false)),
                LaneMenuItem.Choice(Strings.PropertiesSpoken, spoken, () => tools.SetVocalSpoken(true)),
                LaneMenuItem.Separator,
                LaneMenuItem.Choice(Strings.PropertiesSlide, slide, () => tools.SetVocalSlide(!slide)),
            ];
        }

        /// <summary>The groups' items, with a line between each group and the next.</summary>
        private static List<LaneMenuItem> Joined(List<List<LaneMenuItem>> groups)
        {
            var items = new List<LaneMenuItem>();
            foreach (var group in groups)
            {
                if (items.Count > 0)
                    items.Add(LaneMenuItem.Separator);
                items.AddRange(group);
            }

            return items;
        }
    }
}
