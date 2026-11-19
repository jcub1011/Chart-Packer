using System.Globalization;

namespace ChartShared.Core;

/// <summary>Turns a <see cref="double"/> into the <see cref="decimal"/> a person would write for it.</summary>
/// <remarks>
/// From .NET 11 a cast converts the double's exact binary value, so <c>(decimal)0.1</c> is
/// <c>0.1000000000000000055511151231</c> rather than <c>0.1</c>, and a number box bound to it shows those digits.
/// Before, the cast kept 15 significant digits, which is what a double holds faithfully; this does the same.
/// </remarks>
public static class DecimalConversion
{
    /// <summary>
    /// <paramref name="value"/> rounded to 15 significant digits, as a decimal: 0.1 gives <c>0.1m</c>.
    /// </summary>
    /// <exception cref="OverflowException">The value is not a number, or is outside what a decimal holds.</exception>
    public static decimal ToDecimal(double value)
    {
        if (!double.IsFinite(value))
            throw new OverflowException();

        Span<char> text = stackalloc char[32];
        value.TryFormat(text, out int length, "G15", CultureInfo.InvariantCulture);
        return decimal.Parse(text[..length], NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
