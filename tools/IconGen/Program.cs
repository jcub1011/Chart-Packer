using System.Buffers.Binary;
using System.Globalization;
using System.Xml.Linq;
using SkiaSharp;

// Renders the program icons from the Bootstrap Icons sources in Sources/ (see README.md):
//
//   dotnet run --project tools/IconGen
//
// Each source is drawn as it is, filled in its own colour, and written over the .ico it's for. Run from anywhere
// inside the repo.

// The sizes Windows asks an icon for, from the small list view to the extra-large icons.
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 256];

// The colours are darker shades of the red, orange and blue frets (ChartPacker.UI/Controls/Highway/HighwayPalette.cs).
// They aren't the app's theme colours, so a restyle doesn't change them.
(string Source, string Icon, SKColor Fill)[] icons =
[
    ("box-seam-fill.svg", "ChartPacker/Assets/ChartPacker.ico", SKColor.Parse("#B22222")),
    ("bar-chart-line-fill.svg", "ChartBuilder/Assets/ChartBuilder.ico", SKColor.Parse("#D2670A")),
    ("disc-fill.svg", "installer/SngFile.ico", SKColor.Parse("#1E64B4")),
];

string? repo = FindRepo(Directory.GetCurrentDirectory());
if (repo is null)
{
    Console.Error.WriteLine("Run this from inside the Chart-Packer repo.");
    return 1;
}

foreach (var (source, icon, fill) in icons)
{
    using SKPath glyph = ReadGlyph(Path.Combine(repo, "tools", "IconGen", "Sources", source));
    string target = Path.Combine(repo, icon);
    File.WriteAllBytes(target, WriteIco([.. sizes.Select(size => (size, Render(glyph, fill, size)))]));
    Console.WriteLine($"{source} -> {icon}");
}

return 0;

static string? FindRepo(string folder)
{
    for (var dir = new DirectoryInfo(folder); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Chart-Packer.slnx")))
            return dir.FullName;
    }

    return null;
}

// A Bootstrap Icons SVG is one path in a square viewBox (0 0 16 16). The glyph comes back scaled to a 1-unit box, and
// keeps the path's fill rule: box-seam-fill is even-odd, and its seams fill in solid without it.
static SKPath ReadGlyph(string svg)
{
    XNamespace ns = "http://www.w3.org/2000/svg";
    XElement root = XDocument.Load(svg).Root!;
    XElement element = root.Descendants(ns + "path").Single();
    var path = SKPath.ParseSvgPathData(element.Attribute("d")!.Value) ?? throw new InvalidDataException($"{svg} has a path Skia can't read.");
    if ((string?)element.Attribute("fill-rule") == "evenodd")
        path.FillType = SKPathFillType.EvenOdd;

    float[] box = [.. root.Attribute("viewBox")!.Value.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).Select(n => float.Parse(n, CultureInfo.InvariantCulture))];
    if (box[2] != box[3])
        throw new InvalidDataException($"{svg}'s viewBox isn't square.");

    path.Transform(SKMatrix.CreateTranslation(-box[0], -box[1]).PostConcat(SKMatrix.CreateScale(1 / box[2], 1 / box[2])));
    return path;
}

// The glyph at size × size in one colour, as straight-alpha BGRA rows from the top, which is what an icon holds.
static byte[] Render(SKPath glyph, SKColor fill, int size)
{
    using var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
    using (var canvas = new SKCanvas(bitmap))
    {
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(size);

        using var body = new SKPaint { IsAntialias = true, Color = fill, Style = SKPaintStyle.Fill };
        canvas.DrawPath(glyph, body);
    }

    var pixels = new byte[size * size * 4];
    var straight = new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Unpremul);
    unsafe
    {
        fixed (byte* p = pixels)
        {
            if (!bitmap.PeekPixels().ReadPixels(straight, (IntPtr)p, size * 4))
                throw new InvalidOperationException($"Couldn't read the {size} px icon back.");
        }
    }

    return pixels;
}

// An .ico: 256 px is stored as PNG, as Windows expects; the smaller sizes as 32-bit bitmaps, which everything reads.
static byte[] WriteIco(IReadOnlyList<(int Size, byte[] Pixels)> images)
{
    var entries = images.Select(i => i.Size >= 256 ? EncodePng(i.Size, i.Pixels) : EncodeBitmap(i.Size, i.Pixels)).ToList();

    using var ico = new MemoryStream();
    using var writer = new BinaryWriter(ico);
    writer.Write((ushort)0); // reserved
    writer.Write((ushort)1); // type: icon
    writer.Write((ushort)images.Count);

    int offset = 6 + 16 * images.Count;
    for (int i = 0; i < images.Count; i++)
    {
        int size = images[i].Size;
        writer.Write((byte)(size >= 256 ? 0 : size)); // 0 means 256
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)0); // colour count: not a palette image
        writer.Write((byte)0); // reserved
        writer.Write((ushort)1); // planes
        writer.Write((ushort)32); // bits per pixel
        writer.Write(entries[i].Length);
        writer.Write(offset);
        offset += entries[i].Length;
    }

    foreach (byte[] entry in entries)
        writer.Write(entry);

    writer.Flush();
    return ico.ToArray();
}

static byte[] EncodePng(int size, byte[] pixels)
{
    var info = new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Unpremul);
    unsafe
    {
        fixed (byte* p = pixels)
        {
            using var image = SKImage.FromPixelCopy(info, (IntPtr)p, size * 4);
            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
            return png.ToArray();
        }
    }
}

// A BITMAPINFOHEADER, the colour rows from the bottom up, then the 1-bit AND mask, left clear since the alpha
// channel does its job. The header's height counts both, so it's twice the icon's.
static byte[] EncodeBitmap(int size, byte[] pixels)
{
    int maskStride = (size + 31) / 32 * 4;
    var data = new byte[40 + pixels.Length + maskStride * size];
    var header = data.AsSpan(0, 40);
    BinaryPrimitives.WriteInt32LittleEndian(header, 40);
    BinaryPrimitives.WriteInt32LittleEndian(header[4..], size);
    BinaryPrimitives.WriteInt32LittleEndian(header[8..], size * 2);
    BinaryPrimitives.WriteInt16LittleEndian(header[12..], 1); // planes
    BinaryPrimitives.WriteInt16LittleEndian(header[14..], 32); // bits per pixel
    BinaryPrimitives.WriteInt32LittleEndian(header[20..], pixels.Length + maskStride * size); // image size

    int stride = size * 4;
    for (int row = 0; row < size; row++)
        pixels.AsSpan(row * stride, stride).CopyTo(data.AsSpan(40 + (size - 1 - row) * stride));

    return data;
}
