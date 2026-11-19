using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ChartShared.UI.Controls.Skia;
using SkiaSharp;

namespace ChartBuilder.Tests.Preview;

/// <summary>
/// The highways' drawing for SkiaSharp: recorded on the UI thread into a <see cref="DrawList"/> that makes no garbage once it's
/// warm, with colours, opacity and bounds worked out as Avalonia works them out, and pictures kept until no drawing holds them.
/// </summary>
[TestClass]
public sealed class SkiaDrawListTests
{
    private static readonly IBrush Red = new ImmutableSolidColorBrush(Color.FromArgb(200, 220, 40, 40));
    private static readonly IPen Edge = new ImmutablePen(Color.FromArgb(0xA0, 0, 0, 0).ToUInt32(), 2);
    private static readonly IPen Dashed = new ImmutablePen(new ImmutableSolidColorBrush(Colors.White), 1, new ImmutableDashStyle([3, 3], 0));
    private static readonly HighwayPath Hexagon = HighwayPath.Polygon([new Point(0, 10), new Point(8, 0), new Point(32, 0), new Point(40, 10), new Point(32, 20), new Point(8, 20)]);

    [TestMethod]
    public void RecordingAFrameAgainMakesNoGarbage()
    {
        var list = new DrawList();
        for (int i = 0; i < 3; i++)
            RecordScene(list);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20; i++)
            RecordScene(list);

        Assert.AreEqual(0, GC.GetAllocatedBytesForCurrentThread() - before, "Bytes allocated recording 20 warm frames.");
    }

    [TestMethod]
    public void OpacityIsWorkedIntoTheColourAsAvaloniaWorksItIn()
    {
        var list = new DrawList();
        using (list.PushOpacity(0.45))
            list.FillRectangle(Red, new Rect(0, 0, 10, 10));

        // Avalonia's Skia backend: the colour's alpha times the brush's and the pushed opacity, cut to a whole 255th.
        var op = list.Ops[0];
        Assert.AreEqual((uint)(byte)(200 * 0.45), op.Color >> 24);
        Assert.AreEqual(0x00DC2828u, op.Color & 0x00FFFFFF);
    }

    [TestMethod]
    public void BoundsTakeInTransformsButNotClips()
    {
        var list = new DrawList();
        list.FillRectangle(Red, new Rect(10, 10, 20, 20));
        using (list.PushClip(new Rect(0, 0, 5, 5)))
        using (list.PushTransform(Matrix.CreateTranslation(100, 0)))
            list.FillRectangle(Red, new Rect(0, 0, 5, 5));

        // As Avalonia's bounds of a recorded drawing: what's clipped still counts, where it's moved to.
        Assert.AreEqual(new Rect(10, 0, 95, 30), list.Bounds);
    }

    [TestMethod]
    public void ALineReachesHalfItsPenAcrossIt()
    {
        var list = new DrawList();
        list.DrawLine(Edge, new Point(0, 10), new Point(50, 10));

        // A flat cap: out across the line by half the pen, not along it.
        Assert.AreEqual(0, list.Bounds.Left, 1e-9);
        Assert.AreEqual(50, list.Bounds.Right, 1e-9);
        Assert.AreEqual(9, list.Bounds.Top, 1e-9);
        Assert.AreEqual(11, list.Bounds.Bottom, 1e-9);
    }

    [TestMethod]
    public void ResettingEmptiesTheDrawing()
    {
        var list = new DrawList();
        RecordScene(list);

        list.Reset();

        Assert.AreEqual(0, list.Count);
        Assert.AreEqual(0, list.ResourceCount);
        Assert.AreEqual(default, list.Bounds);
    }

    [TestMethod]
    public void APictureLetGoIsKeptUntilNoDrawingHoldsIt()
    {
        var image = HighwayImage.Of(SKImage.Create(new SKImageInfo(4, 4)));
        var list = new DrawList();
        list.DrawImage(image, new Rect(0, 0, 4, 4), new Rect(0, 0, 8, 8));

        image.Dispose();
        Assert.AreNotEqual(IntPtr.Zero, image.Image!.Handle, "Disposed while a drawing still holds it.");

        list.Reset();
        Assert.AreEqual(IntPtr.Zero, image.Image.Handle, "Kept after the last drawing holding it let it go.");
    }

    [TestMethod]
    public void APictureNoDrawingHoldsGoesWhenLetGo()
    {
        var image = HighwayImage.Of(SKImage.Create(new SKImageInfo(4, 4)));

        image.Dispose();

        Assert.AreEqual(IntPtr.Zero, image.Image!.Handle);
    }

    [TestMethod]
    public void AShapeIsFilledFromItsFilledFiguresAndEdgedAlongAll()
    {
        var builder = new HighwayPath.Builder();
        builder.BeginFigure(new Point(0, 0), false);
        builder.LineTo(new Point(10, 0));
        builder.EndFigure(false);
        var line = builder.Build();

        Assert.IsNull(line.FillPath, "An unfilled figure fills nothing.");
        Assert.IsNotNull(line.StrokePath);
        Assert.IsNotNull(Hexagon.FillPath);
        Assert.AreEqual(new Rect(0, 0, 40, 20), Hexagon.Bounds);
    }

    [TestMethod]
    public void WithNoPenAShapeReachesAsFarAsAllItsFiguresWhetherFilledOrNot()
    {
        Assert.AreEqual(new Rect(0, 0, 40, 20), Hexagon.ReachWith(null), "Every figure filled.");

        // A filled square, and a line out past it that isn't.
        var builder = new HighwayPath.Builder();
        builder.BeginFigure(new Point(0, 0), true);
        builder.LineTo(new Point(10, 0));
        builder.LineTo(new Point(10, 10));
        builder.LineTo(new Point(0, 10));
        builder.EndFigure(true);
        builder.BeginFigure(new Point(10, 10), false);
        builder.LineTo(new Point(30, 10));
        builder.EndFigure(false);

        Assert.AreEqual(new Rect(0, 0, 30, 10), builder.Build().ReachWith(null), "Some figures only edged.");
    }

    [TestMethod]
    public void WhatsRecordedIsDrawnWhereItWasRecorded()
    {
        var canvas = SkiaRaster.Begin();
        canvas.FillRectangle(new ImmutableSolidColorBrush(Colors.Red), new Rect(2, 2, 4, 4));
        using var image = SkiaRaster.End(10, 10);
        using var pixels = SKBitmap.FromImage(image);

        Assert.AreEqual(SKColors.Red, pixels.GetPixel(3, 3));
        Assert.AreEqual(0, pixels.GetPixel(0, 0).Alpha, "Clear outside the rectangle.");
        Assert.AreEqual(0, pixels.GetPixel(7, 7).Alpha, "Clear outside the rectangle.");
    }

    private static void RecordScene(DrawList list)
    {
        list.Reset();
        list.FillRectangle(Red, new Rect(0, 0, 100, 40));
        list.DrawRectangle(Red, Edge, new Rect(10, 10, 30, 12), 3, 3);
        list.DrawLine(Dashed, new Point(0, 50), new Point(100, 50));
        using (list.PushOpacity(0.5))
        using (list.PushClip(new Rect(0, 0, 50, 50)))
        using (list.PushTransform(Matrix.CreateTranslation(20, 20)))
        {
            list.DrawGeometry(Red, Edge, Hexagon);
            using (list.PushGeometryClip(Hexagon))
                list.FillRectangle(Red, new Rect(0, 0, 40, 10));
        }
    }
}
