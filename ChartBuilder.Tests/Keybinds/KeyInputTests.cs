using Avalonia.Input;
using ChartShared.Core.Keybinds;
using ChartShared.UI.Keybinds;

namespace ChartBuilder.Tests.Keybinds;

[TestClass]
public sealed class KeyInputTests
{
    [TestMethod]
    public void ModifiersConvertBothWaysAndMetaIsLeftOut()
    {
        Assert.AreEqual(KeyMods.Ctrl | KeyMods.Alt, KeyInput.ModsOf(KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta));
        Assert.AreEqual(KeyModifiers.Control | KeyModifiers.Shift, KeyInput.ToModifiers(KeyMods.Ctrl | KeyMods.Shift));
        Assert.IsTrue(KeyInput.HasMeta(KeyModifiers.Meta | KeyModifiers.Shift));
        Assert.IsFalse(KeyInput.HasMeta(KeyModifiers.Shift));
    }

    [TestMethod]
    public void EveryTokenARealKeyWritesIsOneItsChordParsesBack()
    {
        foreach (Key key in Enum.GetValues<Key>().Where(k => k != Key.None))
        {
            var chord = new KeyChord(key.ToString(), KeyMods.None);
            if (chord.IsModifierOnly)
                continue;

            Assert.AreEqual(key, KeyInput.ToKey(KeyChord.Parse(chord.ToString())), key.ToString());
        }
    }

    [TestMethod]
    public void ALogicalChordBecomesAnAvaloniaGesture()
    {
        var gesture = KeyInput.ToGesture(KeyChord.Parse("Ctrl+Shift+Z"));

        Assert.IsNotNull(gesture);
        Assert.AreEqual(Key.Z, gesture.Key);
        Assert.AreEqual(KeyModifiers.Control | KeyModifiers.Shift, gesture.KeyModifiers);
    }

    [TestMethod]
    public void APhysicalTokenIsNotAGesture()
    {
        Assert.IsNull(KeyInput.ToGesture(KeyChord.Parse("Ctrl+BracketLeft")));
        Assert.AreEqual(PhysicalKey.BracketLeft, KeyInput.ToPhysicalKey(KeyChord.Parse("BracketLeft")));
    }

    [TestMethod]
    [DataRow("F1", true)]
    [DataRow("F5", true)]
    [DataRow("F24", true)]
    [DataRow("F25", false)]
    [DataRow("F", false)]
    [DataRow("Space", false)]
    [DataRow("Fx", false)]
    public void FunctionKeysAreTold(string token, bool expected)
    {
        Assert.AreEqual(expected, KeyInput.IsFunctionKey(token));
    }

    [TestMethod]
    public void TheSharedActionsHaveNoDefaultThatClashes()
    {
        var map = new KeyMap(new KeybindCatalog(SharedKeys.Actions()));

        Assert.AreEqual(0, map.AllConflicts().Count);
    }

    [TestMethod]
    public void TheSharedActionsKeepTodaysKeys()
    {
        var map = new KeyMap(new KeybindCatalog(SharedKeys.Actions()));

        Assert.AreEqual("Ctrl+S", map[SharedKeys.Save]);
        Assert.AreEqual("Ctrl+,", map[SharedKeys.Settings]);
        Assert.AreEqual("Ctrl+Z", map[SharedKeys.Undo]);
        Assert.AreEqual("Ctrl+Y", map[SharedKeys.Redo]);
        Assert.AreEqual("Space", map[SharedKeys.PlayPause]);
        CollectionAssert.AreEqual(KeyChord.List("Ctrl+Y", "Ctrl+Shift+Z").ToArray(), map.ChordsOf(SharedKeys.Redo).ToArray());
    }

    [TestMethod]
    public void UndoAndRedoAreFixedAndYieldToATextBox()
    {
        var catalog = new KeybindCatalog(SharedKeys.Actions());

        foreach (string id in new[] { SharedKeys.Undo, SharedKeys.Redo })
        {
            Assert.IsTrue(catalog[id].Locked, id);
            Assert.IsTrue(catalog[id].YieldsToTextInput, id);
        }
    }
}
