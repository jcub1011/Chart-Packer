using System;
using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Recovery;
using ChartShared.Core.Charts;
using ChartShared.Core.Scanning;
using ChartBuilder.Resources;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// The Editor window's autosave (charter-spec C15 item 6): every few minutes, and when playback stops, the chart is
    /// written to the recovery folder, never into the song, if it has changed since. Saving deletes it.
    /// </summary>
    public sealed partial class EditorWindowViewModel
    {
        // How soon an autosave that found the chart busy tries again.
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

        private DispatcherTimer? _autosave;
        private DispatcherTimer? _autosaveRetry;

        // A count of the chart's and the song's changes, and that count when the chart was last autosaved.
        private int _version;
        private int _autosavedVersion = -1;
        private bool _hasRecoveryFile;
        private bool _isAutosaving;

        // The autosave being written, if there is one, which never fails; a save or a discard waits for it.
        private Task _autosaveWrite = Task.CompletedTask;

        // A new chart's key in the recovery folder until it's first saved, after which it's the song's.
        private string _newChartKey = RecoveryStore.KeyOfNew(Guid.NewGuid());

        /// <summary>Where the chart open is autosaved: the song's key, or a new chart's own.</summary>
        private string RecoveryKey => Song.Entry is { } entry ? RecoveryStore.KeyOf(entry.FullPath) : _newChartKey;

        /// <summary>
        /// A chart recovered from an autosave (charter-spec C2 item 7): unsaved until it's saved, and autosaved over the
        /// recovery file it came from.
        /// </summary>
        /// <param name="songLock">The song's lock; none for a new chart that was never saved.</param>
        /// <param name="recoveryKey">The recovery file it came from.</param>
        public static EditorWindowViewModel ForRecovered(Services.EditorServices services, EditorSong song, SongLock? songLock, string recoveryKey)
        {
            var viewModel = new EditorWindowViewModel(services, song, songLock, startsSaved: false);
            viewModel.Messages.Info(Strings.RecoveredNotice);
            if (song.IsNew)
                viewModel._newChartKey = recoveryKey;
            viewModel._hasRecoveryFile = true;
            return viewModel;
        }

        /// <summary>Starts autosaving, on the interval in Settings and when playback stops.</summary>
        private void StartAutosave()
        {
            _autosave = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(_settings.Settings.AutosaveMinutes) };
            _autosave.Tick += (_, _) => _ = AutosaveAsync();
            _autosave.Start();
            _autosaveRetry = new DispatcherTimer(DispatcherPriority.Background) { Interval = RetryDelay };
            _autosaveRetry.Tick += (_, _) =>
            {
                _autosaveRetry.Stop();
                _ = AutosaveAsync();
            };
            Playback.PropertyChanged += OnPlaybackForAutosave;
        }

        private void StopAutosave()
        {
            _autosave?.Stop();
            _autosaveRetry?.Stop();
            Playback.PropertyChanged -= OnPlaybackForAutosave;
        }

        private void OnPlaybackForAutosave(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PlaybackViewModel.IsPlaying) && !Playback.IsPlaying)
                _ = AutosaveAsync();
        }

        /// <summary>Counts a change, so the next autosave writes the chart.</summary>
        private void CountChange() => _version++;

        /// <summary>
        /// Autosaves the chart if it has unsaved changes it hasn't been autosaved with, off the UI thread on a snapshot;
        /// or deletes the recovery file once nothing is unsaved. Never throws: a failure is logged, and the next one tries
        /// again.
        /// </summary>
        public async Task AutosaveAsync()
        {
            if (_isAutosaving || _closing.IsCancellationRequested)
                return;

            switch (Autosave.Next(HasUnsavedChanges, _hasRecoveryFile, _version, _autosavedVersion, Editor.Session.HasPreview || IsSaving))
            {
                case AutosaveAction.Delete:
                    DeleteRecovery();
                    return;
                case AutosaveAction.Later:
                    _autosaveRetry?.Start();
                    return;
                case AutosaveAction.Nothing:
                    return;
            }

            int version = _version;
            var snapshot = Song.Chart.Clone();
            var manifest = Autosave.ManifestFor(Song, TitleText(), DateTimeOffset.Now);
            string key = RecoveryKey;
            _isAutosaving = true;
            var write = Task.Run(() => _services.Recovery.WriteAsync(key, MidiChartWriter.Write(snapshot), manifest));
            _autosaveWrite = write.ContinueWith(static _ => { }, TaskScheduler.Default);
            try
            {
                await write;
                _autosavedVersion = version;
                _hasRecoveryFile = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A ChartFormatException, a chart that can't be written as MIDI, is an IOException too.
                Logger.Warning(ex, "Autosaving to the recovery folder failed");
            }
            finally
            {
                _isAutosaving = false;
            }
        }

        /// <summary>
        /// Autosaves the chart now, on this thread, if it has unsaved changes: for the OS shutting down, which won't wait
        /// for the close prompt.
        /// </summary>
        public void AutosaveNow()
        {
            if (!HasUnsavedChanges || Editor.Session.HasPreview)
                return;
            try
            {
                _services.Recovery.WriteAsync(RecoveryKey, MidiChartWriter.Write(Song.Chart), Autosave.ManifestFor(Song, TitleText(), DateTimeOffset.Now))
                    .GetAwaiter().GetResult();
                _hasRecoveryFile = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warning(ex, "Autosaving at shutdown failed");
            }
        }

        /// <summary>
        /// Writes the recovery file from what a save is about to write, before it touches the song, so a crash mid-save
        /// leaves it to recover.
        /// </summary>
        private async Task WriteRecoveryForSaveAsync(byte[] midi, int version)
        {
            try
            {
                var manifest = Autosave.ManifestFor(Song, TitleText(), DateTimeOffset.Now);
                string key = RecoveryKey;
                await Task.Run(() => _services.Recovery.WriteAsync(key, midi, manifest));
                _autosavedVersion = version;
                _hasRecoveryFile = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warning(ex, "Writing the recovery file before saving failed");
            }
        }

        /// <summary>
        /// After a save went through: the recovery file goes if nothing is unsaved since the snapshot, and otherwise the
        /// next autosave writes the edits made meanwhile. A new chart's goes, as its song now has a key of its own.
        /// </summary>
        private void AfterSaved(string? keyBefore)
        {
            if (keyBefore is not null && keyBefore != RecoveryKey)
                _services.Recovery.Delete(keyBefore);
            if (!HasUnsavedChanges)
                DeleteRecovery();
            else
                _autosavedVersion = -1;
        }

        /// <summary>Waits for an autosave being written, so nothing writes the recovery file under a save or a discard.</summary>
        private Task WaitForAutosaveAsync() => _autosaveWrite;

        /// <summary>Deletes the chart's recovery file: once saved, or its edits discarded.</summary>
        private void DeleteRecovery()
        {
            _services.Recovery.Delete(RecoveryKey);
            _hasRecoveryFile = false;
            _autosavedVersion = -1;
        }

        private string TitleText() => Song.Title ?? Strings.EditorUntitledChart;
    }
}
