using ChartPacker.Core.Charts;
using ChartPacker.Core.Merging;
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
