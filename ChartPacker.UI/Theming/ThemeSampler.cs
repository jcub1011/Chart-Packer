using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ChartPacker.Core.Theming;
using Serilog;

namespace ChartPacker.UI.Theming
{
    /// <summary>Finds the <see cref="ThemeSeed"/> of an image file's bytes, for <see cref="DynamicTheme"/>.</summary>
    public static class ThemeSampler
    {
        /// <summary>The width the image is decoded at to sample it: enough for its colours, a fraction of the work.</summary>
        public const int SampleWidth = 64;

        /// <summary>
        /// Decodes <paramref name="bytes"/> small and finds its seed. Safe on any thread; nothing is kept, so the
        /// caller needn't dispose anything.
        /// </summary>
        /// <returns>The seed, or <see langword="null"/> when the image is too grey to have one or isn't an image.</returns>
        public static ThemeSeed? FromImageBytes(byte[] bytes)
        {
            ArgumentNullException.ThrowIfNull(bytes);
            try
            {
                using var image = Bitmap.DecodeToWidth(new MemoryStream(bytes, writable: false), SampleWidth, BitmapInterpolationMode.MediumQuality);
                // Copied into a known layout: decoders pick their own, often premultiplied.
                using var target = new WriteableBitmap(image.PixelSize, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
                using var frame = target.Lock();
                image.CopyPixels(frame);

                var pixels = new byte[frame.RowBytes * frame.Size.Height];
                Marshal.Copy(frame.Address, pixels, 0, pixels.Length);
                return ThemeSeed.Extract(pixels, frame.Size.Width, frame.Size.Height, frame.RowBytes);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Not an image, or one the decoder can't read: no theme, as there's no picture.
                Log.ForContext(typeof(ThemeSampler)).Debug(e, "An image's colours couldn't be sampled");
                return null;
            }
        }
    }
}
