using ChartShared.UI.Services;
using ChartShared.UI.Views;

namespace ChartBuilder.Tests.Windows;

/// <summary>The Settings window's saved size, tidied as the settings are loaded.</summary>
[TestClass]
public sealed class SettingsWindowSizeTests
{
    [TestMethod]
    public void KeepsASizeSavedWhole()
    {
        var saved = new SavedSize { SettingsWindowWidth = 820, SettingsWindowHeight = 560 };

        SettingsWindowSize.Sanitize(saved);

        Assert.AreEqual(820, saved.SettingsWindowWidth);
        Assert.AreEqual(560, saved.SettingsWindowHeight);
    }

    [TestMethod]
    public void DropsAHalfSavedSizeWhole()
    {
        var saved = new SavedSize { SettingsWindowWidth = 820 };

        SettingsWindowSize.Sanitize(saved);

        Assert.IsNull(saved.SettingsWindowWidth);
        Assert.IsNull(saved.SettingsWindowHeight);
    }

    [TestMethod]
    public void ClampsASizeBiggerThanAnyScreen()
    {
        var saved = new SavedSize { SettingsWindowWidth = 20000, SettingsWindowHeight = 560 };

        SettingsWindowSize.Sanitize(saved);

        Assert.AreEqual(WindowBounds.MaxSize.Width, saved.SettingsWindowWidth);
        Assert.AreEqual(560, saved.SettingsWindowHeight);
    }

    private sealed class SavedSize : ISettingsWindowSize
    {
        public double? SettingsWindowWidth { get; set; }

        public double? SettingsWindowHeight { get; set; }
    }
}
