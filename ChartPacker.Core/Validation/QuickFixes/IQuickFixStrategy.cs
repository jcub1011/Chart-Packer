namespace ChartPacker.Core.Validation.QuickFixes;

/// <summary>
/// Works out the fix for findings of one rule. A strategy only proposes a fix when there's exactly one right
/// answer; it's registered in <see cref="QuickFixRegistry"/>.
/// </summary>
public interface IQuickFixStrategy
{
    /// <summary>The rule whose findings this fixes, e.g. <see cref="SngRules.Sng060"/>.</summary>
    string RuleId { get; }

    /// <summary>The fix for <paramref name="finding"/>, found in <paramref name="pairs"/>.</summary>
    /// <param name="finding">A finding of <see cref="RuleId"/>, naming its key.</param>
    /// <param name="pairs">The metadata the finding was found in, as drafted.</param>
    /// <returns>The fix, or <see langword="null"/> when there's no single right one.</returns>
    QuickFix? Propose(Finding finding, IReadOnlyList<KeyValuePair<string, string>> pairs);
}
