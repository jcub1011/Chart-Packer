using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using Avalonia.Collections;
using ChartBuilder.Resources;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// The Messages dialog, opened from the message bar: what the Editor has said this session, the latest picked, narrowed
    /// by a search. Disposed once the dialog closes, so the session's messages stop updating it.
    /// </summary>
    public sealed partial class MessagesDialogViewModel : ViewModelBase, IDisposable
    {
        private readonly ObservableCollection<EditorMessage> _entries;

        /// <summary>The search the list is filtered by, trimmed.</summary>
        private string _query = "";

        public MessagesDialogViewModel(EditorMessages log)
            : this(log.Entries, log.Latest)
        {
        }

        /// <param name="entries">Every message, newest first, followed while the dialog is open.</param>
        /// <param name="selected">The message shown in full to begin with.</param>
        public MessagesDialogViewModel(ObservableCollection<EditorMessage> entries, EditorMessage? selected)
        {
            ArgumentNullException.ThrowIfNull(entries);
            _entries = entries;
            Selected = selected;
            _entries.CollectionChanged += OnEntriesChanged;
            RefreshShown();
        }

        /// <summary>The messages the search shows, newest first, kept up to date while the dialog is open.</summary>
        public AvaloniaList<EditorMessage> Shown { get; } = [];

        public bool HasEntries => _entries.Count > 0;

        /// <summary>There are messages, but the search matches none of them.</summary>
        public bool ShowNoMatches => HasEntries && Shown.Count == 0;

        public string NoMatchesText => string.Format(CultureInfo.CurrentCulture, Strings.MessagesNoMatchesFormat, _query);

        /// <summary>Narrows the list to the messages whose text, severity or time contains it.</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ClearSearchCommand))]
        public partial string? SearchText { get; set; }

        /// <summary>The message shown in full.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSelected))]
        public partial EditorMessage? Selected { get; set; }

        public bool HasSelected => Selected is not null;

        public void Dispose() => _entries.CollectionChanged -= OnEntriesChanged;

        partial void OnSearchTextChanged(string? value)
        {
            _query = value?.Trim() ?? "";
            RefreshShown();
        }

        private bool HasSearchText() => !string.IsNullOrWhiteSpace(SearchText);

        // Only with something to clear, so Escape in an empty search is left to close the window.
        [RelayCommand(CanExecute = nameof(HasSearchText))]
        private void ClearSearch() => SearchText = null;

        private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshShown();

        /// <summary>
        /// Lists the messages the search matches. A message picked that no longer shows gives way to the newest that does,
        /// so the one in full is always one in the list.
        /// </summary>
        private void RefreshShown()
        {
            ListRows.Replace(Shown, [.. _entries.Where(Matches)]);
            if (Selected is null || !Shown.Contains(Selected))
                Selected = Shown.FirstOrDefault();

            OnPropertyChanged(nameof(HasEntries));
            OnPropertyChanged(nameof(ShowNoMatches));
            OnPropertyChanged(nameof(NoMatchesText));
        }

        /// <summary>Whether the message's text, severity or time contains the search, ignoring case.</summary>
        private bool Matches(EditorMessage message)
        {
            if (_query.Length == 0)
                return true;

            return Contains(message.Text) || Contains(message.SeverityText) || Contains(message.TimeText);

            bool Contains(string text) => text.Contains(_query, StringComparison.CurrentCultureIgnoreCase);
        }
    }
}
