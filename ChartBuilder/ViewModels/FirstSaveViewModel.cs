using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ChartBuilder.Resources;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Saving;
using ChartPacker.Services;
using ChartPacker.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// A new chart's first save (charter-spec C1, The first save): the song's name and artist, and where its song folder
    /// goes, a parent folder and a folder name. The folder name follows "artist - name" until it's edited, and an existing
    /// folder can never be chosen.
    /// </summary>
    public sealed partial class FirstSaveViewModel : ViewModelBase
    {
        private readonly IFolderPickerService _folders;

        // Whether the user has typed a folder name of their own, which the name and artist then leave alone.
        private bool _folderNameEdited;
        private bool _following;

        /// <param name="folders">Picks the parent folder.</param>
        /// <param name="name">The song's name to start with, such as the first stem's title tag.</param>
        /// <param name="artist">The artist to start with.</param>
        /// <param name="parentFolder">The parent folder to start with: the last one used, or Chart Packer's library folder.</param>
        public FirstSaveViewModel(IFolderPickerService folders, string name, string artist, string? parentFolder)
        {
            _folders = folders;
            Name = name;
            Artist = artist;
            ParentFolder = parentFolder ?? "";
            FollowName();
        }

        /// <summary>The song's name, <c>name</c> in <c>song.ini</c>.</summary>
        [ObservableProperty]
        public partial string Name { get; set; }

        /// <summary>The artist, <c>artist</c> in <c>song.ini</c>.</summary>
        [ObservableProperty]
        public partial string Artist { get; set; }

        /// <summary>The folder the song's folder is made in.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Problem), nameof(HasProblem), nameof(TargetPath))]
        [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
        public partial string ParentFolder { get; set; }

        /// <summary>The song folder's name.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Problem), nameof(HasProblem), nameof(TargetPath))]
        [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
        public partial string FolderName { get; set; } = "";

        /// <summary>Where the song folder will be made.</summary>
        public string TargetPath => string.IsNullOrWhiteSpace(ParentFolder) || FolderName.Length == 0 ? "" : Path.Combine(ParentFolder, FolderName);

        /// <summary>Why the song folder can't be made there, or null when it can.</summary>
        public string? Problem => NewChartFolder.ProblemOf(ParentFolder, FolderName) switch
        {
            null => null,
            { Kind: NewChartFolderProblemKind.NoParent } => Strings.FirstSaveNoParent,
            { Kind: NewChartFolderProblemKind.ParentMissing } => Strings.FirstSaveParentMissing,
            { Kind: NewChartFolderProblemKind.Exists } => Strings.FirstSaveExists,
            { Reason: { } reason } => reason,
            _ => Strings.FirstSaveInvalidName,
        };

        public bool HasProblem => Problem is not null;

        /// <summary>The values the save writes to <c>song.ini</c>: the name and artist, those that aren't empty.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> Values
        {
            get
            {
                var values = new List<KeyValuePair<string, string>>();
                if (Name.Trim() is { Length: > 0 } name)
                    values.Add(KeyValuePair.Create("name", name));
                if (Artist.Trim() is { Length: > 0 } artist)
                    values.Add(KeyValuePair.Create("artist", artist));
                return values;
            }
        }

        /// <summary>Where the song folder is made, once the user chose Save.</summary>
        public NewSongTarget Target => new(ParentFolder, FolderName);

        /// <summary>Raised when the user chooses Save, with a place that has no problem.</summary>
        public event System.EventHandler? Saved;

        partial void OnNameChanged(string value) => FollowName();

        partial void OnArtistChanged(string value) => FollowName();

        partial void OnFolderNameChanged(string value)
        {
            if (!_following)
                _folderNameEdited = value != NewChartFolder.NameFor(Artist ?? "", Name ?? "");
        }

        [RelayCommand]
        private async Task BrowseAsync()
        {
            string? folder = await _folders.PickFolderAsync(Strings.FirstSaveBrowseTitle, string.IsNullOrWhiteSpace(ParentFolder) ? null : ParentFolder);
            if (folder is not null)
                ParentFolder = folder;
        }

        [RelayCommand(CanExecute = nameof(CanSave))]
        private void Save() => Saved?.Invoke(this, System.EventArgs.Empty);

        private bool CanSave() => !HasProblem;

        // The folder name follows the name and artist until the user types their own.
        private void FollowName()
        {
            if (_folderNameEdited || Name is null || Artist is null)
                return;
            _following = true;
            try
            {
                FolderName = NewChartFolder.NameFor(Artist, Name);
            }
            finally
            {
                _following = false;
            }
        }
    }
}
