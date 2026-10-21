using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ChartPacker.Resources;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charts;
using ChartShared.Core.Metadata;
using ChartShared.Core.Scanning;
using ChartShared.Core.Validation;
using ChartShared.UI.ViewModels;

namespace ChartPacker.Services
{
    /// <summary>A song whose chart is generated into its draft: its lower difficulties, its star power (CHR066), or both.</summary>
    /// <param name="Title">The song's name, as the library lists it.</param>
    /// <param name="Pairs">Its <c>song.ini</c> as drafted.</param>
    /// <param name="Draft">A copy of its draft, which nothing changes, to read off the UI thread; null for none.</param>
    /// <param name="Targets">Each part's missing difficulties, highest first.</param>
    public sealed record GenerateDifficultiesSong(
        SongEntry Entry,
        string Title,
        IReadOnlyList<KeyValuePair<string, string>> Pairs,
        MetadataDraft? Draft,
        IReadOnlyDictionary<ChartInstrument, IReadOnlyList<ChartDifficulty>> Targets)
    {
        /// <summary>The parts star power is generated for too (CHR066), before the difficulties; none when it isn't.</summary>
        public IReadOnlyList<ChartInstrument> StarPower { get; init; } = [];

        /// <summary>Which of its difficulties are filled; null for every missing one.</summary>
        public LowerDifficultyScope? Scope { get; init; }

        /// <summary>What generating does to the song's chart: its difficulties filled as <see cref="Scope"/> says, with any star power first.</summary>
        public SongGenerationWork Work => new(Targets.Count > 0, StarPower, Scope);

        /// <summary>Each part's missing difficulties in <paramref name="targets"/>, e.g. "Guitar Hard, Medium; Drums Easy".</summary>
        public static string DescribeTargets(IReadOnlyDictionary<ChartInstrument, IReadOnlyList<ChartDifficulty>> targets) =>
            string.Join(
                Strings.ValidatorMissingPartSeparator,
                targets.Select(t => string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.ValidatorMissingPartFormat,
                    ChartLabels.InstrumentName(t.Key),
                    string.Join(", ", t.Value.Select(ChartLabels.DifficultyName)))));
    }
}
