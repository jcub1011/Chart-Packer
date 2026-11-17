using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ChartBuilder.Resources;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charting.Layout;
using ChartShared.Core.Lyrics;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChartBuilder.ViewModels
{
    /// <summary>A vocal part the lyrics dialog can put lyrics into or write them from.</summary>
    public sealed record LyricsPartChoice(VocalTrack Track, string Label);

    /// <summary>
    /// The options of Import lyrics and Export lyrics (issue 270). Importing asks which vocal part, whether the part's own
    /// notes and lyrics are replaced or merged with, and whether the file's word times are used when it has them; exporting
    /// asks which part, and whether the LRC has A2 word times.
    /// </summary>
    public sealed partial class LyricsDialogViewModel : ViewModelBase
    {
        private LyricsDialogViewModel(bool isImport, IReadOnlyList<LyricsPartChoice> parts, string summary, bool hasWordTimes)
        {
            IsImport = isImport;
            Parts = parts;
            SelectedPart = parts[0];
            Summary = summary;
            HasWordTimes = hasWordTimes;
            UseWordTimes = true;
        }

        /// <summary>The dialog for importing <paramref name="document"/>, read from the file <paramref name="fileName"/>.</summary>
        /// <param name="start">The part chosen to begin with.</param>
        public static LyricsDialogViewModel ForImport(string fileName, LyricDocument document, VocalTrack start)
        {
            var parts = PartChoices(System.Enum.GetValues<VocalTrack>());
            var summary = string.Format(CultureInfo.CurrentCulture, Strings.LyricsImportSummaryFormat, fileName, document.Lines.Count, document.WordCount);
            return new LyricsDialogViewModel(true, parts, summary, document.HasWordTimes) { SelectedPart = parts.First(p => p.Track == start) };
        }

        /// <summary>The dialog for exporting one of <paramref name="withLyrics"/>, the parts that have lyrics, the first of them to begin with.</summary>
        public static LyricsDialogViewModel ForExport(IReadOnlyList<VocalTrack> withLyrics) =>
            new(false, PartChoices(withLyrics), Strings.LyricsExportSummary, hasWordTimes: true);

        /// <summary>Whether the dialog imports: otherwise it exports.</summary>
        public bool IsImport { get; }

        public bool IsExport => !IsImport;

        public string Title => IsImport ? Strings.LyricsImportTitle : Strings.LyricsExportTitle;

        public string ConfirmText => IsImport ? Strings.LyricsImportButton : Strings.LyricsExportButton;

        /// <summary>What's being imported, or exported.</summary>
        public string Summary { get; }

        public IReadOnlyList<LyricsPartChoice> Parts { get; }

        [ObservableProperty]
        public partial LyricsPartChoice SelectedPart { get; set; }

        /// <summary>Whether the file has times for its words, which importing may use; exporting may always write them.</summary>
        public bool HasWordTimes { get; }

        /// <summary>Importing: whether the file's word times are used. Exporting: whether A2 word times are written.</summary>
        [ObservableProperty]
        public partial bool UseWordTimes { get; set; }

        /// <summary>The label of the word times option.</summary>
        public string WordTimesText => IsImport ? Strings.LyricsUseWordTimes : Strings.LyricsIncludeWordTimes;

        public bool ShowsWordTimes => IsExport || HasWordTimes;

        /// <summary>Importing: whether the part's own notes and lyrics are replaced; otherwise merged with.</summary>
        [ObservableProperty]
        public partial bool IsReplace { get; set; } = true;

        [ObservableProperty]
        public partial bool IsMerge { get; set; }

        public LyricImportMode Mode => IsReplace ? LyricImportMode.Replace : LyricImportMode.Merge;

        partial void OnIsReplaceChanged(bool value) => IsMerge = !value;

        partial void OnIsMergeChanged(bool value) => IsReplace = !value;

        private static IReadOnlyList<LyricsPartChoice> PartChoices(IEnumerable<VocalTrack> tracks) =>
            [.. tracks.Select(static t => new LyricsPartChoice(t, ChartLabels.VocalTrackName(t)))];
    }
}
