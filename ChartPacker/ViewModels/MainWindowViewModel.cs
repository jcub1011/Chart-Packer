using System.Globalization;
using System.Threading.Tasks;
using ChartPacker.Resources;
using ChartPacker.Services;
using CommunityToolkit.Mvvm.Input;

namespace ChartPacker.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase
    {
        private readonly SettingsService _settings;
        private readonly MetadataDraftStore _drafts;
        private readonly IConfirmationService _confirmation;
        private readonly ISettingsWindowService _settingsWindow;

        public MainWindowViewModel(
            LibraryViewModel library,
            SongDetailViewModel detail,
            SettingsService settings,
            MetadataDraftStore drafts,
            IConfirmationService confirmation,
            ISettingsWindowService settingsWindow,
            NoticeViewModel notice)
        {
            Library = library;
            Detail = detail;
            _settings = settings;
            _drafts = drafts;
            _confirmation = confirmation;
            _settingsWindow = settingsWindow;
            Notice = notice;

            Library.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(LibraryViewModel.SelectedEntries))
                    _ = Detail.ShowAsync(Library.SelectedEntries);
            };
        }

        public LibraryViewModel Library { get; }

        public SongDetailViewModel Detail { get; }

        /// <summary>What the latest download did, shown along the bottom for a few seconds.</summary>
        public NoticeViewModel Notice { get; }

        /// <summary>The pack/unpack results panel, always shown under both panels.</summary>
        public JobViewModel Job => Library.Job;

        /// <summary>Persisted width of the library panel. The view applies it on open and writes it back on close.</summary>
        public double LibraryPaneWidth
        {
            get => _settings.Settings.LibraryPaneWidth;
            set => _settings.Settings.LibraryPaneWidth = value;
        }

        [RelayCommand]
        private Task OpenSettingsAsync() => _settingsWindow.ShowAsync();

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
