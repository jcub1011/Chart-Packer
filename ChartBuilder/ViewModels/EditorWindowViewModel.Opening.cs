using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ChartBuilder.Resources;
using ChartBuilder.Services;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Recovery;
using ChartShared.Core.Charts;
using ChartShared.Core.Launching;
using ChartShared.Core.Merging;
using ChartShared.Core.Scanning;
using ChartShared.UI.Services;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>A song on the File menu's Open recent list.</summary>
    public sealed class RecentSongItem(RecentSong song)
    {
        public string Title => song.Title;

        public string Path => song.Path;

        /// <summary>When it was last opened: today, yesterday, or the date.</summary>
        public string OpenedText
        {
            get
            {
                var opened = song.OpenedAt.ToLocalTime().Date;
                var today = DateTime.Today;
                if (opened == today)
                    return Strings.RecentToday;
                if (opened == today.AddDays(-1))
                    return Strings.RecentYesterday;
                return opened.ToString("d", CultureInfo.CurrentCulture);
            }
        }

        public string ToolTip => string.Format(CultureInfo.CurrentCulture, Strings.RecentOpenToolTipFormat, song.Path);
    }

    /// <summary>The Editor window's File menu's opening: the picker, a new chart, and the recent projects.</summary>
    public sealed partial class EditorWindowViewModel
    {
        // How many of a conversion warning's places it lists before saying how many more there are.
        private const int WarningTimesShown = 5;

        /// <summary>Whether a song is being opened, so nothing else can be started meanwhile.</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(NewChartCommand), nameof(OpenChartCommand), nameof(OpenRecentCommand))]
        public partial bool IsOpening { get; private set; }

        /// <summary>
        /// What's being opened, while a song is read and its Editor made, which the window shows over the chart with a
        /// spinner; null otherwise.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsLoading))]
        public partial string? LoadingText { get; private set; }

        public bool IsLoading => LoadingText is not null;

        /// <summary>The songs opened lately, the latest first.</summary>
        public ObservableCollection<RecentSongItem> Recent { get; } = [];

        public bool HasRecent => Recent.Count > 0;

        private bool CanOpen() => !IsOpening && !IsSaving;

        /// <summary>Closes what's open, once it's saved or discarded, and opens a new chart, with no audio.</summary>
        [RelayCommand(CanExecute = nameof(CanOpen))]
        private Task NewChartAsync() => StartNewChartAsync([]);

        /// <summary>
        /// Closes what's open, once it's saved or discarded, and opens a new chart (charter-spec C1), with
        /// <paramref name="audioFiles"/>, e.g. ones dropped on the window, as its stems. It has no folder until it's
        /// first saved. Never throws: it's started from a drop or a command, so a failure is said on the message bar.
        /// </summary>
        public async Task StartNewChartAsync(IReadOnlyList<string> audioFiles)
        {
            if (!CanOpen())
                return;
            if (!await ConfirmCloseAsync())
                return;

            try
            {
                _services.Editors.OpenNewChart(audioFiles);
            }
            catch (Exception ex)
            {
                // Nothing above this catches it, so anything not caught here would take the app down.
                Trace.TraceError($"Opening a new chart failed: {ex}");
                Messages.Error(string.Format(CultureInfo.CurrentCulture, Strings.UnexpectedErrorFormat, ex.Message));
            }
        }

        /// <summary>Picks a song folder or <c>.sng</c>, and opens it.</summary>
        [RelayCommand(CanExecute = nameof(CanOpen))]
        private async Task OpenChartAsync()
        {
            if (await _services.FilePicker.PickSongAsync(Strings.OpenSongPickerTitle) is { } path)
                await OpenSongAsync(path);
        }

        [RelayCommand(CanExecute = nameof(CanOpen))]
        private Task OpenRecentAsync(RecentSongItem? song) => song is null ? Task.CompletedTask : OpenSongAsync(song.Path);

        /// <summary>
        /// Opens the song at <paramref name="path"/> in the Editor window in place of this Editor, once its chart is saved
        /// or discarded (charter-spec C2): a folder, a <c>.sng</c>, or a file in a song folder. Never throws: it's
        /// started from the command line, a drop, the picker or the Recent list, so whatever stops it is said on the
        /// message bar instead.
        /// </summary>
        public async Task OpenSongAsync(string path)
        {
            if (!CanOpen())
                return;
            if (!await ConfirmCloseAsync())
                return;

            SongLock? songLock = null;
            bool ownLock = false;
            IsOpening = true;
            try
            {
                if (SongOpener.EntryOf(path) is not { } entry)
                {
                    ForgetRecent(path);
                    Messages.Error(string.Format(CultureInfo.CurrentCulture, Strings.OpenSongMissingFormat, path));
                    return;
                }

                // Opening this window's own song again reads it afresh, under the lock this window already holds.
                if (_songLock is { } held && SongLock.KeyOf(held.SongPath) == SongLock.KeyOf(entry.FullPath))
                {
                    songLock = held;
                    ownLock = true;
                }
                else
                {
                    // Held from before the song is read, so nothing else can open it to edit meanwhile.
                    songLock = SongLock.TryAcquire(entry.FullPath, SongLockHolder.ChartBuilder);
                    if (songLock is null)
                    {
                        // Said in a dialog: the song the user asked for isn't opened, which the message bar is too easy to miss.
                        await _confirmations.AlertAsync(
                            Strings.SongLockedTitle,
                            LockedText(entry.FullPath, Strings.OpenSongLockedFormat, Strings.OpenSongLockedInPackerFormat, Strings.OpenSongLockedElsewhereFormat));
                        return;
                    }
                }

                string opening = string.Format(CultureInfo.CurrentCulture, Strings.OpeningSongFormat, entry.DisplayName);
                Messages.Info(opening);
                LoadingText = opening;
                var reading = Stopwatch.StartNew();
                var result = await SongOpener.OpenAsync(entry);
                Logger.Information("Read {Song} in {ReadMs} ms", entry.DisplayName, reading.ElapsedMilliseconds);

                // An autosave of edits a crash or a shutdown left unsaved is offered first (charter-spec C2 item 7).
                RecoveryFile? recovered = null;
                if (result.Song is not null || result.Empty is not null)
                {
                    var (goOn, file) = await OfferRecoveryAsync(entry);
                    if (!goOn)
                        return;

                    recovered = file;
                }

                if (result.Empty is { } empty)
                {
                    // A song with neither chart file can be given one (charter-spec C1, New chart in this song).
                    if (recovered is null && !await _confirmations.ConfirmAsync(
                            Strings.NewChartInSongTitle,
                            string.Format(CultureInfo.CurrentCulture, Strings.NewChartInSongMessageFormat, entry.DisplayName),
                            Strings.NewChartInSongButton))
                    {
                        return;
                    }

                    result = result with { Song = SongOpener.NewChartIn(empty, EmptyChart()) };
                }

                if (result.Song is not { } song)
                {
                    if (result.Problem is { Kind: SongOpenProblemKind.Missing })
                        ForgetRecent(path);
                    Messages.Error(ProblemText(result.Problem!, entry.FullPath));
                    return;
                }

                // A .chart's conversion is shown before the editor opens, so the user can go back (charter-spec C2 item 3).
                if (song.ImportWarnings.Count > 0 && !await _confirmations.ConfirmAsync(
                        Strings.ImportTitle,
                        string.Format(CultureInfo.CurrentCulture, Strings.ImportMessageFormat, song.Title, string.Join(Environment.NewLine, song.ImportWarnings.Select(Describe))),
                        Strings.ImportConfirmButton))
                {
                    return;
                }

                RememberRecent(song);

                // A recovered chart is read again, from its autosave, off the UI thread as the song was.
                var recoveredSong = recovered is null ? null : await Task.Run(() => Autosave.Recover(song, recovered));

                // The new Editor is made on the UI thread, which holds the window up for a big chart, so what's opening is
                // on screen first.
                await _services.Editors.WaitForRenderAsync();

                // This Editor is disposed as the new one opens, so it mustn't let go of the lock it hands over as it is.
                if (ownLock)
                    _songLock = null;
                if (recovered is not null && recoveredSong is not null)
                    _services.Editors.OpenRecovered(recoveredSong, songLock, recovered.Key);
                else
                    _services.Editors.OpenSong(song, songLock);
                songLock = null;
            }
            catch (Exception ex)
            {
                // Nothing above this catches it, so anything not caught here would take the app down.
                Trace.TraceError($"Opening {path} failed: {ex}");
                Messages.Error(string.Format(CultureInfo.CurrentCulture, Strings.OpenSongUnreadableFormat, path, ex.Message));
            }
            finally
            {
                // Still held only if the editor didn't take it; this window's own lock stays with it.
                if (!ownLock)
                    songLock?.Dispose();
                else if (songLock is not null)
                    _songLock = songLock;
                LoadingText = null;
                IsOpening = false;
            }
        }

        /// <summary>
        /// Says that the song at <paramref name="path"/> is held by something else, in the format for who holds it:
        /// another Chart Builder, a Chart Packer with unsaved changes to it, or another that can't be told.
        /// </summary>
        private static string LockedText(string path, string builderFormat, string packerFormat, string elsewhereFormat)
        {
            string format = SongLock.HolderOf(path) switch
            {
                SongLockHolder.ChartBuilder => builderFormat,
                SongLockHolder.ChartPacker => packerFormat,
                _ => elsewhereFormat,
            };
            return string.Format(CultureInfo.CurrentCulture, format, path);
        }

        /// <summary>
        /// Offers the new charts a crash left autosaved but never saved, the latest first: Recover, Delete, or Cancel
        /// to leave the rest alone. Called once, as the app starts. Stops after a recovery, as the recovered chart opens
        /// in this Editor's place.
        /// </summary>
        public async Task OfferUnsavedChartsAsync()
        {
            while (_services.Recovery.NewCharts() is [var file, ..])
            {
                string when = file.Manifest.SavedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
                var choice = await _confirmations.ChooseAsync(
                    Strings.UnsavedRecoverTitle,
                    string.Format(CultureInfo.CurrentCulture, Strings.UnsavedRecoverMessageFormat, file.Manifest.Title, when),
                    Strings.RecoverButton,
                    Strings.DiscardUnsavedButton);
                if (choice == ConfirmChoice.First)
                {
                    LoadingText = string.Format(CultureInfo.CurrentCulture, Strings.OpeningSongFormat, file.Manifest.Title);
                    try
                    {
                        // Read off the UI thread, as a song is, then made into its Editor once what's opening is on screen.
                        var song = await Task.Run(() => Autosave.RecoverNew(file));
                        await _services.Editors.WaitForRenderAsync();
                        _services.Editors.OpenRecovered(song, null, file.Key);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Messages.Error(string.Format(CultureInfo.CurrentCulture, Strings.RecoverFailedFormat, ex.Message));
                    }
                    finally
                    {
                        LoadingText = null;
                    }

                    return;
                }

                if (choice == ConfirmChoice.Second)
                {
                    _services.Recovery.Delete(file.Key);
                    continue;
                }

                return;
            }
        }

        /// <summary>
        /// Offers the song's autosave, if it has one worth offering: Recover or Discard, warning when the song's chart has
        /// changed since. One that's the song's chart already is deleted without asking.
        /// </summary>
        /// <returns>
        /// Whether to go on opening the song, false when the user cancelled; and the autosave to recover, or null to open
        /// the song as it is, as when there's none or it was discarded.
        /// </returns>
        private async Task<(bool GoOn, RecoveryFile? File)> OfferRecoveryAsync(SongEntry entry)
        {
            if (_services.Recovery.Find(entry.FullPath) is not { } file)
                return (true, null);

            var offer = await RecoveryStore.AssessAsync(file, entry);
            if (offer == RecoveryOffer.Stale)
            {
                _services.Recovery.Delete(file.Key);
                return (true, null);
            }

            string when = file.Manifest.SavedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            string message = string.Format(CultureInfo.CurrentCulture, Strings.RecoverMessageFormat, entry.DisplayName, when);
            if (offer == RecoveryOffer.OfferChanged)
                message += Environment.NewLine + Environment.NewLine + Strings.RecoverChangedWarning;
            switch (await _confirmations.ChooseAsync(Strings.RecoverTitle, message, Strings.RecoverButton, Strings.RecoverDiscardButton))
            {
                case ConfirmChoice.First:
                    return (true, file);
                case ConfirmChoice.Second:
                    _services.Recovery.Delete(file.Key);
                    return (true, null);
                default:
                    return (false, null);
            }
        }

        private void RememberRecent(OpenedSong song)
        {
            _settings.Settings.RecentSongs = RecentSongs.Add(
                _settings.Settings.RecentSongs,
                new RecentSong { Path = song.Entry.FullPath, Title = song.Title, OpenedAt = DateTimeOffset.Now });
            _settings.Save();
            RefreshRecent();
        }

        /// <summary>Takes a song that's no longer there off the Recent list.</summary>
        private void ForgetRecent(string path)
        {
            if (!_settings.Settings.RecentSongs.Any(s => RecentSongs.SamePath(s.Path, path)))
                return;

            _settings.Settings.RecentSongs = RecentSongs.Remove(_settings.Settings.RecentSongs, path);
            _settings.Save();
            RefreshRecent();
        }

        /// <summary>Lists the Recent songs again, as saved.</summary>
        private void RefreshRecent()
        {
            Recent.Clear();
            foreach (var song in _settings.Settings.RecentSongs)
                Recent.Add(new RecentSongItem(song));
            OnPropertyChanged(nameof(HasRecent));
        }

        /// <summary>A new chart as New chart starts one: the default resolution, 120 BPM and 4/4, with a Lead part.</summary>
        private EditableChart EmptyChart() => EditableChart.CreateNew(new NewChartSettings(
            "", _settings.Settings.DefaultChartResolution, NewChartSettings.DefaultBpm, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.Guitar }, DrumsVariant.Pro));

        private static string ProblemText(SongOpenProblem problem, string path) => problem.Kind switch
        {
            SongOpenProblemKind.Missing => string.Format(CultureInfo.CurrentCulture, Strings.OpenSongMissingFormat, path),
            SongOpenProblemKind.NoChart => string.Format(CultureInfo.CurrentCulture, Strings.OpenSongNoChartFormat, path),
            SongOpenProblemKind.ChartTooLarge => string.Format(CultureInfo.CurrentCulture, Strings.OpenSongTooLargeFormat, path),
            _ => string.Format(CultureInfo.CurrentCulture, Strings.OpenSongUnreadableFormat, path, problem.Detail),
        };

        /// <summary>A conversion warning, with the first few places it happens, as song time in the <c>.chart</c>.</summary>
        private static string Describe(ChartMergeWarning warning)
        {
            string format = warning.Issue switch
            {
                ChartMergeIssue.SustainsChanged => Strings.ImportIssueSustainsFormat,
                ChartMergeIssue.MarkingsChanged => Strings.ImportIssueMarkingsFormat,
                ChartMergeIssue.NotesChanged => Strings.ImportIssueNotesChangedFormat,
                _ => Strings.ImportIssueNotesLostFormat,
            };
            string part = MidiPartCatalog.PartOf(warning.TrackName) is { IsKnown: true } known ? ChartLabels.PartName(known) : warning.TrackName;
            string text = string.Format(CultureInfo.CurrentCulture, format, part, ChartLabels.DifficultyName(warning.Difficulty));
            if (warning.Times.Count == 0)
                return text;

            string times = string.Join(", ", warning.Times.Take(WarningTimesShown).Select(t => SongTime.Format(t, decimals: 1)));
            if (warning.Times.Count > WarningTimesShown)
                times = string.Format(CultureInfo.CurrentCulture, Strings.ImportIssueMoreTimesFormat, times, warning.Times.Count - WarningTimesShown);
            return string.Format(CultureInfo.CurrentCulture, Strings.ImportIssueTimesFormat, text, times);
        }

        internal static string ProblemFormat(BuilderArgsProblemKind kind) => kind switch
        {
            BuilderArgsProblemKind.UnknownOption => Strings.ArgsUnknownOptionFormat,
            _ => Strings.ArgsUnexpectedArgumentFormat,
        };
    }
}
