using System.Globalization;
using System.Linq;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Library;
using ChartPacker.Core.Merging;
using ChartPacker.Core.Metadata;
using ChartPacker.Resources;

namespace ChartPacker.ViewModels
{
    /// <summary>The names instruments, parts and difficulties are shown by.</summary>
    internal static class ChartLabels
    {
        public static string InstrumentName(ChartInstrument instrument) => instrument switch
        {
            ChartInstrument.Guitar => Strings.InstrumentGuitar,
            ChartInstrument.Bass => Strings.InstrumentBass,
            ChartInstrument.Drums => Strings.InstrumentDrums,
            ChartInstrument.Keys => Strings.InstrumentKeys,
            ChartInstrument.Rhythm => Strings.InstrumentRhythm,
            ChartInstrument.GuitarCoop => Strings.InstrumentGuitarCoop,
            ChartInstrument.GhlGuitar => Strings.InstrumentGhlGuitar,
            _ => Strings.InstrumentGhlBass,
        };

        public static string DifficultyName(ChartDifficulty difficulty) => difficulty switch
        {
            ChartDifficulty.Easy => Strings.DifficultyEasy,
            ChartDifficulty.Medium => Strings.DifficultyMedium,
            ChartDifficulty.Hard => Strings.DifficultyHard,
            _ => Strings.DifficultyExpert,
        };

        /// <summary>The one-letter name of <paramref name="difficulty"/>: E, M, H or X.</summary>
        public static string DifficultyLetter(ChartDifficulty difficulty) => difficulty switch
        {
            ChartDifficulty.Easy => Strings.MergeDifficultyEasyLetter,
            ChartDifficulty.Medium => Strings.MergeDifficultyMediumLetter,
            ChartDifficulty.Hard => Strings.MergeDifficultyHardLetter,
            _ => Strings.MergeDifficultyExpertLetter,
        };

        /// <summary>What <paramref name="count"/> is of: its difficulty, its harmony line, or, for vocals, just notes.</summary>
        public static string NoteCountLabel(NoteCount count) =>
            count.Difficulty is { } difficulty ? DifficultyName(difficulty)
            : count.Line > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.NoteCountHarmonyFormat, count.Line)
            : Strings.NoteCountVocals;

        /// <summary><paramref name="count"/> notes, e.g. <c>1,234 notes</c>.</summary>
        public static string NoteCountText(int count) =>
            count == 1 ? Strings.NoteCountOne : string.Format(CultureInfo.CurrentCulture, Strings.NoteCountFormat, count);

        /// <summary>The name of the instrument <paramref name="slot"/> rates, e.g. <c>Pro Drums (PS)</c>.</summary>
        public static string SlotName(DifficultySlot slot) => slot.Key switch
        {
            "diff_drums_real" => Strings.InstrumentProDrums,
            "diff_drums_real_ps" => Strings.InstrumentProDrumsPs,
            "diff_keys_real_ps" => Strings.InstrumentProKeysPs,
            "diff_band" => Strings.InstrumentBand,
            _ => MidiPartCatalog.Known.FirstOrDefault(p => p.Id == slot.PartId) is { } part ? PartName(part) : slot.Key,
        };

        /// <summary>The short tag that marks <paramref name="modifier"/>'s variant on an icon, or <see langword="null"/> for none.</summary>
        public static string? ModifierTag(DifficultyModifier modifier) => modifier switch
        {
            DifficultyModifier.Coop => Strings.DifficultyTagCoop,
            DifficultyModifier.Pro => Strings.DifficultyTagPro,
            DifficultyModifier.Pro22 => Strings.DifficultyTagPro22,
            DifficultyModifier.Ghl => Strings.DifficultyTagGhl,
            DifficultyModifier.PhaseShift => Strings.DifficultyTagPhaseShift,
            _ => null,
        };

        /// <summary>The name of <paramref name="part"/>, or its first track's name for a part that isn't known.</summary>
        public static string PartName(MidiPart part) => part.Id switch
        {
            "guitar" => Strings.InstrumentGuitar,
            "guitar_coop" => Strings.InstrumentGuitarCoop,
            "rhythm" => Strings.InstrumentRhythm,
            "bass" => Strings.InstrumentBass,
            "drums" => Strings.InstrumentDrums,
            "keys" => Strings.InstrumentKeys,
            "pro_keys" => Strings.MergePartProKeys,
            "vocals" => Strings.MergePartVocals,
            "harmonies" => Strings.MergePartHarmonies,
            "pro_guitar" => Strings.MergePartProGuitar,
            "pro_guitar_22" => Strings.MergePartProGuitar22,
            "pro_bass" => Strings.MergePartProBass,
            "pro_bass_22" => Strings.MergePartProBass22,
            "guitar_ghl" => Strings.InstrumentGhlGuitar,
            "bass_ghl" => Strings.InstrumentGhlBass,
            "rhythm_ghl" => Strings.MergePartGhlRhythm,
            "guitar_coop_ghl" => Strings.MergePartGhlCoop,
            "dance" => Strings.MergePartDance,
            _ => part.TrackNames[0],
        };
    }
}
