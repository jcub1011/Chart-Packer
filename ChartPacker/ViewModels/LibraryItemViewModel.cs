using ChartPacker.Core.Scanning;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChartPacker.ViewModels
{
    /// <summary>A song source in the library list.</summary>
    public sealed partial class LibraryItemViewModel(SongEntry entry, bool hasDrafts) : ObservableObject
    {
        public SongEntry Entry { get; } = entry;

        /// <summary>The song has metadata changes that haven't been saved.</summary>
        [ObservableProperty]
        private bool _hasDrafts = hasDrafts;
    }
}
