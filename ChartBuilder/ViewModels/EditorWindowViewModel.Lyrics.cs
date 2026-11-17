using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ChartBuilder.Resources;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charting.Layout;
using ChartShared.Core.Lyrics;
using ChartShared.Core.Scanning;
using ChartShared.Core.Text;
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
            else if (!dialog.UsesPitches)
                document = document.WithoutPitches();

            // A duet's singers go into the two harmonies; otherwise the first singer's lines into the part.
            var choice = dialog.SelectedPart;
            var singers = document.Singers;
            IReadOnlyList<(VocalTrack, LyricDocument)> parts = choice.Duet is { } second
                ? [(choice.Track, document.ForSinger(singers[0])), (second, document.ForSinger(singers.Count > 1 ? singers[1] : singers[0]))]
                : [(choice.Track, document.ForSinger(singers[0]))];
            var result = LyricImport.Apply(session, parts, dialog.Mode);
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

        /// <summary>
        /// File → Export lyrics: writes a vocal part's lyrics as an UltraStar song, with their pitches, or two harmonies as a
        /// duet; or as an LRC file, with or without A2 word timing.
        /// </summary>
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
            var choice = dialog.SelectedPart;
            VocalTrack[] tracks = choice.Duet is { } second ? [choice.Track, second] : [choice.Track];
            var contents = tracks.Select(session.VocalContentOf).ToList();
            if (contents.Any(static c => c is null))
                return;

            var format = dialog.Format;
            string text = format == LyricFormat.Lrc
                ? LrcWriter.Write(contents[0]!, tick => session.AudioAt(tick), dialog.UseWordTimes)
                : UltraStarWriter.Write([.. contents.Select(static c => c!)], tick => session.AudioAt(tick), session.TempoMap.Resolution, UltraStarSongOf());
            if (text.Length == 0)
            {
                Messages.Warning(Strings.LyricsExportNone);
                return;
            }

            string? path = await _services.FilePicker.PickLyricsSaveAsync(
                Strings.LyricsExportTitle, SuggestedLyricsName(format == LyricFormat.Lrc ? ".lrc" : ".txt"), format);
            if (path is null)
                return;

            try
            {
                await File.WriteAllTextAsync(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                Messages.Info(string.Format(CultureInfo.CurrentCulture, Strings.LyricsExportedFormat, path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Messages.Error(string.Format(CultureInfo.CurrentCulture, Strings.LyricsExportFailedFormat, ex.Message));
            }
        }

        // The song's title as a file name with extension, for the save dialog to start with.
        private string SuggestedLyricsName(string extension)
        {
            string name = new([.. (Song.Title ?? string.Empty).Where(static c => !Path.GetInvalidFileNameChars().Contains(c))]);
            return (name.Trim().Length == 0 ? "lyrics" : name.Trim()) + extension;
        }

        // What the UltraStar header says of the song: its song.ini as the next save leaves it, and its audio's file names.
        private UltraStarSong UltraStarSongOf()
        {
            string? Value(string key) => Song.EffectiveValue(key) is { } value && RichText.ToPlainText(value).Trim() is { Length: > 0 } plain ? plain : null;
            string? Stem(string stem) => Song.Tracks.FirstOrDefault(t => t.Stem == stem) is { } track ? Path.GetFileName(track.StoredName) : null;

            return new UltraStarSong(
                Title: Value("name") ?? Song.Entry?.DisplayName ?? UntitledSong,
                Artist: Value("artist") ?? UnknownArtist,
                Creator: Value("charter"),
                Genre: Value("genre"),
                Year: Value("year"),
                Audio: Stem(LaneStems.Song) ?? (Song.Tracks.Count > 0 ? Path.GetFileName(Song.Tracks[0].StoredName) : null),
                Vocals: Stem("vocals"));
        }

        // The header's title and artist when the song has none: a file's tag, so not translated.
        private const string UntitledSong = "Untitled";
        private const string UnknownArtist = "Unknown";
    }
}
