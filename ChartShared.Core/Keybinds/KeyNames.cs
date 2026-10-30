namespace ChartShared.Core.Keybinds;

/// <summary>
/// The on-keyboard names of key tokens, for labels and for telling that a logical token and a physical one are the same
/// key (<c>OemOpenBrackets</c> and <c>BracketLeft</c> are both <c>[</c>).
/// </summary>
public static class KeyNames
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Add"] = "Num +",
        ["NumPadAdd"] = "Num +",
        ["Subtract"] = "Num -",
        ["NumPadSubtract"] = "Num -",
        ["Multiply"] = "Num *",
        ["NumPadMultiply"] = "Num *",
        ["Divide"] = "Num /",
        ["NumPadDivide"] = "Num /",
        ["Decimal"] = "Num .",
        ["NumPadDecimal"] = "Num .",
        ["NumPadEnter"] = "Num Enter",
        ["OemComma"] = ",",
        ["Comma"] = ",",
        ["OemPeriod"] = ".",
        ["Period"] = ".",
        ["OemMinus"] = "-",
        ["Minus"] = "-",
        ["OemPlus"] = "=",
        ["Equal"] = "=",
        ["OemOpenBrackets"] = "[",
        ["BracketLeft"] = "[",
        ["OemCloseBrackets"] = "]",
        ["BracketRight"] = "]",
        ["OemSemicolon"] = ";",
        ["Semicolon"] = ";",
        ["OemQuotes"] = "'",
        ["Quote"] = "'",
        ["OemQuestion"] = "/",
        ["Slash"] = "/",
        ["OemPipe"] = "\\",
        ["Backslash"] = "\\",
        ["OemTilde"] = "`",
        ["Backquote"] = "`",
        ["Return"] = "Enter",
        ["Enter"] = "Enter",
        ["Escape"] = "Esc",
        ["Back"] = "Backspace",
        ["Backspace"] = "Backspace",
        ["Prior"] = "PageUp",
        ["PageUp"] = "PageUp",
        ["Next"] = "PageDown",
        ["PageDown"] = "PageDown",
        ["Capital"] = "CapsLock",
        ["CapsLock"] = "CapsLock",
        ["Del"] = "Delete",
        ["ArrowUp"] = "Up",
        ["ArrowDown"] = "Down",
        ["ArrowLeft"] = "Left",
        ["ArrowRight"] = "Right",
    };

    // Names an Avalonia Key goes by more than once (Key.OemOpenBrackets is Key.Oem4), where Enum.ToString picks one of them, and
    // not always the one a binding is written with: a pressed Key.Enter is "Return", and a Key.OemTilde "Oem3".
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Return"] = "Enter",
        ["Oem1"] = "OemSemicolon",
        ["Oem2"] = "OemQuestion",
        ["Oem3"] = "OemTilde",
        ["Oem4"] = "OemOpenBrackets",
        ["Oem5"] = "OemPipe",
        ["Oem6"] = "OemCloseBrackets",
        ["Oem7"] = "OemQuotes",
        ["Oem102"] = "OemBackslash",
    };

    /// <summary>
    /// The one name a key's token goes by, so a key pressed (<c>Oem4</c>) and the same key as a binding is written
    /// (<c>OemOpenBrackets</c>) are the same token. A token with no other name is returned as it is.
    /// </summary>
    public static string Canonical(string token) => Aliases.TryGetValue(token, out var name) ? name : token;

    /// <summary>
    /// The name a key goes by on a keyboard, where the token isn't it: <c>KeyH</c> is <c>H</c>, <c>D1</c> and
    /// <c>Digit1</c> are <c>1</c>, <c>OemComma</c> is <c>,</c>. A token with no better name is returned as it is.
    /// </summary>
    public static string Display(string token)
    {
        if (token.Length == 4 && token.StartsWith("Key", StringComparison.OrdinalIgnoreCase) && char.IsAsciiLetter(token[3]))
            return char.ToUpperInvariant(token[3]).ToString();
        if (token.Length == 6 && token.StartsWith("Digit", StringComparison.OrdinalIgnoreCase) && char.IsAsciiDigit(token[5]))
            return token[5].ToString();
        if (token.Length == 2 && (token[0] is 'D' or 'd') && char.IsAsciiDigit(token[1]))
            return token[1].ToString();
        if (token.Length == 7 && token.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase) && char.IsAsciiDigit(token[6]))
            return "Num " + token[6];
        if (token.Length == 1 && char.IsAsciiLetter(token[0]))
            return char.ToUpperInvariant(token[0]).ToString();
        return Names.TryGetValue(token, out var name) ? name : token;
    }
}
