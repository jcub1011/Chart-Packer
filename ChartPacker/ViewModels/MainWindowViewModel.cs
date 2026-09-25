using System.Globalization;
using System.Threading.Tasks;
using ChartPacker.Resources;
using ChartPacker.Services;

namespace ChartPacker.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase
    {
        private readonly SettingsService _settings;
        private readonly MetadataDraftStore _drafts;
        private readonly IConfirmationService _confirmation;

        public MainWindowViewModel(
            LibraryViewModel library,
            SongDetailViewModel detail,
            SettingsService settings,
            MetadataDraftStore drafts,
            IConfirmationService confirmation)
        {
            Library = library;
            Detail = detail;
            _settings = settings;
            _drafts = drafts;
            _confirmation = confirmation;

            Library.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(LibraryViewModel.SelectedEntries))
                    _ = Detail.ShowAsync(Library.SelectedEntries);
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

        /// <summary>Some song has metadata changes that closing would lose.</summary>
        public bool HasUnsavedDrafts => _drafts.DirtyCount > 0;

        /// <summary>Asks whether to close and lose the unsaved metadata changes.</summary>
        public Task<bool> ConfirmDiscardDraftsAsync()
        {
            int count = _drafts.DirtyCount;
            string message = count == 1
                ? Strings.CloseUnsavedOne
                : string.Format(CultureInfo.CurrentCulture, Strings.CloseUnsavedFormat, count);
            return _confirmation.ConfirmAsync(Strings.CloseUnsavedTitle, message, Strings.DiscardAndCloseButton);
        }
    }
}
