using System;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// A new chart's first save asking for the song's name and artist (charter-spec C1, The first save) when either isn't
    /// set yet. Whichever is set starts filled in; both are needed to go on.
    /// </summary>
    public sealed partial class SongInfoViewModel : ViewModelBase
    {
        /// <param name="name">The song's name as the chart has it, or empty when it's not set.</param>
        /// <param name="artist">The artist as the chart has it, or empty when it's not set.</param>
        public SongInfoViewModel(string name, string artist)
        {
            Name = name;
            Artist = artist;
        }

        /// <summary>The song's name, <c>name</c> in <c>song.ini</c>.</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
        public partial string Name { get; set; }

        /// <summary>The artist, <c>artist</c> in <c>song.ini</c>.</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
        public partial string Artist { get; set; }

        /// <summary>Whether the name is already set, so the dialog starts on the artist instead.</summary>
        public bool HasName => !string.IsNullOrWhiteSpace(Name);

        /// <summary>Raised when the user chooses Save with both set.</summary>
        public event EventHandler? Confirmed;

        [RelayCommand(CanExecute = nameof(CanSave))]
        private void Save() => Confirmed?.Invoke(this, EventArgs.Empty);

        private bool CanSave() => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Artist);
    }
}
