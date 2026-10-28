using Avalonia;
using Avalonia.Controls;
using ChartShared.UI.Services;
using ChartShared.UI.Views;

namespace ChartBuilder.Tests.Windows;

/// <summary>How a window is remembered as it closes, and the windows saved, tidied as the settings are loaded.</summary>
[TestClass]
public sealed class WindowMemoryTests
{
    private static readonly Size Opened = new(820, 560);

    private static readonly WindowBounds Moved = new(new PixelPoint(-1200, 40), Opened);

    private static readonly WindowBounds Resized = new(new PixelPoint(100, 40), new Size(1000, 700));

    [TestMethod]
    public void KeepsASizeTheUserChose()
    {
        var captured = WindowMemory.Captured(null, WindowState.Normal, Resized, Opened, position: false);

        Assert.AreEqual(new SavedWindow { Width = 1000, Height = 700 }, captured);
    }

    [TestMethod]
    public void KeepsTheSizeSavedWhenItClosesAtTheSizeItOpenedAt()
    {
        // A size fitted down to a smaller screen isn't one the user chose.
        var saved = new SavedWindow { Width = 2400, Height = 1400 };

        Assert.IsNull(WindowMemory.Captured(saved, WindowState.Normal, Moved, Opened, position: false));
    }

    [TestMethod]
    public void KeepsTheSizeSavedWhenItClosesRoundedToTheScreensPixels()
    {
        // At 175%, the window's size comes back as whole pixels: a fraction of a pixel off the size it opened at.
        var saved = new SavedWindow { Width = 2400, Height = 1400 };
        var rounded = new WindowBounds(new PixelPoint(100, 40), new Size(Opened.Width + 0.57, Opened.Height - 0.29));

        Assert.IsNull(WindowMemory.Captured(saved, WindowState.Normal, rounded, Opened, position: false));
    }

    [TestMethod]
    public void KeepsASizeTheUserChoseJustBigger()
    {
        var bigger = new WindowBounds(new PixelPoint(100, 40), new Size(Opened.Width + WindowMemory.SizeTolerance, Opened.Height));

        var captured = WindowMemory.Captured(null, WindowState.Normal, bigger, Opened, position: false);

        Assert.AreEqual(new SavedWindow { Width = bigger.Size.Width, Height = Opened.Height }, captured);
    }

    [TestMethod]
    public void KeepsWhereAMainWindowWasWithItsSize()
    {
        var captured = WindowMemory.Captured(null, WindowState.Normal, Moved, Opened, position: true);

        Assert.AreEqual(new SavedWindow { X = -1200, Y = 40, Width = 820, Height = 560 }, captured);
    }

    [TestMethod]
    public void KeepsWhereAMainWindowWasOnlyForAMainWindow()
    {
        var captured = WindowMemory.Captured(null, WindowState.Normal, Resized, Opened, position: false);

        Assert.IsNull(captured?.X);
        Assert.IsNull(captured?.Y);
    }

    [TestMethod]
    public void KeepsTheNormalSizeOfAWindowClosedMaximized()
    {
        var saved = new SavedWindow { Width = 1000, Height = 700 };

        var captured = WindowMemory.Captured(saved, WindowState.Maximized, null, Opened, position: false);

        Assert.AreEqual(saved with { Maximized = true }, captured);
    }

    [TestMethod]
    public void TellsFullScreenApartFromMaximized()
    {
        var saved = new SavedWindow { Width = 1000, Height = 700, Maximized = true };

        var captured = WindowMemory.Captured(saved, WindowState.FullScreen, null, Opened, position: true);

        Assert.AreEqual(saved with { Maximized = false, FullScreen = true }, captured);
    }

    [TestMethod]
    public void OpensNormalAgainOnceClosedNormal()
    {
        var saved = new SavedWindow { Width = 1000, Height = 700, Maximized = true };

        var captured = WindowMemory.Captured(saved, WindowState.Normal, Resized, new Size(1000, 700), position: false);

        Assert.AreEqual(saved with { Maximized = false }, captured);
    }

    [TestMethod]
    public void ChangesNothingWhenClosedMinimized()
    {
        var saved = new SavedWindow { Width = 1000, Height = 700, Maximized = true };

        Assert.IsNull(WindowMemory.Captured(saved, WindowState.Minimized, null, Opened, position: true));
    }

    [TestMethod]
    public void KeepsWindowsSavedWhole()
    {
        var main = new SavedWindow { X = -1200, Y = 40, Width = 1280, Height = 800, Maximized = true };
        var dialog = new SavedWindow { Width = 820, Height = 560 };
        var saved = Saved(("MainWindow", main), ("SettingsWindow", dialog));

        WindowMemory.Sanitize(saved);

        Assert.AreEqual(main, saved.Windows!["MainWindow"]);
        Assert.AreEqual(dialog, saved.Windows["SettingsWindow"]);
    }

    [TestMethod]
    public void DropsAHalfSavedSizeWithThePositionThatNeedsIt()
    {
        var saved = Saved(("MainWindow", new SavedWindow { X = 100, Y = 40, Width = 1280, Maximized = true }));

        WindowMemory.Sanitize(saved);

        Assert.AreEqual(new SavedWindow { Maximized = true }, saved.Windows!["MainWindow"]);
    }

    [TestMethod]
    public void DropsAHalfSavedPosition()
    {
        var saved = Saved(("MainWindow", new SavedWindow { X = 100, Width = 1280, Height = 800 }));

        WindowMemory.Sanitize(saved);

        Assert.AreEqual(new SavedWindow { Width = 1280, Height = 800 }, saved.Windows!["MainWindow"]);
    }

    [TestMethod]
    public void ClampsASizeBiggerThanAnyScreen()
    {
        var saved = Saved(("SettingsWindow", new SavedWindow { Width = 20000, Height = 560 }));

        WindowMemory.Sanitize(saved);

        Assert.AreEqual(new SavedWindow { Width = WindowBounds.MaxSize.Width, Height = 560 }, saved.Windows!["SettingsWindow"]);
    }

    [TestMethod]
    public void DropsWindowsWithNoNameOrNothingToRemember()
    {
        var saved = Saved(
            (" ", new SavedWindow { Width = 820, Height = 560 }),
            ("ReduceWindow", null!),
            ("PreviewWindow", new SavedWindow { Width = double.NaN, Height = 560 }));

        WindowMemory.Sanitize(saved);

        Assert.IsNull(saved.Windows);
    }

    [TestMethod]
    public void KeepsNoMoreThanTheMostWindows()
    {
        var saved = new Remembered { Windows = [] };
        for (int i = 0; i < WindowMemory.MaxWindows + 10; i++)
            saved.Windows[$"Window{i}"] = new SavedWindow { Width = 820, Height = 560 };

        WindowMemory.Sanitize(saved);

        Assert.HasCount(WindowMemory.MaxWindows, saved.Windows);
    }

    private static Remembered Saved(params (string Key, SavedWindow Window)[] windows)
    {
        var saved = new Remembered { Windows = [] };
        foreach (var (key, window) in windows)
            saved.Windows[key] = window;
        return saved;
    }

    private sealed class Remembered : IRememberedWindows
    {
        public Dictionary<string, SavedWindow>? Windows { get; set; }
    }
}
