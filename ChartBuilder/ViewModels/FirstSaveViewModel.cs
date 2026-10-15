using System.IO;
using System.Threading.Tasks;
using ChartBuilder.Resources;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Saving;
using ChartShared.UI.Services;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// A new chart's first save (charter-spec C1, The first save): where its song folder goes, a parent folder and a folder
    /// name. The folder name starts as "artist - name", from the name and artist set in the Chart Inspector, and an
    /// existing folder can never be chosen.
    /// </summary>
    public sealed partial class FirstSaveViewModel : ViewModelBase
    {
        private readonly IFolderPickerService _folders;

        /// <param name="folders">Picks the parent folder.</param>
        /// <param name="name">The song's name, as the Chart Inspector has it.</param>
        /// <param name="artist">The artist, as the Chart Inspector has it.</param>
        /// <param name="parentFolder">The parent folder to start with: the last one used, or Chart Packer's library folder.</param>
        public FirstSaveViewModel(IFolderPickerService folders, string name, string artist, string? parentFolder)
        {
            _folders = folders;
            Name = name;
            Artist = artist;
            ParentFolder = parentFolder ?? "";
            FolderName = NewChartFolder.NameFor(artist, name);
        }

        /// <summary>The song's name, <c>name</c> in <c>song.ini</c>.</summary>
        public string Name { get; }

        /// <summary>The artist, <c>artist</c> in <c>song.ini</c>.</summary>
        public string Artist { get; }

        /// <summary>The song being saved, as "Artist – Name".</summary>
        public string SongTitle => $"{Artist} – {Name}";

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

        /// <summary>Where the song folder is made, once the user chose Save.</summary>
        public NewSongTarget Target => new(ParentFolder, FolderName);

        /// <summary>Raised when the user chooses Save, with a place that has no problem.</summary>
        public event System.EventHandler? Saved;

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
    }
}
