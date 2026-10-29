using System;
using Avalonia.Input;
using ChartShared.Core.Keybinds;

namespace ChartShared.UI.Keybinds;

/// <summary>Turning Avalonia's keys into <see cref="KeyChord"/>s and back. Core has no Avalonia, so the tokens are names.</summary>
public static class KeyInput
{
    /// <summary>The modifiers of a key press; Meta isn't one a chord can carry, so it's dropped (see <see cref="HasMeta"/>).</summary>
    public static KeyMods ModsOf(KeyModifiers modifiers)
    {
        var mods = KeyMods.None;
        if (modifiers.HasFlag(KeyModifiers.Control))
            mods |= KeyMods.Ctrl;
        if (modifiers.HasFlag(KeyModifiers.Shift))
            mods |= KeyMods.Shift;
        if (modifiers.HasFlag(KeyModifiers.Alt))
            mods |= KeyMods.Alt;
        return mods;
    }

    /// <summary>Whether the Windows or Command key is down, which no binding uses.</summary>
    public static bool HasMeta(KeyModifiers modifiers) => modifiers.HasFlag(KeyModifiers.Meta);

    public static KeyModifiers ToModifiers(KeyMods mods)
    {
        var modifiers = KeyModifiers.None;
        if (mods.HasFlag(KeyMods.Ctrl))
            modifiers |= KeyModifiers.Control;
        if (mods.HasFlag(KeyMods.Shift))
            modifiers |= KeyModifiers.Shift;
        if (mods.HasFlag(KeyMods.Alt))
            modifiers |= KeyModifiers.Alt;
        return modifiers;
    }

    /// <summary>The press as a token of the character the layout gives the key (<c>Z</c>, <c>OemComma</c>).</summary>
    public static KeyChord Logical(KeyEventArgs e) => new(e.Key.ToString(), ModsOf(e.KeyModifiers));

    /// <summary>The press as a token of where the key is (<c>KeyZ</c>, <c>BracketLeft</c>), whatever the layout.</summary>
    public static KeyChord Physical(KeyEventArgs e) => new(e.PhysicalKey.ToString(), ModsOf(e.KeyModifiers));

    /// <summary>The chord as an Avalonia gesture, for a menu item; null when its token isn't a logical <see cref="Key"/>.</summary>
    public static KeyGesture? ToGesture(KeyChord chord) =>
        !chord.IsEmpty && Enum.TryParse(chord.Key, ignoreCase: true, out Key key) && key != Key.None
            ? new KeyGesture(key, ToModifiers(chord.Mods))
            : null;

    /// <summary>The key a logical chord's token names, or null for a token that isn't one.</summary>
    public static Key? ToKey(KeyChord chord) =>
        !chord.IsEmpty && Enum.TryParse(chord.Key, ignoreCase: true, out Key key) && key != Key.None ? key : null;

    /// <summary>The key a physical chord's token names, or null for a token that isn't one.</summary>
    public static PhysicalKey? ToPhysicalKey(KeyChord chord) =>
        !chord.IsEmpty && Enum.TryParse(chord.Key, ignoreCase: true, out PhysicalKey key) && key != PhysicalKey.None ? key : null;

    /// <summary>Whether the token is a function key, which types nothing and so reaches a window even from a text box.</summary>
    public static bool IsFunctionKey(string token) =>
        token.Length is 2 or 3 && token[0] is 'F' or 'f' && int.TryParse(token.AsSpan(1), out int number) && number is >= 1 and <= 24;
}
