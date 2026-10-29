using System.Collections;

namespace ChartShared.Core.Keybinds;

/// <summary>
/// Every action an app (or both) has keys for. Built once at startup from the shared actions and the app's own.
/// </summary>
public sealed class KeybindCatalog : IEnumerable<KeyAction>
{
    private readonly List<KeyAction> _actions;
    private readonly Dictionary<string, KeyAction> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<KeyScope, List<KeyAction>> _byScope = [];

    /// <exception cref="ArgumentException">Two actions share an id.</exception>
    public KeybindCatalog(IEnumerable<KeyAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        _actions = [.. actions];
        foreach (var action in _actions)
        {
            if (!_byId.TryAdd(action.Id, action))
                throw new ArgumentException($"Two actions have the id '{action.Id}'.", nameof(actions));
            if (!_byScope.TryGetValue(action.Scope, out var inScope))
                _byScope[action.Scope] = inScope = [];
            inScope.Add(action);
        }
    }

    /// <summary>The actions in the order they were registered, which is the order they're listed and dispatched in.</summary>
    public IReadOnlyList<KeyAction> Actions => _actions;

    public KeyAction this[string id] => _byId[id];

    public bool TryGet(string id, out KeyAction action) => _byId.TryGetValue(id, out action!);

    public bool Contains(string id) => _byId.ContainsKey(id);

    /// <summary>The actions registered in exactly <paramref name="scope"/>, not those of its ancestors.</summary>
    public IReadOnlyList<KeyAction> InScope(KeyScope scope) => _byScope.TryGetValue(scope, out var inScope) ? inScope : [];

    /// <summary>A catalog of the actions of each of <paramref name="catalogs"/>, in order.</summary>
    public static KeybindCatalog Combine(params KeybindCatalog[] catalogs) => new(catalogs.SelectMany(c => c._actions));

    public IEnumerator<KeyAction> GetEnumerator() => _actions.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
