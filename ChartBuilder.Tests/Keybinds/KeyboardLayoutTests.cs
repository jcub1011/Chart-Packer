using Avalonia.Input;
using ChartShared.Core.Keybinds;

namespace ChartBuilder.Tests.Keybinds;

[TestClass]
public sealed class KeyboardLayoutTests
{
    private static IEnumerable<KeyboardKey> Keys => KeyboardLayout.Rows.SelectMany(row => row).Where(key => !key.IsGap);

    [TestMethod]
    public void EveryKeysTokensAreKeysAvaloniaKnowsBy()
    {
        foreach (var key in Keys)
        {
            Assert.IsTrue(Enum.TryParse<Key>(key.Logical, ignoreCase: false, out _), $"{key.Name}: '{key.Logical}' isn't an Avalonia Key");
            Assert.IsTrue(Enum.TryParse<PhysicalKey>(key.Physical, ignoreCase: false, out _), $"{key.Name}: '{key.Physical}' isn't an Avalonia PhysicalKey");
        }
    }

    [TestMethod]
    public void EveryKeysTokensAreLabelledAsItsCapIs()
    {
        foreach (var key in Keys)
        {
            Assert.AreEqual(key.Name, KeyNames.Display(KeyNames.Canonical(key.Logical)), $"{key.Name} (logical)");
            Assert.AreEqual(key.Name, KeyNames.Display(KeyNames.Canonical(key.Physical)), $"{key.Name} (physical)");
        }
    }

    [TestMethod]
    public void ATokenIsTheKeyForTheKindOfScopeThatMatchesIt()
    {
        var semicolon = Keys.Single(key => key.Name == ";");

        Assert.AreEqual("OemSemicolon", semicolon.TokenFor(KeyMatch.Logical));
        Assert.AreEqual("Semicolon", semicolon.TokenFor(KeyMatch.Physical));
    }

    [TestMethod]
    public void KeysAreNamedOnceAndTheMainRowsAreAsWideAsEachOther()
    {
        CollectionAssert.AreEqual(Keys.Select(key => key.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), Keys.Select(key => key.Name).ToArray());

        // The function row, the number row, and the two rows of letters that run to the edge.
        double[] widths = [.. KeyboardLayout.Rows.Take(4).Select(row => row.Sum(key => key.Width))];
        Assert.IsTrue(widths.All(width => width == 15), string.Join(", ", widths));
    }

    [TestMethod]
    public void TheLayoutKnowsWhichKeysItHas()
    {
        Assert.IsTrue(KeyboardLayout.Contains("Esc"));
        Assert.IsTrue(KeyboardLayout.Contains("a".ToUpperInvariant()));
        Assert.IsTrue(KeyboardLayout.Contains(","));
        Assert.IsFalse(KeyboardLayout.Contains("Num +"));
        Assert.IsFalse(KeyboardLayout.Contains(""));
    }
}
