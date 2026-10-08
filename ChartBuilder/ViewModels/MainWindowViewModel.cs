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
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Recovery;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Launching;
using ChartPacker.Core.Merging;
using ChartPacker.Core.Scanning;
using ChartPacker.UI.Services;
using ChartPacker.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>A song on the home screen's Recent list.</summary>
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

    /// <summary>
    /// A new chart that was autosaved but never saved, on the home screen until it's recovered or discarded (charter-spec
    /// C15 item 6).
    /// </summary>
    public sealed class UnsavedChartItem(RecoveryFile file)
    {
        public RecoveryFile File => file;

        public string Title => file.Manifest.Title;

        /// <summary>When it was autosaved.</summary>
        public string SavedText => string.Format(
            CultureInfo.CurrentCulture, Strings.UnsavedChartSavedFormat, file.Manifest.SavedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
    }

    /// <summary>Chart Builder's launcher: the home screen, which hands over to the Editor window (charter-spec §4.5).</summary>
    /// <param name="picker">Picks a song to open.</param>
    /// <param name="confirmations">Asks before a <c>.chart</c> whose conversion changes it is opened.</param>
    /// <param name="settings">Where the recent songs are kept.</param>
    /// <param name="lockFolder">Where the songs open are locked (<see cref="SongLock"/>).</param>
    /// <param name="recovery">Where the Editor's autosaves are, offered when their song opens.</param>
    public sealed partial class MainWindowViewModel(
        IEditorWindowService editors,
        ISettingsWindowService settingsWindow,
        IFilePickerService picker,
        IConfirmationService confirmations,
        BuilderSettingsService settings,
        string lockFolder,
        RecoveryStore recovery) : ViewModelBase
    {
        // How many of a conversion warning's places it lists before saying how many more there are.
        private const int WarningTimesShown = 5;

        /// <summary>What happened last, e.g. why the command line couldn't be used.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasMessage))]
        public partial string? Message { get; set; }

        /// <summary>Whether <see cref="Message"/> is a problem.</summary>
        [ObservableProperty]
        public partial bool IsError { get; set; }

        /// <summary>Whether a song is being opened, so nothing else can be started meanwhile.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsIdle))]
        [NotifyCanExecuteChangedFor(nameof(NewChartCommand), nameof(OpenChartCommand), nameof(OpenRecentCommand), nameof(RecoverUnsavedCommand), nameof(DiscardUnsavedCommand))]
        public partial bool IsOpening { get; private set; }

        public bool IsIdle => !IsOpening;

        public bool HasMessage => Message is not null;

        /// <summary>The songs opened lately, the latest first.</summary>
        public ObservableCollection<RecentSongItem> Recent { get; } = [.. settings.Settings.RecentSongs.Select(s => new RecentSongItem(s))];

        public bool HasRecent => Recent.Count > 0;

        /// <summary>New charts that were autosaved but never saved, as a crash left them, the latest first.</summary>
        public ObservableCollection<UnsavedChartItem> UnsavedCharts { get; } = [.. recovery.NewCharts().Select(f => new UnsavedChartItem(f))];

        public bool HasUnsavedCharts => UnsavedCharts.Count > 0;

        /// <summary>Opens an unsaved new chart from its autosave, unsaved until it's saved.</summary>
        [RelayCommand(CanExecute = nameof(IsIdle))]
        private void RecoverUnsaved(UnsavedChartItem? item)
        {
            if (item is null)
                return;
            try
            {
                editors.OpenRecovered(Autosave.RecoverNew(item.File), null, item.File.Key);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowError(string.Format(CultureInfo.CurrentCulture, Strings.RecoverFailedFormat, ex.Message));
            }
        }

        /// <summary>Deletes an unsaved new chart's autosave, after asking.</summary>
        [RelayCommand(CanExecute = nameof(IsIdle))]
        private async Task DiscardUnsavedAsync(UnsavedChartItem? item)
        {
            if (item is null || !await confirmations.ConfirmAsync(
                    Strings.DiscardUnsavedTitle,
                    string.Format(CultureInfo.CurrentCulture, Strings.DiscardUnsavedMessageFormat, item.Title),
                    Strings.DiscardUnsavedButton))
                return;
            recovery.Delete(item.File.Key);
            UnsavedCharts.Remove(item);
            OnPropertyChanged(nameof(HasUnsavedCharts));
        }

        /// <summary>Opens what the command line asks for. Called once, when the window is first shown.</summary>
        public void Start(BuilderArgs args)
        {
            switch (args)
            {
                case { Problem: { } problem }:
                    ShowError(string.Format(CultureInfo.CurrentCulture, ProblemFormat(problem.Kind), problem.Argument));
                    break;
                case { Start: BuilderStart.NewChart }:
                    StartNewChart(args.AudioFiles);
                    break;
                case { Start: BuilderStart.OpenSong, SongPath: { } path }:
                    _ = OpenSongAsync(path);
                    break;
                case { Start: BuilderStart.Calibrate }:
                    _ = editors.ShowCalibrationAsync();
                    break;
            }
        }

        /// <summary>Closes the launcher and opens a new chart in the Editor, with no audio.</summary>
        [RelayCommand(CanExecute = nameof(IsIdle))]
        private void NewChart() => StartNewChart([]);

        /// <summary>
        /// Closes the launcher and opens a new chart in the Editor (charter-spec C1), with <paramref name="audioFiles"/>,
        /// e.g. ones dropped on the window, as its stems. It has no folder until it's first saved. Never throws: it's
        /// started from the command line, a drop or a command, so a failure is shown on the home screen instead.
        /// </summary>
        public void StartNewChart(IReadOnlyList<string> audioFiles)
        {
            if (IsOpening)
                return;

            try
            {
                editors.OpenNewChart(audioFiles);
            }
            catch (Exception ex)
            {
                // Nothing above this catches it, so anything not caught here would take the app down.
                Trace.TraceError($"Opening a new chart failed: {ex}");
                ShowError(string.Format(CultureInfo.CurrentCulture, Strings.UnexpectedErrorFormat, ex.Message));
            }
        }

        /// <summary>Picks a song folder or <c>.sng</c>, and opens it.</summary>
        [RelayCommand(CanExecute = nameof(IsIdle))]
        private async Task OpenChartAsync()
        {
            if (await picker.PickSongAsync(Strings.OpenSongPickerTitle) is { } path)
                await OpenSongAsync(path);
        }

        [RelayCommand(CanExecute = nameof(IsIdle))]
        private Task OpenRecentAsync(RecentSongItem? song) => song is null ? Task.CompletedTask : OpenSongAsync(song.Path);

        /// <summary>
        /// Opens the song at <paramref name="path"/> in the editor (charter-spec C2): a folder, a <c>.sng</c>, or a file in
        /// a song folder. Never throws: it's started from the command line, a drop, the picker or the Recent list, so
        /// whatever stops it is shown on the home screen instead.
        /// </summary>
        public async Task OpenSongAsync(string path)
        {
            if (IsOpening)
                return;

            SongLock? songLock = null;
            IsOpening = true;
            try
            {
                if (SongOpener.EntryOf(path) is not { } entry)
                {
                    ForgetRecent(path);
                    ShowError(string.Format(CultureInfo.CurrentCulture, Strings.OpenSongMissingFormat, path));
                    return;
                }

                // Held from before the song is read, so another Chart Builder can't open it meanwhile.
                songLock = SongLock.TryAcquire(entry.FullPath, lockFolder);
                if (songLock is null)
                {
                    ShowError(string.Format(CultureInfo.CurrentCulture, Strings.OpenSongLockedFormat, entry.FullPath));
                    return;
                }

                ShowInfo(string.Format(CultureInfo.CurrentCulture, Strings.OpeningSongFormat, entry.DisplayName));
                var result = await SongOpener.OpenAsync(entry);

                // An autosave of edits a crash or a shutdown left unsaved is offered first (charter-spec C2 item 7).
                RecoveryFile? recovered = null;
                if (result.Song is not null || result.Empty is not null)
                {
                    var (goOn, file) = await OfferRecoveryAsync(entry);
                    if (!goOn)
                    {
                        Message = null;
                        return;
                    }

                    recovered = file;
                }

                if (result.Empty is { } empty)
                {
                    // A song with neither chart file can be given one (charter-spec C1, New chart in this song).
                    if (recovered is null && !await confirmations.ConfirmAsync(
                            Strings.NewChartInSongTitle,
                            string.Format(CultureInfo.CurrentCulture, Strings.NewChartInSongMessageFormat, entry.DisplayName),
                            Strings.NewChartInSongButton))
                    {
                        Message = null;
                        return;
                    }

                    result = result with { Song = SongOpener.NewChartIn(empty, EmptyChart()) };
                }

                if (result.Song is not { } song)
                {
                    if (result.Problem is { Kind: SongOpenProblemKind.Missing })
                        ForgetRecent(path);
                    ShowError(ProblemText(result.Problem!, entry.FullPath));
                    return;
                }

                // A .chart's conversion is shown before the editor opens, so the user can go back (charter-spec C2 item 3).
                if (song.ImportWarnings.Count > 0 && !await confirmations.ConfirmAsync(
                        Strings.ImportTitle,
                        string.Format(CultureInfo.CurrentCulture, Strings.ImportMessageFormat, song.Title, string.Join(Environment.NewLine, song.ImportWarnings.Select(Describe))),
                        Strings.ImportConfirmButton))
                {
                    Message = null;
                    return;
                }

                RememberRecent(song);
                if (recovered is not null)
                    editors.OpenRecovered(Autosave.Recover(song, recovered), songLock, recovered.Key);
                else
                    editors.OpenSong(song, songLock);
                songLock = null;
            }
            catch (Exception ex)
            {
                // Nothing above this catches it, so anything not caught here would take the app down.
                Trace.TraceError($"Opening {path} failed: {ex}");
                ShowError(string.Format(CultureInfo.CurrentCulture, Strings.OpenSongUnreadableFormat, path, ex.Message));
            }
            finally
            {
                // Still held only if the editor didn't take it.
                songLock?.Dispose();
                IsOpening = false;
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
            if (recovery.Find(entry.FullPath) is not { } file)
                return (true, null);

            var offer = await RecoveryStore.AssessAsync(file, entry);
            if (offer == RecoveryOffer.Stale)
            {
                recovery.Delete(file.Key);
                return (true, null);
            }

            string when = file.Manifest.SavedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            string message = string.Format(CultureInfo.CurrentCulture, Strings.RecoverMessageFormat, entry.DisplayName, when);
            if (offer == RecoveryOffer.OfferChanged)
                message += Environment.NewLine + Environment.NewLine + Strings.RecoverChangedWarning;
            switch (await confirmations.ChooseAsync(Strings.RecoverTitle, message, Strings.RecoverButton, Strings.RecoverDiscardButton))
            {
                case ConfirmChoice.First:
                    return (true, file);
                case ConfirmChoice.Second:
                    recovery.Delete(file.Key);
                    return (true, null);
                default:
                    return (false, null);
            }
        }

        [RelayCommand]
        private Task OpenSettingsAsync() => settingsWindow.ShowAsync();

        private void RememberRecent(OpenedSong song)
        {
            settings.Settings.RecentSongs = RecentSongs.Add(
                settings.Settings.RecentSongs,
                new RecentSong { Path = song.Entry.FullPath, Title = song.Title, OpenedAt = DateTimeOffset.Now });
            settings.Save();
        }

        /// <summary>Takes a song that's no longer there off the Recent list.</summary>
        private void ForgetRecent(string path)
        {
            if (!settings.Settings.RecentSongs.Any(s => RecentSongs.SamePath(s.Path, path)))
                return;

            settings.Settings.RecentSongs = RecentSongs.Remove(settings.Settings.RecentSongs, path);
            settings.Save();
            foreach (var item in Recent.Where(s => RecentSongs.SamePath(s.Path, path)).ToList())
                Recent.Remove(item);
            OnPropertyChanged(nameof(HasRecent));
        }

        private void ShowError(string message)
        {
            IsError = true;
            Message = message;
        }

        private void ShowInfo(string message)
        {
            IsError = false;
            Message = message;
        }

        /// <summary>A new chart as New chart starts one: the default resolution, 120 BPM and 4/4, with a Lead part.</summary>
        private EditableChart EmptyChart() => EditableChart.CreateNew(new NewChartSettings(
            "", settings.Settings.DefaultChartResolution, NewChartSettings.DefaultBpm, 4, 4, new HashSet<ChartInstrument> { ChartInstrument.Guitar }, DrumsVariant.Pro));

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

        private static string ProblemFormat(BuilderArgsProblemKind kind) => kind switch
        {
            BuilderArgsProblemKind.UnknownOption => Strings.ArgsUnknownOptionFormat,
            _ => Strings.ArgsUnexpectedArgumentFormat,
        };
    }
}
