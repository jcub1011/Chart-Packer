using System.Diagnostics.CodeAnalysis;
using ChartPacker.Core.Metadata;

namespace ChartPacker.Core.Validation.QuickFixes;

/// <summary>
/// The quick fixes for metadata findings whose fix is simple and has one right answer (project spec §6), one
/// <see cref="IQuickFixStrategy"/> per rule.
/// </summary>
/// <remarks>
/// A fix is worked out from the metadata as drafted, at the time it's asked for, so it's never stale. To add one,
/// write a strategy and list it in <see cref="Strategies"/>.
/// </remarks>
public static class QuickFixRegistry
{
    /// <summary>Every strategy, at most one per rule.</summary>
    public static IReadOnlyList<IQuickFixStrategy> Strategies { get; } =
    [
        new LenientBooleanFix(),
        new EmptyValueFix(),
        new ForbiddenValueCharacterFix(),
        new LegacyKeyFix(),
        new DuplicateKeyFix(),
    ];

    private static readonly Dictionary<string, IQuickFixStrategy> ByRule = Strategies.ToDictionary(s => s.RuleId, StringComparer.Ordinal);

    /// <summary>Whether findings of <paramref name="ruleId"/> can have a quick fix.</summary>
    public static bool IsSupported(string ruleId)
    {
        ArgumentNullException.ThrowIfNull(ruleId);
        return ByRule.ContainsKey(ruleId);
    }

    /// <summary>The fix for <paramref name="finding"/>, found in <paramref name="pairs"/>.</summary>
    /// <param name="finding">A finding about one key's pair, from <see cref="MetadataValidator"/>.</param>
    /// <param name="pairs">The metadata the finding was found in, as drafted.</param>
    /// <returns>The fix, or <see langword="null"/> when the rule has none or this finding has no single right one.</returns>
    public static QuickFix? Propose(Finding finding, IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(pairs);
        return ByRule.TryGetValue(finding.RuleId, out var strategy) ? strategy.Propose(finding, pairs) : null;
    }

    /// <summary>Checks <paramref name="pairs"/> and proposes a fix for every finding that has one, in the findings' order.</summary>
    /// <param name="pairs">The metadata as drafted.</param>
    /// <param name="duplicateKeys">The keys a <c>song.ini</c> repeats, as for <see cref="MetadataValidator.Validate"/>.</param>
    public static IReadOnlyList<QuickFix> ProposeAll(
        IReadOnlyList<KeyValuePair<string, string>> pairs,
        IReadOnlyCollection<string>? duplicateKeys = null)
    {
        ArgumentNullException.ThrowIfNull(pairs);

        var fixes = new List<QuickFix>();
        foreach (var finding in MetadataValidator.Validate(pairs, duplicateKeys))
        {
            if (Propose(finding, pairs) is { } fix)
                fixes.Add(fix);
        }

        return fixes;
    }

    /// <summary>
    /// Fixes everything about <paramref name="key"/> that has a fix, in <paramref name="item"/>'s draft. Fixes are
    /// applied one at a time, each worked out from the metadata as the last left it, failures before hints, so two
    /// fixes to one pair (stripping a legacy key's value, then renaming it) don't undo each other.
    /// </summary>
    /// <param name="item">The song's saved metadata, the keys its <c>song.ini</c> repeats, and its draft, which the fixes go into.</param>
    /// <param name="key">The key whose findings to fix, ignoring case.</param>
    /// <param name="includeHints">Whether to fix hints, such as a legacy key, as well as failures.</param>
    /// <returns>How many fixes were applied.</returns>
    public static int FixKey(MetadataItem item, string key, bool includeHints = true)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(key);

        // Each fix clears its finding, so this is only a guard against two strategies undoing each other forever.
        int maxFixes = Strategies.Count * 2;
        int applied = 0;
        while (applied < maxFixes && NextFix(item, key, includeHints) is { } fix)
        {
            fix.ApplyTo(item);
            applied++;
        }

        return applied;
    }

    private static QuickFix? NextFix(MetadataItem item, string key, bool includeHints)
    {
        var pairs = item.Draft.ApplyTo(item.Original);
        var findings = MetadataValidator.Validate(pairs, item.RemainingDuplicateKeys())
            .Where(f => f.Key is not null && f.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            .Where(f => includeHints || MetadataValidator.IsFailing(f))
            .OrderBy(f => MetadataValidator.IsFailing(f) ? 0 : 1);
        foreach (var finding in findings)
        {
            if (Propose(finding, pairs) is { } fix)
                return fix;
        }

        return null;
    }

    /// <summary>
    /// The finding's key and its value in <paramref name="pairs"/>: the first pair with the key, ignoring case, as
    /// the editor shows it.
    /// </summary>
    internal static bool TryGetValue(
        Finding finding,
        IReadOnlyList<KeyValuePair<string, string>> pairs,
        [NotNullWhen(true)] out string? key,
        [NotNullWhen(true)] out string? value)
    {
        if (finding.Key is { Length: > 0 } findingKey)
        {
            foreach (var pair in pairs)
            {
                if (pair.Key.Equals(findingKey, StringComparison.OrdinalIgnoreCase))
                {
                    key = pair.Key;
                    value = pair.Value;
                    return true;
                }
            }
        }

        key = null;
        value = null;
        return false;
    }
}
