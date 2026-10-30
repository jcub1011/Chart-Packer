namespace ChartShared.Core.Keybinds;

/// <summary>
/// One key of <see cref="KeyboardLayout"/>: the key as <see cref="KeyNames.Display"/> names it, what its cap says, the
/// token a binding to it is written with in each kind of scope, and how wide it is. A gap has no name.
/// </summary>
/// <param name="Name">The key as a binding is labelled (<c>Esc</c>, <c>,</c>, <c>PageUp</c>); empty for a gap.</param>
/// <param name="Caption">What the cap says, which is <paramref name="Name"/> unless that's too long for it.</param>
/// <param name="Logical">The token in a <see cref="KeyMatch.Logical"/> scope: the name of an Avalonia <c>Key</c>.</param>
/// <param name="Physical">The token in a <see cref="KeyMatch.Physical"/> scope: the name of an Avalonia <c>PhysicalKey</c>.</param>
/// <param name="Width">In key widths, where a letter's is 1.</param>
public sealed record KeyboardKey(string Name, string Caption, string Logical, string Physical, double Width)
{
    public bool IsGap => Name.Length == 0;

    /// <summary>The token a binding to the key is written with in a scope that matches as <paramref name="match"/>.</summary>
    public string TokenFor(KeyMatch match) => match == KeyMatch.Physical ? Physical : Logical;
}

/// <summary>
/// The keys the keyboard shortcuts window draws in its keyboard view, as rows of an ANSI keyboard with its function row.
/// The modifier keys and the lock keys aren't among them: a chord's modifiers are the layer the view shows, and a lock key
/// can't be a binding. A chord whose key isn't here (a key of the number pad, say) is shown beside them.
/// </summary>
public static class KeyboardLayout
{
    private static KeyboardKey Key(string name, string logical, string physical, double width = 1, string? caption = null) =>
        new(name, caption ?? name, logical, physical, width);

    private static KeyboardKey Letter(char letter) => Key(letter.ToString(), letter.ToString(), letter.ToString());

    private static KeyboardKey Digit(char digit) => Key(digit.ToString(), "D" + digit, "Digit" + digit);

    private static KeyboardKey Function(int number) => Key("F" + number, "F" + number, "F" + number);

    private static KeyboardKey Gap(double width) => new("", "", "", "", width);

    private static IEnumerable<KeyboardKey> Letters(string letters) => letters.Select(Letter);

    public static IReadOnlyList<IReadOnlyList<KeyboardKey>> Rows { get; } =
    [
        [Key("Esc", "Escape", "Escape"), Gap(1), .. Enumerable.Range(1, 4).Select(Function), Gap(0.5), .. Enumerable.Range(5, 4).Select(Function), Gap(0.5), .. Enumerable.Range(9, 4).Select(Function)],
        [
            Key("`", "OemTilde", "Backquote"),
            .. "1234567890".Select(Digit),
            Key("-", "OemMinus", "Minus"),
            Key("=", "OemPlus", "Equal"),
            Key("Backspace", "Back", "Backspace", 2, "Bksp"),
        ],
        [
            Key("Tab", "Tab", "Tab", 1.5),
            .. Letters("QWERTYUIOP"),
            Key("[", "OemOpenBrackets", "BracketLeft"),
            Key("]", "OemCloseBrackets", "BracketRight"),
            Key("\\", "OemPipe", "Backslash", 1.5),
        ],
        [
            Gap(1.75),
            .. Letters("ASDFGHJKL"),
            Key(";", "OemSemicolon", "Semicolon"),
            Key("'", "OemQuotes", "Quote"),
            Key("Enter", "Enter", "Enter", 2.25),
        ],
        [
            Gap(2.25),
            .. Letters("ZXCVBNM"),
            Key(",", "OemComma", "Comma"),
            Key(".", "OemPeriod", "Period"),
            Key("/", "OemQuestion", "Slash"),
        ],
        [
            Gap(3.5),
            Key("Space", "Space", "Space", 6),
        ],
        [
            Key("Insert", "Insert", "Insert", 1.5, "Ins"),
            Key("Delete", "Delete", "Delete", 1.5, "Del"),
            Key("Home", "Home", "Home", 1.5),
            Key("End", "End", "End", 1.5),
            Key("PageUp", "PageUp", "PageUp", 1.5, "PgUp"),
            Key("PageDown", "PageDown", "PageDown", 1.5, "PgDn"),
            Gap(0.5),
            Key("Left", "Left", "ArrowLeft", 1, "←"),
            Key("Up", "Up", "ArrowUp", 1, "↑"),
            Key("Down", "Down", "ArrowDown", 1, "↓"),
            Key("Right", "Right", "ArrowRight", 1, "→"),
        ],
    ];

    /// <summary>Whether the layout has a cap for the key named <paramref name="name"/> (as <see cref="KeyNames.Display"/> gives it).</summary>
    public static bool Contains(string name) =>
        Rows.Any(row => row.Any(key => !key.IsGap && string.Equals(key.Name, name, StringComparison.OrdinalIgnoreCase)));
}
