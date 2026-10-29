using Avalonia.Platform.Storage;
using ChartShared.UI.Services;

namespace ChartBuilder.Tests.Help;

/// <summary>What opening a help page or the notices does when the browser can, and can't, be launched.</summary>
[TestClass]
public sealed class HelpLauncherTests
{
    private sealed class FakeLauncher(Func<Uri, bool> launch) : ILauncher
    {
        public List<Uri> Launched { get; } = [];

        public Task<bool> LaunchUriAsync(Uri uri)
        {
            Launched.Add(uri);
            return Task.FromResult(launch(uri));
        }

        public Task<bool> LaunchFileAsync(IStorageItem storageItem) => Task.FromResult(false);
    }

    private static Task<string> Ref() => Task.FromResult("v1.0.0");

    private static string Url(string gitRef) => "https://example.test/" + gitRef;

    [TestMethod]
    public async Task OpensTheAddressOfTheRef()
    {
        var launcher = new FakeLauncher(_ => true);

        bool opened = await HelpLauncher.OpenAsync(launcher, Ref, Url, "a page");

        Assert.IsTrue(opened);
        CollectionAssert.AreEqual(new[] { new Uri("https://example.test/v1.0.0") }, launcher.Launched);
    }

    [TestMethod]
    public async Task ReportsFailureWithNoLauncher()
    {
        Assert.IsFalse(await HelpLauncher.OpenAsync(null, Ref, Url, "a page"));
    }

    [TestMethod]
    public async Task ReportsFailureWhenTheLaunchIsRefused()
    {
        var launcher = new FakeLauncher(_ => false);

        Assert.IsFalse(await HelpLauncher.OpenAsync(launcher, Ref, Url, "a page"));
        Assert.HasCount(1, launcher.Launched);
    }

    [TestMethod]
    public async Task SwallowsAnExceptionFromTheLaunch()
    {
        var launcher = new FakeLauncher(_ => throw new InvalidOperationException("no handler"));

        Assert.IsFalse(await HelpLauncher.OpenAsync(launcher, Ref, Url, "a page"));
    }

    [TestMethod]
    public async Task SwallowsAnExceptionFromTheRefLookup()
    {
        var launcher = new FakeLauncher(_ => true);

        bool opened = await HelpLauncher.OpenAsync(launcher, () => throw new InvalidOperationException("offline"), Url, "a page");

        Assert.IsFalse(opened);
        Assert.IsEmpty(launcher.Launched);
    }
}
