using System;
using System.Threading;
using Avalonia;
using SkiaSharp;

namespace ChartShared.UI.Controls.Skia
{
    /// <summary>A picture a highway draws, a SkiaSharp image. Whoever keeps it disposes it when they let it go.</summary>
    /// <remarks>
    /// A SkiaSharp image may still be in a drawing the render thread has yet to draw, or is drawing now, when it's let go:
    /// each <see cref="DrawList"/> it's drawn in holds it until the list is cleared, and it's only disposed once it's both
    /// let go and in no list.
    /// </remarks>
    public sealed class HighwayImage : IDisposable
    {
        private int _holds;
        private int _released;

        private HighwayImage(SKImage image)
        {
            Image = image;
            Size = new PixelSize(image.Width, image.Height);
        }

        public SKImage Image { get; }

        public PixelSize Size { get; }

        public static HighwayImage Of(SKImage image) => new(image);

        /// <summary>Lets the picture go: it's disposed now, or once no drawing holds it.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;

            if (Volatile.Read(ref _holds) == 0)
                DisposeImage();
        }

        /// <summary>A drawing that will draw it takes hold of it.</summary>
        internal void Hold() => Interlocked.Increment(ref _holds);

        /// <summary>A drawing that held it is cleared, on whichever thread: the last one lets go of a picture already let go.</summary>
        internal void Unhold()
        {
            if (Interlocked.Decrement(ref _holds) == 0 && Volatile.Read(ref _released) != 0)
                DisposeImage();
        }

        private int _disposed;

        private void DisposeImage()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Image.Dispose();
        }
    }
}
