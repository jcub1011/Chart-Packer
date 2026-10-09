using ChartPacker.Core.Validation;

namespace ChartPacker.Core.Charting.Lint;

/// <summary>
/// The findings of one chart lint run, kept per rule so the next run after a change can replace just the ones it checked
/// again (<see cref="ChartLinter.Relint"/>). Immutable, so it can be handed from the lint thread to the UI.
/// </summary>
public sealed class ChartLintResult
{
    private readonly Dictionary<string, Finding[]> _byRule;
    private readonly Finding[] _findings;

    /// <param name="byRule">Each rule run, by ID, and its findings, sorted by <see cref="FindingOrder"/>.</param>
    internal ChartLintResult(Dictionary<string, Finding[]> byRule)
    {
        _byRule = byRule;
        _findings = [.. byRule.Values.SelectMany(static f => f)];
        Array.Sort(_findings, FindingOrder.Comparer);
    }

    /// <summary>A result with no rules run and no findings.</summary>
    public static ChartLintResult Empty { get; } = new(new Dictionary<string, Finding[]>(StringComparer.Ordinal));

    /// <summary>
    /// Every finding, in order: those without a chart location first, then by part, difficulty (every difficulty first),
    /// tick, last tick and rule ID.
    /// </summary>
    public IReadOnlyList<Finding> Findings => _findings;

    /// <summary>The IDs of the rules that were run.</summary>
    public IReadOnlyCollection<string> RuleIds => _byRule.Keys;

    /// <summary>The findings of the rule <paramref name="ruleId"/>, in order; none when it wasn't run.</summary>
    public IReadOnlyList<Finding> FindingsOf(string ruleId)
    {
        ArgumentNullException.ThrowIfNull(ruleId);
        return _byRule.TryGetValue(ruleId, out var findings) ? findings : [];
    }

    /// <summary>The findings of the rule <paramref name="ruleId"/>, when it was run.</summary>
    internal bool TryGetFindings(string ruleId, out Finding[] findings)
    {
        if (_byRule.TryGetValue(ruleId, out var found))
        {
            findings = found;
            return true;
        }

        findings = [];
        return false;
    }
}

/// <summary>
/// The order lint findings are kept and shown in: those without a chart location first, by their tick, then by part,
/// difficulty (every difficulty first), tick, last tick, rule ID, then the rest of the finding, so the order never
/// depends on how they were found.
/// </summary>
internal static class FindingOrder
{
    public static IComparer<Finding> Comparer { get; } = Comparer<Finding>.Create(Compare);

    public static int Compare(Finding? a, Finding? b)
    {
        if (ReferenceEquals(a, b))
            return 0;
        if (a is null)
            return -1;
        if (b is null)
            return 1;

        int c = CompareLocations(a.Location, b.Location);
        if (c == 0)
            c = string.CompareOrdinal(a.Track, b.Track);
        if (c == 0)
            c = Nullable.Compare(a.Tick, b.Tick);
        if (c == 0)
            c = string.CompareOrdinal(a.RuleId, b.RuleId);
        if (c == 0)
            c = a.Severity.CompareTo(b.Severity);
        if (c == 0)
            c = string.CompareOrdinal(a.Message, b.Message);
        if (c == 0)
            c = string.CompareOrdinal(a.Key, b.Key);
        return c != 0 ? c : Nullable.Compare(a.Offset, b.Offset);
    }

    /// <summary>Puts two lists sorted in this order together, in order.</summary>
    public static Finding[] Merge(List<Finding> a, List<Finding> b)
    {
        var merged = new Finding[a.Count + b.Count];
        int i = 0, j = 0, k = 0;
        while (i < a.Count && j < b.Count)
            merged[k++] = Compare(a[i], b[j]) <= 0 ? a[i++] : b[j++];
        while (i < a.Count)
            merged[k++] = a[i++];
        while (j < b.Count)
            merged[k++] = b[j++];
        return merged;
    }

    private static int CompareLocations(ChartLocation? a, ChartLocation? b)
    {
        if (a is null || b is null)
            return a is null ? (b is null ? 0 : -1) : 1;
        int c = a.Part.CompareTo(b.Part);
        if (c == 0)
            c = Nullable.Compare(a.Difficulty, b.Difficulty);
        if (c == 0)
            c = a.Tick.CompareTo(b.Tick);
        return c != 0 ? c : a.EndTick.CompareTo(b.EndTick);
    }
}
