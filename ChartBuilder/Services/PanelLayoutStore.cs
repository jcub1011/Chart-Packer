using System;
using System.Collections.Generic;
using System.Linq;

namespace ChartBuilder.Services
{
    /// <summary>
    /// How the user arranged the Editor's side panels: each panel's tabs in the order they were dragged to, each tab's
    /// sections likewise, and which sections are collapsed. Everything is by key, so a tab or section added in a later
    /// version takes its default place, and one taken away is forgotten. Kept in <see cref="BuilderSettings"/>.
    /// </summary>
    public sealed class PanelLayoutStore
    {
        /// <summary>The most keys a saved list keeps, against a damaged file.</summary>
        public const int MaxKeys = 64;

        /// <summary>The left panel's key, for its tabs.</summary>
        public const string LeftPanel = "left";

        /// <summary>The right panel's key, for its tabs.</summary>
        public const string RightPanel = "right";

        private readonly Dictionary<string, List<string>> _tabs = [];
        private readonly Dictionary<string, List<string>> _sections = [];
        private readonly HashSet<string> _collapsed = [];

        /// <summary>
        /// A panel's tabs or a tab's sections were arranged, or a section collapsed or expanded, with the panel's or the tab's
        /// key. Every view of that panel or tab follows, such as a tab docked and the same tab in a window of its own.
        /// </summary>
        public event EventHandler<string>? Changed;

        /// <summary>The layout saved in <paramref name="settings"/>.</summary>
        public static PanelLayoutStore From(BuilderSettings settings)
        {
            var store = new PanelLayoutStore();
            if (settings.EditorLeftTabs is { } left)
                store._tabs[LeftPanel] = [.. left];
            if (settings.EditorRightTabs is { } right)
                store._tabs[RightPanel] = [.. right];
            foreach (var (tab, order) in settings.EditorSectionOrder ?? [])
                store._sections[tab] = [.. order];
            store._collapsed.UnionWith(settings.EditorCollapsedSections ?? []);
            return store;
        }

        /// <summary>Puts the layout in <paramref name="settings"/>, to be saved.</summary>
        public void WriteTo(BuilderSettings settings)
        {
            settings.EditorLeftTabs = _tabs.TryGetValue(LeftPanel, out var left) ? [.. left] : null;
            settings.EditorRightTabs = _tabs.TryGetValue(RightPanel, out var right) ? [.. right] : null;
            settings.EditorSectionOrder = _sections.Count > 0 ? _sections.ToDictionary(p => p.Key, p => p.Value.ToList()) : null;
            settings.EditorCollapsedSections = _collapsed.Count > 0 ? [.. _collapsed.Order(StringComparer.Ordinal)] : null;
        }

        /// <summary>The order to show a panel's tabs in, of those it has (<paramref name="present"/>, in their default order).</summary>
        public IReadOnlyList<string> TabOrder(string panel, IReadOnlyList<string> present) =>
            Arrange(_tabs.GetValueOrDefault(panel), present);

        /// <summary>Remembers the order a panel's tabs were dragged to.</summary>
        public void SetTabOrder(string panel, IEnumerable<string> order) => Set(_tabs, panel, order);

        /// <summary>Puts a panel's tabs back in their default order.</summary>
        public void ResetTabs(string panel)
        {
            if (_tabs.Remove(panel))
                Changed?.Invoke(this, panel);
        }

        /// <summary>The order to show a tab's sections in, of those it has (<paramref name="present"/>, in their default order).</summary>
        public IReadOnlyList<string> SectionOrder(string tab, IReadOnlyList<string> present) =>
            Arrange(_sections.GetValueOrDefault(tab), present);

        /// <summary>Remembers the order a tab's sections were dragged to.</summary>
        public void SetSectionOrder(string tab, IEnumerable<string> order) => Set(_sections, tab, order);

        /// <summary>Whether a tab's section is collapsed.</summary>
        public bool IsCollapsed(string tab, string section) => _collapsed.Contains(CollapsedKey(tab, section));

        /// <summary>Collapses or expands a tab's section.</summary>
        public void SetCollapsed(string tab, string section, bool collapsed)
        {
            string key = CollapsedKey(tab, section);
            if (collapsed ? _collapsed.Add(key) : _collapsed.Remove(key))
                Changed?.Invoke(this, tab);
        }

        /// <summary>Puts a tab's sections back in their default order, all expanded.</summary>
        public void ResetSections(string tab)
        {
            bool changed = _sections.Remove(tab);
            changed |= _collapsed.RemoveWhere(k => k.StartsWith(tab + "/", StringComparison.Ordinal)) > 0;
            if (changed)
                Changed?.Invoke(this, tab);
        }

        /// <summary>
        /// The keys in <paramref name="present"/> (in their default order), in the order <paramref name="saved"/> has them:
        /// one saved that isn't present is dropped, and one present that wasn't saved goes after the key before it in the
        /// default order, or first.
        /// </summary>
        public static IReadOnlyList<string> Arrange(IReadOnlyList<string>? saved, IReadOnlyList<string> present)
        {
            var known = new HashSet<string>(present, StringComparer.Ordinal);
            var order = (saved ?? []).Where(known.Contains).Distinct(StringComparer.Ordinal).ToList();
            for (int i = 0; i < present.Count; i++)
            {
                if (order.Contains(present[i], StringComparer.Ordinal))
                    continue;
                int at = i == 0 ? 0 : order.IndexOf(present[i - 1]) + 1;
                order.Insert(at, present[i]);
            }
            return order;
        }

        private void Set(Dictionary<string, List<string>> orders, string key, IEnumerable<string> order)
        {
            var list = order.Distinct(StringComparer.Ordinal).ToList();
            if (orders.TryGetValue(key, out var old) && old.SequenceEqual(list, StringComparer.Ordinal))
                return;
            orders[key] = list;
            Changed?.Invoke(this, key);
        }

        private static string CollapsedKey(string tab, string section) => tab + "/" + section;
    }
}
