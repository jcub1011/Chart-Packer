using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ChartPacker.Core.Settings;
using ChartPacker.Resources;
using ChartPacker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartPacker.ViewModels.Settings
{
    /// <summary>A category in the Settings window's tree, or the "All settings" entry at its top.</summary>
    public sealed partial class SettingsCategoryViewModel : ObservableObject
    {
        public SettingsCategoryViewModel(SettingCategory category, IReadOnlyList<SettingsCategoryViewModel> children)
        {
            Category = category;
            Label = category.Label;
            Children = children;
        }

        private SettingsCategoryViewModel(string label)
        {
            Label = label;
            Children = [];
        }

        /// <summary>The entry that shows every category, so there's always a way back from a selected one.</summary>
        public static SettingsCategoryViewModel All() => new(SharedStrings.SettingsAllCategories);

        /// <summary>The category, or <see langword="null"/> for the "All settings" entry.</summary>
        public SettingCategory? Category { get; }

        public string Label { get; }

        public IReadOnlyList<SettingsCategoryViewModel> Children { get; }

        [ObservableProperty]
        public partial bool IsExpanded { get; set; } = true;
    }

    /// <summary>The settings of one category, under its path as a heading.</summary>
    public sealed class SettingsGroupViewModel(string title, IReadOnlyList<SettingViewModel> items)
    {
        public string Title { get; } = title;

        public IReadOnlyList<SettingViewModel> Items { get; } = items;
    }

    /// <summary>
    /// The Settings window: a category tree, the selected category's settings, and a search box that
    /// narrows both. Edits are staged in a <see cref="SettingsSession"/> until OK or Apply.
    /// </summary>
    public sealed partial class SettingsWindowViewModel : ViewModelBase
    {
        private const string PathSeparator = " › ";

        private readonly SettingsCatalog _catalog;
        private readonly SettingsSession _session;
        private readonly Action _save;
        private readonly Dictionary<SettingDefinition, SettingViewModel> _editors = [];
        private readonly SettingsCategoryViewModel _all = SettingsCategoryViewModel.All();

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

            RebuildTree();
        }

        [ObservableProperty]
        public partial string SearchText { get; set; } = "";

        /// <summary>The category tree; while searching, only the categories with a match.</summary>
        [ObservableProperty]
        public partial IReadOnlyList<SettingsCategoryViewModel> Categories { get; set; } = [];

        /// <summary>The category whose settings are shown, with its children's. "All settings" (or none) shows every category.</summary>
        [ObservableProperty]
        public partial SettingsCategoryViewModel? SelectedCategory { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasNoMatches))]
        public partial IReadOnlyList<SettingsGroupViewModel> Groups { get; set; } = [];

        public bool HasChanges => _session.HasChanges;

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
            RebuildTree();
        }

        partial void OnSelectedCategoryChanged(SettingsCategoryViewModel? value) => RefreshGroups();

        /// <summary>Rebuilds the tree for the search, and selects "All settings" so every match shows until a category is picked.</summary>
        private void RebuildTree()
        {
            var matches = _catalog.Search(SearchText).ToHashSet();
            Categories = [_all, .. _catalog.RootCategories.Select(c => BuildCategory(c, matches)).OfType<SettingsCategoryViewModel>()];

            if (SelectedCategory == _all)
                RefreshGroups();
            else
                SelectedCategory = _all;
        }

        /// <returns><see langword="null"/> when neither the category nor its children have a match.</returns>
        private SettingsCategoryViewModel? BuildCategory(SettingCategory category, HashSet<SettingDefinition> matches)
        {
            var children = _catalog.ChildrenOf(category)
                .Select(c => BuildCategory(c, matches))
                .OfType<SettingsCategoryViewModel>()
                .ToList();

            bool hasMatch = children.Count > 0 || _catalog.DefinitionsIn(category).Any(matches.Contains);
            return hasMatch ? new SettingsCategoryViewModel(category, children) : null;
        }

        private void RefreshGroups()
        {
            var matches = _catalog.Search(SearchText).ToHashSet();
            var groups = new List<SettingsGroupViewModel>();
            IReadOnlyList<SettingCategory> roots = SelectedCategory?.Category is { } selected ? [selected] : _catalog.RootCategories;
            foreach (var root in roots)
                AddGroups(root, matches, groups);

            Groups = groups;
        }

        private void AddGroups(SettingCategory category, HashSet<SettingDefinition> matches, List<SettingsGroupViewModel> groups)
        {
            var items = _catalog.DefinitionsIn(category)
                .Where(matches.Contains)
                .Select(d => _editors[d])
                .ToList();
            if (items.Count > 0)
                groups.Add(new SettingsGroupViewModel(string.Join(PathSeparator, category.Path), items));

            foreach (var child in _catalog.ChildrenOf(category))
                AddGroups(child, matches, groups);
        }
    }
}
