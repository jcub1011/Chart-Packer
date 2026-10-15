namespace ChartShared.Core.Theming;

/// <summary>An sRGB colour with alpha, 8 bits a channel, independent of any UI toolkit.</summary>
public readonly record struct ThemeColor(byte A, byte R, byte G, byte B)
{
    /// <summary>Opaque white.</summary>
    public static ThemeColor White { get; } = new(255, 255, 255, 255);

    /// <summary>The colour packed as <c>0xAARRGGBB</c>.</summary>
    public static ThemeColor FromArgb(uint argb) => new((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    /// <summary>This colour packed as <c>0xAARRGGBB</c>.</summary>
    public uint ToArgb() => ((uint)A << 24) | ((uint)R << 16) | ((uint)G << 8) | B;

    /// <summary>This colour with its alpha replaced.</summary>
    public ThemeColor WithAlpha(byte alpha) => this with { A = alpha };

    /// <summary>WCAG 2 relative luminance, 0 for black to 1 for white. Alpha is ignored.</summary>
    public double RelativeLuminance =>
        (0.2126 * Oklab.ToLinear(R)) + (0.7152 * Oklab.ToLinear(G)) + (0.0722 * Oklab.ToLinear(B));

    /// <summary>The WCAG 2 contrast ratio between two opaque colours, from 1 (none) to 21 (black on white).</summary>
    public static double Contrast(ThemeColor first, ThemeColor second)
    {
        double a = first.RelativeLuminance, b = second.RelativeLuminance;
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <inheritdoc />
    public override string ToString() => $"#{ToArgb():X8}";
}
