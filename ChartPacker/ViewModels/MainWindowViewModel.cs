using ChartPacker.Services;

namespace ChartPacker.ViewModels
{
    public partial class MainWindowViewModel(LibraryViewModel library, SettingsService settings) : ViewModelBase
    {
        public LibraryViewModel Library { get; } = library;

        /// <summary>Persisted width of the library panel. The view applies it on open and writes it back on close.</summary>
        public double LibraryPaneWidth
        {
            get => settings.Settings.LibraryPaneWidth;
            set => settings.Settings.LibraryPaneWidth = value;
        }
    }
}
