using System.Globalization;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Resources;

namespace ChartPacker.Core.Validation;

/// <summary>
/// Checks metadata pairs against the metadata rules of the catalogue (project spec §6): what
/// <c>song.ini</c> can't hold, duplicate keys, and values that don't fit a known key's type.
/// </summary>
/// <remarks>
/// <para>
/// Written from sng-spec §6.2 (the canonical value forms), A7 (duplicate keys) and A8 (strict
/// booleans); the compound forms follow <see cref="KnownKeyType"/>.
/// </para>
/// <para>
/// Every finding names its pair's <see cref="Finding.Key"/>, and its message describes the pair
/// without naming the key, so it can be shown under that key's row. An empty value (SNG061) is a
/// warning here rather than packing's info: in the editor it's a value about to be lost.
/// </para>
/// </remarks>
public static class MetadataValidator
{
    /// <summary>
    /// Checks every pair in <paramref name="pairs"/>, and reports each key that appears more than
    /// once, in the pairs or in <paramref name="duplicateKeys"/> (a <c>song.ini</c>'s, which its
    /// reader has already merged).
    /// </summary>
    public static IReadOnlyList<Finding> Validate(
        IReadOnlyList<KeyValuePair<string, string>> pairs,
        IReadOnlyCollection<string>? duplicateKeys = null)
    {
        ArgumentNullException.ThrowIfNull(pairs);

        var findings = new List<Finding>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicates = new HashSet<string>(duplicateKeys ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var pair in pairs)
        {
            if (!seen.Add(pair.Key))
                duplicates.Add(pair.Key);
        }

        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in pairs)
        {
            if (duplicates.Contains(pair.Key) && reported.Add(pair.Key))
                findings.Add(new Finding(SngRules.Sng022, Severity.Error, FindingMessages.PairDuplicateKey, Key: pair.Key));
            findings.AddRange(ValidatePair(pair.Key, pair.Value));
        }

        return findings;
    }

    /// <summary>Checks one pair on its own: everything but duplicates. At most one finding is about the value.</summary>
    public static IReadOnlyList<Finding> ValidatePair(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        var findings = new List<Finding>();
        if (key.Length == 0)
            findings.Add(new Finding(SngRules.Sng023, Severity.Error, FindingMessages.PairKeyEmpty, Key: key));
        else if (MetadataRules.KeyHasForbiddenCharacter(key))
            findings.Add(new Finding(SngRules.Sng020, Severity.Error, FindingMessages.PairKeyForbidden, Key: key));

        if (KnownKeys.LegacyReplacements.TryGetValue(key, out string? replacement))
        {
            findings.Add(new Finding(SngRules.Sng025, Severity.Info,
                string.Format(CultureInfo.CurrentCulture, FindingMessages.PairLegacyKeyFormat, replacement), Key: key));
        }

        if (MetadataRules.ValueHasForbiddenCharacter(value))
            findings.Add(new Finding(SngRules.Sng021, Severity.Error, FindingMessages.PairValueForbidden, Key: key));
        else if (value.Length == 0)
            findings.Add(new Finding(SngRules.Sng061, Severity.Warning, FindingMessages.PairValueEmpty, Key: key));
        else if (KnownKeys.TryGet(key, out var known) && CheckType(known.Type, value) is { } typeFinding)
            findings.Add(typeFinding with { Key = key });

        return findings;
    }

    /// <summary>Whether <paramref name="finding"/> counts against the metadata: an error or a warning, not a hint.</summary>
    public static bool IsFailing(Finding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return finding.Severity is Severity.Error or Severity.Warning;
    }

    private static Finding? CheckType(KnownKeyType type, string value) => type switch
    {
        KnownKeyType.Integer => IsInteger(value) ? null : Mismatch(FindingMessages.PairExpectedInteger),
        KnownKeyType.Boolean => CheckBoolean(value),
        KnownKeyType.ColorCode => IsColorCode(value) ? null : Mismatch(FindingMessages.PairExpectedColor),
        KnownKeyType.IntegerPair => IsIntegerPair(value) ? null : Mismatch(FindingMessages.PairExpectedIntegerPair),
        KnownKeyType.ProGuitarTuning => IsTuning(value) ? null : Mismatch(FindingMessages.PairExpectedTuning),
        _ => null,
    };

    private static Finding Mismatch(string message) => new(SngRules.Sng024, Severity.Warning, message);

    /// <summary><c>True</c>/<c>False</c> pass; another case of them, <c>0</c> or <c>1</c> is lenient (SNG060); anything else is a mismatch.</summary>
    private static Finding? CheckBoolean(string value)
    {
        if (value is "True" or "False")
            return null;

        string? canonical =
            value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1" ? "True"
            : value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0" ? "False"
            : null;
        return canonical is null
            ? Mismatch(FindingMessages.PairExpectedBoolean)
            : new Finding(SngRules.Sng060, Severity.Warning,
                string.Format(CultureInfo.CurrentCulture, FindingMessages.PairLenientBooleanFormat, canonical));
    }

    /// <summary>An optional <c>+</c> or <c>-</c>, then one or more of the digits <c>0–9</c> (sng-spec §6.2).</summary>
    internal static bool IsInteger(ReadOnlySpan<char> value)
    {
        if (value.Length > 0 && value[0] is '+' or '-')
            value = value[1..];
        if (value.IsEmpty)
            return false;
        foreach (char c in value)
        {
            if (!char.IsAsciiDigit(c))
                return false;
        }

        return true;
    }

    /// <summary><c>#</c> then 3, 4, 6 or 8 hex digits.</summary>
    private static bool IsColorCode(string value)
    {
        if (value.Length < 2 || value[0] != '#')
            return false;
        var digits = value.AsSpan(1);
        return digits.Length is 3 or 4 or 6 or 8 && !digits.ContainsAnyExcept("0123456789abcdefABCDEF");
    }

    /// <summary>Two integers separated by whitespace.</summary>
    private static bool IsIntegerPair(string value)
    {
        string[] parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2 && parts.All(p => IsInteger(p));
    }

    /// <summary>4 to 6 integers separated by whitespace, optionally followed by a name in double quotes.</summary>
    private static bool IsTuning(string value)
    {
        string offsets = value;
        int quote = value.IndexOf('"', StringComparison.Ordinal);
        if (quote >= 0)
        {
            string name = value[quote..].TrimEnd();
            if (name.Length < 2 || name[^1] != '"' || name.AsSpan(1, name.Length - 2).Contains('"'))
                return false;
            offsets = value[..quote];
        }

        string[] parts = offsets.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length is >= 4 and <= 6 && parts.All(p => IsInteger(p));
    }
}
