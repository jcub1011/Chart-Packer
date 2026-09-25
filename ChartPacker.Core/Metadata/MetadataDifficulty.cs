using System.Globalization;

namespace ChartPacker.Core.Metadata;

/// <summary>Difficulty keys (<c>diff_guitar</c>, <c>diff_drums_real</c>, …) and their "unset" convention.</summary>
public static class MetadataDifficulty
{
    private const string Prefix = "diff_";

    /// <summary>True when <paramref name="key"/> is a difficulty key, compared ignoring case.</summary>
    public static bool IsDifficultyKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when <paramref name="key"/> is a difficulty key and <paramref name="value"/> is a
    /// negative integer, which means no difficulty is set (conventionally <c>-1</c>).
    /// </summary>
    public static bool IsUnset(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return IsDifficultyKey(key)
            && long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long difficulty)
            && difficulty < 0;
    }
}
