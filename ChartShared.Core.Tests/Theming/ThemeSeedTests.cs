using ChartShared.Core.Theming;

namespace ChartShared.Core.Tests.Theming;

[TestClass]
public sealed class ThemeSeedTests
{
    private const int Size = 64;

    private static readonly ThemeColor Red = new(255, 255, 0, 0);
    private static readonly ThemeColor Blue = new(255, 0, 0, 255);
    private static readonly ThemeColor Black = new(255, 0, 0, 0);

    [TestMethod]
    [DataRow(255, 0, 0)]
    [DataRow(0, 255, 0)]
    [DataRow(0, 0, 255)]
    [DataRow(249, 115, 22)]
    public void ASolidColourIsItsOwnSeed(int r, int g, int b)
    {
        var colour = new ThemeColor(255, (byte)r, (byte)g, (byte)b);

        var seed = Extract(Fill(colour));

        Assert.IsNotNull(seed);
        Assert.AreEqual(Oklab.FromSrgb(colour).Hue, seed.Hue, 0.5);
        Assert.AreEqual(Oklab.FromSrgb(colour).Chroma, seed.Chroma, 1e-6);
    }

    [TestMethod]
    [DataRow(0x00u)]
    [DataRow(0xFFu)]
    [DataRow(0x80u)]
    [DataRow(0x30u)]
    public void AGreyImageHasNoSeed(uint level)
    {
        byte v = (byte)level;

        Assert.IsNull(Extract(Fill(new ThemeColor(255, v, v, v))));
    }

    [TestMethod]
    public void ANearlyGreyImageHasNoSeed()
    {
        // A slate grey: a hint of blue, but too little to read as a colour.
        Assert.IsNull(Extract(Fill(new ThemeColor(255, 0x5A, 0x5E, 0x66))));
    }

    [TestMethod]
    public void AVividPatchOnBlackIsTheSeed()
    {
        var orange = new ThemeColor(255, 249, 115, 22);
        var pixels = Draw((x, y) => y < 7 ? orange : Black);

        var seed = Extract(pixels);

        Assert.IsNotNull(seed);
        Assert.AreEqual(Oklab.FromSrgb(orange).Hue, seed.Hue, 0.5);
    }

    [TestMethod]
    public void AFewColourfulPixelsAreNotEnough()
    {
        var pixels = Draw((x, y) => y == 0 && x < 32 ? Red : Black);

        Assert.IsNull(Extract(pixels));
    }

    [TestMethod]
    public void TheMoreColourfulHueWinsOverTheMoreCommonOne()
    {
        var mutedTeal = Oklab.FromLch(0.6, 0.05, 190).ToSrgb();
        var magenta = Oklab.FromLchInGamut(0.6, 0.25, 330).ToSrgb();
        var pixels = Draw((x, y) => y < 25 ? magenta : mutedTeal);

        var seed = Extract(pixels);

        Assert.IsNotNull(seed);
        Assert.AreEqual(330, seed.Hue, 2);
    }

    [TestMethod]
    public void HuesEitherSideOfZeroAverageToZero()
    {
        var below = Oklab.FromLch(0.6, 0.12, 356).ToSrgb();
        var above = Oklab.FromLch(0.6, 0.12, 4).ToSrgb();
        var pixels = Draw((x, y) => x % 2 == 0 ? below : above);

        var seed = Extract(pixels);

        Assert.IsNotNull(seed);
        Assert.IsLessThan(2, Math.Min(seed.Hue, 360 - seed.Hue));
    }

    [TestMethod]
    public void TransparentPixelsAreIgnored()
    {
        var pixels = Draw((x, y) => y < Size / 2 ? Red.WithAlpha(0) : Blue);

        var seed = Extract(pixels);

        Assert.IsNotNull(seed);
        Assert.AreEqual(Oklab.FromSrgb(Blue).Hue, seed.Hue, 0.5);
    }

    [TestMethod]
    public void AFullyTransparentImageHasNoSeed()
    {
        Assert.IsNull(Extract(Fill(Red.WithAlpha(0))));
    }

    [TestMethod]
    public void PaddingPastEachRowIsIgnored()
    {
        const int stride = (Size * 4) + 12;
        var pixels = new byte[stride * Size];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < stride / 4; x++)
                Put(pixels, (y * stride) + (x * 4), x < Size ? Blue : Red);
        }

        var seed = ThemeSeed.Extract(pixels, Size, Size, stride);

        Assert.IsNotNull(seed);
        Assert.AreEqual(Oklab.FromSrgb(Blue).Hue, seed.Hue, 0.5);
    }

    [TestMethod]
    public void TheSameImageGivesTheSameSeed()
    {
        var pixels = Draw((x, y) => new ThemeColor(255, (byte)(x * 4), (byte)(y * 4), (byte)((x + y) * 2)));

        Assert.AreEqual(Extract(pixels), Extract(pixels));
    }

    [TestMethod]
    public void AnEmptyImageHasNoSeed()
    {
        Assert.IsNull(ThemeSeed.Extract([], 0, 0, 0));
    }

    [TestMethod]
    public void AStrideShorterThanARowThrows()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ThemeSeed.Extract(new byte[64], 4, 4, 12));
    }

    [TestMethod]
    public void PixelsShorterThanTheImageThrow()
    {
        Assert.ThrowsExactly<ArgumentException>(() => ThemeSeed.Extract(new byte[63], 4, 4, 16));
    }

    private static ThemeSeed? Extract(byte[] pixels) => ThemeSeed.Extract(pixels, Size, Size, Size * 4);

    private static byte[] Fill(ThemeColor colour) => Draw((_, _) => colour);

    private static byte[] Draw(Func<int, int, ThemeColor> pixel)
    {
        var pixels = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
                Put(pixels, ((y * Size) + x) * 4, pixel(x, y));
        }

        return pixels;
    }

    private static void Put(byte[] pixels, int offset, ThemeColor colour)
    {
        pixels[offset] = colour.B;
        pixels[offset + 1] = colour.G;
        pixels[offset + 2] = colour.R;
        pixels[offset + 3] = colour.A;
    }
}
