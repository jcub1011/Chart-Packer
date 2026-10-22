using System.Globalization;

namespace ChartShared.Core.Updating;

/// <summary>A parsed semantic version: <c>major.minor.patch[-prerelease][+build]</c>, with an optional leading <c>v</c>.</summary>
/// <param name="Major">The major version.</param>
/// <param name="Minor">The minor version.</param>
/// <param name="Patch">The patch version.</param>
/// <param name="Prerelease">The pre-release suffix without the dash, or null for a stable release.</param>
public sealed record SemanticVersion(int Major, int Minor, int Patch, string? Prerelease) : IComparable<SemanticVersion>
{
    /// <summary>Parses <paramref name="text"/> like <c>v1.2.3</c> or <c>1.2.3-beta.1</c>. Gives null when it isn't one.</summary>
    public static SemanticVersion? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        string s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
            s = s[1..];
        if (s.Length == 0)
            return null;

        // Build metadata after '+' doesn't affect precedence, and may itself hold dashes; drop it first.
        int plus = s.IndexOf('+');
        if (plus >= 0)
            s = s[..plus];

        string? prerelease = null;
        int dash = s.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = s[(dash + 1)..];
            s = s[..dash];
            if (prerelease.Split('.').Any(id => id.Length == 0))
                return null;
        }

        string[] parts = s.Split('.');
        if (parts.Length is < 1 or > 3)
            return null;

        int[] numbers = [0, 0, 0];
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0 || (parts[i].Length > 1 && parts[i][0] == '0'))
                return null;
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out int n))
                return null;
            numbers[i] = n;
        }

        return new SemanticVersion(numbers[0], numbers[1], numbers[2], prerelease);
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
            return 1;
        int compared = Major.CompareTo(other.Major);
        if (compared != 0)
            return compared;
        compared = Minor.CompareTo(other.Minor);
        if (compared != 0)
            return compared;
        compared = Patch.CompareTo(other.Patch);
        if (compared != 0)
            return compared;
        // A stable release outranks its pre-releases; two pre-releases compare by suffix.
        if (Prerelease is null && other.Prerelease is null)
            return 0;
        if (Prerelease is null)
            return 1;
        if (other.Prerelease is null)
            return -1;
        return ComparePrerelease(Prerelease, other.Prerelease);
    }

    public static bool operator >(SemanticVersion? left, SemanticVersion? right) =>
        left is not null && left.CompareTo(right) > 0;

    public static bool operator <(SemanticVersion? left, SemanticVersion? right) =>
        right is not null && right.CompareTo(left) > 0;

    /// <summary>
    /// SemVer pre-release precedence: dot-separated identifiers left to right, numeric ones by value and below
    /// alphanumeric ones, the rest ordinally; when one runs out first, it's the lower. So <c>beta.2</c> &lt;
    /// <c>beta.10</c> and <c>alpha</c> &lt; <c>alpha.1</c>.
    /// </summary>
    private static int ComparePrerelease(string left, string right)
    {
        string[] a = left.Split('.');
        string[] b = right.Split('.');
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            bool aNumeric = ulong.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out ulong aValue);
            bool bNumeric = ulong.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out ulong bValue);
            int compared = (aNumeric, bNumeric) switch
            {
                (true, true) => aValue.CompareTo(bValue),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(a[i], b[i]),
            };
            if (compared != 0)
                return compared;
        }

        return a.Length.CompareTo(b.Length);
    }
}
