namespace ChartShared.Core.Tests;

[TestClass]
public sealed class UserConfigPathsTests
{
    // A running app writes its log while the tests' UserConfigGuard watches the config folder, so the two must not
    // overlap.
    [TestMethod]
    public void TheLogFolderIsOutsideTheConfigFolder()
    {
        string config = Path.GetFullPath(UserConfigPaths.ConfigFolder) + Path.DirectorySeparatorChar;
        string logs = Path.GetFullPath(UserConfigPaths.LogFolder) + Path.DirectorySeparatorChar;

        Assert.IsFalse(logs.StartsWith(config, StringComparison.OrdinalIgnoreCase));
    }
}
