using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChartBuilder.Resources;
using ChartShared.Core.Charting.Layout;

namespace ChartBuilder.Services
{
    /// <summary>
    /// The layout templates (issue 263): named arrangements of the Editor's lanes, which the View menu loads in place of
    /// the lanes open, and saves the lanes open over or as a new one, and Manage layouts adds to, renames, reorders,
    /// overwrites and deletes. Kept in <see cref="BuilderSettings.LayoutTemplates"/>, saved as each change is made.
    /// </summary>
    /// <remarks>
    /// Names are unique ignoring case and the space around them. Each template's lanes are copied in and out, so the
    /// layout a template was saved from, or loaded into, never changes it. Settings that have never had templates start
    /// with the default one (<see cref="LaneDockLayout.CreateDefault"/>), which is written only once something changes.
    /// </remarks>
    public sealed class LayoutTemplateStore
    {
        /// <summary>The longest a template's name can be.</summary>
        public const int MaxNameLength = 64;

        /// <summary>The most templates there can be, against a damaged file.</summary>
        public const int MaxTemplates = 100;

        private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

        private readonly BuilderSettingsService _settings;

        // The templates, in order. Once something changes, the settings' own list, which BuilderSettingsService.Load
        // has already sanitized; until then, theirs or, when they have none, the default alone.
        private readonly List<LayoutTemplate> _templates;

        public LayoutTemplateStore(BuilderSettingsService settings)
        {
            ArgumentNullException.ThrowIfNull(settings);
            _settings = settings;
            _templates = settings.Settings.LayoutTemplates
                ?? [new LayoutTemplate { Name = Strings.LayoutDefaultName, Lanes = LaneDockLayout.CreateDefault().ToState() }];
        }

        /// <summary>A template was added, overwritten, renamed, moved or deleted.</summary>
        public event EventHandler? Changed;

        /// <summary>The templates' names, in order.</summary>
        public IReadOnlyList<string> Names => [.. _templates.Select(t => t.Name!)];

        /// <summary>Whether no more templates can be added.</summary>
        public bool IsFull => _templates.Count >= MaxTemplates;

        /// <summary>Whether there's a template named <paramref name="name"/>, ignoring case and the space around it.</summary>
        public bool Contains(string? name) => IndexOf(name) >= 0;

        /// <summary>A copy of the lanes of the template named <paramref name="name"/>, or null when there's none.</summary>
        public LaneDockState? Get(string? name) => IndexOf(name) is >= 0 and var i ? Copy(_templates[i].Lanes) : null;

        /// <summary>
        /// Whether a template is <paramref name="lanes"/>: the same lanes, docked the same way, sized the same to within
        /// rounding, with the same view settings. Loading another template over lanes that are one loses nothing.
        /// </summary>
        public bool HasLanes(LaneDockState? lanes)
        {
            if (Canonical(lanes) is not { } wanted)
                return false;
            return _templates.Any(t => Canonical(t.Lanes) is { } saved && JsonNode.DeepEquals(saved, wanted));
        }

        /// <summary>
        /// What's wrong with <paramref name="name"/> as a template's name, or null when nothing is: it's blank, too long, or
        /// another template's. <paramref name="except"/> is the template being renamed, whose own name is allowed; with none,
        /// the name is for a new template, which can't be added once the store <see cref="IsFull"/>.
        /// </summary>
        public string? ValidateName(string? name, string? except = null)
        {
            string trimmed = name?.Trim() ?? "";
            if (trimmed.Length == 0)
                return Strings.LayoutNameBlank;
            if (trimmed.Length > MaxNameLength)
                return string.Format(CultureInfo.CurrentCulture, Strings.LayoutNameTooLongFormat, MaxNameLength);
            if (Contains(trimmed) && (except is null || !NameComparer.Equals(trimmed, except.Trim())))
                return string.Format(CultureInfo.CurrentCulture, Strings.LayoutNameTakenFormat, trimmed);
            if (except is null && IsFull)
                return string.Format(CultureInfo.CurrentCulture, Strings.LayoutLimitFormat, MaxTemplates);
            return null;
        }

        /// <summary>Adds a template of <paramref name="lanes"/> named <paramref name="name"/>, after the others.</summary>
        /// <returns>False, adding nothing, when the name isn't allowed (<see cref="ValidateName"/>) or the lanes aren't a layout.</returns>
        public bool Add(string name, LaneDockState lanes)
        {
            if (ValidateName(name) is not null || Copy(lanes) is not { } copy)
                return false;
            _templates.Add(new LayoutTemplate { Name = name.Trim(), Lanes = copy });
            Commit();
            return true;
        }

