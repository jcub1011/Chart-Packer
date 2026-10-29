using System.ComponentModel;

namespace ChartShared.Core.Keybinds;

/// <summary>What is wrong with a chord as a binding for an action.</summary>
public enum ChordProblem
{
    None,

    /// <summary>It's no key, or only a modifier.</summary>
    ModifierOnly,

    /// <summary>The operating system takes it (Alt+F4).</summary>
    OsReserved,

    /// <summary>It has no modifier, and the action doesn't allow that (<see cref="KeyAction.AllowBare"/>).</summary>
    NeedsModifier,
}

/// <summary>Two actions that can be live together and share a key.</summary>
/// <param name="Chord">The key, as a label.</param>
public readonly record struct KeyConflict(KeyAction First, KeyAction Second, string Chord);

/// <summary>What <see cref="KeyMap.ApplyLayout"/> did with a layout.</summary>
/// <param name="Applied">Entries that set an action's keys.</param>
/// <param name="Ignored">Entries for an unknown or locked action, or with no key that could be used.</param>
public readonly record struct LayoutApplied(int Applied, int Ignored);

/// <summary>
/// The keys each action has right now: its defaults, unless the user changed them. The one place that answers what a key
/// does, what an action's key is called, and whether a new binding clashes.
/// </summary>
/// <remarks>
/// <para>
/// A change raises <see cref="PropertyChanged"/> for <c>Item[]</c>, so a binding to <see cref="this[string]"/> (a menu's
/// shortcut text, a tooltip) refreshes the moment a key is rebound.
/// </para>
/// <para>
/// A map is for the UI thread. The keybinds window edits a <see cref="Clone"/> and the live map takes it with
/// <see cref="CopyFrom"/> on OK, so Cancel leaves nothing changed.
/// </para>
/// </remarks>
public sealed class KeyMap : INotifyPropertyChanged
{
    /// <summary>The most keys one action can have, so a hand-edited file can't make a row unreadable.</summary>
    public const int MaxChordsPerAction = 8;

    // Only the actions whose keys differ from their defaults, so a catalog that gains an action gives it its defaults.
    private readonly Dictionary<string, KeyChord[]> _overrides = new(StringComparer.Ordinal);

    public KeyMap(KeybindCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Catalog = catalog;
    }

    public KeybindCatalog Catalog { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after any change to the keys.</summary>
    public event EventHandler? Changed;

    /// <summary>The label of the action's primary key (see <see cref="Display"/>), for bindings.</summary>
    public string this[string id] => Display(id);

    /// <summary>The actions whose keys differ from their defaults, with their keys (empty for one the user unbound).</summary>
    public IReadOnlyDictionary<string, KeyChord[]> Overrides => _overrides;

    /// <summary>The keys the action answers to, primary first; none for an unknown action or one with no key.</summary>
    public IReadOnlyList<KeyChord> ChordsOf(string id) =>
        _overrides.TryGetValue(id, out var chords) ? chords : Catalog.TryGet(id, out var action) ? action.Defaults : [];

    /// <summary>Whether the user changed the action's keys from its defaults.</summary>
    public bool IsCustomized(string id) => _overrides.ContainsKey(id);

    /// <summary>
    /// The label to show for the action: its <see cref="KeyAction.DisplayOverride"/>, else its primary key as
    /// <see cref="KeyChord.ToDisplayString"/> has it, else empty (unbound, or no such action).
    /// </summary>
    public string Display(string id)
    {
        if (!Catalog.TryGet(id, out var action))
            return "";
        if (action.DisplayOverride is { } text)
            return text;
        var chords = ChordsOf(id);
        return chords.Count > 0 ? chords[0].ToDisplayString() : "";
    }

    /// <summary>
    /// The actions that answer a key press in <paramref name="active"/>, innermost scope first and in registration order
    /// within one. The caller runs them in that order until one handles the press.
    /// </summary>
    /// <param name="active">The innermost scope live now; its ancestors are live too.</param>
    /// <param name="logical">The press as a token of the character the layout gives the key.</param>
    /// <param name="physical">The press as a token of the key's position.</param>
    public IReadOnlyList<KeyAction> Resolve(KeyScope active, KeyChord logical, KeyChord physical)
    {
        List<KeyAction>? hits = null;
        for (var scope = active; scope is not null; scope = scope.Parent)
        {
            var pressed = scope.Match == KeyMatch.Physical ? physical : logical;
            foreach (var action in Catalog.InScope(scope))
            {
                foreach (var bound in ChordsOf(action.Id))
                {
                    if (bound.Equals(pressed))
                    {
                        (hits ??= []).Add(action);
                        break;
                    }
                }
            }
        }

        return hits ?? (IReadOnlyList<KeyAction>)[];
    }

    /// <summary>What is wrong with <paramref name="chord"/> as a binding for <paramref name="action"/>, if anything.</summary>
    public static ChordProblem Validate(KeyAction action, KeyChord chord)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (chord.IsModifierOnly)
            return ChordProblem.ModifierOnly;
        if (chord.IsOsReserved)
            return ChordProblem.OsReserved;
        return chord.Mods == KeyMods.None && !action.AllowBare ? ChordProblem.NeedsModifier : ChordProblem.None;
    }

