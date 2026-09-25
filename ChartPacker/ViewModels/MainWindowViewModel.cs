using ChartPacker.Services;

namespace ChartPacker.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase
    {
        private readonly SettingsService _settings;

        public MainWindowViewModel(LibraryViewModel library, SongDetailViewModel detail, SettingsService settings)
        {
            Library = library;
            Detail = detail;
            _settings = settings;

            Library.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(LibraryViewModel.SelectedEntry))
                    _ = Detail.ShowAsync(Library.SelectedEntry);
                else if (e.PropertyName == nameof(LibraryViewModel.SelectedCount))
                    Detail.SelectionCount = Library.SelectedCount;
            };
        }

        public LibraryViewModel Library { get; }

        public SongDetailViewModel Detail { get; }

        /// <summary>The pack/unpack results panel, shown under both panels.</summary>
        public JobViewModel Job => Library.Job;

        /// <summary>Persisted width of the library panel. The view applies it on open and writes it back on close.</summary>
        public double LibraryPaneWidth
        {
            get => _settings.Settings.LibraryPaneWidth;
            set => _settings.Settings.LibraryPaneWidth = value;
        }
    }
}
