using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ChartBuilder.Controls.Editing
{
    /// <summary>
    /// Small pictures a lane draws again every frame, the labels and marks standing up on a perspective highway, kept side by side in
    /// one picture, a page, and drawn from their part of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each picture drawn in a frame is a resource of that frame's drawing, which Avalonia lists, one more entry for each distinct one;
    /// drawn from one page, they're all the one. A picture is put on the page when it's first asked for: drawn on its own, as it was,
    /// and its pixels copied into a free place, a pixel clear of its neighbours so smoothing doesn't bring theirs in.
    /// </para>
    /// <para>
    /// The page is a <see cref="WriteableBitmap"/>, which may be written while the screen is drawn from it, as the video's frames are;
    /// a picture is only ever written where nothing has been drawn from yet. When there's no room left, a new page is begun and the old
    /// let go: the drawings already made with it keep it until they're done with.
    /// </para>
    /// </remarks>
    internal sealed class PictureAtlas<TKey> : IDisposable
        where TKey : notnull
    {
        // The clear pixel round each picture, so one drawn smoothed, shrunk by the tilt, takes nothing of its neighbours.
        private const int Gutter = 1;

        private readonly PixelSize _size;
        private readonly Dictionary<TKey, PixelRect> _places = [];
        private WriteableBitmap? _page;

        // Where the next picture goes: along the current shelf, and how tall the shelf is so far.
        private int _x;
        private int _y;
        private int _shelf;

        /// <param name="size">How large each page is, in pixels.</param>
        public PictureAtlas(PixelSize size)
        {
            _size = size;
        }

        /// <summary>The page <paramref name="key"/>'s picture is on and where, in its pixels, or false when it isn't on the page yet.</summary>
        public bool TryGet(TKey key, out WriteableBitmap page, out Rect source)
        {
            if (_page is { } current && _places.TryGetValue(key, out var place))
            {
                page = current;
                source = new Rect(place.X, place.Y, place.Width, place.Height);
                return true;
            }

            page = null!;
            source = default;
            return false;
        }

        /// <summary>
        /// Puts <paramref name="picture"/> on the page for <paramref name="key"/>, on a new page if there's no room on this one: the page
        /// and where it is, in its pixels; false when it's too large for a page at all.
        /// </summary>
        public bool TryAdd(TKey key, RenderTargetBitmap picture, out WriteableBitmap page, out Rect source)
        {
            var pixels = picture.PixelSize;
            int width = pixels.Width + (2 * Gutter), height = pixels.Height + (2 * Gutter);
            page = null!;
            source = default;
            if (width > _size.Width || height > _size.Height)
                return false;

            if (_page is null || !TryPlace(width, height, out int x, out int y))
            {
                NewPage();
                TryPlace(width, height, out x, out y);
            }

            var current = _page!;
            using (var target = current.Lock())
            {
                // Its pixels, row by row, into the page's at the place found, inside the gutter; the page's stride is its own.
                var at = new PixelPoint(x + Gutter, y + Gutter);
                int stride = target.RowBytes;
                nint start = target.Address + (at.Y * stride) + (at.X * 4);
                // Avalonia takes the buffer to be whole rows of the stride; the gutter leaves a row of the page below every picture, so
                // that many bytes from its start are all the page's.
                int length = pixels.Height * stride;
                picture.CopyPixels(new PixelRect(pixels), start, length, stride);
            }

            var place = new PixelRect(x + Gutter, y + Gutter, pixels.Width, pixels.Height);
            _places[key] = place;
            page = current;
            source = new Rect(place.X, place.Y, place.Width, place.Height);
            return true;
        }

        /// <summary>Finds room for a picture of <paramref name="width"/> by <paramref name="height"/>, gutter and all, on the current page.</summary>
        private bool TryPlace(int width, int height, out int x, out int y)
        {
            if (_x + width > _size.Width)
            {
                // The next shelf, under this one.
                _y += _shelf;
                _x = 0;
                _shelf = 0;
            }

            if (_y + height > _size.Height)
            {
                x = y = 0;
                return false;
            }

            x = _x;
            y = _y;
            _x += width;
            _shelf = Math.Max(_shelf, height);
            return true;
        }

        /// <summary>
        /// Begins a new, clear page, letting the last go and forgetting what was on it: what was drawn from it this frame keeps it as it
        /// was until that drawing is done with.
        /// </summary>
        private void NewPage()
        {
            _page?.Dispose();
            _places.Clear();
            _x = _y = _shelf = 0;

            // At 96 dots to the inch, the page's pixels are its units, so a picture's place on it is in pixels as drawn from.
            _page = new WriteableBitmap(_size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using var target = _page.Lock();
            unsafe
            {
                new Span<byte>((void*)target.Address, target.RowBytes * _size.Height).Clear();
            }
        }

        public void Dispose()
        {
            _page?.Dispose();
            _page = null;
            _places.Clear();
        }
    }
}
