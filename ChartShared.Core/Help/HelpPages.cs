namespace ChartShared.Core.Help;

/// <summary>
/// The help pages, which each window's help button opens. A page's name is its file's name in <c>docs/help</c>
/// (<c>Merge-Compare</c> is <c>docs/help/Merge-Compare.md</c>).
/// </summary>
/// <remarks>
/// The pages are in the repository, and a release opens them at its own tag (<see cref="TagFor"/>), so each version shows the
/// help it shipped with, whatever has changed or been removed since. Until the tag exists, which is while the release is being
/// tested, they're opened at its release branch (<see cref="BranchFor"/>) instead. The main branch is the last resort, for a build that has neither (a dev build, or a hotfix with no release branch), as its pages may be newer
/// than the build but are better than a missing page.
/// </remarks>
public static class HelpPages
{
    /// <summary>The front page.</summary>
    public const string Home = "Home";

    public const string GettingStarted = "Getting-Started";

    // Chart Packer.
    public const string ChartPacker = "Chart-Packer";
    public const string Dedup = "Dedup";
    public const string JobProgress = "Job-Progress";
    public const string LowerDifficulties = "Lower-Difficulties";
    public const string Merge = "Merge";
    public const string MergeCompare = "Merge-Compare";
    public const string MessageDialog = "Message-Dialog";
    public const string Validator = "Validator";
    public const string ValidatorProgress = "Validator-Progress";

    // Chart Builder.
    public const string ChartBuilder = "Chart-Builder";
    public const string Difficulty = "Difficulty";
    public const string ExportProgress = "Export-Progress";
    public const string ExportSng = "Export-SNG";
    public const string FirstSave = "First-Save";
    public const string GeneratePhrases = "Generate-Phrases";
    public const string InputDevices = "Input-Devices";
    public const string LaneMapping = "Lane-Mapping";
    public const string Messages = "Messages";
    public const string Preview = "Preview";
    public const string Reduce = "Reduce";

    // Both apps'.
    public const string Calibration = "Calibration";
    public const string ChartIssues = "Chart-Issues";
    public const string ConfirmDialog = "Confirm-Dialog";
    public const string CrashDialog = "Crash-Dialog";
    public const string KeyboardShortcuts = "Keyboard-Shortcuts";
    public const string Settings = "Settings";

    // The branch the pages are opened at when a version has neither a tag nor a release branch. Private, as every public constant is a page.
    private const string DefaultBranch = "main";

    private const string BlobUrl = "https://github.com/jcub1011/Chart-Packer/blob/";

    private const string Folder = "/docs/help/";

    /// <summary>The git tag of the release <paramref name="version"/>: <c>v1.2.0</c> for <c>1.2.0</c> or <c>1.2.0+abc123</c>.</summary>
    public static string TagFor(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        int build = version.IndexOf('+');
        return "v" + (build < 0 ? version : version[..build]).Trim();
    }

    /// <summary>
    /// The release branch of <paramref name="version"/>: <c>release/1.2.0</c> for <c>1.2.0</c>, <c>1.2.0-beta.1</c> or
    /// <c>1.2.0+abc123</c>, as a pre-release is made on its release's branch.
    /// </summary>
    public static string BranchFor(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        string numbers = version.Trim().Split('+', '-')[0];
        return "release/" + numbers;
    }

    /// <summary>
    /// What to open the pages of <paramref name="version"/> at: its tag once it exists, its release branch while it doesn't, and
    /// the main branch when that's missing too. <paramref name="tagExists"/> and <paramref name="branchExists"/> are
    /// null when that couldn't be found out (offline); then the tag is tried, as it's what a release has, and the branch is tried
    /// when the tag is known to be missing, as it's the next best.
    /// </summary>
    public static string RefFor(string version, bool? tagExists, bool? branchExists = null)
    {
        if (tagExists != false)
            return TagFor(version);
        return branchExists == false ? DefaultBranch : BranchFor(version);
    }

    /// <summary>The address of the help page <paramref name="page"/> as it is at <paramref name="gitRef"/> (a tag or branch); <see cref="Home"/> for no page.</summary>
    public static string UrlFor(string? page, string gitRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gitRef);
        string name = string.IsNullOrWhiteSpace(page) ? Home : page.Trim();
        // A ref's slashes stay (release/1.2.0), as GitHub reads the first part of the path after /blob/ as the ref.
        string escaped = string.Join('/', gitRef.Trim().Split('/').Select(Uri.EscapeDataString));
        return BlobUrl + escaped + Folder + Uri.EscapeDataString(name) + ".md";
    }
}
