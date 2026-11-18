using SkiaSharp;

namespace ChartShared.UI.Controls.Skia
{
    /// <summary>
    /// Draws a picture for SkiaSharp on the UI thread, in memory, as a highway's waveform tiles are: drawn once into a
    /// <see cref="DrawList"/>, the same calls a highway's drawing makes, then played onto a picture of its own.
    /// </summary>
    /// <remarks>One drawing at a time: <see cref="Begin"/>, draw on what it gives, <see cref="End"/>. For the UI thread.</remarks>
    public static class SkiaRaster
    {
        private static readonly DrawList List = new();
        private static readonly DrawListPlayer Player = new();

        /// <summary>A canvas to draw the picture on, from its top left, in its pixels.</summary>
        public static IHighwayCanvas Begin()
        {
            List.Reset();
            return List;
        }

        /// <summary>The picture drawn since <see cref="Begin"/>, <paramref name="width"/> by <paramref name="height"/> pixels, clear where nothing was drawn.</summary>
        public static SKImage End(int width, int height)
        {
            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            surface.Canvas.Clear(SKColors.Transparent);
            Player.Play(List, surface.Canvas, 1);
            List.Reset();
            return surface.Snapshot();
        }
    }
}
