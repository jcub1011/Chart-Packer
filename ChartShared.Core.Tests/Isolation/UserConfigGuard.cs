using System.Diagnostics;

namespace ChartShared.Core.Tests.Isolation;

/// <summary>
/// Tests must NEVER read or write the real user preferences/settings files; they use temp files
/// only. This guard enforces that for the whole run: it snapshots the real config folder before
/// any test runs and fails the run if anything in it was created, modified or deleted.
/// </summary>
/// <remarks>
/// <para>Every test project must carry an equivalent guard. If it fires, find the test that resolved
/// <see cref="UserConfigPaths"/> (directly or through a service's default path) and give it a temp
/// path instead. MSTest reports a failed assembly cleanup against the last test that ran, so the test
/// it names isn't the one at fault.</para>
/// <para>Chart Builder and Chart Packer save their own files here as they run: Chart Builder its
/// settings as a window closes or an option changes. When one of them was running as the run started
/// or ended, and only the files the apps keep changed, the change is put down to the app: the guard
/// warns rather than fails, so working in the app while the tests run doesn't fail them. Anything else
/// changed still fails it.</para>
/// </remarks>
[TestClass]
public sealed class UserConfigGuard
{
    /// <summary>The apps' process names, which keep their own files in the config folder.</summary>
    internal static readonly string[] AppProcesses = ["ChartBuilder", "ChartPacker"];

    private static FolderSnapshot? s_before;
    private static bool s_appAtStart;

    [AssemblyInitialize]
    public static void SnapshotRealConfig(TestContext _)
    {
        s_before = FolderSnapshot.Take(UserConfigPaths.ConfigFolder);
        s_appAtStart = AppRunning();
    }

    [AssemblyCleanup]
    public static void VerifyRealConfigUntouched()
    {
        var after = FolderSnapshot.Take(UserConfigPaths.ConfigFolder);
        var changes = s_before!.DiffTo(after);
        if (changes.Count == 0)
            return;

        string described = string.Join("; ", changes);
        if (IsTheAppsOwn(s_before.ChangedPaths(after), s_appAtStart || AppRunning()))
        {
            Console.Error.WriteLine(
                $"warning: the real user config folder '{UserConfigPaths.ConfigFolder}' changed during the run ({described}), " +
                "while Chart Builder or Chart Packer was running; put down to the app saving its own files.");
            return;
        }

        Assert.Fail(
            $"The test run changed the real user config folder '{UserConfigPaths.ConfigFolder}': " +
            $"{described}. Tests must use temp files only.");
    }

    /// <summary>
    /// Whether changes to <paramref name="changed"/> (relative to the config folder; null for the folder itself) are the
    /// apps' own: an app was running (<paramref name="appRan"/>), and each is a file or folder the apps keep there.
    /// </summary>
    internal static bool IsTheAppsOwn(IReadOnlyList<string?> changed, bool appRan) =>
        appRan && changed.Count > 0 && changed.All(path => path is not null && IsAppFile(path));

    // The files the apps keep in the config folder (UserConfigPaths), and anything in the recovery folder.
    private static bool IsAppFile(string path)
    {
        string folder = UserConfigPaths.ConfigFolder;
        string[] files =
        [
            UserConfigPaths.SettingsFile, UserConfigPaths.BuilderSettingsFile, UserConfigPaths.ReductionRulesFile,
            UserConfigPaths.AudioLatencyFile, UserConfigPaths.InputBindingsFile, UserConfigPaths.UpdateSettingsFile,
            UserConfigPaths.GeneratorModelSettingsFile, UserConfigPaths.GenerationDefaultsFile,
        ];
        string recovery = Path.GetRelativePath(folder, UserConfigPaths.RecoveryFolder) + Path.DirectorySeparatorChar;
        string models = Path.GetRelativePath(folder, UserConfigPaths.GeneratorModelsFolder) + Path.DirectorySeparatorChar;
        return files.Any(f => string.Equals(Path.GetRelativePath(folder, f), path, StringComparison.OrdinalIgnoreCase))
            || path.StartsWith(recovery, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(models, StringComparison.OrdinalIgnoreCase);
    }

    private static bool AppRunning()
    {
        foreach (string name in AppProcesses)
        {
            var processes = Process.GetProcessesByName(name);
            bool running = processes.Length > 0;
            foreach (var process in processes)
                process.Dispose();
            if (running)
                return true;
        }

        return false;
    }
}
