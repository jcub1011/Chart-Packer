using System.Globalization;
using System.Text;
using ChartShared.Core.Metadata;
using ChartShared.Core.Resources;

namespace ChartShared.Core.Validation.QuickFixes;

/// <summary>A boolean written another way, e.g. <c>1</c> or <c>true</c> (SNG060): it's written as <c>True</c> or <c>False</c>.</summary>
public sealed class LenientBooleanFix : IQuickFixStrategy
{
    public string RuleId => SngRules.Sng060;

    public QuickFix? Propose(Finding finding, IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        if (!QuickFixRegistry.TryGetValue(finding, pairs, out string? key, out string? value)
            || MetadataValidator.CanonicalBoolean(value) is not { } canonical)
        {
            return null;
        }

        return new QuickFix(RuleId, key,
            string.Format(CultureInfo.CurrentCulture, FindingMessages.QuickFixSetValueFormat, canonical),
            [new MetadataEdit(key, canonical)]);
    }
}

/// <summary>A pair with an empty value (SNG061), which packing would leave out anyway: it's removed.</summary>
public sealed class EmptyValueFix : IQuickFixStrategy
{
    public string RuleId => SngRules.Sng061;

    public QuickFix? Propose(Finding finding, IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        if (!QuickFixRegistry.TryGetValue(finding, pairs, out string? key, out string? value) || value.Length != 0)
            return null;

        return new QuickFix(RuleId, key, FindingMessages.QuickFixRemoveKey, [new MetadataEdit(key, null)]);
    }
}

/// <summary>
/// A value holding characters <c>song.ini</c> can't (SNG021): <c>;</c> and NUL are dropped, and each line break
/// becomes a space. A value left empty is removed.
/// </summary>
public sealed class ForbiddenValueCharacterFix : IQuickFixStrategy
{
    public string RuleId => SngRules.Sng021;

    public QuickFix? Propose(Finding finding, IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        if (!QuickFixRegistry.TryGetValue(finding, pairs, out string? key, out string? value)
            || !MetadataRules.ValueHasForbiddenCharacter(value))
        {
            return null;
        }

        string cleaned = Clean(value);
        return new QuickFix(RuleId, key, FindingMessages.QuickFixStripForbidden,
            [new MetadataEdit(key, cleaned.Length == 0 ? null : cleaned)]);
    }

    /// <summary>Drops <c>;</c> and NUL, turns each run of <c>\r</c> and <c>\n</c> into one space, and trims.</summary>
    internal static string Clean(string value)
    {
        var builder = new StringBuilder(value.Length);
        bool inBreak = false;
        foreach (char c in value)
        {
            if (c is '\r' or '\n')
            {
                if (!inBreak)
                    builder.Append(' ');
                inBreak = true;
                continue;
            }

            inBreak = false;
            if (c is not (';' or '\0'))
                builder.Append(c);
        }

        return builder.ToString().Trim();
    }
}

/// <summary>
/// A key stored more than once (SNG022): it's set once, to the value it has, which saving writes on one line. A
/// <c>song.ini</c>'s repeats are merged to the last one, which is what games read, so it always has a fix; a
/// <c>.sng</c>'s are kept, and only have one when they all agree.
/// </summary>
public sealed class DuplicateKeyFix : IQuickFixStrategy
{
    public string RuleId => SngRules.Sng022;

    public QuickFix? Propose(Finding finding, IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        if (!QuickFixRegistry.TryGetValue(finding, pairs, out string? key, out string? value)
            || pairs.Any(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && !string.Equals(p.Value, value, StringComparison.Ordinal)))
        {
            return null;
        }

        return new QuickFix(RuleId, key, FindingMessages.QuickFixMergeDuplicates, [new MetadataEdit(key, value)]);
    }
}

/// <summary>
/// A legacy key (SNG025): it's renamed to the key that replaced it, keeping its value. When the song already has
/// the new key, the two values could disagree, so there's no fix.
/// </summary>
public sealed class LegacyKeyFix : IQuickFixStrategy
{
    public string RuleId => SngRules.Sng025;

    public QuickFix? Propose(Finding finding, IReadOnlyList<KeyValuePair<string, string>> pairs)
    {
        if (!QuickFixRegistry.TryGetValue(finding, pairs, out string? key, out string? value)
            || !KnownKeys.LegacyReplacements.TryGetValue(key, out string? replacement)
            || pairs.Any(p => p.Key.Equals(replacement, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return new QuickFix(RuleId, key,
            string.Format(CultureInfo.CurrentCulture, FindingMessages.QuickFixRenameKeyFormat, replacement),
            [new MetadataEdit(key, null), new MetadataEdit(replacement, value)]);
    }
}
