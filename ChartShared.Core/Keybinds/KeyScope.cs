namespace ChartShared.Core.Keybinds;

/// <summary>How a scope's keys are matched against what was pressed.</summary>
public enum KeyMatch
{
    /// <summary>By the character the layout gives the key: Ctrl+Z is wherever the layout puts Z. For commands.</summary>
    Logical,

    /// <summary>By where the key is, whatever the layout: <c>[</c> and <c>]</c> work on a German keyboard. For a canvas's keys.</summary>
    Physical,
}

/// <summary>
/// Where an action's keys are live: a window, or a control inside one. Scopes form a tree, a window's under
/// <see cref="Global"/> and a control's under its window's.
/// </summary>
/// <remarks>
/// A key press is resolved from the innermost scope that is active outwards. Two scopes <see cref="Overlaps"/> when one is
/// the other or its ancestor, since an action in either could answer the same press; only actions in overlapping scopes can
/// clash. Siblings never do, so a key can mean one thing in the editor and another in the Preview window.
/// </remarks>
public sealed class KeyScope
{
    /// <summary>Live in every window of both apps.</summary>
    public static readonly KeyScope Global = new("global");

    /// <param name="parent">The scope this one is inside; <see cref="Global"/> when null, so every scope is under it.</param>
    public KeyScope(string id, KeyScope? parent = null, KeyMatch match = KeyMatch.Logical)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        Id = id;
        // Global is being built when it's still null, and is the one scope with no parent.
        Parent = parent ?? Global;
        Match = match;
    }

    public string Id { get; }

    public KeyScope? Parent { get; }

    public KeyMatch Match { get; }

    /// <summary>Whether this scope is <paramref name="other"/> or one of its ancestors.</summary>
    public bool IsSameOrAncestorOf(KeyScope other)
    {
        for (var scope = other; scope is not null; scope = scope.Parent)
        {
            if (ReferenceEquals(scope, this))
                return true;
        }

        return false;
    }

    /// <summary>Whether actions in this scope and in <paramref name="other"/> can be live together.</summary>
    public bool Overlaps(KeyScope other) => IsSameOrAncestorOf(other) || other.IsSameOrAncestorOf(this);

    public override string ToString() => Id;
}
