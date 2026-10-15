namespace ChartPacker.Core.Theming;

/// <summary>
/// The colours of a theme grown from a <see cref="ThemeSeed"/>, for a dark UI: an accent in the seed's hue, dark enough
/// for white text on it, and greys tinted faintly toward it. Each colour is worked out in Oklab, so the same lightness
/// looks equally light whatever the hue.
/// </summary>
/// <remarks>
/// A palette doesn't hold the app's own colours. Greys are re-tinted from whatever they are now
/// (<see cref="Resolve"/>), so the theme follows the app's surfaces instead of copying their values.
/// </remarks>
public sealed class ThemePalette
{
    /// <summary>The least contrast for text on the accent, WCAG AA for normal text.</summary>
    public const double TextContrast = 4.5;

    /// <summary>The least contrast for the accent against the background, WCAG AA for UI components.</summary>
    public const double ComponentContrast = 3;

    // The accent starts here and darkens until white text on it passes TextContrast; the app's violet is about 0.61.
    private const double AccentLightness = 0.6;
    private const double DarkestAccent = 0.45;
    private const double AccentStep = 0.05;

    // Accents are at least this colourful, so a muted cover still gives an accent that reads as one.
    private const double MinAccentChroma = 0.09;
    private const double MaxAccentChroma = 0.19;

    private const double AccentTextLightness = 0.82;
    private const double AccentHighlightLightness = 0.93;

    // How strongly greys are tinted: a little of the seed's own chroma, kept between a hint and a wash.
    private const double MinTintChroma = 0.012;
    private const double MaxTintChroma = 0.028;
    private const double TintShare = 0.22;

    private const double SpectrumLightness = 0.68;
    private const double SpectrumHueSpread = 40;

    private ThemePalette(ThemeSeed seed)
    {
        Seed = seed;
        double hue = seed.Hue;
        double accentChroma = Math.Clamp(seed.Chroma * 1.2, MinAccentChroma, MaxAccentChroma);
        TintChroma = Math.Clamp(seed.Chroma * TintShare, MinTintChroma, MaxTintChroma);

        // Darken the accent until white text on it is readable; a hue that can't get there by the floor gets dark text.
        double lightness = AccentLightness;
        var accent = Oklab.FromLchInGamut(lightness, accentChroma, hue).ToSrgb();
        while (ThemeColor.Contrast(ThemeColor.White, accent) < TextContrast && lightness > DarkestAccent)
        {
            lightness = Math.Max(DarkestAccent, lightness - 0.01);
            accent = Oklab.FromLchInGamut(lightness, accentChroma, hue).ToSrgb();
        }

        Accent = accent;
        AccentHover = Oklab.FromLchInGamut(lightness + AccentStep, accentChroma, hue).ToSrgb();
        AccentPressed = Oklab.FromLchInGamut(lightness - AccentStep, accentChroma, hue).ToSrgb();
        AccentForeground = ThemeColor.Contrast(ThemeColor.White, accent) >= TextContrast
            ? ThemeColor.White
            : Oklab.FromLchInGamut(0.18, TintChroma, hue).ToSrgb();
        AccentText = Oklab.FromLchInGamut(AccentTextLightness, Math.Clamp(seed.Chroma, 0.06, 0.11), hue).ToSrgb();
        AccentHighlight = Oklab.FromLchInGamut(AccentHighlightLightness, Math.Clamp(seed.Chroma, 0.03, 0.045), hue).ToSrgb();

        double spectrumChroma = Math.Clamp(seed.Chroma * 1.2, 0.1, 0.17);
        SpectrumLow = Oklab.FromLchInGamut(SpectrumLightness, spectrumChroma, hue - SpectrumHueSpread).ToSrgb();
        SpectrumMiddle = Oklab.FromLchInGamut(SpectrumLightness, spectrumChroma, hue).ToSrgb();
        SpectrumHigh = Oklab.FromLchInGamut(SpectrumLightness, spectrumChroma, hue + SpectrumHueSpread).ToSrgb();
    }

    /// <summary>The seed the palette grew from.</summary>
    public ThemeSeed Seed { get; }

    /// <summary>How much chroma greys take on when re-tinted.</summary>
    public double TintChroma { get; }

    /// <inheritdoc cref="ThemeRole.Accent"/>
    public ThemeColor Accent { get; }

    /// <inheritdoc cref="ThemeRole.AccentHover"/>
    public ThemeColor AccentHover { get; }

    /// <inheritdoc cref="ThemeRole.AccentPressed"/>
    public ThemeColor AccentPressed { get; }

    /// <inheritdoc cref="ThemeRole.AccentForeground"/>
    public ThemeColor AccentForeground { get; }

    /// <inheritdoc cref="ThemeRole.AccentText"/>
    public ThemeColor AccentText { get; }

    /// <inheritdoc cref="ThemeRole.AccentHighlight"/>
    public ThemeColor AccentHighlight { get; }

    /// <inheritdoc cref="ThemeRole.SpectrumLow"/>
    public ThemeColor SpectrumLow { get; }

    /// <inheritdoc cref="ThemeRole.SpectrumMiddle"/>
    public ThemeColor SpectrumMiddle { get; }

    /// <inheritdoc cref="ThemeRole.SpectrumHigh"/>
    public ThemeColor SpectrumHigh { get; }

    /// <summary>The palette grown from <paramref name="seed"/>.</summary>
    public static ThemePalette From(ThemeSeed seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        return new(seed);
    }

    /// <summary>
    /// The themed colour for something of role <paramref name="role"/> that is <paramref name="original"/> in the
    /// app's own theme. It keeps the original's alpha, so a translucent tint stays as translucent.
    /// </summary>
    public ThemeColor Resolve(ThemeRole role, ThemeColor original)
    {
        var colour = role switch
        {
            ThemeRole.Neutral => Retint(original),
            ThemeRole.Accent => Accent,
            ThemeRole.AccentHover => AccentHover,
            ThemeRole.AccentPressed => AccentPressed,
            ThemeRole.AccentForeground => AccentForeground,
            ThemeRole.AccentText => AccentText,
            ThemeRole.AccentHighlight => AccentHighlight,
            ThemeRole.SpectrumLow => SpectrumLow,
            ThemeRole.SpectrumMiddle => SpectrumMiddle,
            ThemeRole.SpectrumHigh => SpectrumHigh,
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
        };
        return colour.WithAlpha(original.A);
    }

    /// <summary><paramref name="grey"/> at the same lightness, tinted toward the seed's hue.</summary>
    public ThemeColor Retint(ThemeColor grey) =>
        Oklab.FromLchInGamut(Oklab.FromSrgb(grey).L, TintChroma, Seed.Hue).ToSrgb(grey.A);
}
