using System.Globalization;
using System.Linq;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charting.Layout;
using ChartShared.Core.Charts;
using ChartShared.Core.Library;
using ChartShared.Core.Merging;
using ChartShared.Core.Metadata;
using ChartShared.UI.Resources;

namespace ChartShared.UI.ViewModels
{
    /// <summary>The names instruments, parts and difficulties are shown by.</summary>
    public static class ChartLabels
    {
        public static string InstrumentName(ChartInstrument instrument) => instrument switch
        {
            ChartInstrument.Guitar => SharedStrings.InstrumentGuitar,
            ChartInstrument.Bass => SharedStrings.InstrumentBass,
            ChartInstrument.Drums => SharedStrings.InstrumentDrums,
            ChartInstrument.Keys => SharedStrings.InstrumentKeys,
            ChartInstrument.Rhythm => SharedStrings.InstrumentRhythm,
            ChartInstrument.GuitarCoop => SharedStrings.InstrumentGuitarCoop,
            ChartInstrument.GhlGuitar => SharedStrings.InstrumentGhlGuitar,
            ChartInstrument.GhlRhythm => SharedStrings.InstrumentGhlRhythm,
            ChartInstrument.GhlCoop => SharedStrings.InstrumentGhlCoop,
            _ => SharedStrings.InstrumentGhlBass,
        };

        public static string DifficultyName(ChartDifficulty difficulty) => difficulty switch
        {
            ChartDifficulty.Easy => SharedStrings.DifficultyEasy,
            ChartDifficulty.Medium => SharedStrings.DifficultyMedium,
            ChartDifficulty.Hard => SharedStrings.DifficultyHard,
            _ => SharedStrings.DifficultyExpert,
        };

        /// <summary>The name of a vocal part: Vocals, or Harmony 1 to Harmony 3.</summary>
        public static string VocalTrackName(VocalTrack track) => track switch
        {
            VocalTrack.Vocals => SharedStrings.VocalTrackVocals,
            _ => string.Format(CultureInfo.CurrentCulture, SharedStrings.VocalTrackHarmonyFormat, (int)track),
        };

        /// <summary>
        /// The name of a MIDI track a finding is in: a vocal part's name for <c>PART VOCALS</c> or <c>HARM1</c> to
        /// <c>HARM3</c>, any other track's own name, or null for none.
        /// </summary>
        public static string? TrackName(string? midiTrack) =>
            VocalTracks.Of(midiTrack) is { } vocal ? VocalTrackName(vocal)
            : string.IsNullOrWhiteSpace(midiTrack) ? null
            : midiTrack.Trim();

        /// <summary>
        /// Where a finding is in the song, as "m12 · beat 3", or from where it starts to where it ends, as
        /// "m12 · beat 3 – m14 · beat 1", when <paramref name="end"/> is somewhere else.
        /// </summary>
        public static string PlaceText(MeasureBeat start, MeasureBeat? end = null)
        {
            string place = Place(start);
            return end is { } last && Place(last) is var to && to != place
                ? string.Format(CultureInfo.CurrentCulture, SharedStrings.FindingPlaceRangeFormat, place, to)
                : place;

            static string Place(MeasureBeat at) => string.Format(CultureInfo.CurrentCulture, SharedStrings.FindingPlaceFormat, at.Measure, at.Beat);
        }

        /// <summary>The one-letter name of <paramref name="difficulty"/>: E, M, H or X.</summary>
        public static string DifficultyLetter(ChartDifficulty difficulty) => difficulty switch
        {
            ChartDifficulty.Easy => SharedStrings.MergeDifficultyEasyLetter,
            ChartDifficulty.Medium => SharedStrings.MergeDifficultyMediumLetter,
            ChartDifficulty.Hard => SharedStrings.MergeDifficultyHardLetter,
            _ => SharedStrings.MergeDifficultyExpertLetter,
        };

        /// <summary>What <paramref name="count"/> is of: its difficulty, its harmony line, or, for vocals, just notes.</summary>
        public static string NoteCountLabel(NoteCount count) =>
            count.Difficulty is { } difficulty ? DifficultyName(difficulty)
            : count.Line > 0 ? string.Format(CultureInfo.CurrentCulture, SharedStrings.NoteCountHarmonyFormat, count.Line)
            : SharedStrings.NoteCountVocals;

        /// <summary><paramref name="count"/> notes, e.g. <c>1,234 notes</c>.</summary>
        public static string NoteCountText(int count) =>
            count == 1 ? SharedStrings.NoteCountOne : string.Format(CultureInfo.CurrentCulture, SharedStrings.NoteCountFormat, count);

        /// <summary>The name of the instrument <paramref name="slot"/> rates, e.g. <c>Pro Drums (PS)</c>.</summary>
        public static string SlotName(DifficultySlot slot) => slot.Key switch
        {
            "diff_drums_real" => SharedStrings.InstrumentProDrums,
            "diff_drums_real_ps" => SharedStrings.InstrumentProDrumsPs,
            "diff_keys_real_ps" => SharedStrings.InstrumentProKeysPs,
            "diff_band" => SharedStrings.InstrumentBand,
            _ => MidiPartCatalog.Known.FirstOrDefault(p => p.Id == slot.PartId) is { } part ? PartName(part) : slot.Key,
        };

        /// <summary>The short tag that marks <paramref name="modifier"/>'s variant on an icon, or <see langword="null"/> for none.</summary>
        public static string? ModifierTag(DifficultyModifier modifier) => modifier switch
        {
            DifficultyModifier.Coop => SharedStrings.DifficultyTagCoop,
            DifficultyModifier.Pro => SharedStrings.DifficultyTagPro,
            DifficultyModifier.Pro22 => SharedStrings.DifficultyTagPro22,
            DifficultyModifier.Ghl => SharedStrings.DifficultyTagGhl,
            DifficultyModifier.PhaseShift => SharedStrings.DifficultyTagPhaseShift,
            _ => null,
        };

        /// <summary>The name of <paramref name="part"/>, or its first track's name for a part that isn't known.</summary>
        public static string PartName(MidiPart part) => part.Id switch
        {
            "guitar" => SharedStrings.InstrumentGuitar,
            "guitar_coop" => SharedStrings.InstrumentGuitarCoop,
            "rhythm" => SharedStrings.InstrumentRhythm,
            "bass" => SharedStrings.InstrumentBass,
            "drums" => SharedStrings.InstrumentDrums,
            "keys" => SharedStrings.InstrumentKeys,
            "pro_keys" => SharedStrings.MergePartProKeys,
            "vocals" => SharedStrings.MergePartVocals,
            "harmonies" => SharedStrings.MergePartHarmonies,
            "pro_guitar" => SharedStrings.MergePartProGuitar,
            "pro_guitar_22" => SharedStrings.MergePartProGuitar22,
            "pro_bass" => SharedStrings.MergePartProBass,
            "pro_bass_22" => SharedStrings.MergePartProBass22,
            "guitar_ghl" => SharedStrings.InstrumentGhlGuitar,
            "bass_ghl" => SharedStrings.InstrumentGhlBass,
            "rhythm_ghl" => SharedStrings.MergePartGhlRhythm,
            "guitar_coop_ghl" => SharedStrings.MergePartGhlCoop,
            "dance" => SharedStrings.MergePartDance,
            _ => part.TrackNames[0],
        };
    }
}
