using Avalonia;
using ChartShared.UI.Controls.Highway;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// The camera behind a perspective highway: the plane is tilted back about its bottom edge, and the notes drawn upright on
/// top of it are placed by the camera's own maths, so that maths must agree with the tilt Avalonia draws.
/// </summary>
[TestClass]
public sealed class PerspectiveCameraTests
{
    private const double Width = 420;
    private const double Height = 760;

    private static readonly Point[] PlanePoints =
    [
        new(210, 2900), new(0, 2900), new(420, 2900), new(60, 2000), new(360, 1500), new(210, 900), new(10, 400), new(410, 100), new(210, 0),
    ];

    private static PerspectiveCamera Camera(double tilt = PerspectiveCamera.DefaultTilt, double farWidth = PerspectiveCamera.DefaultFarWidth, double fill = PerspectiveCamera.DefaultFill) =>
        new(Width, Height, tilt, farWidth, fill);

    [TestMethod]
    public void ProjectingThenUnprojectingGivesTheSamePlanePoint()
    {
        foreach (var camera in new[] { Camera(), Camera(45, 0.35, 0.8), Camera(75, 0.7, 1) })
        {
            foreach (var plane in PlanePoints)
            {
                var point = new Point(plane.X * Width / 420, plane.Y * camera.PlaneHeight / 2900);
                var back = camera.Unproject(camera.Project(point));

                Assert.IsNotNull(back, $"{point} at {camera.Tilt}°.");
                Assert.AreEqual(point.X, back.Value.X, 1e-6, $"{point} at {camera.Tilt}°, x.");
                Assert.AreEqual(point.Y, back.Value.Y, 1e-6, $"{point} at {camera.Tilt}°, y.");
            }
        }
    }

    [TestMethod]
    public void TheNearEdgeStaysWhereItIs()
    {
        var camera = Camera();

        var left = camera.Project(new Point(0, camera.PlaneHeight));
        var right = camera.Project(new Point(Width, camera.PlaneHeight));

        Assert.AreEqual(0, left.X, 1e-9);
        Assert.AreEqual(Width, right.X, 1e-9);
        Assert.AreEqual(Height, left.Y, 1e-9, "The bottom of the plane is the bottom of the host.");
        Assert.AreEqual(1, camera.ScaleAt(camera.PlaneHeight), 1e-12);
    }

    [TestMethod]
    public void TheFarEndIsAsWideAndAsHighAsTheCameraSaysItIs()
    {
        foreach (var camera in new[] { Camera(), Camera(50, 0.3, 0.75), Camera(70, 0.6, 1) })
        {
            var left = camera.Project(new Point(0, 0));
            var right = camera.Project(new Point(Width, 0));

            Assert.AreEqual(Width * camera.FarWidth, right.X - left.X, 1e-6, $"{camera.Tilt}°'s width at the far end.");
            Assert.AreEqual(Height * (1 - camera.Fill), left.Y, 1e-6, $"{camera.Tilt}°'s height at the far end.");
            Assert.AreEqual(camera.FarWidth, camera.ScaleAt(0), 1e-9);
        }
    }

    [TestMethod]
    public void ThePlaneIsTallerThanTheHostAndItsTopIsAboveTheHost()
    {
        var camera = Camera();

        Assert.IsGreaterThan(Height, camera.PlaneHeight);
        Assert.AreEqual(Height - camera.PlaneHeight, camera.PlaneTop, 1e-9);
        Assert.IsLessThan(0, camera.PlaneTop);
    }

    [TestMethod]
    public void TheMathsAgreesWithTheTiltAvaloniaDraws()
    {
        // What Avalonia does to a point of the control: about its origin, here the bottom centre of the plane, by the
        // transform's matrix, with the perspective divide. The control sits PlaneTop down the host.
        foreach (var camera in new[] { Camera(), Camera(45, 0.35, 0.8), Camera(75, 0.7, 1) })
        {
            var m = camera.Transform.Value;
            foreach (var plane in PlanePoints)
            {
                var point = new Point(plane.X * Width / 420, plane.Y * camera.PlaneHeight / 2900);
                double dx = point.X - (Width / 2);
                double dy = point.Y - camera.PlaneHeight;
                double divide = (dx * m.M13) + (dy * m.M23) + m.M33;
                double x = (((dx * m.M11) + (dy * m.M21) + m.M31) / divide) + (Width / 2);
                double y = (((dx * m.M12) + (dy * m.M22) + m.M32) / divide) + camera.PlaneHeight + camera.PlaneTop;

                var projected = camera.Project(point);
                // Avalonia builds the matrix in single precision, so a thousandth of a pixel is as close as they can agree.
                Assert.AreEqual(x, projected.X, 1e-3, $"{point} at {camera.Tilt}°, x.");
                Assert.AreEqual(y, projected.Y, 1e-3, $"{point} at {camera.Tilt}°, y.");
            }
        }
    }

