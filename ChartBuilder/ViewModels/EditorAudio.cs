using System;
using System.Collections.Generic;
using ChartBuilder.Services;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Layout;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Scanning;
using ChartPacker.UI.Controls;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// The open chart's audio as the editor draws and analyses it (charter-spec C12–C14): the stems' waveforms, their
    /// spectrograms and onsets, and which audio each lane is drawn over. Held for as long as the chart is open, and let go
    /// of when it closes; read again from the start when the song's tracks change (<see cref="SetTracks"/>).
    /// </summary>
    public sealed class EditorAudio : IDisposable
    {
        private readonly EditorMessages? _messages;
        private long _budget;

        /// <param name="messages">Where analysis says what it's doing, and what it couldn't.</param>
        public EditorAudio(EditorSong song, BuilderSettings settings, EditorMessages? messages = null)
        {
            ArgumentNullException.ThrowIfNull(song);
            ArgumentNullException.ThrowIfNull(settings);
            _messages = messages;
            _budget = BudgetOf(settings);
            Entry = song.Entry;
            Tracks = song.Tracks;
            (Waveforms, Analysis) = Open(Entry, Tracks);
        }

        /// <summary>Raised on the UI thread when something drawn from the audio may have changed.</summary>
        public event EventHandler? Changed;

        /// <summary>Raised after <see cref="SetTracks"/>, once the tracks, waveforms and analysis are the new ones.</summary>
        public event EventHandler? TracksChanged;

        public SongEntry? Entry { get; private set; }

        /// <summary>The song's tracks as the editor plays them; a staged one is played from where it is until it's saved.</summary>
        public IReadOnlyList<SongTrack> Tracks { get; private set; }

        public bool HasAudio => Tracks.Count > 0;

        public StemWaveforms Waveforms { get; private set; }

        /// <summary>The spectrograms and onsets.</summary>
        public AudioAnalysis Analysis { get; private set; }

        /// <summary>The whole song as it plays, or null without audio.</summary>
        public AudioSource? Mix => AudioSource.MixOf(Tracks);

        /// <summary>Starts reading the stems' waveforms.</summary>
        public void Start() => Waveforms.Start();

        /// <summary>Takes up what may have changed in Settings: the spectrogram memory budget.</summary>
        public void ReloadSettings(BuilderSettings settings)
        {
            _budget = BudgetOf(settings);
            Analysis.SetBudget(_budget);
        }

        /// <summary>The audio a lane showing <paramref name="instrument"/> is drawn over, as <paramref name="options"/> picks it.</summary>
        public AudioSource? SourceFor(LaneAudioOptions options, ChartInstrument instrument) => AudioSource.Resolve(options, instrument, Tracks);

        /// <summary>
        /// Lets go of every waveform and analysis read, and the files they hold open, and starts on
        /// <paramref name="tracks"/> of <paramref name="entry"/>: after a track is added, replaced or removed, or the song
        /// saved. With no tracks, it holds nothing open, as a save that replaces a track needs.
        /// </summary>
        public void SetTracks(SongEntry? entry, IReadOnlyList<SongTrack> tracks)
        {
            ArgumentNullException.ThrowIfNull(tracks);
            Close();
            Entry = entry;
            Tracks = tracks;
            (Waveforms, Analysis) = Open(entry, tracks);
            Waveforms.Start();
            TracksChanged?.Invoke(this, EventArgs.Empty);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose() => Close();

        private (StemWaveforms Waveforms, AudioAnalysis Analysis) Open(SongEntry? entry, IReadOnlyList<SongTrack> tracks)
        {
            var waveforms = new StemWaveforms(entry, tracks);
            var analysis = new AudioAnalysis(entry, tracks, _budget, _messages);
            waveforms.Changed += OnPartChanged;
            analysis.Changed += OnPartChanged;
            return (waveforms, analysis);
        }

        private void Close()
        {
            if (Waveforms is null)
                return;
            Waveforms.Changed -= OnPartChanged;
            Analysis.Changed -= OnPartChanged;
            Waveforms.Dispose();
            Analysis.Dispose();

            // What's drawn of these tracks is no use once they've changed, or the chart has closed.
            LaneWaveformPainter.Clear();
            SpectrogramPainter.Clear();
        }

        private void OnPartChanged(object? sender, EventArgs e)
        {
            // A disposed reader's last word, posted before it was let go of, isn't news.
            if (ReferenceEquals(sender, Waveforms) || ReferenceEquals(sender, Analysis))
                Changed?.Invoke(this, EventArgs.Empty);
        }

        private static long BudgetOf(BuilderSettings settings) =>
            Math.Clamp(settings.SpectrogramBudgetMb, BuilderSettings.MinSpectrogramBudgetMb, BuilderSettings.MaxSpectrogramBudgetMb) * 1024L * 1024;
    }
}
