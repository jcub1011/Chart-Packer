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
    /// <summary>
    /// A vocal part the lyrics dialog can put lyrics into or write them from; with <paramref name="Duet"/>, two parts as an
    /// UltraStar duet: <paramref name="Track"/> its first singer, <paramref name="Duet"/> its second.
    /// </summary>
    public sealed record LyricsPartChoice(VocalTrack Track, string Label, VocalTrack? Duet = null)
    {
        /// <summary>Harmony 1 and Harmony 2, as a duet's first and second singers.</summary>
        public static LyricsPartChoice Harmonies { get; } = new(VocalTrack.Harmony1, Strings.LyricsDuetPart, VocalTrack.Harmony2);

        /// <summary>Whether it's two parts, a duet.</summary>
        public bool IsDuet => Duet is not null;
    }

    /// <summary>A file format lyrics can be exported as.</summary>
    public sealed record LyricsFormatChoice(LyricFormat Format, string Label);

    /// <summary>
    /// The options of Import lyrics and Export lyrics (issue 270). Importing asks which vocal part, or a duet's two, whether
    /// the part's own notes and lyrics are replaced or merged with, and whether the file's word times and pitches are used
    /// when it has them; exporting asks the format, UltraStar first, which part, or two as a duet, and, for LRC, whether it
    /// has A2 word times.
    /// </summary>
    public sealed partial class LyricsDialogViewModel : ViewModelBase
    {
        private readonly IReadOnlyList<VocalTrack> _withLyrics;
        private readonly string _importSummary;

        private LyricsDialogViewModel(
            bool isImport, IReadOnlyList<LyricsPartChoice> parts, IReadOnlyList<VocalTrack> withLyrics, string importSummary,
            bool hasWordTimes, bool hasPitches)
        {
            IsImport = isImport;
            _withLyrics = withLyrics;
            _importSummary = importSummary;
            Parts = parts;
            SelectedPart = parts[0];
            Formats = isImport ? [] : [new(LyricFormat.UltraStar, Strings.LyricsFormatUltraStar), new(LyricFormat.Lrc, Strings.LyricsFormatLrc)];
            SelectedFormat = isImport ? null : Formats[0];
            HasWordTimes = hasWordTimes;
            HasPitches = hasPitches;
            UseWordTimes = true;
            UsePitches = true;
        }

        /// <summary>The dialog for importing <paramref name="document"/>, read from the file <paramref name="fileName"/>.</summary>
        /// <param name="start">The part chosen to begin with, unless the file is a duet, when its two parts are.</param>
        public static LyricsDialogViewModel ForImport(string fileName, LyricDocument document, VocalTrack start)
        {
            bool duet = document.Singers.Count > 1;
            IReadOnlyList<LyricsPartChoice> parts = duet
                ? [LyricsPartChoice.Harmonies, .. PartChoices(System.Enum.GetValues<VocalTrack>())]
                : PartChoices(System.Enum.GetValues<VocalTrack>());
            string summary = string.Format(
                CultureInfo.CurrentCulture,
                duet ? Strings.LyricsImportDuetSummaryFormat : Strings.LyricsImportSummaryFormat,
                fileName,
                document.Lines.Count,
                document.WordCount);
            return new LyricsDialogViewModel(true, parts, [], summary, document.HasWordTimes, document.HasPitches)
            {
                SelectedPart = duet ? parts[0] : parts.First(p => !p.IsDuet && p.Track == start),
            };
        }

        /// <summary>The dialog for exporting one of <paramref name="withLyrics"/>, the parts that have lyrics, the first of them to begin with.</summary>
        public static LyricsDialogViewModel ForExport(IReadOnlyList<VocalTrack> withLyrics) =>
            new(false, ExportParts(withLyrics, LyricFormat.UltraStar), withLyrics, string.Empty, hasWordTimes: true, hasPitches: false);

        /// <summary>Whether the dialog imports: otherwise it exports.</summary>
        public bool IsImport { get; }

        public bool IsExport => !IsImport;

        public string Title => IsImport ? Strings.LyricsImportTitle : Strings.LyricsExportTitle;

        public string ConfirmText => IsImport ? Strings.LyricsImportButton : Strings.LyricsExportButton;

        /// <summary>What's being imported, or what the export writes.</summary>
        public string Summary => IsImport ? _importSummary
            : SelectedFormat?.Format == LyricFormat.Lrc ? Strings.LyricsExportSummary : Strings.LyricsExportUltraStarSummary;

        /// <summary>Exporting: the formats, UltraStar first. Importing: none.</summary>
        public IReadOnlyList<LyricsFormatChoice> Formats { get; }

        /// <summary>Exporting: the format written. Importing: null.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Format), nameof(Summary), nameof(ShowsWordTimes))]
        public partial LyricsFormatChoice? SelectedFormat { get; set; }

        /// <summary>The format written: UltraStar unless LRC is chosen.</summary>
        public LyricFormat Format => SelectedFormat?.Format ?? LyricFormat.UltraStar;

        /// <summary>The parts to choose from: exporting, a duet of the harmonies only when the format is UltraStar.</summary>
        [ObservableProperty]
        public partial IReadOnlyList<LyricsPartChoice> Parts { get; set; }

        [ObservableProperty]
        public partial LyricsPartChoice SelectedPart { get; set; }

        /// <summary>Whether the file has times for its words, which importing may use; exporting may always write them.</summary>
        public bool HasWordTimes { get; }

        /// <summary>Importing: whether the file's word times are used. Exporting: whether A2 word times are written.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(UsesPitches), nameof(ImportNote))]
        public partial bool UseWordTimes { get; set; }

        /// <summary>The label of the word times option.</summary>
        public string WordTimesText => IsImport ? Strings.LyricsUseWordTimes : Strings.LyricsIncludeWordTimes;

        /// <summary>Importing: when the file has word times. Exporting: when the format is LRC.</summary>
        public bool ShowsWordTimes => IsImport ? HasWordTimes : Format == LyricFormat.Lrc;

        /// <summary>Whether the file has pitches for its words, which importing may use.</summary>
        public bool HasPitches { get; }

        /// <summary>Importing: whether the file's pitches are used, while its word times are.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(UsesPitches), nameof(ImportNote))]
        public partial bool UsePitches { get; set; }

        public bool ShowsPitches => IsImport && HasPitches;

        /// <summary>Whether the import puts the words in at their pitches: it's chosen, and so are the word times they're sung at.</summary>
        public bool UsesPitches => ShowsPitches && UsePitches && UseWordTimes;

        /// <summary>The note under the replace and merge options: whether the words are sung or spoken.</summary>
        public string ImportNote => UsesPitches ? Strings.LyricsImportPitchedNote : Strings.LyricsImportNote;

        /// <summary>Importing: whether the part's own notes and lyrics are replaced; otherwise merged with.</summary>
        [ObservableProperty]
        public partial bool IsReplace { get; set; } = true;

        [ObservableProperty]
        public partial bool IsMerge { get; set; }

        public LyricImportMode Mode => IsReplace ? LyricImportMode.Replace : LyricImportMode.Merge;

        partial void OnIsReplaceChanged(bool value) => IsMerge = !value;

        partial void OnIsMergeChanged(bool value) => IsReplace = !value;

        partial void OnSelectedFormatChanged(LyricsFormatChoice? value)
        {
            if (IsImport)
                return;

            // LRC has no duets: the duet gives way to its first singer.
            var was = SelectedPart;
            Parts = ExportParts(_withLyrics, Format);
            SelectedPart = Parts.FirstOrDefault(p => p == was) ?? Parts.FirstOrDefault(p => p.Track == was?.Track) ?? Parts[0];
        }

        // The parts that have lyrics, and, for UltraStar when the first two harmonies both have, the two as a duet, last.
        private static IReadOnlyList<LyricsPartChoice> ExportParts(IReadOnlyList<VocalTrack> withLyrics, LyricFormat format)
        {
            var parts = PartChoices(withLyrics);
            return format == LyricFormat.UltraStar && withLyrics.Contains(VocalTrack.Harmony1) && withLyrics.Contains(VocalTrack.Harmony2)
                ? [.. parts, LyricsPartChoice.Harmonies]
                : parts;
        }

        private static IReadOnlyList<LyricsPartChoice> PartChoices(IEnumerable<VocalTrack> tracks) =>
            [.. tracks.Select(static t => new LyricsPartChoice(t, ChartLabels.VocalTrackName(t)))];
    }
}
