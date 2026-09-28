using Avalonia.Controls;
using ChartPacker.ViewModels;

namespace ChartPacker.Views
{
    public partial class LibraryView : UserControl
    {
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
    }
}
