using Avalonia.Input;
using ChartBuilder.Controls.Editing;
using ChartBuilder.Resources;
using ChartShared.Core.Charting.Editing;

namespace ChartBuilder.Tests.Editing;

/// <summary>The keys that pick the editing tools (charter-spec §9): Alt and 1 to 6, in the toolbar's order.</summary>
[TestClass]
public sealed class ToolShortcutTests
{
    [TestMethod]
    [DataRow(PhysicalKey.Digit1, EditorTool.Select)]
    [DataRow(PhysicalKey.Digit2, EditorTool.Place)]
    [DataRow(PhysicalKey.Digit3, EditorTool.Erase)]
    [DataRow(PhysicalKey.Digit4, EditorTool.Phrase)]
    [DataRow(PhysicalKey.Digit5, EditorTool.Tempo)]
    [DataRow(PhysicalKey.Digit6, EditorTool.Sync)]
    public void AltAndADigitPicksTheToolInToolbarOrder(PhysicalKey key, EditorTool expected) =>
        Assert.AreEqual(expected, EditorKeys.ToolOf(key, KeyModifiers.Alt));

    [TestMethod]
    [DataRow(PhysicalKey.Digit1, KeyModifiers.None)]
    [DataRow(PhysicalKey.Digit1, KeyModifiers.Shift)]
    [DataRow(PhysicalKey.Digit1, KeyModifiers.Control)]
    [DataRow(PhysicalKey.Digit1, KeyModifiers.Alt | KeyModifiers.Control)]
    [DataRow(PhysicalKey.Digit1, KeyModifiers.Alt | KeyModifiers.Shift)]
    [DataRow(PhysicalKey.Digit1, KeyModifiers.Alt | KeyModifiers.Meta)]
    [DataRow(PhysicalKey.Digit0, KeyModifiers.Alt)]
    [DataRow(PhysicalKey.Digit7, KeyModifiers.Alt)]
    [DataRow(PhysicalKey.L, KeyModifiers.Alt)]
    [DataRow(PhysicalKey.ArrowUp, KeyModifiers.Alt)]
    public void OtherKeysPickNoTool(PhysicalKey key, KeyModifiers modifiers) =>
        Assert.IsNull(EditorKeys.ToolOf(key, modifiers));

    // Alt and the number pad's digits are Windows' Alt codes, which type a character: not ours.
    [TestMethod]
    [DataRow(PhysicalKey.NumPad0)]
    [DataRow(PhysicalKey.NumPad1)]
    [DataRow(PhysicalKey.NumPad4)]
    [DataRow(PhysicalKey.NumPad6)]
    public void TheNumberPadPicksNoTool(PhysicalKey key) =>
        Assert.IsNull(EditorKeys.ToolOf(key, KeyModifiers.Alt));

    [TestMethod]
    public void EveryToolInTheTableIsPickedOnceAndItsShortcutNamesItsKey()
    {
        CollectionAssert.AllItemsAreUnique(EditorKeys.ToolKeys.ToList());

        for (int i = 0; i < EditorKeys.ToolKeys.Count; i++)
        {
            var tool = EditorKeys.ToolKeys[i];
            var key = PhysicalKey.Digit1 + i;

            Assert.AreEqual(tool, EditorKeys.ToolOf(key, KeyModifiers.Alt), $"The key {key} doesn't pick {tool}.");
            Assert.AreEqual($"Alt+{i + 1}", EditorKeys.ShortcutOf(tool));
        }
    }

    [TestMethod]
    public void ATableToolThatNoKeyPicksHasNoShortcut()
    {
        Assert.IsNull(EditorKeys.ShortcutOf(EditorTool.Loop));
        Assert.IsFalse(EditorKeys.ToolKeys.Contains(EditorTool.Loop));
    }

    [TestMethod]
    public void TheHeadingsNameEachToolAndItsKey()
    {
        var headings = new Dictionary<EditorTool, string>
        {
            [EditorTool.Select] = ToolHeadings.Select,
            [EditorTool.Place] = ToolHeadings.Place,
            [EditorTool.Erase] = ToolHeadings.Erase,
            [EditorTool.Phrase] = ToolHeadings.Phrase,
            [EditorTool.Tempo] = ToolHeadings.Tempo,
            [EditorTool.Sync] = ToolHeadings.Sync,
        };
        var names = new Dictionary<EditorTool, string>
        {
            [EditorTool.Select] = Strings.ToolSelect,
            [EditorTool.Place] = Strings.ToolPlace,
            [EditorTool.Erase] = Strings.ToolErase,
            [EditorTool.Phrase] = Strings.ToolPhrase,
            [EditorTool.Tempo] = Strings.ToolTempo,
            [EditorTool.Sync] = Strings.ToolSync,
        };

        CollectionAssert.AreEquivalent(EditorKeys.ToolKeys.ToList(), headings.Keys.ToList(), "A tool of the table has no heading, or the other way round.");
        foreach (var (tool, heading) in headings)
        {
            StringAssert.Contains(heading, names[tool]);
            StringAssert.Contains(heading, EditorKeys.ShortcutOf(tool)!);
        }
    }

    [TestMethod]
    public void TheAcceleratorsAreTheShortcuts()
    {
        Assert.AreEqual(EditorKeys.ShortcutOf(EditorTool.Select), ToolHeadings.SelectKey);
        Assert.AreEqual(EditorKeys.ShortcutOf(EditorTool.Place), ToolHeadings.PlaceKey);
        Assert.AreEqual(EditorKeys.ShortcutOf(EditorTool.Erase), ToolHeadings.EraseKey);
        Assert.AreEqual(EditorKeys.ShortcutOf(EditorTool.Phrase), ToolHeadings.PhraseKey);
        Assert.AreEqual(EditorKeys.ShortcutOf(EditorTool.Tempo), ToolHeadings.TempoKey);
        Assert.AreEqual(EditorKeys.ShortcutOf(EditorTool.Sync), ToolHeadings.SyncKey);
    }
}
