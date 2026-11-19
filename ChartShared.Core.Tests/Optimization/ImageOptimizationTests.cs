using ChartShared.Core.Optimization;
using ChartShared.Core.Scanning;
using ChartShared.Core.Sng;

namespace ChartShared.Core.Tests.Optimization;

/// <summary>
/// The image optimizer's decisions, which don't need a codec: the size an image is fitted to, which encodes are made,
/// which result is kept, the header reader that gives size and format, and carrying a rename into the metadata.
/// </summary>
[TestClass]
public sealed class ImageOptimizationTests
{
    public TestContext TestContext { get; set; }

    // ---- Fitting -------------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow(2000, 1000, 512, 256)]
    [DataRow(1000, 2000, 256, 512)]
    [DataRow(512, 512, 512, 512)]
    [DataRow(513, 513, 512, 512)]
    [DataRow(1024, 1024, 512, 512)]
    public void AlbumArtIsFittedInside512(int width, int height, int expectedWidth, int expectedHeight) =>
        Assert.AreEqual((expectedWidth, expectedHeight), ImageOptimization.FitWithin(width, height, ImageOptimizeOptions.AlbumArtDefault));

    [TestMethod]
    [DataRow(3840, 2160, 1920, 1080)]
    [DataRow(4000, 1000, 1920, 480)]
    [DataRow(1000, 4000, 270, 1080)]
    [DataRow(1920, 1080, 1920, 1080)]
    [DataRow(2000, 1080, 1920, 1037)]
    public void ABackgroundIsFittedInside1920By1080(int width, int height, int expectedWidth, int expectedHeight) =>
        Assert.AreEqual((expectedWidth, expectedHeight), ImageOptimization.FitWithin(width, height, ImageOptimizeOptions.BackgroundDefault));

    [TestMethod]
    [DataRow(100, 100)]
    [DataRow(511, 300)]
    [DataRow(1, 1)]
    public void AnImageInsideTheLimitsIsNeverScaledUp(int width, int height) =>
        Assert.AreEqual((width, height), ImageOptimization.FitWithin(width, height, ImageOptimizeOptions.AlbumArtDefault));

    [TestMethod]
    public void WithNoLimitsTheSizeIsKept() =>
        Assert.AreEqual((9000, 7000), ImageOptimization.FitWithin(9000, 7000, null, null));

    [TestMethod]
    public void OneLimitAloneBinds()
    {
        Assert.AreEqual((500, 250), ImageOptimization.FitWithin(2000, 1000, 500, null));
        Assert.AreEqual((250, 500), ImageOptimization.FitWithin(500, 1000, null, 500));
    }

    [TestMethod]
    public void TheAspectRatioIsKeptAndASideNeverVanishes()
    {
        var (width, height) = ImageOptimization.FitWithin(1999, 1333, 512, 512);
        Assert.AreEqual(512, width);
        Assert.AreEqual(341, height);
        Assert.IsLessThan(0.005, Math.Abs((1999.0 / 1333) - ((double)width / height)));

        Assert.AreEqual((512, 1), ImageOptimization.FitWithin(10000, 1, 512, 512), "A long thin image keeps one pixel.");
    }

