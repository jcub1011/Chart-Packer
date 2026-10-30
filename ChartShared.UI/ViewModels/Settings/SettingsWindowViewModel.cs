using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ChartShared.Core.Settings;
using ChartShared.UI.Resources;
using ChartShared.UI.Services;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartShared.UI.ViewModels.Settings
{
    /// <summary>A top-level category in the Settings window's list, or the "All Settings" entry at its top.</summary>
    public sealed partial class SettingsCategoryViewModel : ObservableObject
    {
        private readonly IReadOnlyList<SettingViewModel> _settings;

        /// <param name="settings">Every setting in the category and the ones nested in it, whether or not a search shows it.</param>
        /// <param name="subtitle">The page header's second line: what the category holds.</param>
        /// <param name="matchCount">How many of its settings match the search, or <see langword="null"/> when not searching.</param>
        public SettingsCategoryViewModel(SettingCategory category, IReadOnlyList<SettingViewModel> settings, string subtitle, int? matchCount)
            : this(category.Label, category.IconKey, settings, subtitle, matchCount)
        {
            Category = category;
        }

        private SettingsCategoryViewModel(string label, string? iconKey, IReadOnlyList<SettingViewModel> settings, string subtitle, int? matchCount)
        {
            Label = label;
            IconKey = iconKey;
            Subtitle = subtitle;
            MatchCount = matchCount;
            _settings = settings;
            ModifiedCount = settings.Count(s => s.IsModified);
        }

        /// <summary>The entry that shows every category, so there's always a way back from a selected one.</summary>
        public static SettingsCategoryViewModel All(IReadOnlyList<SettingViewModel> settings, int? matchCount) =>
            new(SharedStrings.SettingsAllCategories, "MoreStroke", settings, "", matchCount);

        /// <summary>The category, or <see langword="null"/> for the "All Settings" entry.</summary>
        public SettingCategory? Category { get; }

        public string Label { get; }

        /// <summary>The key of the category's line icon, or <see langword="null"/> when it has none.</summary>
        public string? IconKey { get; }

        public bool HasIcon => IconKey is not null;

        /// <summary>The names of the sections under the category's heading, comma separated.</summary>
        public string Subtitle { get; }

        public bool HasSubtitle => Subtitle.Length > 0;

        /// <summary>How many settings match the search, or <see langword="null"/> while there isn't one.</summary>
        public int? MatchCount { get; }

        public bool HasMatchCount => MatchCount is not null;

        /// <summary>How many of its settings have a change that OK or Apply hasn't written yet.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasModified))]
        public partial int ModifiedCount { get; private set; }

        public bool HasModified => ModifiedCount > 0;

        /// <summary>Counts the modified settings again, after one is staged or the staged changes are written.</summary>
        public void RefreshModified() => ModifiedCount = _settings.Count(s => s.IsModified);
    }

    /// <summary>The settings of one category, under a section heading.</summary>
    public sealed class SettingsGroupViewModel(string title, IReadOnlyList<SettingViewModel> items)
    {
        /// <summary>The heading, or empty for a category's own settings under its page header.</summary>
        public string Title { get; } = title;

        public bool HasTitle => Title.Length > 0;

        public IReadOnlyList<SettingViewModel> Items { get; } = items;
    }

    /// <summary>
    /// The Settings window: a list of categories, the selected category's settings, and a search box that
    /// narrows both. Edits are staged in a <see cref="SettingsSession"/> until OK or Apply.
    /// </summary>
    public sealed partial class SettingsWindowViewModel : ViewModelBase
    {
        private const string PathSeparator = " › ";

        private readonly SettingsCatalog _catalog;
        private readonly SettingsSession _session;
        private readonly Action _save;
        private readonly Dictionary<SettingDefinition, SettingViewModel> _editors = [];
        private SettingsCategoryViewModel _all;
        private HashSet<SettingDefinition> _matches = [];

        /// <param name="folderPicker">Picks folders for folder settings; its dialogs should be owned by the Settings window.</param>
        /// <param name="save">Persists the settings after changes are applied.</param>
        public SettingsWindowViewModel(SettingsCatalog catalog, IFolderPickerService folderPicker, Action save)
        {
            _catalog = catalog;
            _save = save;
            _session = new SettingsSession(catalog);
            foreach (var edit in _session.Edits)
                _editors.Add(edit.Definition, SettingEditorFactory.Create(edit, folderPicker));

            _session.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(SettingsSession.HasChanges))
                    return;
                OnPropertyChanged(nameof(HasChanges));
                ApplyCommand.NotifyCanExecuteChanged();
            };

            foreach (var editor in _editors.Values)
            {
                editor.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(SettingViewModel.IsModified))
                        RefreshModifiedCounts();
                };
            }

            _all = SettingsCategoryViewModel.All([.. _editors.Values], null);
            RebuildCategories();
        }

        [ObservableProperty]
        public partial string SearchText { get; set; } = "";

        /// <summary>The top-level categories; while searching, only the ones with a match. "All Settings" comes first.</summary>
        [ObservableProperty]
        public partial IReadOnlyList<SettingsCategoryViewModel> Categories { get; set; } = [];

        /// <summary>The category whose settings are shown, with its children's. "All Settings" (or none) shows every category.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(PageTitle), nameof(PageSubtitle), nameof(HasPageSubtitle))]
        public partial SettingsCategoryViewModel? SelectedCategory { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasNoMatches))]
        public partial IReadOnlyList<SettingsGroupViewModel> Groups { get; set; } = [];

        /// <summary>The page header: the selected category's name.</summary>
        public string PageTitle => SelectedCategory?.Label ?? _all.Label;

        /// <summary>What the page holds: the selected category's sections.</summary>
        public string PageSubtitle => SelectedCategory?.Subtitle ?? "";

        public bool HasPageSubtitle => PageSubtitle.Length > 0;

        public bool HasChanges => _session.HasChanges;

        /// <summary>How many settings have a change that OK or Apply hasn't written yet.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasUnsaved), nameof(UnsavedText))]
        public partial int UnsavedCount { get; private set; }

        public bool HasUnsaved => UnsavedCount > 0;

        public string UnsavedText => UnsavedCount == 1
            ? SharedStrings.SettingsUnsavedOne
            : string.Format(CultureInfo.CurrentCulture, SharedStrings.SettingsUnsavedFormat, UnsavedCount);

        public bool HasNoMatches => Groups.Count == 0;

        public string NoMatchesText => string.Format(CultureInfo.CurrentCulture, SharedStrings.SettingsNoMatchesFormat, SearchText.Trim());

        /// <summary>Writes the staged changes and saves them. OK does this too, then closes.</summary>
        [RelayCommand(CanExecute = nameof(HasChanges))]
        private void Apply() => ApplyChanges();

        public void ApplyChanges()
        {
            if (_session.Apply())
                _save();
        }

        partial void OnSearchTextChanged(string value)
        {
            OnPropertyChanged(nameof(NoMatchesText));
            RebuildCategories();
        }

        partial void OnSelectedCategoryChanged(SettingsCategoryViewModel? value) => RefreshGroups();

        private void RefreshModifiedCounts()
        {
            foreach (var category in Categories)
                category.RefreshModified();
            UnsavedCount = _editors.Values.Count(e => e.IsModified);
        }

        /// <summary>Rebuilds the list for the search, and selects "All Settings" so every match shows until a category is picked.</summary>
        private void RebuildCategories()
        {
            var matches = _matches = _catalog.Search(SearchText).ToHashSet();
            bool searching = !string.IsNullOrWhiteSpace(SearchText);

            // A new "All Settings" entry each time, to carry the match count, so it's never the one already selected and
            // the page is rebuilt for the new matches.
            _all = SettingsCategoryViewModel.All([.. _editors.Values], searching ? matches.Count : null);
            Categories = [_all, .. _catalog.RootCategories.Select(c => BuildCategory(c, matches, searching)).OfType<SettingsCategoryViewModel>()];
            UnsavedCount = _editors.Values.Count(e => e.IsModified);
            SelectedCategory = _all;
        }

        /// <returns><see langword="null"/> when neither the category nor its children have a match.</returns>
        private SettingsCategoryViewModel? BuildCategory(SettingCategory category, HashSet<SettingDefinition> matches, bool searching)
        {
            var under = DefinitionsUnder(category).ToList();
            int matching = under.Count(matches.Contains);
            if (matching == 0)
                return null;

            // While searching, only the sections that have a match, which are the ones the page shows.
            string subtitle = string.Join(
                ", ",
                _catalog.ChildrenOf(category).Where(c => !searching || DefinitionsUnder(c).Any(matches.Contains)).Select(c => c.Label));
            return new SettingsCategoryViewModel(category, [.. under.Select(d => _editors[d])], subtitle, searching ? matching : null);
        }

        /// <summary>The settings in <paramref name="category"/> and the categories nested in it.</summary>
        private IEnumerable<SettingDefinition> DefinitionsUnder(SettingCategory category) =>
            _catalog.DefinitionsIn(category).Concat(_catalog.ChildrenOf(category).SelectMany(DefinitionsUnder));

        private void RefreshGroups()
        {
            var matches = _matches;
            var groups = new List<SettingsGroupViewModel>();

            // Under "All Settings" each group says where it is; under one category the page header does, so only its
            // nested categories need a heading.
            bool showPath = SelectedCategory?.Category is null;
            IReadOnlyList<SettingCategory> roots = SelectedCategory?.Category is { } selected ? [selected] : _catalog.RootCategories;
            foreach (var root in roots)
                AddGroups(root, matches, groups, showPath);

            Groups = groups;
        }

        private void AddGroups(SettingCategory category, HashSet<SettingDefinition> matches, List<SettingsGroupViewModel> groups, bool showPath)
        {
            var items = _catalog.DefinitionsIn(category)
                .Where(matches.Contains)
                .Select(d => _editors[d])
                .ToList();
            if (items.Count > 0)
            {
                string title = showPath
                    ? string.Join(PathSeparator, category.Path)
                    : category.Parent is null ? "" : category.Label;
                groups.Add(new SettingsGroupViewModel(title, items));
            }

            foreach (var child in _catalog.ChildrenOf(category))
                AddGroups(child, matches, groups, showPath);
        }
    }
}
