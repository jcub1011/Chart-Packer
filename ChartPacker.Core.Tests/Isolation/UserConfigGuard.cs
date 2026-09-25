namespace ChartPacker.Core.Tests.Isolation;

/// <summary>
/// Tests must NEVER read or write the real user preferences/settings files; they use temp files
/// only. This guard enforces that for the whole run: it snapshots the real config folder before
/// any test runs and fails the run if anything in it was created, modified or deleted.
/// </summary>
/// <remarks>
/// Every test project must carry an equivalent guard. If it fires, find the test that resolved
/// <see cref="UserConfigPaths"/> (directly or through a service's default path) and give it a temp
/// path instead. It can also fire if the app itself runs and saves settings during a test run.
/// </remarks>
[TestClass]
public sealed class UserConfigGuard
{
    private static FolderSnapshot? s_before;

    [AssemblyInitialize]
    public static void SnapshotRealConfig(TestContext _) =>
        s_before = FolderSnapshot.Take(UserConfigPaths.ConfigFolder);

    [AssemblyCleanup]
    public static void VerifyRealConfigUntouched()
    {
        var changes = s_before!.DiffTo(FolderSnapshot.Take(UserConfigPaths.ConfigFolder));
        if (changes.Count > 0)
        {
            Assert.Fail(
                $"The test run changed the real user config folder '{UserConfigPaths.ConfigFolder}': " +
                $"{string.Join("; ", changes)}. Tests must use temp files only.");
        }
    }
}
