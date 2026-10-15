using System;
using System.Collections.ObjectModel;
using System.Globalization;
using ChartBuilder.Resources;
using ChartPacker.Core.Validation;
using ChartPacker.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChartBuilder.ViewModels
{
    /// <summary>Something the Editor said: why something failed or was refused, or what it did, and when.</summary>
    /// <param name="Key">Which running message it is, such as tap tempo's, so the next one replaces it; null for none.</param>
    public sealed record EditorMessage(Severity Severity, string Text, DateTime Time, string? Key = null)
    {
        /// <summary>The text on one line, for the message bar.</summary>
        public string Summary => Text.ReplaceLineEndings(" ");

        public string TimeText => Time.ToString("HH:mm:ss", CultureInfo.CurrentCulture);

        public string SeverityText => Severity switch
        {
            Severity.Error => Strings.MessageSeverityError,
            Severity.Warning => Strings.MessageSeverityWarning,
            _ => Strings.MessageSeverityInfo,
        };

        public bool IsError => Severity == Severity.Error;

        public bool IsWarning => Severity == Severity.Warning;

        public bool IsInfo => Severity == Severity.Info;

        /// <summary>The message as one line of text, for the clipboard.</summary>
        public override string ToString() => $"{TimeText} {SeverityText}: {Text}";
    }

    /// <summary>
    /// What the Editor has said this session, newest first: the message bar along the bottom shows the latest, and the
    /// Messages dialog all of them.
    /// </summary>
    public sealed partial class EditorMessages : ViewModelBase
    {
        /// <summary>The most kept; the oldest go first.</summary>
        public const int Capacity = 200;

        /// <summary>Every message said this session, newest first.</summary>
        public ObservableCollection<EditorMessage> Entries { get; } = [];

        /// <summary>The message the bar shows, which stays until another is said. Null before the first.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasLatest))]
        public partial EditorMessage? Latest { get; private set; }

        public bool HasLatest => Latest is not null;

        public void Info(string text, string? key = null) => Post(Severity.Info, text, key);

        public void Warning(string text, string? key = null) => Post(Severity.Warning, text, key);

        public void Error(string text, string? key = null) => Post(Severity.Error, text, key);

        /// <summary>
        /// Says <paramref name="text"/>. The same message said again, such as a refusal repeated as a note is dragged, or
        /// the next of a running message with the same <paramref name="key"/>, replaces the latest rather than adding one.
        /// </summary>
        public void Post(Severity severity, string text, string? key = null)
        {
            ArgumentNullException.ThrowIfNull(text);
            var message = new EditorMessage(severity, text, DateTime.Now, key);
            if (Entries.Count > 0 && Repeats(Entries[0], message))
            {
                Entries[0] = message;
            }
            else
            {
                Entries.Insert(0, message);
                while (Entries.Count > Capacity)
                    Entries.RemoveAt(Entries.Count - 1);
            }

            Latest = message;
        }

        private static bool Repeats(EditorMessage latest, EditorMessage next) =>
            next.Key is not null ? latest.Key == next.Key : latest.Key is null && latest.Severity == next.Severity && latest.Text == next.Text;
    }
}
