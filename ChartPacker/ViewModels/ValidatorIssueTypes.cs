using System;
using System.Collections.Generic;
using ChartPacker.Core.Validation;
using ChartPacker.Resources;
using ChartPacker.Services;

namespace ChartPacker.ViewModels
{
    /// <summary>
    /// The kinds of issue the Validator lists, by rule ID, as its type filter names and orders them.
    /// </summary>
    public static class ValidatorIssueTypes
    {
        /// <summary>A song whose metadata couldn't be read at all. Not a rule of the catalogue.</summary>
        public const string Unreadable = "unreadable";

        // The order the filter shows them in: problems with the key first, then with the value, then the song.
        private static readonly (string Id, Func<string> Name)[] Catalogue =
        [
            (SngRules.Sng022, () => Strings.ValidatorTypeDuplicateKey),
            (SngRules.Sng023, () => Strings.ValidatorTypeEmptyKey),
            (SngRules.Sng020, () => Strings.ValidatorTypeKeyCharacter),
            (SngRules.Sng021, () => Strings.ValidatorTypeValueCharacter),
            (SngRules.Sng024, () => Strings.ValidatorTypeWrongType),
            (SngRules.Sng060, () => Strings.ValidatorTypeBoolean),
            (SngRules.Sng061, () => Strings.ValidatorTypeEmptyValue),

            // Chart lint (charter-spec C16 item 5), by the catalogue's groups.
            (ValidatorIssueTypesIds.ChartTiming, () => Strings.ValidatorTypeChartTiming),
            (ValidatorIssueTypesIds.ChartFrets, () => Strings.ValidatorTypeChartFrets),
            (ValidatorIssueTypesIds.ChartDrums, () => Strings.ValidatorTypeChartDrums),
            (ValidatorIssueTypesIds.ChartPhrases, () => Strings.ValidatorTypeChartPhrases),
            (ValidatorIssueTypesIds.ChartSong, () => Strings.ValidatorTypeChartSong),
            (ValidatorIssueTypesIds.ChartVocals, () => Strings.ValidatorTypeChartVocals),
            (ValidatorIssueTypesIds.ChartLoad, () => Strings.ValidatorTypeChartLoad),
            (Unreadable, () => Strings.ValidatorTypeUnreadable),
        ];

        /// <summary>What the type is called; a rule outside the catalogue goes by its ID.</summary>
        public static string NameOf(string id)
        {
            foreach (var (known, name) in Catalogue)
            {
                if (known == id)
                    return name();
            }

            return id;
        }

        /// <summary>Orders types as the filter shows them: the catalogue's order, then any others by ID.</summary>
        public static IComparer<string> Order { get; } = Comparer<string>.Create((a, b) =>
        {
            int byRank = RankOf(a).CompareTo(RankOf(b));
            return byRank != 0 ? byRank : string.CompareOrdinal(a, b);
        });

        private static int RankOf(string id)
        {
            for (int i = 0; i < Catalogue.Length; i++)
            {
                if (Catalogue[i].Id == id)
                    return i;
            }

            return Catalogue.Length;
        }
    }
}
