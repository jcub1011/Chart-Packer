using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ChartBuilder.Resources;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charting.Layout;
using ChartShared.Core.Lyrics;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    // Import lyrics and Export lyrics (issue 270): File menu commands. Each picks a file, asks its options in the lyrics
    // dialog, and says what it did on the message bar. An import is one undoable step.
    public sealed partial class EditorWindowViewModel
    {
        /// <summary>
        /// File → Import lyrics: reads an LRC, SRT, VTT, TTML, KAR or UltraStar file, or the lyrics tags of an audio file, and
        /// puts its words into a vocal part, replacing the part's own or merging with them.
        /// </summary>
        [RelayCommand]
        private async Task ImportLyricsAsync()
        {
            string? path = await _services.FilePicker.PickLyricsFileAsync(Strings.LyricsImportTitle);
            if (path is null)
                return;

            LyricDocument document;
            try
            {
                document = await Task.Run(() => LyricFiles.Read(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Messages.Error(string.Format(CultureInfo.CurrentCulture, Strings.LyricsImportFailedFormat, ex.Message));
                return;
            }

            var session = Editor.Session;
            var start = Enum.GetValues<VocalTrack>().FirstOrDefault(t => session.VocalContentOf(t) is { Lyrics.Count: > 0 });
            var dialog = LyricsDialogViewModel.ForImport(Path.GetFileName(path), document, start);
            if (!await _services.Authoring.ShowLyricsAsync(dialog))
                return;

            if (dialog.ShowsWordTimes && !dialog.UseWordTimes)
                document = document.WithoutWordTimes();
            var track = dialog.SelectedPart.Track;
            var result = LyricImport.Apply(session, track, document, dialog.Mode);
            if (!result.Done)
            {
                Messages.Error(string.Format(CultureInfo.CurrentCulture, Strings.LyricsImportFailedFormat, result.Message));
                return;
            }

            string part = dialog.SelectedPart.Label;
            Messages.Info(result.Skipped == 0
                ? string.Format(CultureInfo.CurrentCulture, Strings.LyricsImportedFormat, result.Words, part)
                : string.Format(CultureInfo.CurrentCulture, Strings.LyricsImportedSkippedFormat, result.Words, part, result.Skipped));
        }

        /// <summary>File → Export lyrics: writes a vocal part's lyrics as an LRC file, with or without A2 word timing.</summary>
        [RelayCommand]
        private async Task ExportLyricsAsync()
        {
            var session = Editor.Session;
            var withLyrics = Enum.GetValues<VocalTrack>().Where(t => session.VocalContentOf(t) is { Lyrics.Count: > 0 }).ToList();
            if (withLyrics.Count == 0)
            {
                Messages.Warning(Strings.LyricsExportNone);
                return;
            }

            var dialog = LyricsDialogViewModel.ForExport(withLyrics);
            if (!await _services.Authoring.ShowLyricsAsync(dialog))
                return;

            // Read after the dialog, as the chart can't change under it but is looked at as it is when it's confirmed.
            if (session.VocalContentOf(dialog.SelectedPart.Track) is not { } content)
                return;
            string lrc = LrcWriter.Write(content, tick => session.AudioAt(tick), dialog.UseWordTimes);
            if (lrc.Length == 0)
            {
                Messages.Warning(Strings.LyricsExportNone);
                return;
            }

            string? path = await _services.FilePicker.PickLrcSaveAsync(Strings.LyricsExportTitle, SuggestedLyricsName());
            if (path is null)
                return;

            try
            {
                await File.WriteAllTextAsync(path, lrc, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                Messages.Info(string.Format(CultureInfo.CurrentCulture, Strings.LyricsExportedFormat, path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Messages.Error(string.Format(CultureInfo.CurrentCulture, Strings.LyricsExportFailedFormat, ex.Message));
            }
        }

        // The song's title as a file name, for the save dialog to start with.
        private string SuggestedLyricsName()
        {
            string name = new([.. (Song.Title ?? string.Empty).Where(static c => !Path.GetInvalidFileNameChars().Contains(c))]);
            return (name.Trim().Length == 0 ? "lyrics" : name.Trim()) + ".lrc";
        }
    }
}
