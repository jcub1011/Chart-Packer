using ChartShared.Core.Keybinds;

namespace ChartShared.Core.Tests.Keybinds;

[TestClass]
public sealed class KeyChordTests
{
    [TestMethod]
    [DataRow("Ctrl+Shift+Z", "Z", KeyMods.Ctrl | KeyMods.Shift)]
    [DataRow("Alt+1", "1", KeyMods.Alt)]
    [DataRow("F5", "F5", KeyMods.None)]
    [DataRow("Ctrl+Shift+Alt+OemComma", "OemComma", KeyMods.Ctrl | KeyMods.Shift | KeyMods.Alt)]
    public void AChordParsesAndWritesBack(string text, string key, KeyMods mods)
    {
        Assert.IsTrue(KeyChord.TryParse(text, out var chord));

        Assert.AreEqual(key, chord.Key);
        Assert.AreEqual(mods, chord.Mods);
        Assert.AreEqual(text, chord.ToString());
    }

    [TestMethod]
    public void ModifiersMayComeInAnyOrderAndCase()
    {
        Assert.IsTrue(KeyChord.TryParse("alt+CTRL+shift+Q", out var chord));

        Assert.AreEqual("Ctrl+Shift+Alt+Q", chord.ToString());
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("  ")]
    [DataRow("Ctrl+")]
    [DataRow("Ctrl++")]
    [DataRow("Ctrl+Ctrl+Z")]
    [DataRow("Meta+Z")]
    [DataRow("Ctrl+Shift")]
    [DataRow("LeftCtrl")]
    [DataRow("Ctrl+Z+X")]
    [DataRow("Ctrl+Sp ace")]
    [DataRow("Alt+System")]
    [DataRow("ImeProcessed")]
    [DataRow("Ctrl+DeadCharProcessed")]
    public void ThingsThatAreNotChordsAreRefused(string text)
    {
        Assert.IsFalse(KeyChord.TryParse(text, out _));
    }

    [TestMethod]
    public void ChordsAreEqualIgnoringCaseOfTheToken()
    {
        Assert.AreEqual(KeyChord.Parse("Ctrl+z"), KeyChord.Parse("Ctrl+Z"));
        Assert.AreEqual(KeyChord.Parse("Ctrl+z").GetHashCode(), KeyChord.Parse("Ctrl+Z").GetHashCode());
        Assert.AreNotEqual(KeyChord.Parse("Ctrl+Z"), KeyChord.Parse("Ctrl+Shift+Z"));
    }

    [TestMethod]
    public void ALogicalAndAPhysicalTokenOfOneKeyShareAConflictKey()
    {
        Assert.AreEqual(KeyChord.Parse("Ctrl+OemOpenBrackets").ConflictKey, KeyChord.Parse("Ctrl+BracketLeft").ConflictKey);
        Assert.AreEqual(KeyChord.Parse("Ctrl+Z").ConflictKey, KeyChord.Parse("Ctrl+KeyZ").ConflictKey);
        Assert.AreEqual(KeyChord.Parse("Alt+D1").ConflictKey, KeyChord.Parse("Alt+Digit1").ConflictKey);
        Assert.AreNotEqual(KeyChord.Parse("Ctrl+Z").ConflictKey, KeyChord.Parse("Ctrl+Shift+Z").ConflictKey);
    }

    [TestMethod]
    public void TheDisplayTextUsesTheKeysOwnName()
    {
        Assert.AreEqual("Ctrl+,", KeyChord.Parse("Ctrl+OemComma").ToDisplayString());
        Assert.AreEqual("Alt+1", KeyChord.Parse("Alt+Digit1").ToDisplayString());
        Assert.AreEqual("H", KeyChord.Parse("KeyH").ToDisplayString());
        Assert.AreEqual("Esc", KeyChord.Parse("Escape").ToDisplayString());
        Assert.AreEqual("Shift+PageDown", KeyChord.Parse("Shift+Next").ToDisplayString());
    }

    [TestMethod]
    [DataRow("Alt+F4")]
    [DataRow("Alt+Tab")]
    [DataRow("Ctrl+Shift+Escape")]
    public void ChordsTheOperatingSystemTakesAreReserved(string text)
    {
        Assert.IsTrue(KeyChord.Parse(text).IsOsReserved);
    }

    [TestMethod]
    public void OrdinaryChordsAreNotReserved()
    {
        Assert.IsFalse(KeyChord.Parse("Ctrl+S").IsOsReserved);
        Assert.IsFalse(KeyChord.Parse("Escape").IsOsReserved);
        Assert.IsFalse(KeyChord.Parse("Alt+1").IsOsReserved);
    }

    [TestMethod]
    public void TheDefaultChordIsEmpty()
    {
        KeyChord chord = default;

        Assert.IsTrue(chord.IsEmpty);
        Assert.IsTrue(chord.IsModifierOnly);
        Assert.AreEqual("", chord.ToString());
    }
}
