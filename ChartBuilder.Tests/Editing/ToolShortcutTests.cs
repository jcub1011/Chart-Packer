using Avalonia.Input;
using ChartBuilder.Controls.Editing;
using ChartBuilder.Services;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Keybinds;
using ChartShared.UI.Keybinds;

namespace ChartBuilder.Tests.Editing;

/// <summary>The keys that pick the editing tools (charter-spec §9): Alt and 1 to 6 by default, in the toolbar's order.</summary>
[TestClass]
public sealed class ToolShortcutTests
{
    private static readonly KeyMap Map = new(BuilderKeys.Catalog());

    // The press as the key dispatcher sees it: where the key is, and what it types.
    private static IReadOnlyList<KeyAction> Pressed(PhysicalKey physical, Key logical, KeyModifiers modifiers) =>
        Map.Resolve(BuilderKeys.Tools, new KeyChord(logical.ToString(), KeyInput.ModsOf(modifiers)), new KeyChord(physical.ToString(), KeyInput.ModsOf(modifiers)));

    private static EditorTool? ToolPicked(PhysicalKey physical, Key logical, KeyModifiers modifiers) =>
        Pressed(physical, logical, modifiers).Select(a => EditorKeys.ToolOf(a.Id)).FirstOrDefault(t => t is not null);

    [TestMethod]
    [DataRow(PhysicalKey.Digit1, Key.D1, EditorTool.Select)]
    [DataRow(PhysicalKey.Digit2, Key.D2, EditorTool.Place)]
    [DataRow(PhysicalKey.Digit3, Key.D3, EditorTool.Erase)]
    [DataRow(PhysicalKey.Digit4, Key.D4, EditorTool.Phrase)]
    [DataRow(PhysicalKey.Digit5, Key.D5, EditorTool.Tempo)]
    [DataRow(PhysicalKey.Digit6, Key.D6, EditorTool.Sync)]
    public void AltAndADigitPicksTheToolInToolbarOrder(PhysicalKey key, Key typed, EditorTool expected) =>
        Assert.AreEqual(expected, ToolPicked(key, typed, KeyModifiers.Alt));

    [TestMethod]
    [DataRow(PhysicalKey.Digit1, Key.D1, KeyModifiers.None)]
    [DataRow(PhysicalKey.Digit1, Key.D1, KeyModifiers.Shift)]
    [DataRow(PhysicalKey.Digit1, Key.D1, KeyModifiers.Control)]
    [DataRow(PhysicalKey.Digit1, Key.D1, KeyModifiers.Alt | KeyModifiers.Control)]
    [DataRow(PhysicalKey.Digit1, Key.D1, KeyModifiers.Alt | KeyModifiers.Shift)]
    [DataRow(PhysicalKey.Digit0, Key.D0, KeyModifiers.Alt)]
    [DataRow(PhysicalKey.Digit7, Key.D7, KeyModifiers.Alt)]
    [DataRow(PhysicalKey.L, Key.L, KeyModifiers.Alt)]
    [DataRow(PhysicalKey.ArrowUp, Key.Up, KeyModifiers.Alt)]
    public void OtherKeysPickNoTool(PhysicalKey key, Key typed, KeyModifiers modifiers) =>
        Assert.IsNull(ToolPicked(key, typed, modifiers));

    // Alt and the number pad's digits are Windows' Alt codes, which type a character: not ours.
    [TestMethod]
    [DataRow(PhysicalKey.NumPad0, Key.NumPad0)]
    [DataRow(PhysicalKey.NumPad1, Key.NumPad1)]
    [DataRow(PhysicalKey.NumPad4, Key.NumPad4)]
    [DataRow(PhysicalKey.NumPad6, Key.NumPad6)]
    public void TheNumberPadPicksNoTool(PhysicalKey key, Key typed) =>
        Assert.IsNull(ToolPicked(key, typed, KeyModifiers.Alt));

    [TestMethod]
    public void EveryToolInTheTableIsPickedOnceAndItsActionNamesItsKey()
    {
        CollectionAssert.AllItemsAreUnique(EditorKeys.ToolKeys.ToList());
        Assert.AreEqual(EditorKeys.ToolKeys.Count, BuilderKeys.ToolActions.Count);

        for (int i = 0; i < EditorKeys.ToolKeys.Count; i++)
        {
            var tool = EditorKeys.ToolKeys[i];
            string action = BuilderKeys.ToolActions[i];

            Assert.AreEqual(tool, EditorKeys.ToolOf(action));
            Assert.AreEqual($"Alt+{i + 1}", Map.Display(action));
            Assert.AreEqual(tool, ToolPicked(PhysicalKey.Digit1 + i, Key.D1 + i, KeyModifiers.Alt), $"Alt and the digit {i + 1} doesn't pick {tool}.");
        }
    }

    [TestMethod]
    public void ATableToolThatNoKeyPicksHasNoAction()
    {
        Assert.IsFalse(EditorKeys.ToolKeys.Contains(EditorTool.Loop));
        Assert.IsNull(EditorKeys.ToolOf(BuilderKeys.LoopTool));
    }

    // A focused text box keeps Alt and a digit, as it did before the keys were the key map's.
    [TestMethod]
    public void TheToolKeysYieldToATextBox()
    {
        var catalog = BuilderKeys.Catalog();

        foreach (string id in BuilderKeys.ToolActions)
            Assert.IsTrue(catalog[id].YieldsToTextInput, id);
    }

    [TestMethod]
    public void ARebindMovesAToolsKeyAndItsLabel()
    {
        var map = new KeyMap(BuilderKeys.Catalog());

        Assert.IsTrue(map.Set(BuilderKeys.ToolPlace, KeyChord.List("Alt+P")));

        Assert.AreEqual("Alt+P", map.Display(BuilderKeys.ToolPlace));
        Assert.AreEqual(BuilderKeys.ToolPlace, map.Resolve(BuilderKeys.Tools, new KeyChord("P", KeyMods.Alt), new KeyChord("P", KeyMods.Alt)).Single().Id);
        Assert.AreEqual(0, map.Resolve(BuilderKeys.Tools, new KeyChord("D2", KeyMods.Alt), new KeyChord("Digit2", KeyMods.Alt)).Count);
    }
}
