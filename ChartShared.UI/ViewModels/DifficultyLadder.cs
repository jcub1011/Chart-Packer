using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using ChartShared.UI.Resources;

namespace ChartShared.UI.ViewModels
{
    /// <summary>
    /// A difficulty on the ladder (<see cref="DifficultyLadder"/>): its suggested rating, and its peak notes per second as a
    /// share of the part's highest.
    /// </summary>
    /// <param name="IsDraft">Whether it's one of the drafts.</param>
    /// <param name="IsOutOfOrder">Whether it's rated as hard as the difficulty above it, or harder.</param>
    public sealed record DifficultyLadderRow(string Difficulty, string Tier, string Peak, double Share, bool IsDraft, bool IsOutOfOrder);

    /// <summary>
    /// The difficulty estimator's view of a part (charter-spec C9, C18 item 3): each difficulty with notes, Expert first,
    /// with its suggested rating, the drafts of a reduction in place, and where one isn't easier than the one above it.
    /// Chart Builder's Reduce dialog and Chart Packer's Generate difficulties dialog show it (<c>DifficultyLadderView</c>).
    /// </summary>
    /// <param name="OrderWarning">Where a lower difficulty isn't easier than the one above it; empty when they're in order.</param>
    public sealed record DifficultyLadder(IReadOnlyList<DifficultyLadderRow> Rows, string OrderWarning)
    {
        /// <summary>No ladder, before a check comes in.</summary>
        public static DifficultyLadder Empty { get; } = new([], "");

        public bool HasOrderWarning => OrderWarning.Length > 0;

        /// <summary>The ladder of <paramref name="check"/>, with <paramref name="drafted"/> marked as the drafts.</summary>
        public static DifficultyLadder Of(ReductionCheck check, IReadOnlyCollection<ChartDifficulty> drafted)
        {
            ArgumentNullException.ThrowIfNull(check);
            ArgumentNullException.ThrowIfNull(drafted);
            double highest = check.Estimates.Values.Select(static e => e.Basis.PeakNps).DefaultIfEmpty(0).Max();
            List<DifficultyLadderRow> rows = [.. Enum.GetValues<ChartDifficulty>().OrderDescending()
                .Where(check.Estimates.ContainsKey)
                .Select(d => new DifficultyLadderRow(
                    ChartLabels.DifficultyName(d),
                    check.Estimates[d].Tier.ToString(CultureInfo.CurrentCulture),
                    string.Format(CultureInfo.CurrentCulture, SharedStrings.DifficultyLadderPeakFormat, check.Estimates[d].Basis.PeakNps),
                    highest > 0 ? check.Estimates[d].Basis.PeakNps / highest : 0,
                    drafted.Contains(d),
                    check.OutOfOrder.Contains(d)))];
            string warning = check.OutOfOrder.Count == 0
                ? ""
                : string.Format(CultureInfo.CurrentCulture, SharedStrings.DifficultyLadderOrderWarningFormat, string.Join(", ", check.OutOfOrder.Select(ChartLabels.DifficultyName)));
            return new DifficultyLadder(rows, warning);
        }
    }
}
