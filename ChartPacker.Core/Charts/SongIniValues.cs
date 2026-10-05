namespace ChartPacker.Core.Charts;

/// <summary>
/// Reads song.ini values the way YARG does (<c>YARGTextReader</c> and <c>IniModifierCollection</c> in
/// YARG.Core), so a chart is read with the settings the game plays it with.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Of a key's spellings (e.g. <c>pro_drum</c> and <c>pro_drums</c>), the last one given wins.</item>
/// <item>A number is an optional sign then digits, and stops at the first other character, so <c>64.5</c> is 64.
/// One past the range of its type is the type's limit. A key whose value isn't a number counts as 0.</item>
/// <item>A boolean is true when its value starts with <c>1</c>, or with <c>true</c> in any case.</item>
/// </list>
/// </remarks>
internal static class SongIniValues
{
    /// <summary>The value of the last of <paramref name="keys"/> that <paramref name="pairs"/> has, ignoring case.</summary>
    public static string? Last(IReadOnlyList<KeyValuePair<string, string>> pairs, params string[] keys)
    {
        string? value = null;
        foreach (var (key, v) in pairs)
        {
            if (keys.Any(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)))
                value = v;
        }

        return value;
    }

    /// <summary><paramref name="value"/> as a number within <paramref name="min"/> and <paramref name="max"/>; <see langword="null"/> when the key isn't given.</summary>
    public static long? Integer(string? value, long min = long.MinValue, long max = long.MaxValue)
    {
        if (value is null)
            return null;

        var text = value.AsSpan().TrimStart();
        bool negative = false;
        if (text.Length > 0 && text[0] is '-' or '+')
        {
            // An unsigned type has no minus sign.
            if (text[0] == '-' && min >= 0)
                return 0;
            negative = text[0] == '-';
            text = text[1..];
        }

        if (text.IsEmpty || !char.IsAsciiDigit(text[0]))
            return 0;

        decimal magnitude = 0;
        foreach (char c in text)
        {
            if (!char.IsAsciiDigit(c))
                break;
            magnitude = Math.Min((magnitude * 10) + (c - '0'), (decimal)ulong.MaxValue + 1);
        }

        decimal signed = negative ? -magnitude : magnitude;
        return (long)Math.Clamp(signed, min, max);
    }

    /// <summary><paramref name="value"/> as a boolean; <see langword="null"/> when the key isn't given.</summary>
    public static bool? Boolean(string? value) =>
        value is null ? null : value.TrimStart() is var text && (text.StartsWith('1') || text.StartsWith("true", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// <paramref name="value"/> as a real number: an optional sign, digits, and a fraction after a <c>.</c>;
    /// <see langword="null"/> when the key isn't given.
    /// </summary>
    public static double? Real(string? value)
    {
        if (value is null)
            return null;

        var text = value.AsSpan().TrimStart();
        double sign = 1;
        if (text.Length > 0 && text[0] is '-' or '+')
        {
            sign = text[0] == '-' ? -1 : 1;
            text = text[1..];
        }

        if (text.IsEmpty || (!char.IsAsciiDigit(text[0]) && text[0] != '.'))
            return 0;

        double result = 0;
        int i = 0;
        for (; i < text.Length && char.IsAsciiDigit(text[i]); i++)
            result = (result * 10) + (text[i] - '0');
        if (i < text.Length && text[i] == '.')
        {
            double divisor = 1;
            for (i++; i < text.Length && char.IsAsciiDigit(text[i]); i++)
            {
                divisor *= 10;
                result += (text[i] - '0') / divisor;
            }
        }

        return result * sign;
    }
}