    /// <summary>
    /// The other actions, locked ones too, that already have <paramref name="chord"/> and whose scope overlaps
    /// <paramref name="id"/>'s: the ones that would clash if the action took the key.
    /// </summary>
    public IReadOnlyList<KeyAction> FindConflicts(string id, KeyChord chord)
    {
        if (chord.IsEmpty || !Catalog.TryGet(id, out var self))
            return [];

        string key = chord.ConflictKey;
        List<KeyAction>? hits = null;
        foreach (var other in Catalog)
        {
            if (other.Id == id || !other.Scope.Overlaps(self.Scope))
                continue;
            if (ChordsOf(other.Id).Any(c => c.ConflictKey == key))
                (hits ??= []).Add(other);
        }

        return hits ?? (IReadOnlyList<KeyAction>)[];
    }

    /// <summary>Every pair of actions that can be live together and share a key, as the keybinds window flags them.</summary>
    public IReadOnlyList<KeyConflict> AllConflicts()
    {
        var keyed = new List<(KeyAction Action, string Key)>();
        foreach (var action in Catalog)
        {
            foreach (var chord in ChordsOf(action.Id).Distinct())
                keyed.Add((action, chord.ConflictKey));
        }

        var conflicts = new List<KeyConflict>();
        for (int i = 0; i < keyed.Count; i++)
        {
            for (int j = i + 1; j < keyed.Count; j++)
            {
                if (keyed[i].Key == keyed[j].Key && keyed[i].Action != keyed[j].Action
                    && keyed[i].Action.Scope.Overlaps(keyed[j].Action.Scope))
                    conflicts.Add(new KeyConflict(keyed[i].Action, keyed[j].Action, keyed[i].Key));
            }
        }

        return conflicts;
    }

    /// <summary>
    /// Whether <paramref name="chord"/> is a key that an action reserved for itself during a playtest (<see cref="KeyAction.ReservedDuringPlaytest"/>),
    /// so it can't be an instrument key. Only a key with no modifier can be.
    /// </summary>
    public bool IsReservedDuringPlaytest(KeyChord chord)
    {
        if (chord.IsEmpty || chord.Mods != KeyMods.None)
            return false;

        string key = chord.ConflictKey;
        return Catalog.Any(a => a.ReservedDuringPlaytest && ChordsOf(a.Id).Any(c => c.Mods == KeyMods.None && c.ConflictKey == key));
    }

