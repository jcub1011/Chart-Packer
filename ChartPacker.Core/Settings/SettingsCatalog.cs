using System.Globalization;

namespace ChartPacker.Core.Settings;

/// <summary>
/// Every setting a settings window shows, grouped in a category tree. The catalog is data: adding or
/// removing a setting is adding or removing its definition.
/// </summary>
public sealed class SettingsCatalog
{
    private const CompareOptions SearchOptions = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    private readonly Dictionary<SettingCategory, List<SettingCategory>> _children = [];
    private readonly Dictionary<SettingCategory, List<SettingDefinition>> _definitionsIn = [];

    /// <param name="categories">Every category, parents before their children, in display order.</param>
    /// <param name="definitions">Every setting, in display order within its category.</param>
    /// <exception cref="ArgumentException">
    /// An id is used twice, a category or parent isn't listed (or is listed after its children), or a setting
    /// requires one that isn't a listed toggle, or itself.
    /// </exception>
    public SettingsCatalog(IEnumerable<SettingCategory> categories, IEnumerable<SettingDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentNullException.ThrowIfNull(definitions);

        var categoryIds = new HashSet<string>(StringComparer.Ordinal);
        var roots = new List<SettingCategory>();
        foreach (var category in categories)
        {
            if (!categoryIds.Add(category.Id))
                throw new ArgumentException($"Category id '{category.Id}' is used twice.", nameof(categories));

            if (category.Parent is { } parent)
            {
                if (!_children.TryGetValue(parent, out var siblings))
                    throw new ArgumentException($"Category '{category.Id}' is listed before its parent '{parent.Id}', or the parent isn't listed.", nameof(categories));
                siblings.Add(category);
            }
            else
            {
                roots.Add(category);
            }

            _children.Add(category, []);
            _definitionsIn.Add(category, []);
        }

        var definitionIds = new HashSet<string>(StringComparer.Ordinal);
        var all = new List<SettingDefinition>();
        foreach (var definition in definitions)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(definition.Id, nameof(definitions));
            if (!definitionIds.Add(definition.Id))
                throw new ArgumentException($"Setting id '{definition.Id}' is used twice.", nameof(definitions));
            if (!_definitionsIn.TryGetValue(definition.Category, out var inCategory))
                throw new ArgumentException($"Setting '{definition.Id}' is in category '{definition.Category.Id}', which isn't listed.", nameof(definitions));

            inCategory.Add(definition);
            all.Add(definition);
        }

        foreach (var definition in all.Where(d => d.RequiresId is not null))
        {
            var required = all.FirstOrDefault(d => d.Id == definition.RequiresId);
            if (required is not ToggleSetting || ReferenceEquals(required, definition))
                throw new ArgumentException($"Setting '{definition.Id}' requires '{definition.RequiresId}', which isn't another listed toggle.", nameof(definitions));
        }

        RootCategories = roots;

        // Catalog order is tree order, so search results and a session's edits read top to bottom like the tree.
        var ordered = new List<SettingDefinition>(all.Count);
        foreach (var root in roots)
            AddInTreeOrder(root, ordered);
        Definitions = ordered;
    }

    public IReadOnlyList<SettingCategory> RootCategories { get; }

    /// <summary>Every setting, in tree order: a category's own settings, then its children's.</summary>
    public IReadOnlyList<SettingDefinition> Definitions { get; }

    public IReadOnlyList<SettingCategory> ChildrenOf(SettingCategory category) => _children[category];

    /// <summary>The settings directly in <paramref name="category"/>, not in its children.</summary>
    public IReadOnlyList<SettingDefinition> DefinitionsIn(SettingCategory category) => _definitionsIn[category];

    /// <summary>
    /// The settings matching every whitespace-separated term of <paramref name="query"/>, in tree order.
    /// A term matches when it appears, ignoring case and accents, in the label, description, a keyword or a
    /// category label. A blank query matches everything.
    /// </summary>
    public IReadOnlyList<SettingDefinition> Search(string? query)
    {
        var terms = (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0)
            return Definitions;

        var compare = CultureInfo.CurrentCulture.CompareInfo;
        return [.. Definitions.Where(d => terms.All(term => Matches(d, term, compare)))];
    }

    private static bool Matches(SettingDefinition definition, string term, CompareInfo compare)
    {
        bool Contains(string text) => compare.IndexOf(text, term, SearchOptions) >= 0;

        return Contains(definition.Label)
            || Contains(definition.Description)
            || definition.Keywords.Any(Contains)
            || definition.Category.Path.Any(Contains);
    }

    private void AddInTreeOrder(SettingCategory category, List<SettingDefinition> ordered)
    {
        ordered.AddRange(_definitionsIn[category]);
        foreach (var child in _children[category])
            AddInTreeOrder(child, ordered);
    }
}
