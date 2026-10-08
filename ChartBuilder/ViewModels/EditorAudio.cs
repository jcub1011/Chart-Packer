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
    /// of when it closes.
    /// </summary>
    public sealed class EditorAudio : IDisposable
    {
        /// <param name="messages">Where analysis says what it's doing, and what it couldn't.</param>
        public EditorAudio(EditorSong song, BuilderSettings settings, EditorMessages? messages = null)
        {
            ArgumentNullException.ThrowIfNull(song);
            ArgumentNullException.ThrowIfNull(settings);
            Entry = song.Entry;
            Tracks = song.Tracks;
            Waveforms = new StemWaveforms(Entry, Tracks);
            Analysis = new AudioAnalysis(Entry, Tracks, BudgetOf(settings), messages);
            Waveforms.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
            Analysis.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Raised on the UI thread when something drawn from the audio may have changed.</summary>
        public event EventHandler? Changed;

        public SongEntry? Entry { get; }

        /// <summary>The song's tracks as it was opened; a new chart's are played from where they are until it's saved.</summary>
        public IReadOnlyList<SongTrack> Tracks { get; }

        public bool HasAudio => Tracks.Count > 0;

        public StemWaveforms Waveforms { get; }

        /// <summary>The spectrograms and onsets.</summary>
        public AudioAnalysis Analysis { get; }

        /// <summary>The whole song as it plays, or null without audio.</summary>
        public AudioSource? Mix => AudioSource.MixOf(Tracks);

        /// <summary>Starts reading the stems' waveforms.</summary>
        public void Start() => Waveforms.Start();

        /// <summary>Takes up what may have changed in Settings: the spectrogram memory budget.</summary>
        public void ReloadSettings(BuilderSettings settings) => Analysis.SetBudget(BudgetOf(settings));

        /// <summary>The audio a lane showing <paramref name="instrument"/> is drawn over, as <paramref name="options"/> picks it.</summary>
        public AudioSource? SourceFor(LaneAudioOptions options, ChartInstrument instrument) => AudioSource.Resolve(options, instrument, Tracks);

        public void Dispose()
        {
            Waveforms.Dispose();
            Analysis.Dispose();

            // What's drawn of this chart's audio is no use to the next.
            LaneWaveformPainter.Clear();
            SpectrogramPainter.Clear();
        }

        private static long BudgetOf(BuilderSettings settings) =>
            Math.Clamp(settings.SpectrogramBudgetMb, BuilderSettings.MinSpectrogramBudgetMb, BuilderSettings.MaxSpectrogramBudgetMb) * 1024L * 1024;
    }
}