    /// <summary>
    /// Gives the action <paramref name="chords"/>, primary first, dropping any that can't be a binding for it
    /// (<see cref="Validate"/>), repeats, and those past <see cref="MaxChordsPerAction"/>. None unbinds it.
    /// </summary>
    /// <returns>False for an unknown or locked action, which is left as it is.</returns>
    public bool Set(string id, IEnumerable<KeyChord> chords)
    {
        ArgumentNullException.ThrowIfNull(chords);
        if (!Catalog.TryGet(id, out var action) || action.Locked)
            return false;

        Store(action, Clean(action, chords));
        Raise();
        return true;
    }

    /// <summary>Puts the action back to its default keys.</summary>
    public void Reset(string id)
    {
        if (_overrides.Remove(id))
            Raise();
    }

    /// <summary>Puts every action back to its default keys.</summary>
    public void ResetAll()
    {
        if (_overrides.Count == 0)
            return;
        _overrides.Clear();
        Raise();
    }

    /// <summary>
    /// Takes keys from a layout (<c>keybinds.json</c>'s <c>Bindings</c>: action id to chord text). An entry for an action this
    /// catalog doesn't have, or that is locked, or none of whose keys can be used, is ignored; one with no keys at all unbinds the action.
    /// A key a locked action has is one that can't be used, as in the keybinds window.
    /// </summary>
    /// <param name="replace">True to put every action not in the layout back to its defaults first; false to leave those as they are.</param>
    public LayoutApplied ApplyLayout(IReadOnlyDictionary<string, string[]> bindings, bool replace)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        if (replace)
            _overrides.Clear();

        int applied = 0, ignored = 0;
        foreach (var (id, tokens) in bindings)
        {
            if (tokens is null || !Catalog.TryGet(id, out var action) || action.Locked)
            {
                ignored++;
                continue;
            }

            var parsed = tokens.Select(t => KeyChord.TryParse(t, out var chord) ? chord : default);
            var clean = Clean(action, parsed);
            if (tokens.Length > 0 && clean.Count == 0)
            {
                ignored++;
                continue;
            }

            Store(action, clean);
            applied++;
        }

        Raise();
        return new LayoutApplied(applied, ignored);
    }

    /// <summary>The whole layout, each changeable action with the keys it has now, as an export writes it.</summary>
    public Dictionary<string, string[]> Export()
    {
        var layout = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var action in Catalog.Where(a => !a.Locked))
            layout[action.Id] = [.. ChordsOf(action.Id).Select(c => c.ToString())];
        return layout;
    }

    /// <summary>A map of the same catalog with the same keys, to edit without touching this one.</summary>
    public KeyMap Clone()
    {
        var copy = new KeyMap(Catalog);
        copy.CopyFromQuietly(this);
        return copy;
    }

    /// <summary>Takes <paramref name="other"/>'s keys, as the live map does when the keybinds window is accepted.</summary>
    public void CopyFrom(KeyMap other)
    {
        ArgumentNullException.ThrowIfNull(other);
        CopyFromQuietly(other);
        Raise();
    }

    private void CopyFromQuietly(KeyMap other)
    {
        _overrides.Clear();
        foreach (var (id, chords) in other._overrides)
            _overrides[id] = chords;
    }

    private List<KeyChord> Clean(KeyAction action, IEnumerable<KeyChord> chords)
    {
        var clean = new List<KeyChord>();
        var seen = new HashSet<KeyChord>();
        foreach (var chord in chords)
        {
            if (clean.Count == MaxChordsPerAction)
                break;
            // A locked action's key is never given to another: the window refuses it, and so does a layout.
            if (Validate(action, chord) == ChordProblem.None && !TakenByLocked(action, chord) && seen.Add(chord))
                clean.Add(chord);
        }

        return clean;
    }

    private bool TakenByLocked(KeyAction action, KeyChord chord) =>
        FindConflicts(action.Id, chord).Any(other => other.Locked);

    private void Store(KeyAction action, List<KeyChord> chords)
    {
        if (chords.SequenceEqual(action.Defaults))
            _overrides.Remove(action.Id);
        else
            _overrides[action.Id] = [.. chords];
    }

    private void Raise()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
