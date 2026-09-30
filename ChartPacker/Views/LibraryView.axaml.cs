using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using ChartPacker.ViewModels;
using CommunityToolkit.Mvvm.Input;

namespace ChartPacker.Views
{
    public partial class LibraryView : UserControl
    {
        // Ctrl+F anywhere in the window goes to the search box.
        private readonly KeyBinding _focusSearch;

        public LibraryView()
        {
            InitializeComponent();

            _focusSearch = new KeyBinding
            {
                Gesture = new KeyGesture(Key.F, KeyModifiers.Control),
                Command = new RelayCommand(FocusSearch),
            };

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
            TopLevel.GetTopLevel(this)?.KeyBindings.Add(_focusSearch);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            TopLevel.GetTopLevel(this)?.KeyBindings.Remove(_focusSearch);
            base.OnDetachedFromVisualTree(e);
        }

        private void FocusSearch()
        {
            if (SearchBox.IsEffectivelyVisible && SearchBox.Focus(NavigationMethod.Tab))
                SearchBox.SelectAll();
        }
    }
}
