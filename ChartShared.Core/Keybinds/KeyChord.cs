namespace ChartShared.Core.Keybinds;

/// <summary>The modifiers a <see cref="KeyChord"/> can carry. Meta (the Windows or Command key) is deliberately not one.</summary>
[Flags]
public enum KeyMods
{
    None = 0,
    Ctrl = 1,
    Shift = 2,
    Alt = 4,
}

/// <summary>
/// A key with its modifiers, as <c>keybinds.json</c> stores it: <c>Ctrl+Shift+Z</c>, <c>Alt+1</c>, <c>F5</c>.
/// </summary>
/// <remarks>
/// <para>
/// The key is a token, not a code: the name of an Avalonia <c>Key</c> for a <see cref="KeyMatch.Logical"/> scope
/// (<c>Z</c>, <c>OemComma</c>), or of a <c>PhysicalKey</c> for a <see cref="KeyMatch.Physical"/> one (<c>KeyH</c>,
/// <c>BracketLeft</c>). This type is in Core, which has no Avalonia, so it only checks a token's shape; the UI layer
/// turns tokens into keys and back.
/// </para>
/// <para>
/// Two chords are equal when their modifiers match and their tokens match ignoring case. A logical and a physical token
/// for the same key are not equal, since a scope only ever compares chords of its own kind; <see cref="ConflictKey"/>
/// is what puts them on common ground.
/// </para>
/// </remarks>
public readonly struct KeyChord : IEquatable<KeyChord>
{
    // Keys that are only a modifier, or that Avalonia reports for a press that isn't a key (an IME or dead key, Alt+letter on
    // some platforms): a chord is never one of these on its own.
    private static readonly HashSet<string> ModifierKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "None", "System", "ImeProcessed", "DeadCharProcessed", "Ctrl", "Control", "Shift", "Alt", "Meta", "Win", "LeftCtrl", "RightCtrl", "LeftShift", "RightShift", "LeftAlt", "RightAlt", "LWin", "RWin",
        "ControlLeft", "ControlRight", "ShiftLeft", "ShiftRight", "AltLeft", "AltRight", "MetaLeft", "MetaRight",
    };

    private readonly string? _key;

    public KeyChord(string key, KeyMods mods)
    {
        _key = key;
        Mods = mods;
    }

    /// <summary>The key's token; empty for a chord that is nothing.</summary>
    public string Key => _key ?? "";

    public KeyMods Mods { get; }

    public bool IsEmpty => Key.Length == 0;

    /// <summary>Whether the key is only a modifier (Shift on its own, say), which can't be a binding.</summary>
    public bool IsModifierOnly => IsEmpty || ModifierKeys.Contains(Key);

    /// <summary>
    /// Whether the operating system takes the chord before an app sees it, or means something no app should take
    /// (Alt+F4, Alt+Tab, Ctrl+Shift+Esc and the like).
    /// </summary>
    public bool IsOsReserved
    {
        get
        {
            string key = KeyNames.Display(Key);
            return Mods switch
            {
                KeyMods.Alt => key is "F4" or "Tab" or "Space" or "Esc",
                KeyMods.Ctrl => key is "Esc",
                KeyMods.Ctrl | KeyMods.Shift => key is "Esc",
                KeyMods.Ctrl | KeyMods.Alt => key is "Delete",
                _ => false,
            };
        }
    }

    /// <summary>
    /// The chord as text with the key by its on-keyboard name (<c>Ctrl+,</c> where the token is <c>OemComma</c>), for labels.
    /// </summary>
    public string ToDisplayString() => Format(KeyNames.Display(Key));

    /// <summary>
    /// A form of the chord that is the same for a logical and a physical token of one key, so two actions whose scopes can be
    /// active together are known to clash even when their scopes match keys differently.
    /// </summary>
    public string ConflictKey => ToDisplayString().ToUpperInvariant();

    /// <summary>The chord as <c>keybinds.json</c> writes it: modifiers in the order Ctrl, Shift, Alt, then the token.</summary>
    public override string ToString() => Format(Key);

    private string Format(string key)
    {
        if (Mods == KeyMods.None)
            return key;

        var text = new System.Text.StringBuilder();
        if (Mods.HasFlag(KeyMods.Ctrl))
            text.Append("Ctrl+");
        if (Mods.HasFlag(KeyMods.Shift))
            text.Append("Shift+");
        if (Mods.HasFlag(KeyMods.Alt))
            text.Append("Alt+");
        return text.Append(key).ToString();
    }

    /// <summary>Reads a chord as <see cref="ToString"/> writes it. The modifiers may be in any order and case.</summary>
    /// <returns>False for anything that isn't a chord: no key, a repeated or unknown modifier, a key that is only a modifier.</returns>
    public static bool TryParse(string? text, out KeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string[] parts = text.Split('+');
        var mods = KeyMods.None;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            var mod = parts[i].Trim().ToLowerInvariant() switch
            {
                "ctrl" or "control" => KeyMods.Ctrl,
                "shift" => KeyMods.Shift,
                "alt" => KeyMods.Alt,
                _ => KeyMods.None,
            };
            if (mod == KeyMods.None || mods.HasFlag(mod))
                return false;
            mods |= mod;
        }

        string key = parts[^1].Trim();
        if (key.Length == 0 || !key.All(char.IsAsciiLetterOrDigit))
            return false;

        var parsed = new KeyChord(key, mods);
        if (parsed.IsModifierOnly)
            return false;

        chord = parsed;
        return true;
    }

    /// <summary>Reads a chord that is known to be well formed, as a catalog's defaults are.</summary>
    /// <exception cref="FormatException">The text isn't a chord.</exception>
    public static KeyChord Parse(string text) =>
        TryParse(text, out var chord) ? chord : throw new FormatException($"'{text}' isn't a key chord.");

    /// <summary>Reads each of <paramref name="chords"/> with <see cref="Parse"/>, for an action's defaults.</summary>
    public static IReadOnlyList<KeyChord> List(params string[] chords) => [.. chords.Select(Parse)];

    public bool Equals(KeyChord other) => Mods == other.Mods && string.Equals(Key, other.Key, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => obj is KeyChord other && Equals(other);

    public override int GetHashCode() => HashCode.Combine((int)Mods, StringComparer.OrdinalIgnoreCase.GetHashCode(Key));

    public static bool operator ==(KeyChord left, KeyChord right) => left.Equals(right);

    public static bool operator !=(KeyChord left, KeyChord right) => !left.Equals(right);
}
