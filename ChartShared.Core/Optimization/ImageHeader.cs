namespace ChartShared.Core.Optimization;

/// <summary>The image formats an optimizer reads: the ones album art and background images are stored as.</summary>
public enum ImageFormat
{
    /// <summary>PNG.</summary>
    Png,

    /// <summary>JPEG.</summary>
    Jpeg,
}

/// <summary>An image's format and size, read from its header without decoding it.</summary>
/// <param name="Format">The format, by the file's content, not its name.</param>
/// <param name="Width">The width in pixels.</param>
/// <param name="Height">The height in pixels.</param>
public readonly record struct ImageHeader(ImageFormat Format, int Width, int Height)
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The codec key an image of <paramref name="format"/> groups under: <c>image/png</c> or <c>image/jpeg</c>.</summary>
    public static string CodecKeyOf(ImageFormat format) => format == ImageFormat.Png ? "image/png" : "image/jpeg";

    /// <summary>This image's codec key.</summary>
    public string CodecKey => CodecKeyOf(Format);

    /// <summary>Reads the header at <paramref name="stream"/>'s current position.</summary>
    /// <returns>The header, or <see langword="null"/> when the stream isn't a PNG or JPEG, or ends before its size.</returns>
    /// <exception cref="IOException">The stream can't be read.</exception>
    public static ImageHeader? Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        Span<byte> start = stackalloc byte[8];
        if (!TryFill(stream, start[..2]))
            return null;

        if (start[0] == 0xFF && start[1] == 0xD8)
            return ReadJpeg(stream);

        // Not a JPEG: a PNG's signature is 8 bytes.
        return TryFill(stream, start[2..]) && start.SequenceEqual(PngSignature) ? ReadPng(stream) : null;
    }

    private static ImageHeader? ReadPng(Stream stream)
    {
        // The IHDR chunk comes first (its length, its name, then the width and height), but for Apple's CgBI PNGs, which
        // put a CgBI chunk before it.
        Span<byte> chunk = stackalloc byte[16];
        if (!TryFill(stream, chunk[..8]))
            return null;

        if (chunk[4..8].SequenceEqual("CgBI"u8))
        {
            if (!Skip(stream, (int)Math.Min(ReadBigEndian(chunk[..4]) + 4, int.MaxValue)) || !TryFill(stream, chunk[..8]))
                return null;
        }

        if (!chunk[4..8].SequenceEqual("IHDR"u8) || !TryFill(stream, chunk[8..16]))
            return null;

        long width = ReadBigEndian(chunk[8..12]);
        long height = ReadBigEndian(chunk[12..16]);
        return width is > 0 and <= int.MaxValue && height is > 0 and <= int.MaxValue
            ? new ImageHeader(ImageFormat.Png, (int)width, (int)height)
            : null;
    }

    /// <summary>
    /// Whether the PNG at <paramref name="stream"/>'s current position can have a pixel that isn't opaque, from its chunks
    /// alone: it has an alpha channel (colour type 4 or 6) or a <c>tRNS</c> chunk. A PNG that can't is opaque whatever its
    /// pixels, so they needn't be scanned. One that can may still be opaque in fact, as an RGBA export often is.
    /// </summary>
    /// <returns><see langword="false"/> only when the chunks show the image is opaque; <see langword="true"/> when it may not be, or isn't one that can be read.</returns>
    /// <exception cref="IOException">The stream can't be read.</exception>
    public static bool PngCanBeTransparent(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        Span<byte> buffer = stackalloc byte[13];
        if (!TryFill(stream, buffer[..8]) || !buffer[..8].SequenceEqual(PngSignature) || ReadPng(stream) is null)
            return true;

        // ReadPng stopped after IHDR's width and height; the rest of its data and its CRC are 5 + 4 bytes.
        // (A CgBI PNG's pixels aren't ones Skia reads, so what's said of them here matters little.)
        if (!TryFill(stream, buffer[..9]))
            return true;

        // Bit depth, then colour type: 0 grey, 2 RGB, 3 palette, 4 grey with alpha, 6 RGB with alpha.
        if (buffer[1] is 4 or 6)
            return true;

        // Colour types 0, 2 and 3 are opaque unless a tRNS chunk, which comes before the image data, gives them transparency.
        Span<byte> chunk = stackalloc byte[8];
        while (TryFill(stream, chunk))
        {
            if (chunk[4..8].SequenceEqual("tRNS"u8))
                return true;
            if (chunk[4..8].SequenceEqual("IDAT"u8) || chunk[4..8].SequenceEqual("IEND"u8))
                return false;
            if (!Skip(stream, (int)Math.Min(ReadBigEndian(chunk[..4]) + 4, int.MaxValue)))
                return true;
        }

        return true;
    }

    /// <summary>Walks a JPEG's markers to its first frame header, which holds the size.</summary>
    /// <remarks>The stream is just past the SOI marker.</remarks>
    private static ImageHeader? ReadJpeg(Stream stream)
    {
        if (!TryNextMarker(stream, out int marker))
            return null;

        Span<byte> buffer = stackalloc byte[8];
        while (true)
        {
            // Fill bytes: any run of 0xFF before the marker's code.
            while (marker == 0xFF)
            {
                int next = stream.ReadByte();
                if (next < 0)
                    return null;
                marker = next;
            }

            // Frame headers are 0xC0 to 0xCF, but 0xC4 (Huffman tables), 0xC8 (reserved) and 0xCC (arithmetic conditioning) aren't.
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                // Length (2), precision (1), height (2), width (2).
                if (!TryFill(stream, buffer[..7]))
                    return null;

                int height = (buffer[3] << 8) | buffer[4];
                int width = (buffer[5] << 8) | buffer[6];
                return width > 0 && height > 0 ? new ImageHeader(ImageFormat.Jpeg, width, height) : null;
            }

            // The start of the scan comes after every frame header, so there isn't one; EOI ends the image.
            if (marker is 0xDA or 0xD9)
                return null;

            // Standalone markers carry no length: TEM, RSTn, SOI.
            if (marker is 0x01 or (>= 0xD0 and <= 0xD8) || marker == 0x00)
            {
                if (!TryNextMarker(stream, out marker))
                    return null;
                continue;
            }

            if (!TryFill(stream, buffer[..2]))
                return null;
            int length = (buffer[0] << 8) | buffer[1];
            if (length < 2)
                return null;

            // Skip the segment's body, then the marker that follows it.
            if (!Skip(stream, length - 2) || !TryNextMarker(stream, out marker))
                return null;
        }
    }

    /// <summary>Reads to the next 0xFF and the marker code after it.</summary>
    private static bool TryNextMarker(Stream stream, out int marker)
    {
        marker = 0;
        int b;
        do
        {
            b = stream.ReadByte();
            if (b < 0)
                return false;
        }
        while (b != 0xFF);

        marker = stream.ReadByte();
        return marker >= 0;
    }

    private static bool Skip(Stream stream, int count)
    {
        Span<byte> scratch = stackalloc byte[256];
        while (count > 0)
        {
            int read = stream.Read(scratch[..Math.Min(count, scratch.Length)]);
            if (read == 0)
                return false;
            count -= read;
        }

        return true;
    }

    private static bool TryFill(Stream stream, Span<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            int read = stream.Read(buffer);
            if (read == 0)
                return false;
            buffer = buffer[read..];
        }

        return true;
    }

    private static long ReadBigEndian(ReadOnlySpan<byte> bytes) =>
        ((long)bytes[0] << 24) | ((long)bytes[1] << 16) | ((long)bytes[2] << 8) | bytes[3];
}

/// <summary>Reads an image's format and size from its header (<see cref="ImageHeader"/>). Pure managed code, with no decoder.</summary>
public sealed class ImageHeaderProbe : IImageProbe
{
    /// <inheritdoc />
    public Task<ProbeResult?> ProbeAsync(string name, Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();

        var header = ImageHeader.Read(stream);
        return Task.FromResult(header is { } h ? new ProbeResult(h.CodecKey, h.Width, h.Height) : null);
    }
}
