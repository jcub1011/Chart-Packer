using System.Xml.Linq;

namespace ChartShared.Core.Tests.Theming;

/// <summary>
/// <c>Theme.axaml</c> copies Fluent's text box and selectable text menus to put icons on them, so a new Fluent version
/// must be compared with the copies before it's taken.
/// </summary>
[TestClass]
public sealed class FluentCopyTests
{
    [TestMethod]
    public void TheTextMenusAreCopiedFromTheFluentVersionInUse()
    {
        var packages = XDocument.Load(RepoSources.PathOf("Directory.Packages.props"));
        string? version = packages.Descendants("PackageVersion")
            .FirstOrDefault(p => (string?)p.Attribute("Include") == "Avalonia.Themes.Fluent")
            ?.Attribute("Version")?.Value;
        Assert.IsNotNull(version, "Directory.Packages.props has no Avalonia.Themes.Fluent version.");

        string theme = File.ReadAllText(RepoSources.PathOf("ChartShared.UI/Styles/Theme.axaml"));

        Assert.Contains(
            $"Copied from Avalonia.Themes.Fluent {version}.",
            theme,
            $"Avalonia.Themes.Fluent is now {version}. Compare TextBoxContextFlyout and SelectableTextBlockContextFlyout in "
            + "ChartShared.UI/Styles/Theme.axaml with that version's TextBox.xaml and SelectableTextBlock.xaml, bring them up "
            + "to date, then change the version in their comment.");
    }
}
