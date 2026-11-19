using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using ChartBuilder.Resources;
using ChartBuilder.Services;
using ChartShared.Core.Charting.Layout;
using ChartShared.UI.Services;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// Manage Layouts (issue 263): the layout templates, in the order the View menu lists them, and the selected one's
    /// actions. New Layout saves the lanes open as a template at the bottom; Overwrite Layout puts them in the selected
    /// one, the only way besides the View menu's Save Layout that a template changes.
    /// </summary>
    public sealed partial class ManageLayoutsViewModel : ViewModelBase, IDisposable
    {
        private readonly LayoutTemplateStore _store;
        private readonly Func<LaneDockState> _current;
        private readonly IConfirmationService _confirmations;
        private readonly ILayoutDialogService _dialogs;

        /// <param name="current">The lanes open, which a new or overwritten template is.</param>
        /// <param name="confirmations">Asks before a template is overwritten or deleted.</param>
        /// <param name="dialogs">Asks a new or renamed template's name.</param>
        public ManageLayoutsViewModel(LayoutTemplateStore store, Func<LaneDockState> current, IConfirmationService confirmations, ILayoutDialogService dialogs)
        {
            _store = store;
            _current = current;
            _confirmations = confirmations;
            _dialogs = dialogs;
            Layouts = store.Names;
            Selected = Layouts.FirstOrDefault();
            store.Changed += OnStoreChanged;
        }

        /// <summary>The templates' names, in order.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasLayouts))]
        public partial IReadOnlyList<string> Layouts { get; private set; }

        public bool HasLayouts => Layouts.Count > 0;

        /// <summary>The template selected, by name, or null for none.</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RenameCommand), nameof(MoveUpCommand), nameof(MoveDownCommand), nameof(OverwriteCommand), nameof(DeleteCommand))]
        public partial string? Selected { get; set; }

        private int SelectedIndex
        {
            get
            {
                for (int i = 0; Selected is not null && i < Layouts.Count; i++)
                {
                    if (string.Equals(Layouts[i], Selected, StringComparison.Ordinal))
                        return i;
                }

                return -1;
            }
        }

        private bool HasSelection() => SelectedIndex >= 0;

        private bool CanMoveUp() => SelectedIndex > 0;

        private bool CanMoveDown() => SelectedIndex is >= 0 and var i && i < Layouts.Count - 1;

        private bool CanAdd() => !_store.IsFull;

        /// <summary>Saves the lanes open as a new template, after the others, asking its name, and selects it.</summary>
        [RelayCommand(CanExecute = nameof(CanAdd))]
        private async Task NewAsync()
        {
            if (await _dialogs.AskNameAsync(LayoutNameViewModel.ForNew(_store)) is { } name && _store.Add(name, _current()))
                Selected = Layouts.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        }

        [RelayCommand(CanExecute = nameof(HasSelection))]
        private async Task RenameAsync()
        {
            if (Selected is not { } old)
                return;
            if (await _dialogs.AskNameAsync(LayoutNameViewModel.ForRename(_store, old)) is { } name && _store.Rename(old, name))
                Selected = Layouts.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        }

        [RelayCommand(CanExecute = nameof(CanMoveUp))]
        private void MoveUp() => Move(-1);

        [RelayCommand(CanExecute = nameof(CanMoveDown))]
        private void MoveDown() => Move(1);

        /// <summary>Puts the lanes open in the selected template, once the user confirms it.</summary>
        [RelayCommand(CanExecute = nameof(HasSelection))]
        private async Task OverwriteAsync()
        {
            if (Selected is { } name)
                await LayoutPrompts.OverwriteAsync(_confirmations, _store, name, _current);
        }

        /// <summary>Deletes the selected template, once the user confirms it, and selects the one in its place.</summary>
        [RelayCommand(CanExecute = nameof(HasSelection))]
        private async Task DeleteAsync()
        {
            if (Selected is not { } name)
                return;
            int index = SelectedIndex;
            if (!await _confirmations.ConfirmAsync(
                    Strings.LayoutDeleteTitle,
                    string.Format(CultureInfo.CurrentCulture, Strings.LayoutDeleteMessageFormat, name),
                    Strings.LayoutDeleteButton,
                    "TrashStroke")
                || !_store.Delete(name))
            {
                return;
            }

            Selected = Layouts.Count == 0 ? null : Layouts[Math.Min(index, Layouts.Count - 1)];
        }

        public void Dispose() => _store.Changed -= OnStoreChanged;

        private void Move(int offset)
        {
            if (Selected is { } name)
                _store.Move(name, offset);
        }

        // The list follows every change, the menu's as well as this window's, keeping the selection where it can.
        private void OnStoreChanged(object? sender, EventArgs e)
        {
            string? selected = Selected;
            Layouts = _store.Names;
            Selected = selected is null ? null : Layouts.FirstOrDefault(n => string.Equals(n, selected, StringComparison.OrdinalIgnoreCase));
            NewCommand.NotifyCanExecuteChanged();
            MoveUpCommand.NotifyCanExecuteChanged();
            MoveDownCommand.NotifyCanExecuteChanged();
        }
    }
}