    [TestMethod]
    public void ASizeThatCantBeAnImageIsRejected()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ImageOptimization.FitWithin(0, 10, 512, 512));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ImageOptimization.FitWithin(10, -1, 512, 512));
    }

    [TestMethod]
    public void TheDefaultsAreQuality80AndTheIssuesSizes()
    {
        Assert.AreEqual(80, ImageOptimizeOptions.Default.Quality);
        Assert.AreEqual((512, 512), (ImageOptimizeOptions.AlbumArtDefault.MaxWidth, ImageOptimizeOptions.AlbumArtDefault.MaxHeight));
        Assert.AreEqual((1920, 1080), (ImageOptimizeOptions.BackgroundDefault.MaxWidth, ImageOptimizeOptions.BackgroundDefault.MaxHeight));
        Assert.AreEqual(ImageOptimizeOptions.AlbumArtDefault, OptimizeOptions.Default.AlbumArt);
        Assert.AreEqual(ImageOptimizeOptions.BackgroundDefault, OptimizeOptions.Default.BackgroundArt);
        Assert.IsFalse(OptimizeOptions.Default.AlbumArt.Enabled || OptimizeOptions.Default.BackgroundArt.Enabled, "Every strategy is off until it's chosen.");
    }

    [TestMethod]
    public void QualityIsHeldToTheEncodersRange()
    {
        Assert.AreEqual(1, ImageOptimization.ClampQuality(-5));
        Assert.AreEqual(80, ImageOptimization.ClampQuality(80));
        Assert.AreEqual(100, ImageOptimization.ClampQuality(900));
    }

    // ---- Which encodes ---------------------------------------------------------------------------------------------

    [TestMethod]
    public void AJpegStaysAJpegWhateverItsSize()
    {
        Assert.AreEqual(new ImageCandidates(true, false), ImageOptimization.CandidatesFor(ImageFormat.Jpeg, false, false));
        Assert.AreEqual(new ImageCandidates(true, false), ImageOptimization.CandidatesFor(ImageFormat.Jpeg, false, true));
    }

    [TestMethod]
    public void ATransparentPngIsNeverMadeAJpeg()
    {
        Assert.AreEqual(new ImageCandidates(false, false), ImageOptimization.CandidatesFor(ImageFormat.Png, true, false));
        Assert.AreEqual(new ImageCandidates(false, true), ImageOptimization.CandidatesFor(ImageFormat.Png, true, true));
    }

    [TestMethod]
    public void AnOpaquePngTriesAJpegAndWhenResizedAPngToo()
    {
        Assert.AreEqual(new ImageCandidates(true, false), ImageOptimization.CandidatesFor(ImageFormat.Png, false, false));
        Assert.AreEqual(new ImageCandidates(true, true), ImageOptimization.CandidatesFor(ImageFormat.Png, false, true));
    }

    // ---- Which result ----------------------------------------------------------------------------------------------

    [TestMethod]
    public void APngThatsAlreadySmallerThanItsJpegIsKept()
    {
        var choice = ImageOptimization.Choose(ImageFormat.Png, ".png", 1000, jpegBytes: 1500, pngBytes: null);

        Assert.IsTrue(choice.IsKeep);
        Assert.AreEqual(".png", choice.Extension);
        Assert.IsTrue(choice.ToEstimate().AlreadyOptimal);
    }

    [TestMethod]
    public void APngAsLargeAsItsJpegIsKept()
    {
        Assert.IsTrue(ImageOptimization.Choose(ImageFormat.Png, ".png", 1000, 1000, null).IsKeep, "A tie keeps the original.");
    }

    [TestMethod]
    public void AnOpaquePngThatsSmallerAsJpegBecomesAJpg()
    {
        var choice = ImageOptimization.Choose(ImageFormat.Png, ".png", 1000, jpegBytes: 200, pngBytes: null);

        Assert.AreEqual(ImageOutput.Jpeg, choice.Output);
        Assert.AreEqual(200, choice.Bytes);
        Assert.AreEqual(".jpg", choice.Extension);
        Assert.IsFalse(choice.ToEstimate().AlreadyOptimal);
    }

    [TestMethod]
    public void AResizedPngCanBeatItsJpegAndStaysPng()
    {
        var choice = ImageOptimization.Choose(ImageFormat.Png, ".png", 5000, jpegBytes: 900, pngBytes: 700);

        Assert.AreEqual(ImageOutput.Png, choice.Output);
        Assert.AreEqual(700, choice.Bytes);
        Assert.AreEqual(".png", choice.Extension);
    }

    [TestMethod]
    public void ATransparentPngResizedSmallerIsWrittenAsPng()
    {
        var choice = ImageOptimization.Choose(ImageFormat.Png, ".png", 5000, jpegBytes: null, pngBytes: 1200);

        Assert.AreEqual(ImageOutput.Png, choice.Output);
        Assert.AreEqual(".png", choice.Extension);
    }

    [TestMethod]
    public void ATransparentPngThatIsntResizedHasNothingToWrite() =>
        Assert.IsTrue(ImageOptimization.Choose(ImageFormat.Png, ".png", 5000, null, null).IsKeep);

    [TestMethod]
    [DataRow(".jpg", ".jpg")]
    [DataRow(".JPEG", ".jpeg")]
    [DataRow(".png", ".jpg")]
    public void AJpegResultKeepsAJpegExtensionAndOtherwiseBecomesJpg(string source, string expected) =>
        Assert.AreEqual(expected, ImageOptimization.JpegExtensionFor(source));

    [TestMethod]
    public void ARecompressedJpegKeepsItsName()
    {
        var choice = ImageOptimization.Choose(ImageFormat.Jpeg, ".jpeg", 1000, jpegBytes: 400, pngBytes: null);

        Assert.AreEqual(ImageOutput.Jpeg, choice.Output);
        Assert.AreEqual(".jpeg", choice.Extension);
    }

    [TestMethod]
    public void AJpegThatWouldGrowIsKept() =>
        Assert.IsTrue(ImageOptimization.Choose(ImageFormat.Jpeg, ".jpg", 1000, jpegBytes: 1200, pngBytes: null).IsKeep);

    [TestMethod]
    public void AnOversizedImageIsNeverKeptAsItIsEvenWhenTheResizedOneIsLarger()
    {
        // The original breaks the limits, so it isn't a candidate; the planner reports the growth.
        var jpeg = ImageOptimization.Choose(ImageFormat.Jpeg, ".jpg", 1000, jpegBytes: 1200, pngBytes: null, resize: true);
        var png = ImageOptimization.Choose(ImageFormat.Png, ".png", 1000, jpegBytes: null, pngBytes: 1300, resize: true);

        Assert.AreEqual(ImageOutput.Jpeg, jpeg.Output);
        Assert.AreEqual(1200, jpeg.Bytes);
        Assert.AreEqual(ImageOutput.Png, png.Output);
        Assert.AreEqual(1300, png.Bytes);
        Assert.IsFalse(jpeg.ToEstimate().AlreadyOptimal);
    }

    [TestMethod]
    public void AnOversizedImagesSmallestResultWins()
    {
        var choice = ImageOptimization.Choose(ImageFormat.Png, ".png", 1000, jpegBytes: 1400, pngBytes: 1100, resize: true);

        Assert.AreEqual(ImageOutput.Png, choice.Output);
        Assert.AreEqual(1100, choice.Bytes);
    }

    [TestMethod]
    public void AnOversizedImageWithNoResultIsKept() =>
        Assert.IsTrue(ImageOptimization.Choose(ImageFormat.Png, ".png", 1000, null, null, resize: true).IsKeep);

    [TestMethod]
    [DataRow("album", ".jpg")]
    [DataRow("album", ".jpeg")]
    [DataRow("background", ".jpg")]
    [DataRow("background", ".jpeg")]
    public void TheNamesAResultIsStoredUnderAreValid(string stem, string extension)
    {
        string name = stem + extension;

        Assert.IsNull(FilenameRules.FindProblem(name), $"{name} is a valid stored name.");
        Assert.IsTrue(stem == "album" ? AlbumArt.IsAlbumArtName(name) : SongBackgrounds.IsName(name, BackgroundKind.Image), $"{name} is still recognised.");
        Assert.AreEqual(stem == "album" ? AssetKind.AlbumArt : AssetKind.BackgroundImage, AssetInspector.Classify(name));
    }

    // ---- Estimates are grouped by source format --------------------------------------------------------------------

    [TestMethod]
    public void UnprobedJpgAndJpegGroupAsOneFormat()
    {
        var jpg = OptimizationTestSupport.Asset("album.jpg", 10);
        var jpeg = OptimizationTestSupport.Asset("background.jpeg", 10);
        var png = OptimizationTestSupport.Asset("album.png", 10);

        Assert.AreEqual("image/jpeg", jpg.CodecKey);
        Assert.AreEqual("image/jpeg", jpeg.CodecKey);
        Assert.AreEqual("image/png", png.CodecKey);
    }

    // ---- The header reader -----------------------------------------------------------------------------------------

    [TestMethod]
    public void APngsFormatAndSizeAreRead()
    {
        var header = ImageHeader.Read(new MemoryStream(Png(640, 480)));

        Assert.AreEqual(new ImageHeader(ImageFormat.Png, 640, 480), header);
        Assert.AreEqual("image/png", header!.Value.CodecKey);
    }

    [TestMethod]
    public void AJpegsSizeIsReadPastEarlierSegments()
    {
        // JFIF, a quantisation table, then the frame header.
        var header = ImageHeader.Read(new MemoryStream(Jpeg(1920, 1080, 0xC0, withTables: true)));

        Assert.AreEqual(new ImageHeader(ImageFormat.Jpeg, 1920, 1080), header);
        Assert.AreEqual("image/jpeg", header!.Value.CodecKey);
    }

    [TestMethod]
    [DataRow((byte)0xC0)]
    [DataRow((byte)0xC1)]
    [DataRow((byte)0xC2)]
    [DataRow((byte)0xC9)]
    public void EveryKindOfFrameHeaderIsRead(byte marker) =>
        Assert.AreEqual(new ImageHeader(ImageFormat.Jpeg, 300, 200), ImageHeader.Read(new MemoryStream(Jpeg(300, 200, marker, withTables: false))));

    [TestMethod]
    public void AHuffmanTableIsNotMistakenForAFrameHeader()
    {
        // 0xC4 comes before the frame header here and holds a table, not a size.
        var jpeg = Jpeg(77, 55, 0xC0, withTables: true, huffmanFirst: true);

        Assert.AreEqual(new ImageHeader(ImageFormat.Jpeg, 77, 55), ImageHeader.Read(new MemoryStream(jpeg)));
    }

    [TestMethod]
    public void TheFormatIsTheContentsNotTheName()
    {
        // The reader has no name to go by: a PNG's bytes are a PNG whatever the file is called.
        var asPng = ImageHeader.Read(new MemoryStream(Png(10, 20)));

        Assert.AreEqual(ImageFormat.Png, asPng!.Value.Format);
    }

    [TestMethod]
    public void WhatIsntAnImageHasNoHeader()
    {
        Assert.IsNull(ImageHeader.Read(new MemoryStream([])));
        Assert.IsNull(ImageHeader.Read(new MemoryStream(OptimizationTestSupport.Bytes(100, 3))));
        Assert.IsNull(ImageHeader.Read(new MemoryStream("GIF89a\u0001\u0000\u0001\u0000"u8.ToArray())));
    }

    [TestMethod]
    public void ATruncatedHeaderHasNoSize()
    {
        byte[] png = Png(640, 480);
        byte[] jpeg = Jpeg(640, 480, 0xC0, withTables: true);

        Assert.IsNull(ImageHeader.Read(new MemoryStream(png[..20])));
        Assert.IsNull(ImageHeader.Read(new MemoryStream(png[..8])));
        // Cut inside the frame header: the scan marker's 14 bytes follow it.
        Assert.IsNull(ImageHeader.Read(new MemoryStream(jpeg[..(jpeg.Length - 14 - 12)])));
        Assert.IsNull(ImageHeader.Read(new MemoryStream(jpeg[..2])));
    }

    [TestMethod]
    public void ACgBiPngsSizeIsReadPastItsFirstChunk()
    {
        // Apple's PNGs put a CgBI chunk (4 bytes of data) before IHDR.
        var bytes = new List<byte>(Png(640, 480)[..8]);
        bytes.AddRange([0, 0, 0, 4, (byte)'C', (byte)'g', (byte)'B', (byte)'I', 0x50, 0, 32, 6, 0, 0, 0, 0]);
        bytes.AddRange(Png(640, 480)[8..]);

        Assert.AreEqual(new ImageHeader(ImageFormat.Png, 640, 480), ImageHeader.Read(new MemoryStream([.. bytes])));
    }

    [TestMethod]
    public void APngWhoseFirstChunkIsNeitherIhdrNorCgBiHasNoHeader()
    {
        byte[] bytes = Png(640, 480);
        bytes[12] = (byte)'X';

        Assert.IsNull(ImageHeader.Read(new MemoryStream(bytes)));
    }

    [TestMethod]
    [DataRow(0, false, false)]
    [DataRow(2, false, false)]
    [DataRow(3, false, false)]
    [DataRow(3, true, true)]
    [DataRow(2, true, true)]
    [DataRow(4, false, true)]
    [DataRow(6, false, true)]
    public void APngsChunksShowWhetherItCanBeTransparent(int colourType, bool withTrns, bool expected)
    {
        byte[] png = PngWithChunks((byte)colourType, withTrns);

        Assert.AreEqual(expected, ImageHeader.PngCanBeTransparent(new MemoryStream(png)));
    }

    [TestMethod]
    public void WhatCantBeReadAsAPngMightBeTransparent()
    {
        Assert.IsTrue(ImageHeader.PngCanBeTransparent(new MemoryStream([])));
        Assert.IsTrue(ImageHeader.PngCanBeTransparent(new MemoryStream(Jpeg(10, 10, 0xC0, withTables: false))));
        Assert.IsTrue(ImageHeader.PngCanBeTransparent(new MemoryStream(PngWithChunks(2, withTrns: false)[..40])), "A PNG cut off before its image data can't be shown opaque.");
    }

    [TestMethod]
    public void AZeroSizeIsNotAnImage()
    {
        Assert.IsNull(ImageHeader.Read(new MemoryStream(Png(0, 10))));
        Assert.IsNull(ImageHeader.Read(new MemoryStream(Jpeg(10, 0, 0xC0, withTables: false))));
    }

    [TestMethod]
    public async Task TheProbeGivesTheCodecAndSizeTheEstimatesGroupBy()
    {
        var probe = new ImageHeaderProbe();

        var png = await probe.ProbeAsync("album.png", new MemoryStream(Png(512, 512)), TestContext.CancellationToken);
        var jpeg = await probe.ProbeAsync("album.jpg", new MemoryStream(Jpeg(800, 600, 0xC0, withTables: false)), TestContext.CancellationToken);
        var none = await probe.ProbeAsync("album.png", new MemoryStream(OptimizationTestSupport.Bytes(40, 1)), TestContext.CancellationToken);

        Assert.AreEqual(new ProbeResult("image/png", 512, 512), png);
        Assert.AreEqual(new ProbeResult("image/jpeg", 800, 600), jpeg);
        Assert.IsNull(none);
    }

    // ---- Renames reach the metadata --------------------------------------------------------------------------------

    private static IReadOnlyList<KeyValuePair<string, string>> Pairs(params (string Key, string Value)[] pairs) =>
        [.. pairs.Select(p => KeyValuePair.Create(p.Key, p.Value))];

    private static IReadOnlyDictionary<string, string> Renames(params (string From, string To)[] renames) =>
        renames.ToDictionary(r => r.From, r => r.To);

    [TestMethod]
    public void AValueNamingARenamedFileFollowsIt()
    {
        var pairs = Pairs(("name", "Song"), ("background", "Background.PNG"), ("cover", " album.png "), ("artist", "background.png!"));
        var edits = new ChartShared.Core.Metadata.MetadataDraft();

        bool changed = ImageOptimization.UpdateReferences(pairs, Renames(("background.png", "background.jpg"), ("album.png", "album.jpg")), edits);

        Assert.IsTrue(changed);
        Assert.AreSequenceEqual(
            [KeyValuePair.Create("name", "Song"), KeyValuePair.Create("background", "background.jpg"), KeyValuePair.Create("cover", "album.jpg"), KeyValuePair.Create("artist", "background.png!")],
            edits.ApplyTo(pairs));
    }

    [TestMethod]
    public void OnlyTheImageKeysFollowARename()
    {
        // Text that happens to equal the old name isn't a reference to it.
        var pairs = Pairs(("name", "album.png"), ("album", "album.png"), ("charter", "background.png"), ("icon", "album.png"), ("cover", "album.png"));
        var edits = new ChartShared.Core.Metadata.MetadataDraft();

        Assert.IsTrue(ImageOptimization.UpdateReferences(pairs, Renames(("album.png", "album.jpg"), ("background.png", "background.jpg")), edits));
        Assert.AreSequenceEqual(
            [KeyValuePair.Create("name", "album.png"), KeyValuePair.Create("album", "album.png"), KeyValuePair.Create("charter", "background.png"), KeyValuePair.Create("icon", "album.png"), KeyValuePair.Create("cover", "album.jpg")],
            edits.ApplyTo(pairs));
    }

    [TestMethod]
    public void AValueNamingARenamedFileUnderAnotherKeyIsLeftAlone()
    {
        var pairs = Pairs(("name", "album.png"), ("artist", "album.png"));
        var edits = new ChartShared.Core.Metadata.MetadataDraft();

        Assert.IsFalse(ImageOptimization.UpdateReferences(pairs, Renames(("album.png", "album.jpg")), edits));
        Assert.IsFalse(edits.HasMetadataChanges);
    }

    [TestMethod]
    public void NothingNamingARenamedFileLeavesTheMetadataAlone()
    {
        var pairs = Pairs(("name", "album.png is nice"), ("charter", "x"));
        var edits = new ChartShared.Core.Metadata.MetadataDraft();

        Assert.IsFalse(ImageOptimization.UpdateReferences(pairs, Renames(("album.png", "album.jpg")), edits));
        Assert.IsFalse(edits.HasMetadataChanges);
        Assert.IsFalse(ImageOptimization.UpdateReferences(pairs, Renames(), edits));
    }

    [TestMethod]
    public void ADuplicatedKeyIsOnlyEditedWhenEveryValueNamesTheFile()
    {
        var renames = Renames(("album.png", "album.jpg"));
        var mixed = new ChartShared.Core.Metadata.MetadataDraft();
        var same = new ChartShared.Core.Metadata.MetadataDraft();

        Assert.IsFalse(ImageOptimization.UpdateReferences(Pairs(("cover", "album.png"), ("cover", "other")), renames, mixed));
        Assert.IsTrue(ImageOptimization.UpdateReferences(Pairs(("cover", "album.png"), ("cover", "ALBUM.PNG")), renames, same));
        Assert.IsFalse(mixed.HasMetadataChanges);
    }

    // ---- Synthetic headers -----------------------------------------------------------------------------------------

    /// <summary>A PNG's signature and IHDR; nothing after it is read.</summary>
    internal static byte[] Png(int width, int height)
    {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
        bytes.AddRange(BigEndian(width));
        bytes.AddRange(BigEndian(height));
        bytes.AddRange([8, 6, 0, 0, 0, 0, 0, 0, 0]);
        return [.. bytes];
    }

    /// <summary>A PNG's signature, an IHDR of colour type <paramref name="colourType"/>, an optional tRNS chunk, then an IDAT chunk.</summary>
    private static byte[] PngWithChunks(byte colourType, bool withTrns)
    {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' };
        bytes.AddRange(BigEndian(16));
        bytes.AddRange(BigEndian(16));
        bytes.AddRange([8, colourType, 0, 0, 0, 0, 0, 0, 0]);
        // An ancillary chunk (gAMA, here with 3 bytes of data) between IHDR and the image data, to be walked past.
        bytes.AddRange([0, 0, 0, 3, (byte)'g', (byte)'A', (byte)'M', (byte)'A', 1, 2, 3, 0, 0, 0, 0]);
        if (withTrns)
            bytes.AddRange([0, 0, 0, 1, (byte)'t', (byte)'R', (byte)'N', (byte)'S', 0, 0, 0, 0, 0]);
        bytes.AddRange([0, 0, 0, 2, (byte)'I', (byte)'D', (byte)'A', (byte)'T', 0x78, 0x9C, 0, 0, 0, 0]);
        return [.. bytes];
    }

    /// <summary>A JPEG's markers up to a frame header of kind <paramref name="frame"/>, then a scan marker.</summary>
    internal static byte[] Jpeg(int width, int height, byte frame, bool withTables, bool huffmanFirst = false)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };
        if (withTables)
        {
            // JFIF (APP0): length 16.
            bytes.AddRange([0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0]);
            if (huffmanFirst)
                bytes.AddRange(Segment(0xC4, 20));
            bytes.AddRange(Segment(0xDB, 65));
        }

        bytes.AddRange([0xFF, 0xFF, frame, 0, 17, 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1]);
        bytes.AddRange([0xFF, 0xDA, 0, 12, 3, 1, 0, 2, 0x11, 3, 0x11, 0, 63, 0]);
        return [.. bytes];
    }

    private static byte[] Segment(byte marker, int bodyLength)
    {
        var bytes = new List<byte> { 0xFF, marker, (byte)((bodyLength + 2) >> 8), (byte)(bodyLength + 2) };
        // Bytes that include an 0xFF 0xC0 pair, which only a reader that skips the body properly steps over.
        for (int i = 0; i < bodyLength; i++)
            bytes.Add(i == 3 ? (byte)0xFF : i == 4 ? (byte)0xC0 : (byte)i);
        return [.. bytes];
    }

    private static byte[] BigEndian(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
}
