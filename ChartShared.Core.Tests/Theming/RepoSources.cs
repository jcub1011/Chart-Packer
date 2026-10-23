namespace ChartShared.Core.Tests.Theming;

/// <summary>The repository's own source files, for tests that check the apps' XAML and code against each other.</summary>
internal static class RepoSources
{
    /// <summary>The folder holding <c>Chart-Packer.slnx</c>, found from the test's output folder up.</summary>
    public static DirectoryInfo Root { get; } = FindRoot();

    /// <summary>The path of <paramref name="relative"/> (forward slashes) under <see cref="Root"/>.</summary>
    public static string PathOf(string relative) => Path.Combine(Root.FullName, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The files matching <paramref name="pattern"/> under the project folder <paramref name="project"/>, outside bin and obj.</summary>
    public static IEnumerable<string> FilesOf(string project, string pattern) =>
        Directory.EnumerateFiles(PathOf(project), pattern, SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(Path.GetRelativePath(Root.FullName, path)));

    private static bool IsBuildOutput(string relative) =>
        relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj");

    private static DirectoryInfo FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Chart-Packer.slnx")))
                return dir;
        }
        throw new InvalidOperationException($"No Chart-Packer.slnx above {AppContext.BaseDirectory}.");
    }
}
