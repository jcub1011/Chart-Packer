using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ChartBuilder.Resources;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Saving;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Jobs;
using ChartPacker.Core.Launching;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Scanning;
using ChartPacker.Core.Settings;
using ChartPacker.Core.Validation;
using ChartPacker.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace ChartBuilder.ViewModels
{
    /// <summary>The Editor window's saving: Save, a new chart's first save, Save and pack, Export copy, and closing.</summary>
    /// <remarks>
    /// A save takes its snapshot of the chart on the UI thread, then writes and checks it and saves it off it, so editing
    /// goes on meanwhile; the chart counts as saved as it was at the snapshot (charter-spec C17 item 1, §4.6).
    /// </remarks>
    public sealed partial class EditorWindowViewModel
    {
        private static ILogger Logger => Log.ForContext<EditorWindowViewModel>();

        /// <summary>Whether a save, a pack or an export is running.</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(SaveAndPackCommand), nameof(ExportCopyCommand))]
        public partial bool IsSaving { get; private set; }

        /// <summary>
        /// Whether the chart, or the song around it, has edits that aren't saved: closing asks first. A chart recovered from
        /// an autosave has, until it's saved.
        /// </summary>
        public bool HasUnsavedChanges => (_undo.IsDirty || Song.HasUserChanges) && !_discarded;

        // Whether the user chose to close without saving, so the edits no longer count as unsaved.
        private bool _discarded;

        /// <summary>What Save and pack does, or why it can't run, for its tooltip.</summary>
        public string SaveAndPackToolTip => Song.Entry is { Kind: SongEntryKind.SngFile } ? Strings.SaveAndPackSngToolTip
            : Findings.ErrorCount > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.SaveAndPackErrorsToolTipFormat, Findings.ErrorCount)
            : Strings.SaveAndPackToolTip;

        /// <summary>Why Export copy can't run, for its tooltip.</summary>
        public string ExportCopyToolTip => Song.IsNew ? Strings.ExportCopyNewChartToolTip : Strings.ExportCopyToolTip;

        /// <summary>
        /// Asks Save, Discard or Cancel when the chart has unsaved edits (charter-spec C15 item 7), and saves if asked to.
        /// </summary>
        /// <returns>Whether the window can close: nothing unsaved, Discard, or a save that went through.</returns>
        public async Task<bool> ConfirmCloseAsync()
        {
            if (IsSaving)
                return false;
            if (!HasUnsavedChanges)
                return true;

            var choice = await _confirmations.ChooseAsync(
                Strings.CloseUnsavedTitle,
                string.Format(CultureInfo.CurrentCulture, Strings.CloseUnsavedMessageFormat, Song.Title ?? Strings.EditorUntitledChart),
                Strings.SaveButton,
                Strings.DiscardButton);
            switch (choice)
            {
                case ConfirmChoice.First:
                    // Edits made while the save ran aren't in it, so the window stays open to be asked about them.
                    return await SaveCoreAsync() && !HasUnsavedChanges;
                case ConfirmChoice.Second:
                    // The edits are discarded, so their autosave goes with them, once any being written is done.
                    await WaitForAutosaveAsync();
                    DeleteRecovery();
                    _discarded = true;
                    return true;
                default:
                    return false;
            }
        }

        [RelayCommand(CanExecute = nameof(CanSave))]
        private Task SaveAsync() => SaveCoreAsync();

        private bool CanSave() => !IsSaving;

        [RelayCommand(CanExecute = nameof(CanSaveAndPack))]
        private async Task SaveAndPackAsync()
        {
            if ((HasUnsavedChanges || Song.IsNew || Song.PendingChanges().Count > 0 || Song.Media.Count > 0) && !await SaveCoreAsync())
                return;
            if (Song.Entry is not { Kind: SongEntryKind.SongFolder } entry)
                return;

            // An Error is something the games read differently from what's written, so it stops packing (charter-spec C16
            // item 4); the findings are brought up to date with the save first.
            await Findings.SettleAsync();
            if (Findings.ErrorCount > 0)
            {
                Messages.Error(string.Format(CultureInfo.CurrentCulture, Strings.SaveAndPackErrorsFormat, Findings.ErrorCount));
                return;
            }

            var settings = ChartPackerSettingsFile.PackSettings(_services.ChartPackerSettingsFile);
            var (scanRoot, _, layout) = settings.PackOf(entry);

            // The pack folder is Chart Builder's own default: when set, packing goes there without asking,
            // otherwise it asks, starting at the song's parent folder.
            string? destination = _settings.Settings.DefaultPackFolder;
            if (string.IsNullOrWhiteSpace(destination))
            {
                string songPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry.FullPath));
                string projectFolder = Path.GetDirectoryName(songPath) ?? songPath;
                destination = await _services.FolderPicker.PickFolderAsync(Strings.SaveAndPackPickerTitle, projectFolder);
                if (destination is null)
                    return;
            }

            IsSaving = true;
            try
            {
                JobItemResult? result = null;
                await Task.Run(() => SongJob.RunAsync(JobKind.Pack, [entry], scanRoot, destination, settings.Policy, new Reporter(r => result = r), layout));
                var (severity, message) = PackNotice(result);
                Messages.Post(severity, message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Messages.Error(string.Format(CultureInfo.CurrentCulture, Strings.PackFailedFormat, ex.Message));
            }
            finally
            {
                IsSaving = false;
            }
        }

        private bool CanSaveAndPack() => !IsSaving && Song.Entry is not { Kind: SongEntryKind.SngFile } && Findings.ErrorCount == 0;

        [RelayCommand(CanExecute = nameof(CanExportCopy))]
        private async Task ExportCopyAsync()
        {
            if (Song.Entry is not { } entry)
                return;
            string? parent = await _services.FolderPicker.PickFolderAsync(Strings.ExportCopyPickerTitle);
            if (parent is null)
                return;

            string name = entry.Kind == SongEntryKind.SngFile ? Path.GetFileNameWithoutExtension(entry.FullPath) : Path.GetFileName(entry.FullPath);
            string target = Path.Combine(parent, name);
            // The copy is written as a save would write it, [end] after the last note, but the chart open isn't changed.
            var snapshot = Song.Chart.Clone();
            ChartSaveFixups.MoveEnd(snapshot)?.Apply(snapshot);
            var request = ChartSaver.RequestFor(Song, snapshot, Song.PendingChanges(), Song.IsImport ? Song.Opened!.ChartName : null);
            IsSaving = true;
            try
            {
                var written = await Task.Run(() => ChartSaver.Check(request));
                if (!written.IsValid)
                {
                    Messages.Error(ReadBackFailedNotice(written));
                    return;
                }

                var result = await Task.Run(() => ChartSaver.ExportAsync(entry, request, written, target));
                if (result.Extracted)
                    Messages.Info(string.Format(CultureInfo.CurrentCulture, Strings.ExportedFormat, target));
                else
                    Messages.Error(string.Format(CultureInfo.CurrentCulture, Strings.ExportFailedFormat, FirstError(result.Findings) ?? ""));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Messages.Error(string.Format(CultureInfo.CurrentCulture, Strings.ExportFailedFormat, ex.Message));
            }
            finally
            {
                IsSaving = false;
            }
        }

        private bool CanExportCopy() => !IsSaving && !Song.IsNew;

        /// <summary>
        /// Saves the chart and its pending <c>song.ini</c> changes (charter-spec C17 item 1): to the song through its draft,
        /// or for a new chart, as its song folder, asking where first.
        /// </summary>
        /// <returns>Whether it was saved; why not is on the message bar, or the user cancelled.</returns>
        private async Task<bool> SaveCoreAsync()
        {
            if (IsSaving)
                return false;

            // A gesture's preview is taken back, so it's never saved.
            Editor.Session.Tools.Cancel();
            if (Song.IsNew)
                return await FirstSaveAsync();

            var changes = Song.PendingChanges();
            string? removeChart = null;
            if (Song.IsImport)
            {
                // A .chart's first save replaces it with a notes.mid, so the games don't pick between two charts (C2 item 3).
                var choice = await _confirmations.ChooseAsync(
                    Strings.SaveImportTitle,
                    string.Format(CultureInfo.CurrentCulture, Strings.SaveImportMessageFormat, Song.Opened!.ChartName, ChangesText(changes, Song.Media)),
                    Strings.SaveImportRemoveButton,
                    Strings.SaveImportKeepButton);
                if (choice == ConfirmChoice.Cancel)
                    return false;
                if (choice == ConfirmChoice.First)
                    removeChart = Song.Opened.ChartName;
            }
            else if ((changes.Count > 0 || Song.Media.Count > 0) && !await _confirmations.ConfirmAsync(
                         Strings.SaveConfirmTitle,
                         string.Format(CultureInfo.CurrentCulture, Strings.SaveConfirmMessageFormat, ChangesText(changes, Song.Media)),
                         Strings.SaveButton))
            {
                return false;
            }

            var entry = Song.Entry!;
            MoveEndAfterLastNote();
            var point = _undo.Mark();
            int version = _version;
            var request = ChartSaver.RequestFor(Song, Song.Chart.Clone(), changes, removeChart);
            var slots = request.Media?.Keys.ToList() ?? [];
            bool released = false;
            IsSaving = true;
            try
            {
                // An autosave already being written finishes first, so it can't land on the save's recovery file.
                await WaitForAutosaveAsync();
                var written = await Task.Run(() => ChartSaver.Check(request));
                if (!written.IsValid)
                {
                    Messages.Error(ReadBackFailedNotice(written));
                    return false;
                }

                await WriteRecoveryForSaveAsync(written.Bytes, version);

                // The files the save replaces or removes are kept first, so undo can still bring them back, and the editor
                // lets go of the ones it has open, which couldn't be replaced while it held them.
                var stash = _stash ??= MediaStash.Create();
                var kept = await Task.Run(() => stash.KeepAsync(Song, slots));
                released = ReleaseFilesFor(slots);
                var findings = await Task.Run(() => ChartSaver.SaveAsync(entry, request, written));
                if (FirstError(findings) is { } error)
                {
                    Messages.Error(string.Format(CultureInfo.CurrentCulture, Strings.SaveFailedFormat, error));
                    return false;
                }

                _undo.MarkSaved(point);
                Song.Saved(ChartSaver.SavedSong(Song, entry, request), PartsOf(request.Snapshot), new MediaSaveResult(slots, kept));
                AfterSaved(null);
                Messages.Info(Strings.SavedStatus);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Logger.Error(ex, "Saving {Song} failed", entry.FullPath);
                Messages.Error(string.Format(CultureInfo.CurrentCulture, Strings.SaveFailedFormat, ex.Message));
                return false;
            }
            finally
            {
                IsSaving = false;
                if (released)
                    ReopenFiles();
            }
        }

        /// <summary>
        /// Lets go of the song's files the save of <paramref name="slots"/> replaces or removes, which are held open while
        /// they're played and drawn: the audio, when a track changes, and the background video, when it does.
        /// </summary>
        /// <returns>Whether anything was let go of, to be opened again once the save is done.</returns>
        private bool ReleaseFilesFor(IReadOnlyCollection<MediaSlot> slots)
        {
            bool tracks = slots.Any(s => s.Kind == MediaKind.Track);
            bool video = slots.Contains(MediaSlot.BackgroundVideo);
            if (tracks)
            {
                _tracksTimer.Stop();
                Playback.Release();
                Audio.SetTracks(Song.Entry, []);
            }

            if (video)
                Art.Release();
            return tracks || video;
        }

        /// <summary>Opens what <see cref="ReleaseFilesFor"/> let go of again, as the song is now: saved, or as it was.</summary>
        private void ReopenFiles()
        {
            _ = ReloadAudioAsync();
            Art.Refresh();
        }

        /// <summary>
        /// A new chart's first save (charter-spec C1): once Chart settings has its name and artist, asks where its
        /// song folder goes, makes the folder, then locks the song, puts it on the recent songs and goes on with it as a
        /// song opened from there.
        /// </summary>
        private async Task<bool> FirstSaveAsync()
        {
            var pairs = Song.EffectivePairs();
            string Value(string key) => pairs.LastOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value?.Trim() ?? "";
            string name = Value("name");
            string artist = Value("artist");
            if (name.Length == 0 || artist.Length == 0)
            {
                // They're set in Chart settings' Metadata, which is brought up to set them in.
                Messages.Error(Strings.FirstSaveNeedsNameAndArtist);
                LeftPanelTab = ChartSettingsTab;
                IsLeftPanelCollapsed = false;
                return false;
            }

            var dialog = new FirstSaveViewModel(
                _services.FolderPicker,
                name,
                artist,
                _settings.Settings.LastNewChartFolder ?? ChartPackerSettingsFile.LibraryRoot(_services.ChartPackerSettingsFile));
            if (!await _services.FirstSave.AskAsync(dialog))
                return false;

            var target = dialog.Target;
            SongLock? songLock;
            try
            {
                songLock = SongLock.TryAcquire(target.FullPath, _services.LockFolder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Messages.Error(string.Format(CultureInfo.CurrentCulture, Strings.SaveFailedFormat, ex.Message));
                return false;
            }

            if (songLock is null)
            {
                Messages.Error(string.Format(CultureInfo.CurrentCulture, Strings.FirstSaveLockedFormat, target.FullPath));
                return false;
            }

            MoveEndAfterLastNote();
            var point = _undo.Mark();
            string keyBefore = RecoveryKey;
            var request = ChartSaver.RequestFor(Song, Song.Chart.Clone(), Song.PendingChanges());
            var stems = Song.Tracks.Select(t => new NewChartStem(t.StagedSourcePath!, t.Stem)).ToList();
            IsSaving = true;
            try
            {
                await WaitForAutosaveAsync();
                var written = await Task.Run(() => ChartSaver.Check(request));
                if (!written.IsValid)
                {
                    Messages.Error(ReadBackFailedNotice(written));
                    return false;
                }

                // Nothing of the song's own is replaced, so nothing need be kept; the tracks are read from where they are.
                string folder = await Task.Run(() => ChartSaver.SaveNewAsync(target, request, written, stems));
                var entry = SongOpener.EntryOf(folder)!;
                _songLock?.Dispose();
                _songLock = songLock;
                songLock = null;

                _undo.MarkSaved(point);
                Song.Saved(ChartSaver.SavedSong(Song, entry, request), PartsOf(request.Snapshot));
                AfterSaved(keyBefore);
                var settings = _settings.Settings;
                settings.LastNewChartFolder = target.ParentFolder;
                settings.RecentSongs = RecentSongs.Add(settings.RecentSongs, new RecentSong { Path = entry.FullPath, Title = Song.Title ?? "", OpenedAt = DateTimeOffset.Now });
                _settings.Save();
                Messages.Info(string.Format(CultureInfo.CurrentCulture, Strings.FirstSavedStatusFormat, folder));
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Logger.Error(ex, "Saving the new chart to {Folder} failed", target.FullPath);
                Messages.Error(string.Format(CultureInfo.CurrentCulture, Strings.SaveFailedFormat, ex.Message));
                return false;
            }
            finally
            {
                // Still held only if the save didn't go through.
                songLock?.Dispose();
                IsSaving = false;
            }
        }

        private static HashSet<ChartInstrument> PartsOf(EditableChart chart) => [.. chart.Parts.Select(p => p.Instrument)];

        /// <summary>
        /// Moves <c>[end]</c> after the last note (CHR085), as its own undo step, once the save is going ahead, so the model
        /// the editor holds is the one saved.
        /// </summary>
        private void MoveEndAfterLastNote()
        {
            if (ChartSaveFixups.MoveEnd(Song.Chart) is { } moveEnd)
                Editor.Session.Execute(moveEnd);
        }

        // Nothing of the song's own can be edited, undone or redone while a save runs, as the save moves the song on to
        // what it wrote.
        partial void OnIsSavingChanged(bool value)
        {
            ChartSettings.IsSaving = value;
            Metadata.IsSaving = value;
            Tracks.IsSaving = value;
            Art.IsSaving = value;
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
        }

        /// <summary>Refreshes what follows whether the chart is saved: the title's mark.</summary>
        private void RefreshSaveState()
        {
            Title = TitleOf();
            OnPropertyChanged(nameof(HasUnsavedChanges));
            OnPropertyChanged(nameof(SaveAndPackToolTip));
            OnPropertyChanged(nameof(ExportCopyToolTip));
            SaveAndPackCommand.NotifyCanExecuteChanged();
            ExportCopyCommand.NotifyCanExecuteChanged();
        }

        // The save's song.ini changes, then its changes to the art, backgrounds and tracks, a line each.
        private static string ChangesText(IReadOnlyList<PendingSongIniChange> changes, IReadOnlyDictionary<MediaSlot, MediaChange> media)
        {
            var lines = changes.Select(c => c.Value is { } value
                ? string.Format(CultureInfo.CurrentCulture, Strings.PendingChangeFormat, c.Key, value)
                : string.Format(CultureInfo.CurrentCulture, Strings.PendingChangeRemovedFormat, c.Key)).ToList();
            foreach (var (slot, change) in media.OrderBy(m => m.Key.Kind).ThenBy(m => AudioStems.OrderOf(m.Key.Stem ?? "")))
            {
                string what = MediaName(slot);
                lines.Add(change.SourcePath is { } source
                    ? string.Format(CultureInfo.CurrentCulture, Strings.PendingFileFormat, what, Path.GetFileName(source))
                    : string.Format(CultureInfo.CurrentCulture, Strings.PendingFileRemovedFormat, what));
            }

            return lines.Count == 0 ? Strings.PendingChangesNone : string.Join(Environment.NewLine, lines);
        }

        /// <summary>A slot as the save's confirmation names it: "album art", "background video", or a track's stem.</summary>
        private static string MediaName(MediaSlot slot) => slot.Kind switch
        {
            MediaKind.AlbumArt => Strings.PendingAlbumArtName,
            MediaKind.BackgroundImage => Strings.PendingBackgroundImageName,
            MediaKind.BackgroundVideo => Strings.PendingBackgroundVideoName,
            _ => string.Format(CultureInfo.CurrentCulture, Strings.PendingTrackNameFormat, slot.Stem),
        };

        /// <summary>That the chart didn't read back as it was written, and where (charter-spec §4.3), so it wasn't saved.</summary>
        private static string ReadBackFailedNotice(ChartWriteResult written)
        {
            var places = written.GameDifferences
                .Select(d => d.Times.Count > 0 ? $"{d.Kind} ({string.Join(", ", d.Times.Take(3).Select(FormatTime))})" : d.Kind.ToString())
                .Concat(written.ModelDifferences.Take(3).Select(d => d.Description))
                .Concat(written.ReloadIssues.Take(3).Select(i => i.ToString()))
                .Take(5);
            Logger.Error("A save was stopped by its read-back check: {Places}", string.Join("; ", places));
            return string.Format(CultureInfo.CurrentCulture, Strings.SaveReadBackFailedFormat, string.Join("; ", places));
        }

        private static string FormatTime(TimeSpan time) => time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss\.fff" : @"m\:ss\.fff", CultureInfo.CurrentCulture);

        private static string? FirstError(IEnumerable<Finding> findings) => findings.FirstOrDefault(f => f.Severity == Severity.Error)?.Message;

        private static (Severity Severity, string Message) PackNotice(JobItemResult? result) => result switch
        {
            null => (Severity.Warning, Strings.PackNoResult),
            { Status: JobItemStatus.Succeeded or JobItemStatus.SucceededWithWarnings } => (Severity.Info, string.Format(CultureInfo.CurrentCulture, Strings.PackedFormat, result.OutputPath)),
            { Status: JobItemStatus.Skipped, SkipReason: SkipReason.OutputExists } => (Severity.Warning, string.Format(CultureInfo.CurrentCulture, Strings.PackSkippedExistsFormat, result.OutputPath)),
            _ => (Severity.Error, string.Format(CultureInfo.CurrentCulture, Strings.PackFailedFormat, result.ErrorMessage ?? FirstError(result.Findings) ?? result.Status.ToString())),
        };

        /// <summary>Takes a job's results as they come, on the job's thread.</summary>
        private sealed class Reporter(Action<JobItemResult> report) : IProgress<JobItemResult>
        {
            public void Report(JobItemResult value) => report(value);
        }
    }
}
