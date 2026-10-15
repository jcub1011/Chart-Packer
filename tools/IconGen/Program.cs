using System.Buffers.Binary;
using System.Xml.Linq;
using SkiaSharp;

// Renders the program icons from the Material Symbols sources in Sources/ (see README.md):
//
//   dotnet run --project tools/IconGen
//
// Each source is drawn in the accent colour with a dark outline, so it reads on light and dark backgrounds,
// and written over the .ico it's for. Run from anywhere inside the repo.

// The accent of ChartPacker.UI/Styles/Theme.axaml (AccentButtonBackground), and a dark shade of it for the outline.
var fill = SKColor.Parse("#8B5CF6");
var outline = SKColor.Parse("#2E1A6B");

// The sizes Windows asks an icon for, from the small list view to the extra-large icons.
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 256];

(string Source, string Icon)[] icons =
[
    ("inventory_2_fill1_48px.svg", "ChartPacker/Assets/ChartPacker.ico"),
    ("hardware_fill1_48px.svg", "ChartBuilder/Assets/ChartBuilder.ico"),
    ("album_fill1_48px.svg", "installer/SngFile.ico"),
];

string? repo = FindRepo(Directory.GetCurrentDirectory());
if (repo is null)
{
    Console.Error.WriteLine("Run this from inside the Chart-Packer repo.");
    return 1;
}

foreach (var (source, icon) in icons)
{
    using SKPath glyph = ReadGlyph(Path.Combine(repo, "tools", "IconGen", "Sources", source));
    string target = Path.Combine(repo, icon);
    File.WriteAllBytes(target, WriteIco([.. sizes.Select(size => (size, Render(glyph, size)))]));
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

// A Material Symbols SVG is one path in a 960-unit box whose top is at y = -960.
static SKPath ReadGlyph(string svg)
{
    XNamespace ns = "http://www.w3.org/2000/svg";
    string data = XDocument.Load(svg).Descendants(ns + "path").Single().Attribute("d")!.Value;
    var path = SKPath.ParseSvgPathData(data) ?? throw new InvalidDataException($"{svg} has a path Skia can't read.");
    path.Transform(SKMatrix.CreateTranslation(0, 960));
    return path;
}

// The glyph at size × size, as straight-alpha BGRA rows from the top, which is what an icon holds.
byte[] Render(SKPath glyph, int size)
{
    using var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
    using (var canvas = new SKCanvas(bitmap))
    {
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(size / 960f);

        // The outline is a stroke under the fill, so only its outer half shows: 1 px at 16 px, wider as the icon grows.
        float outlineWidth = Math.Max(1f, size / 20f) * 960f / size;
        using var stroke = new SKPaint { IsAntialias = true, Color = outline, Style = SKPaintStyle.Stroke, StrokeWidth = outlineWidth * 2, StrokeJoin = SKStrokeJoin.Round };
        using var body = new SKPaint { IsAntialias = true, Color = fill, Style = SKPaintStyle.Fill };
        canvas.DrawPath(glyph, stroke);
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
