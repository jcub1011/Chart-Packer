using System.Collections.Generic;
using System.Globalization;
using ChartPacker.Resources;
using ChartShared.Core.Validation;
using ChartShared.UI.ViewModels;

namespace ChartPacker.Services
{
    /// <summary>
    /// Chart lint in the library (charter-spec C16 item 5): the session's results, shared by the Validator and the detail
    /// panel, and how a chart finding is put in words there.
    /// </summary>
    public static class LibraryChartLint
    {
        /// <summary>The results of the session, kept in memory only, so a song unchanged since isn't linted again.</summary>
        public static SongChartLintCache Cache { get; } = new();

        /// <summary>The finding's message with its rule, and where it is when it has a place: "CHR022 · … · Guitar Expert, m12 b3".</summary>
        public static string Describe(SongChartFinding finding, int count = 1)
        {
            var f = finding.Finding;
            string text = string.Format(CultureInfo.CurrentCulture, Strings.ChartFindingFormat, f.RuleId, f.Message);
            if (count > 1)
                text = string.Format(CultureInfo.CurrentCulture, Strings.ChartFindingTimesFormat, text, count);
            return Where(finding) is { } where ? string.Format(CultureInfo.CurrentCulture, Strings.ChartFindingWhereFormat, text, where) : text;
        }

        /// <summary>Where the finding is, in words, or null for the song as a whole.</summary>
        public static string? Where(SongChartFinding finding)
        {
            if (finding.Place is not { } place)
                return finding.Finding.Track;
            string at = string.Format(CultureInfo.CurrentCulture, Strings.ChartFindingPlaceFormat, place.Measure, place.Beat);
            if (finding.Finding.Track is { } track)
                return string.Format(CultureInfo.CurrentCulture, Strings.ChartFindingPartPlaceFormat, track, at);
            if (finding.Finding.Location is not { } location)
                return at;
            string part = location.Difficulty is { } difficulty
                ? string.Format(CultureInfo.CurrentCulture, Strings.ChartFindingTrackFormat, ChartLabels.InstrumentName(location.Part), ChartLabels.DifficultyName(difficulty))
                : ChartLabels.InstrumentName(location.Part);
            return string.Format(CultureInfo.CurrentCulture, Strings.ChartFindingPartPlaceFormat, part, at);
        }

        /// <summary>
        /// Where the finding is, as the chart issues list's place pill shows it: in measures and beats, from where it
        /// starts to where it ends when it covers more than one place; null for nowhere in particular.
        /// </summary>
        public static string? PlaceOf(SongChartFinding finding) =>
            finding.Place is { } start ? ChartLabels.PlaceText(start, finding.EndPlace) : null;

        /// <summary>The Validator's type for a chart finding, by the catalogue's group (<see cref="ChrRules.CategoryOf"/>).</summary>
        public static string TypeOf(string ruleId) => ChrRules.CategoryOf(ruleId) switch
        {
            ChrCategory.Timing => ValidatorIssueTypesIds.ChartTiming,
            ChrCategory.Frets => ValidatorIssueTypesIds.ChartFrets,
            ChrCategory.Drums => ValidatorIssueTypesIds.ChartDrums,
            ChrCategory.Phrases => ValidatorIssueTypesIds.ChartPhrases,
            ChrCategory.Song => ValidatorIssueTypesIds.ChartSong,
            ChrCategory.Vocals => ValidatorIssueTypesIds.ChartVocals,
            _ => ValidatorIssueTypesIds.ChartLoad,
        };
    }

    /// <summary>The Validator's types for chart findings, one per group of the catalogue.</summary>
    public static class ValidatorIssueTypesIds
    {
        public const string ChartTiming = "chr-timing";
        public const string ChartFrets = "chr-frets";
        public const string ChartDrums = "chr-drums";
        public const string ChartPhrases = "chr-phrases";
        public const string ChartSong = "chr-song";
        public const string ChartVocals = "chr-vocals";
        public const string ChartLoad = "chr-load";

        public static IReadOnlyList<string> All { get; } = [ChartTiming, ChartFrets, ChartDrums, ChartPhrases, ChartSong, ChartVocals, ChartLoad];
    }
}
