using System.Runtime.InteropServices;

namespace ChartPacker.Thumbnails;

/// <summary>
/// Decodes an album-art image (PNG or JPEG) and scales it into a 32-bit bitmap for Explorer, with GDI+'s flat
/// API: it ships with Windows, reads both formats, and needs no COM beyond the memory stream it reads from.
/// </summary>
internal static unsafe partial class ThumbnailBitmap
{
    private const int GdipOk = 0;
    // Straight (not premultiplied) alpha, which is what WTSAT_ARGB tells Explorer the bitmap holds.
    private const int PixelFormat32bppArgb = 0x0026200A;
    private const int InterpolationModeHighQualityBicubic = 7;
    private const int PixelOffsetModeHighQuality = 2;
    // Mirrors the image past its edges, so the bicubic filter doesn't fade the outermost pixels.
    private const int WrapModeTileFlipXY = 3;
    private const int UnitPixel = 2;

    // GDI+ is started once, the first time it's needed, and never shut down: the DLL is never unloaded.
    private static readonly bool Started = Start();

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInput
    {
        public uint GdiplusVersion;
        public IntPtr DebugEventCallback;
        public int SuppressBackgroundThread;
        public int SuppressExternalCodecs;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    /// <summary>
    /// The image in <paramref name="image"/>, scaled so its longer side is <paramref name="size"/>, as an
    /// <c>HBITMAP</c> the caller owns; or zero when it can't be decoded.
    /// </summary>
    public static IntPtr Create(byte[] image, uint size)
    {
        if (!Started || size == 0 || image.Length == 0)
            return IntPtr.Zero;

        IntPtr memory;
        fixed (byte* p = image)
            memory = SHCreateMemStream(p, (uint)image.Length);
        if (memory == IntPtr.Zero)
            return IntPtr.Zero;

        try
        {
            if (GdipCreateBitmapFromStream(memory, out IntPtr source) != GdipOk)
                return IntPtr.Zero;

            try
            {
                return Scale(source, (int)Math.Min(size, 4096));
            }
            finally
            {
                GdipDisposeImage(source);
            }
        }
        finally
        {
            Com.Release(memory);
        }
    }

    private static IntPtr Scale(IntPtr source, int size)
    {
        if (GdipGetImageWidth(source, out uint sourceWidth) != GdipOk || GdipGetImageHeight(source, out uint sourceHeight) != GdipOk
            || sourceWidth == 0 || sourceHeight == 0)
        {
            return IntPtr.Zero;
        }

        int width = sourceWidth >= sourceHeight ? size : Math.Max(1, (int)Math.Round((double)size * sourceWidth / sourceHeight));
        int height = sourceWidth >= sourceHeight ? Math.Max(1, (int)Math.Round((double)size * sourceHeight / sourceWidth)) : size;

        // A top-down DIB section, which GDI+ then draws into directly.
        var header = new BitmapInfoHeader { Size = sizeof(BitmapInfoHeader), Width = width, Height = -height, Planes = 1, BitCount = 32 };
        void* bits;
        IntPtr dib = CreateDIBSection(IntPtr.Zero, &header, 0, &bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero)
            return IntPtr.Zero;

        bool drawn = false;
        try
        {
            if (GdipCreateBitmapFromScan0(width, height, width * 4, PixelFormat32bppArgb, (IntPtr)bits, out IntPtr target) != GdipOk)
                return IntPtr.Zero;
            try
            {
                drawn = Draw(target, source, width, height, (int)sourceWidth, (int)sourceHeight);
            }
            finally
            {
                GdipDisposeImage(target);
            }
        }
        finally
        {
            if (!drawn)
                DeleteObject(dib);
        }

        return drawn ? dib : IntPtr.Zero;
    }

    private static bool Draw(IntPtr target, IntPtr source, int width, int height, int sourceWidth, int sourceHeight)
    {
        if (GdipGetImageGraphicsContext(target, out IntPtr graphics) != GdipOk)
            return false;
        try
        {
            GdipSetInterpolationMode(graphics, InterpolationModeHighQualityBicubic);
            GdipSetPixelOffsetMode(graphics, PixelOffsetModeHighQuality);
            if (GdipCreateImageAttributes(out IntPtr attributes) != GdipOk)
                return false;
            try
            {
                GdipSetImageAttributesWrapMode(attributes, WrapModeTileFlipXY, 0, 0);
                return GdipDrawImageRectRectI(
                    graphics, source, 0, 0, width, height, 0, 0, sourceWidth, sourceHeight, UnitPixel, attributes, IntPtr.Zero, IntPtr.Zero) == GdipOk;
            }
            finally
            {
                GdipDisposeImageAttributes(attributes);
            }
        }
        finally
        {
            GdipDeleteGraphics(graphics);
        }
    }

    private static bool Start()
    {
        try
        {
            var input = new StartupInput { GdiplusVersion = 1 };
            return GdiplusStartup(out _, &input, IntPtr.Zero) == GdipOk;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [LibraryImport("gdiplus")]
    private static partial int GdiplusStartup(out IntPtr token, StartupInput* input, IntPtr output);

    [LibraryImport("gdiplus")]
    private static partial int GdipCreateBitmapFromStream(IntPtr stream, out IntPtr bitmap);

    [LibraryImport("gdiplus")]
    private static partial int GdipCreateBitmapFromScan0(int width, int height, int stride, int format, IntPtr scan0, out IntPtr bitmap);

    [LibraryImport("gdiplus")]
    private static partial int GdipGetImageWidth(IntPtr image, out uint width);

    [LibraryImport("gdiplus")]
    private static partial int GdipGetImageHeight(IntPtr image, out uint height);

    [LibraryImport("gdiplus")]
    private static partial int GdipDisposeImage(IntPtr image);

    [LibraryImport("gdiplus")]
    private static partial int GdipGetImageGraphicsContext(IntPtr image, out IntPtr graphics);

    [LibraryImport("gdiplus")]
    private static partial int GdipDeleteGraphics(IntPtr graphics);

    [LibraryImport("gdiplus")]
    private static partial int GdipSetInterpolationMode(IntPtr graphics, int mode);

    [LibraryImport("gdiplus")]
    private static partial int GdipSetPixelOffsetMode(IntPtr graphics, int mode);

    [LibraryImport("gdiplus")]
    private static partial int GdipCreateImageAttributes(out IntPtr attributes);

    [LibraryImport("gdiplus")]
    private static partial int GdipDisposeImageAttributes(IntPtr attributes);

    [LibraryImport("gdiplus")]
    private static partial int GdipSetImageAttributesWrapMode(IntPtr attributes, int wrap, uint argb, int clamp);

    [LibraryImport("gdiplus")]
    private static partial int GdipDrawImageRectRectI(
        IntPtr graphics, IntPtr image, int dstX, int dstY, int dstWidth, int dstHeight,
        int srcX, int srcY, int srcWidth, int srcHeight, int srcUnit, IntPtr attributes, IntPtr callback, IntPtr callbackData);

    [LibraryImport("shlwapi")]
    private static partial IntPtr SHCreateMemStream(byte* data, uint size);

    [LibraryImport("gdi32")]
    private static partial IntPtr CreateDIBSection(IntPtr dc, BitmapInfoHeader* header, uint usage, void** bits, IntPtr section, uint offset);

    [LibraryImport("gdi32")]
    private static partial int DeleteObject(IntPtr gdiObject);
}
