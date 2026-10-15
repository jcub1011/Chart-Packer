using ChartPacker.Core.Theming;

namespace ChartPacker.Core.Tests.Theming;

[TestClass]
public sealed class ThemePaletteTests
{
    // The app's background and a border, as Theme.axaml has them.
    private static readonly ThemeColor Background = ThemeColor.FromArgb(0xFF0E0F14);
    private static readonly ThemeColor Border = ThemeColor.FromArgb(0xFF262836);

    private static IEnumerable<object[]> Seeds()
    {
        foreach (double chroma in new[] { 0.05, 0.12, 0.25 })
        {
            for (int hue = 0; hue < 360; hue += 15)
                yield return [hue, chroma];
        }
    }

    [TestMethod]
    [DynamicData(nameof(Seeds))]
    public void TextOnTheAccentIsReadable(int hue, double chroma)
    {
        var palette = ThemePalette.From(new(hue, chroma));

        Assert.IsGreaterThanOrEqualTo(ThemePalette.TextContrast, ThemeColor.Contrast(palette.AccentForeground, palette.Accent));
    }

    [TestMethod]
    [DynamicData(nameof(Seeds))]
    public void TheAccentStandsOutFromTheBackground(int hue, double chroma)
    {
        var palette = ThemePalette.From(new(hue, chroma));

        Assert.IsGreaterThanOrEqualTo(ThemePalette.ComponentContrast, ThemeColor.Contrast(palette.Accent, palette.Retint(Background)));
    }

    [TestMethod]
    [DynamicData(nameof(Seeds))]
    public void AccentTextOnTheBackgroundIsReadable(int hue, double chroma)
    {
        var palette = ThemePalette.From(new(hue, chroma));

        Assert.IsGreaterThanOrEqualTo(ThemePalette.TextContrast, ThemeColor.Contrast(palette.AccentText, palette.Retint(Background)));
    }

    [TestMethod]
    [DynamicData(nameof(Seeds))]
    public void RetintingKeepsLightness(int hue, double chroma)
    {
        var palette = ThemePalette.From(new(hue, chroma));

        foreach (var grey in new[] { Background, Border })
            Assert.AreEqual(Oklab.FromSrgb(grey).L, Oklab.FromSrgb(palette.Retint(grey)).L, 0.01);
    }

    [TestMethod]
    [DataRow(30)]
    [DataRow(140)]
    [DataRow(260)]
    public void ColoursFollowTheSeedsHue(int hue)
    {
        var palette = ThemePalette.From(new(hue, 0.15));

        AssertHue(hue, palette.Accent, 5);
        // A dark grey's few levels of tint round to the nearest byte, which moves its hue further.
        AssertHue(hue, palette.Retint(Border), 12);
        AssertHue(hue - 40, palette.SpectrumLow, 5);
        AssertHue(hue + 40, palette.SpectrumHigh, 5);
    }

    [TestMethod]
    public void AMutedSeedStillGivesAColourfulAccent()
    {
        var palette = ThemePalette.From(new(200, 0.02));

        Assert.IsGreaterThan(0.08, Oklab.FromSrgb(palette.Accent).Chroma);
    }

    [TestMethod]
    public void TintsStayFaint()
    {
        var palette = ThemePalette.From(new(330, 0.3));

        Assert.IsLessThan(0.04, Oklab.FromSrgb(palette.Retint(Border)).Chroma);
    }

    [TestMethod]
    public void HoverIsLighterAndPressedDarker()
    {
        var palette = ThemePalette.From(new(260, 0.15));
        double accent = Oklab.FromSrgb(palette.Accent).L;

        Assert.IsGreaterThan(accent, Oklab.FromSrgb(palette.AccentHover).L);
        Assert.IsLessThan(accent, Oklab.FromSrgb(palette.AccentPressed).L);
    }

    [TestMethod]
    [DataRow(ThemeRole.Neutral)]
    [DataRow(ThemeRole.Accent)]
    [DataRow(ThemeRole.AccentText)]
    [DataRow(ThemeRole.SpectrumMiddle)]
    public void ResolveKeepsTheOriginalsAlpha(ThemeRole role)
    {
        var palette = ThemePalette.From(new(100, 0.15));

        Assert.AreEqual(0x40, palette.Resolve(role, ThemeColor.FromArgb(0x408B5CF6)).A);
    }

    [TestMethod]
    public void ResolveRetintsNeutralsAndTakesTheRest()
    {
        var palette = ThemePalette.From(new(100, 0.15));

        Assert.AreEqual(palette.Retint(Border), palette.Resolve(ThemeRole.Neutral, Border));
        Assert.AreEqual(palette.Accent, palette.Resolve(ThemeRole.Accent, Border));
        Assert.AreEqual(palette.AccentHighlight, palette.Resolve(ThemeRole.AccentHighlight, Border));
    }

    [TestMethod]
    public void FromANullSeedThrows()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => ThemePalette.From(null!));
    }

    private static void AssertHue(double expected, ThemeColor colour, double tolerance)
    {
        double difference = Math.Abs(((Oklab.FromSrgb(colour).Hue - expected) % 360 + 540) % 360 - 180);
        Assert.IsLessThanOrEqualTo(tolerance, difference, $"{colour} is {difference:0.#}° from hue {expected}.");
    }
}
