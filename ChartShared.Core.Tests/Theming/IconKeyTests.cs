using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ChartShared.Core.Tests.Theming;

/// <summary>
/// Every line icon the apps name (a <c>…Stroke</c> key) is in <c>Icons.axaml</c>. A key that isn't only leaves its icon
/// blank: a menu's from code finds nothing, and a <c>DynamicResource</c> isn't checked when the XAML is compiled.
/// </summary>
[TestClass]
public sealed partial class IconKeyTests
{
    private static readonly string[] Projects = ["ChartBuilder", "ChartPacker", "ChartShared.UI"];

    // The theme's own brush keys, Fluent's, some ending in "Stroke" too.
    private static readonly string ThemingFolder = RepoSources.PathOf("ChartShared.UI/Theming") + Path.DirectorySeparatorChar;

    private static readonly HashSet<string> IconKeys = ReadIconKeys();

    [TestMethod]
    public void EveryIconKeyInCodeIsAnIcon()
    {
        var unknown = Projects
            .SelectMany(project => RepoSources.FilesOf(project, "*.cs"))
            .Where(path => !path.StartsWith(ThemingFolder, StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => Unknown(path, CodeKey()))
            .ToList();

        Assert.IsEmpty(unknown, "Icon keys not in ChartShared.UI/Styles/Icons.axaml:\n" + string.Join("\n", unknown));
    }

    [TestMethod]
    public void EveryIconKeyInXamlIsAnIcon()
    {
        var unknown = Projects
            .SelectMany(project => RepoSources.FilesOf(project, "*.axaml"))
            .SelectMany(path => Unknown(path, XamlKey()))
            .ToList();

        Assert.IsEmpty(unknown, "Icon keys not in ChartShared.UI/Styles/Icons.axaml:\n" + string.Join("\n", unknown));
    }

    // Whatever its name: a LineIcon's Data from a resource is an icon. A missing StaticResource doesn't fail the build.
    [TestMethod]
    public void EveryLineIconsDataIsAnIcon()
    {
        var unknown = Projects
            .SelectMany(project => RepoSources.FilesOf(project, "*.axaml"))
            .SelectMany(LineIconKeys)
            .Where(found => !IconKeys.Contains(found.Key))
            .Select(found => $"{found.File}: {found.Key}")
            .Distinct()
            .ToList();

        Assert.IsEmpty(unknown, "LineIcon resources not in ChartShared.UI/Styles/Icons.axaml:\n" + string.Join("\n", unknown));
    }

    private static IEnumerable<string> Unknown(string path, Regex key)
    {
        string relative = Path.GetRelativePath(RepoSources.Root.FullName, path);
        return key.Matches(File.ReadAllText(path))
            .Select(match => match.Groups["key"].Value)
            .Where(found => !IconKeys.Contains(found))
            .Distinct()
            .Select(found => $"{relative}: {found}");
    }

    private static IEnumerable<(string File, string Key)> LineIconKeys(string path)
    {
        string relative = Path.GetRelativePath(RepoSources.Root.FullName, path);
        return XDocument.Load(path).Descendants()
            .Where(e => e.Name.LocalName == "LineIcon")
            .Select(e => ResourceKey().Match((string?)e.Attribute("Data") ?? ""))
            .Where(match => match.Success)
            .Select(match => (relative, match.Groups["key"].Value));
    }

    private static HashSet<string> ReadIconKeys()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var icons = XDocument.Load(RepoSources.PathOf("ChartShared.UI/Styles/Icons.axaml"));
        return [.. icons.Root!.Elements().Select(e => (string?)e.Attribute(x + "Key")).OfType<string>()];
    }

    // A string literal that's a whole key, e.g. "CopyStroke", or one misspelt around its "Stroke".
    [GeneratedRegex("\"(?<key>[A-Za-z]+Strok[A-Za-z0-9]*)\"")]
    private static partial Regex CodeKey();

    // {StaticResource CopyStroke} or {DynamicResource CopyStroke}, as anywhere a key is given, e.g. to a converter.
    [GeneratedRegex(@"\{(?:Static|Dynamic)Resource\s+(?<key>[A-Za-z]+Strok[A-Za-z0-9]*)\s*\}")]
    private static partial Regex XamlKey();

    // A LineIcon's Data="{StaticResource …}" or "{DynamicResource …}".
    [GeneratedRegex(@"^\{(?:Static|Dynamic)Resource\s+(?<key>\w+)\s*\}$")]
    private static partial Regex ResourceKey();
}