    [TestMethod]
    public void ThePlaneNarrowsTheFurtherAlongItIs()
    {
        var camera = Camera();

        double previous = double.MaxValue;
        for (double y = camera.PlaneHeight; y >= 0; y -= camera.PlaneHeight / 20)
        {
            double scale = camera.ScaleAt(y);
            Assert.IsLessThanOrEqualTo(previous, scale, $"At {y}.");
            previous = scale;
        }
    }

    [TestMethod]
    public void TheDepthScaleIsHowFastTheHostHeightChangesAlongThePlane()
    {
        var camera = Camera();

        foreach (double y in new[] { camera.PlaneHeight, camera.PlaneHeight * 0.6, camera.PlaneHeight * 0.2 })
        {
            const double Step = 0.01;
            double slope = (camera.Project(new Point(Width / 2, y + Step)).Y - camera.Project(new Point(Width / 2, y)).Y) / Step;

            Assert.AreEqual(camera.DepthScaleAt(y), slope, 1e-4, $"At {y}.");
        }
    }

    [TestMethod]
    public void NothingAtOrAboveTheVanishingLineIsSeen()
    {
        var camera = Camera();
        double vanishing = camera.Depth / Math.Tan(camera.Tilt * Math.PI / 180);

        Assert.IsNull(camera.Unproject(new Point(Width / 2, Height - vanishing)));
        Assert.IsNull(camera.Unproject(new Point(Width / 2, -5000)));
        Assert.IsNotNull(camera.Unproject(new Point(Width / 2, Height - (vanishing * 0.99))));
    }

    [TestMethod]
    public void TheFarEndFadesOutOverItsLastStretchOnly()
    {
        var camera = Camera();

        Assert.AreEqual(1, camera.OpacityAt(camera.PlaneHeight), 1e-12);
        Assert.AreEqual(1, camera.OpacityAt(camera.PlaneHeight * 0.2), 1e-12, "Within the first 85% of the way there is no fade.");
        Assert.AreEqual(0, camera.OpacityAt(0), 1e-12);
        Assert.IsTrue(camera.OpacityAt(camera.PlaneHeight * 0.07) is > 0 and < 1, "Part way through the fade.");

        double previous = 2;
        for (double y = camera.PlaneHeight; y >= 0; y -= camera.PlaneHeight / 100)
        {
            double opacity = camera.OpacityAt(y);
            Assert.IsLessThanOrEqualTo(previous, opacity, $"At {y}.");
            previous = opacity;
        }
    }

    [TestMethod]
    public void TheFadeGradientRunsDownTheHostFromCoveredToClear()
    {
        foreach (var camera in new[] { Camera(), Camera(45, 0.35, 0.8), Camera(75, 0.7, 1) })
        {
            var stops = camera.FadeStops();

            Assert.IsGreaterThanOrEqualTo(3, stops.Count);
            Assert.AreEqual(1, stops[0].Alpha, 1e-12, $"{camera.Tilt}°: the host above the far end is covered.");
            Assert.AreEqual(0, stops[^1].Alpha, 1e-12, $"{camera.Tilt}°: the strike end is clear.");
            Assert.AreEqual(1, stops[^1].Offset, 1e-12);
            for (int i = 1; i < stops.Count; i++)
            {
                Assert.IsGreaterThan(stops[i - 1].Offset, stops[i].Offset, $"{camera.Tilt}°: offsets rise, at {i}.");
                Assert.IsLessThanOrEqualTo(stops[i - 1].Alpha, stops[i].Alpha, $"{camera.Tilt}°: alphas fall, at {i}.");
            }
        }
    }

    [TestMethod]
    public void TheFadeIsClearOverTheStrikeEndOfTheHost()
    {
        var camera = Camera();
        var stops = camera.FadeStops();

        // Everything below the point 15% of the plane from its far end is clear: at least half the host's height here.
        var clear = stops.Where(s => s.Alpha == 0).ToList();
        Assert.IsNotEmpty(clear);
        Assert.IsLessThan(0.6, clear[0].Offset, "The fade is a short band at the far end, not half the lane.");
    }

    [TestMethod]
    public void AwkwardSettingsAreKeptSomewhereWorkable()
    {
        var camera = new PerspectiveCamera(-5, 0, 0, 0, 0);

        Assert.AreEqual(1, camera.HostWidth);
        Assert.AreEqual(1, camera.HostHeight);
        Assert.AreEqual(5, camera.Tilt);
        Assert.AreEqual(0.05, camera.FarWidth);
        Assert.AreEqual(0.2, camera.Fill);
        Assert.IsTrue(double.IsFinite(camera.PlaneHeight) && double.IsFinite(camera.Depth));

        var steep = new PerspectiveCamera(Width, Height, 200, 5, 5);
        Assert.AreEqual(85, steep.Tilt);
        Assert.AreEqual(0.95, steep.FarWidth);
        Assert.AreEqual(1, steep.Fill);
    }
}
