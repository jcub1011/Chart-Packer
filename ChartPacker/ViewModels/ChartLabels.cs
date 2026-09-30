using ChartPacker.Core.Charts;
using ChartPacker.Resources;

namespace ChartPacker.ViewModels
{
    /// <summary>The names instruments and difficulties are shown by.</summary>
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
    }
}