        /// <summary>Puts <paramref name="lanes"/> in the template named <paramref name="name"/> in place of its own.</summary>
        /// <returns>False, changing nothing, when there's no such template or the lanes aren't a layout.</returns>
        public bool Overwrite(string name, LaneDockState lanes)
        {
            if (IndexOf(name) is not (>= 0 and var i) || Copy(lanes) is not { } copy)
                return false;
            _templates[i].Lanes = copy;
            Commit();
            return true;
        }

        /// <summary>Names the template named <paramref name="name"/> <paramref name="newName"/>, which may differ only in case.</summary>
        /// <returns>False, changing nothing, when there's no such template or the new name isn't allowed.</returns>
        public bool Rename(string name, string newName)
        {
            if (IndexOf(name) is not (>= 0 and var i) || ValidateName(newName, _templates[i].Name) is not null)
                return false;
            string trimmed = newName.Trim();
            if (string.Equals(trimmed, _templates[i].Name, StringComparison.Ordinal))
                return true;
            _templates[i].Name = trimmed;
            Commit();
            return true;
        }

        /// <summary>
        /// Moves the template named <paramref name="name"/> <paramref name="offset"/> places down the list (up, when it's
        /// negative), no further than either end.
        /// </summary>
        /// <returns>Whether it moved.</returns>
        public bool Move(string name, int offset)
        {
            if (IndexOf(name) is not (>= 0 and var i))
                return false;
            int to = Math.Clamp(i + offset, 0, _templates.Count - 1);
            if (to == i)
                return false;
            var template = _templates[i];
            _templates.RemoveAt(i);
            _templates.Insert(to, template);
            Commit();
            return true;
        }

        /// <summary>Deletes the template named <paramref name="name"/>.</summary>
        /// <returns>Whether there was one.</returns>
        public bool Delete(string name)
        {
            if (IndexOf(name) is not (>= 0 and var i))
                return false;
            _templates.RemoveAt(i);
            Commit();
            return true;
        }

        /// <summary>
        /// Templates as saved, without those a damaged or hand-edited file might have: a missing one, one whose name is
        /// blank, too long or another's (ignoring case; the first is kept), or whose lanes aren't a layout. Names are
        /// trimmed, and no more than <see cref="MaxTemplates"/> are kept. Null stays null: no templates saved yet.
        /// </summary>
        public static List<LayoutTemplate>? Sanitize(List<LayoutTemplate>? templates)
        {
            if (templates is null)
                return null;

            var kept = new List<LayoutTemplate>();
            var names = new HashSet<string>(NameComparer);
            foreach (var template in templates)
            {
                if (kept.Count == MaxTemplates)
                    break;
                string name = template?.Name?.Trim() ?? "";
                // The lanes are checked before the name is taken, so a template dropped for its lanes doesn't take a
                // later one's name with it.
                if (name.Length == 0 || name.Length > MaxNameLength || Copy(template!.Lanes) is not { } lanes || !names.Add(name))
                    continue;
                kept.Add(new LayoutTemplate { Name = name, Lanes = lanes });
            }

            return kept;
        }

        private int IndexOf(string? name)
        {
            string trimmed = name?.Trim() ?? "";
            return _templates.FindIndex(t => NameComparer.Equals(t.Name, trimmed));
        }

        /// <summary>
        /// The templates in the settings, and saved. They're the settings' list from then on, which is only changed here
        /// and always saved straight after, and whose lanes are copies no layout holds.
        /// </summary>
        private void Commit()
        {
            _settings.Settings.LayoutTemplates = _templates;
            _settings.Save();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        // A copy of the lanes, as a layout reads them: null when they aren't one.
        private static LaneDockState? Copy(LaneDockState? lanes) => LaneDockLayout.FromState(lanes)?.ToState();

        // The lanes as a layout reads them, as JSON, their weights rounded so a layout read and written again, whose
        // weights are scaled afresh, still matches: null when they aren't a layout.
        private static JsonNode? Canonical(LaneDockState? lanes)
        {
            if (Copy(lanes) is not { } copy)
                return null;
            RoundWeights(copy.Root);
            return JsonSerializer.SerializeToNode(copy, BuilderSettingsJsonContext.Default.LaneDockState);
        }

        private static void RoundWeights(LaneDockNodeState? node)
        {
            if (node is null)
                return;
            if (node.Weights is { } weights)
            {
                for (int i = 0; i < weights.Count; i++)
                    weights[i] = Math.Round(weights[i], WeightDigits);
            }

            foreach (var child in node.Children ?? [])
                RoundWeights(child);
        }

        // How many decimal places weights are compared to: they're scaled to average 1, so far finer than a pixel.
        private const int WeightDigits = 6;
    }
}
