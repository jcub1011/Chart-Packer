namespace ChartShared.Core.Keybinds;

/// <summary>Whether an action runs when its key goes down, or for as long as the key is held.</summary>
public enum KeyActionKind
{
    Press,

    /// <summary>Starts on key down and ends on key up (the loop tool's L).</summary>
    Hold,
}

/// <summary>
/// Something a key can do, and the keys it has by default. The apps register theirs in a <see cref="KeybindCatalog"/>; a
/// <see cref="KeyMap"/> adds what the user changed.
/// </summary>
public sealed class KeyAction
{
    /// <summary>A stable id, namespaced by area (<c>file.save</c>, <c>builder.tool.select</c>): what <c>keybinds.json</c> keys on.</summary>
    public required string Id { get; init; }

    /// <summary>What the action is called in the UI, in the user's language.</summary>
    public required string Name { get; init; }

    public required KeyScope Scope { get; init; }

    /// <summary>
    /// Where the keybinds window lists the action, in the user's language: a name (<c>File</c>), or a path of names that
    /// nest in its category tree, joined with <see cref="CategorySeparator"/> (<c>Editor › Notes</c>).
    /// </summary>
    public required string Category { get; init; }

    /// <summary>What joins the names of a nested <see cref="Category"/>; the Settings window's categories are joined by it too.</summary>
    public const string CategorySeparator = " › ";

    /// <summary>
    /// The keys it has until the user changes them, the first being its primary one, which menus and tooltips show.
    /// Several are fine: Redo has both Ctrl+Y and Ctrl+Shift+Z.
    /// </summary>
    public IReadOnlyList<KeyChord> Defaults { get; init; } = [];

    /// <summary>Listed but not changeable (Copy, Paste, Undo): its keys are always its <see cref="Defaults"/>.</summary>
    public bool Locked { get; init; }

    /// <summary>
    /// Whether the user may bind a key with no modifier. Off for window-wide commands, where a bare letter would steal the
    /// typing of a text box; on for a canvas's keys.
    /// </summary>
    public bool AllowBare { get; init; }

    /// <summary>Whether holding the key repeats the action. Off, an auto-repeat is ignored.</summary>
    public bool Repeatable { get; init; }

    public KeyActionKind Kind { get; init; }

    /// <summary>
    /// Whether a focused text box keeps the key, and the action doesn't run while one has focus: Undo and Redo, since a box
    /// with its own undo handles them itself, and the tool keys, which a box types or leaves alone.
    /// </summary>
    public bool YieldsToTextInput { get; init; }

    /// <summary>Whether the action still runs while a text box has focus. Off, it doesn't.</summary>
    public bool FiresInTextInput { get; init; }

    /// <summary>
    /// Whether a bare key bound to it can't also be an instrument key while a chart is being playtested (Space, Esc and the
    /// like), so a playtest never turns a stop key into a note.
    /// </summary>
    public bool ReservedDuringPlaytest { get; init; }

    /// <summary>
    /// What to show instead of a key, for a locked row that isn't a key at all: <c>Shift+Click</c>, <c>Ctrl+Wheel</c>. Such a
    /// row has no <see cref="Defaults"/> and is never dispatched.
    /// </summary>
    public string? DisplayOverride { get; init; }
}
