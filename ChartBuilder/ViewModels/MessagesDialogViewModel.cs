using System.Collections.ObjectModel;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChartBuilder.ViewModels
{
    /// <summary>The Messages dialog, opened from the message bar: what the Editor has said this session, the latest picked.</summary>
    public sealed partial class MessagesDialogViewModel(EditorMessages log) : ViewModelBase
    {
        /// <summary>Every message, newest first, kept up to date while the dialog is open.</summary>
        public ObservableCollection<EditorMessage> Entries => log.Entries;

        public bool HasEntries => log.Entries.Count > 0;

        /// <summary>The message shown in full.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSelected))]
        public partial EditorMessage? Selected { get; set; } = log.Latest;

        public bool HasSelected => Selected is not null;
    }
}
