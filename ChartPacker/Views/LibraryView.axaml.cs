using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using ChartPacker.Services;
using ChartPacker.ViewModels;
using ChartShared.UI.Keybinds;

namespace ChartPacker.Views
{
    public partial class LibraryView : UserControl
    {
        // The search key, anywhere in the window, goes to the search box, while the list is in one.
        private IDisposable? _searchKey;

        public LibraryView()
        {
            InitializeComponent();

            // The list only creates rows for what's on screen; the view model loads album art for those.
            EntryList.ContainerPrepared += (_, e) =>
            {
                if (DataContext is LibraryViewModel vm && e.Container.DataContext is LibraryItemViewModel item)
                    vm.OnItemRealized(item);
            };
            EntryList.ContainerClearing += (_, e) =>
            {
                if (DataContext is LibraryViewModel vm && e.Container.DataContext is LibraryItemViewModel item)
                    vm.OnItemUnrealized(item);
            };
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _searchKey = KeyDispatcher.Of(TopLevel.GetTopLevel(this))?.On(PackerKeys.Search, FocusSearch);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _searchKey?.Dispose();
            _searchKey = null;
            base.OnDetachedFromVisualTree(e);
        }

        private bool FocusSearch()
        {
            if (!SearchBox.IsEffectivelyVisible || !SearchBox.Focus(NavigationMethod.Tab))
                return false;

            SearchBox.SelectAll();
            return true;
        }
    }
}
