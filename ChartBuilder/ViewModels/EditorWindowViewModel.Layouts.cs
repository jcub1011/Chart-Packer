using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using ChartBuilder.Resources;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>An item of the View menu's Load Layout or Save Layout submenu: a layout template, or Save Layout's New Layout.</summary>
    /// <param name="Name">The template's name; empty for New Layout.</param>
    /// <param name="Command">What choosing it runs, given the item: its submenu's command.</param>
    /// <param name="IsNew">Whether it's Save Layout's New Layout, which saves the lanes as a new template.</param>
    public sealed record LayoutMenuItem(string Name, ICommand Command, bool IsNew = false)
    {
        /// <summary>What the menu shows: the template's name, or New Layout.</summary>
        public string Header => IsNew ? Strings.MenuNewLayout : Name;
    }

    // The View menu's layout templates (issue 263): Load Layout, Save Layout and Manage Layouts. A template loads in place
    // of the lanes open, and only Save Layout and Manage Layouts' Overwrite Layout ever change one.
    public sealed partial class EditorWindowViewModel
    {
        /// <summary>The View menu's Load Layout submenu: each template.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasLayouts))]
        public partial IReadOnlyList<LayoutMenuItem> LoadLayoutItems { get; private set; } = [];

        /// <summary>The View menu's Save Layout submenu: each template, then New Layout.</summary>
        [ObservableProperty]
        public partial IReadOnlyList<LayoutMenuItem> SaveLayoutItems { get; private set; } = [];

        /// <summary>Whether there's a template to load.</summary>
        public bool HasLayouts => LoadLayoutItems.Count > 0;

        private void StartLayouts()
        {
            RefreshLayouts();
            _services.Layouts.Changed += OnLayoutsChanged;
        }

        private void StopLayouts() => _services.Layouts.Changed -= OnLayoutsChanged;

        private void OnLayoutsChanged(object? sender, EventArgs e) => RefreshLayouts();

        private void RefreshLayouts()
        {
            var names = _services.Layouts.Names;
            LoadLayoutItems = [.. names.Select(n => new LayoutMenuItem(n, LoadLayoutCommand))];
            SaveLayoutItems = [.. names.Select(n => new LayoutMenuItem(n, SaveLayoutToCommand)), new LayoutMenuItem("", SaveLayoutToCommand, IsNew: true)];
            // New Layout is unavailable once there are as many templates as there can be, as Manage Layouts' is.
            SaveLayoutToCommand.NotifyCanExecuteChanged();
        }

        /// <summary>
        /// Puts the template's lanes in place of those open, first asking when they aren't saved as a template, as they're
        /// lost for good. The template stays as it is, whatever's done to them after.
        /// </summary>
        [RelayCommand]
        private async Task LoadLayoutAsync(LayoutMenuItem? item)
        {
            if (item is not { IsNew: false })
                return;
            if (_services.Layouts.Get(item.Name) is not { } lanes)
            {
                Messages.Error(string.Format(CultureInfo.CurrentCulture, Strings.LayoutUnreadableFormat, item.Name));
                return;
            }

            if (!await LayoutPrompts.ConfirmLoadAsync(_confirmations, _services.Layouts, item.Name, Lanes.Layout.ToState()))
                return;
            if (!Lanes.Layout.Load(lanes))
                Messages.Error(string.Format(CultureInfo.CurrentCulture, Strings.LayoutUnreadableFormat, item.Name));
        }

        /// <summary>
        /// Saves the lanes open as a template: over the one chosen, once the user confirms it, or as a new one, after the
        /// others, asking its name.
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanSaveLayoutTo))]
        private async Task SaveLayoutToAsync(LayoutMenuItem? item)
        {
            if (!CanSaveLayoutTo(item))
                return;

            var layouts = _services.Layouts;
            if (item!.IsNew)
            {
                if (await _services.LayoutDialogs.AskNameAsync(LayoutNameViewModel.ForNew(layouts)) is { } name)
                    layouts.Add(name, Lanes.Layout.ToState());
                return;
            }

            await LayoutPrompts.OverwriteAsync(_confirmations, layouts, item.Name, Lanes.Layout.ToState);
        }

        // New Layout can't be saved once there are as many templates as there can be.
        private bool CanSaveLayoutTo(LayoutMenuItem? item) => item is not null && !(item.IsNew && _services.Layouts.IsFull);

        /// <summary>Shows Manage Layouts, whose New Layout and Overwrite Layout save the lanes open.</summary>
        [RelayCommand]
        private Task ManageLayoutsAsync() => _services.LayoutDialogs.ShowManageLayoutsAsync(_services.Layouts, () => Lanes.Layout.ToState());
    }
}
