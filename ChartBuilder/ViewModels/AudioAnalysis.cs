using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ChartBuilder.Resources;
using ChartShared.Audio.Analysis;
using ChartShared.Audio.Playback;
using ChartShared.Core.Charting.Analysis;
using ChartShared.Core.Scanning;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// The open chart's audio analysis (charter-spec C13, C14): spectrograms of the stems the lanes show, held within the
    /// memory budget, and onsets found and kept for as long as the chart is open. Nothing is written to disk.
    /// </summary>
    /// <remarks>
    /// <para>The work runs on <see cref="AnalysisWorkers"/>, below normal priority, from decoders of its own, opened off the
    /// UI thread the first time they're needed. A spectrogram's chunks near what's on screen come first; the view fills in
    /// as they arrive.</para>
    /// <para>Its members are for the UI thread, and <see cref="Changed"/> is raised there, at most every
    /// <see cref="ChangedInterval"/>, as more is ready.</para>
    /// </remarks>
    public sealed class AudioAnalysis : IDisposable
    {
        private const string RunningKey = "audio-analysis";
        private const string BudgetKey = "spectrogram-budget";

        private static readonly TimeSpan ChangedInterval = TimeSpan.FromMilliseconds(100);

        private readonly SongEntry? _entry;
        private readonly IReadOnlyList<SongTrack> _tracks;
        private readonly EditorMessages? _messages;
        private readonly CancellationTokenSource _cancel = new();
        private readonly DispatcherTimer _changed;
        private readonly Lock _audioLock = new();

        // On the UI thread: each source's audio, ready to analyse; its onsets; the spectrograms followed for updates.
        private readonly Dictionary<string, Task<Prepared?>> _prepared = [];
        private readonly Dictionary<(string Key, bool Drums), Task<OnsetAnalysis?>> _onsets = [];
        private readonly Dictionary<(string Key, bool Drums), PickedOnsets> _picked = [];
        private readonly HashSet<Spectrogram> _followed = [];
        private readonly HashSet<string> _overBudget = [];
        private long _budget;
        private int _changePending;
        private bool _disposed;

        // Opened by the first analysis, under the lock.
        private SongAudio? _audio;
        private AnalysisWorkers? _workers;
        private SpectrogramCache? _spectrograms;

        public AudioAnalysis(SongEntry? entry, IReadOnlyList<SongTrack> tracks, long budgetBytes, EditorMessages? messages)
        {
            _entry = entry;
            _tracks = tracks;
            _budget = budgetBytes;
            _messages = messages;
            _changed = new DispatcherTimer(DispatcherPriority.Background) { Interval = ChangedInterval };
            _changed.Tick += (_, _) => RaiseChanged();
        }

        /// <summary>Raised on the UI thread when more of an analysis is ready.</summary>
        public event EventHandler? Changed;

        /// <summary>How much memory the spectrograms may take together (charter-spec C13 item 3).</summary>
        public void SetBudget(long bytes)
        {
            _budget = bytes;
            if (_spectrograms is { } spectrograms)
                spectrograms.BudgetBytes = bytes;
            _overBudget.Clear();
        }

        /// <summary>
        /// <paramref name="source"/>'s spectrogram, its chunks near <paramref name="focusSeconds"/> computed first; null
        /// until its audio is open, and when it alone needs more than the whole budget, which is said once.
        /// </summary>
        public Spectrogram? SpectrogramFor(AudioSource source, double focusSeconds)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (_disposed || Ready(source) is not { } prepared || _spectrograms is not { } spectrograms)
                return null;

            var result = spectrograms.GetOrStart(prepared.Key, () => prepared.Input, focusSeconds);
            if (result.Spectrogram is not { } spectrogram)
            {
                if (_overBudget.Add(source.Key))
                {
                    string format = result.IsCrowded ? Strings.SpectrogramBudgetFullFormat : Strings.SpectrogramOverBudgetFormat;
                    _messages?.Warning(string.Format(CultureInfo.CurrentCulture, format, source.Label, result.NeededBytes / (1024.0 * 1024)), BudgetKey);
                }
                return null;
            }

            if (_followed.Add(spectrogram))
            {
                spectrogram.Updated += OnSpectrogramUpdated;
                _followed.RemoveWhere(s => s.IsReleased);
            }

            return spectrogram;
        }

        /// <summary>
        /// <paramref name="source"/>'s onsets, by band for drums (charter-spec C14 item 1); null until they're found,
        /// which this starts in the background.
        /// </summary>
        public OnsetAnalysis? OnsetsFor(AudioSource source, bool drumBands)
        {
            ArgumentNullException.ThrowIfNull(source);
            var task = OnsetsAsync(source, drumBands, requested: false);
            return task.IsCompletedSuccessfully ? task.Result : null;
        }

        /// <summary>
        /// <paramref name="source"/>'s onsets at <paramref name="sensitivity"/>, in time order, every drum band's for drums;
        /// none until they're found, which this starts.
        /// </summary>
        public IReadOnlyList<DetectedOnset> OnsetsAt(AudioSource source, bool drumBands, double sensitivity) =>
            Picked(source, drumBands, sensitivity).Onsets;

        /// <summary>The times of <see cref="OnsetsAt"/>, ascending, for snapping and nudging to them.</summary>
        public IReadOnlyList<double> OnsetTimesAt(AudioSource source, bool drumBands, double sensitivity) =>
            Picked(source, drumBands, sensitivity).Times;

        /// <summary>
        /// The onsets at a sensitivity, kept until another is asked for: picking them is quick, but not for every frame.
        /// </summary>
        private PickedOnsets Picked(AudioSource source, bool drumBands, double sensitivity)
        {
            var key = (source.Key, drumBands);
            if (OnsetsFor(source, drumBands) is not { } analysis)
                return PickedOnsets.None;
            if (_picked.TryGetValue(key, out var picked) && picked.Analysis == analysis && picked.Sensitivity == sensitivity)
                return picked;

            var onsets = analysis.Select(sensitivity);
            picked = new PickedOnsets(analysis, sensitivity, onsets, [.. onsets.Select(o => o.Seconds)]);
            _picked[key] = picked;
            return picked;
        }

        /// <summary>
        /// <paramref name="source"/>'s onsets, found once and kept; <see langword="null"/> when its audio can't be read.
        /// </summary>
        /// <param name="requested">The author is waiting on them, so they come before other background work.</param>
        public Task<OnsetAnalysis?> OnsetsAsync(AudioSource source, bool drumBands, bool requested)
        {
            ArgumentNullException.ThrowIfNull(source);
            var key = (source.Key, drumBands);
            if (_onsets.TryGetValue(key, out var found))
                return found;
            if (_disposed)
                return Task.FromResult<OnsetAnalysis?>(null);

            var task = FindOnsetsAsync(source, drumBands, requested);
            _onsets[key] = task;
            return task;
        }

        /// <summary>
        /// Proposes beats for <paramref name="source"/> (charter-spec C14 items 4 and 5), over <paramref name="range"/> of
        /// its audio or the whole of it, and where measure 1 most likely starts among them, for measures of
        /// <paramref name="beatsPerMeasure"/> beats. Null when its audio can't be read.
        /// </summary>
        public async Task<(BeatProposal Proposal, DownbeatEstimate? Downbeat)?> ProposeAsync(AudioSource source, (double From, double To)? range, int beatsPerMeasure)
        {
            ArgumentNullException.ThrowIfNull(source);

            // The kick band tells measures' first beats from the others.
            if (await OnsetsAsync(source, drumBands: true, requested: true) is not { } onsets)
                return null;

            var token = _cancel.Token;
            try
            {
                return await Task.Run(() =>
                {
                    var proposal = range is { } span
                        ? TempoEstimator.Estimate(onsets, span.From, span.To, token)
                        : TempoEstimator.Estimate(onsets, token);
                    return (proposal, TempoEstimator.FindFirstDownbeat(onsets, proposal.Beats, Math.Max(1, beatsPerMeasure)));
                }, token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _changed.Stop();
            _cancel.Cancel();
            foreach (var spectrogram in _followed)
                spectrogram.Updated -= OnSpectrogramUpdated;
            _followed.Clear();

            // The workers before the audio they read.
            lock (_audioLock)
            {
                _spectrograms?.Dispose();
                _workers?.Dispose();
                _audio?.Dispose();
                _spectrograms = null;
                _workers = null;
                _audio = null;
            }
        }

        private async Task<OnsetAnalysis?> FindOnsetsAsync(AudioSource source, bool drumBands, bool requested)
        {
            var token = _cancel.Token;
            _messages?.Info(string.Format(CultureInfo.CurrentCulture, Strings.OnsetsFindingFormat, source.Label), RunningKey);
            try
            {
                if (await PrepareAsync(source) is not { } prepared || _workers is not { } workers)
                    return null;
                var onsets = await OnsetDetector.Start(prepared.Input, drumBands, workers, requested, token);
                if (!token.IsCancellationRequested)
                {
                    _messages?.Info(string.Format(CultureInfo.CurrentCulture, Strings.OnsetsFoundFormat, source.Label), RunningKey);
                    RequestChanged();
                }

                return onsets;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (IOException ex)
            {
                _messages?.Warning(string.Format(CultureInfo.CurrentCulture, Strings.OnsetsFailedFormat, source.Label, ex.Message), RunningKey);
                return null;
            }
        }

        /// <summary><paramref name="source"/>'s audio ready to analyse, or null while it's being opened (which this starts).</summary>
        private Prepared? Ready(AudioSource source)
        {
            var task = PrepareAsync(source);
            return task.IsCompletedSuccessfully ? task.Result : null;
        }

        /// <summary>Opens <paramref name="source"/>'s audio to analyse, off the UI thread, once.</summary>
        private Task<Prepared?> PrepareAsync(AudioSource source)
        {
            if (_prepared.TryGetValue(source.Key, out var task))
                return task;

            var token = _cancel.Token;
            task = Task.Run(() => Prepare(source, token), token);
            _prepared[source.Key] = task;
            task.ContinueWith(_ => RequestChanged(), TaskScheduler.Default);
            return task;
        }

        private Prepared? Prepare(AudioSource source, CancellationToken token)
        {
            try
            {
                SongAudio audio;
                lock (_audioLock)
                {
                    token.ThrowIfCancellationRequested();
                    if (_audio is null)
                    {
                        _audio = SongAudio.Open(_entry, _tracks, token);
                        _workers = new AnalysisWorkers();
                        _spectrograms = new SpectrogramCache(_workers, _budget) { KeepInUse = TimeSpan.FromSeconds(2) };
                    }

                    audio = _audio;
                }

                var opened = audio.Tracks.Where(t => source.Tracks.Contains(t.Track) && t.Source is not null).ToList();
                if (opened.Count == 0)
                    return null;

                var key = AnalysisKey.Of(_entry, source.Tracks) ?? new AnalysisKey(source.Key);
                return new Prepared(key, AnalysisInput.Mix(audio, opened));
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            {
                if (ex is not OperationCanceledException)
                    Trace.TraceWarning($"Opening the audio to analyse failed: {ex.Message}");
                return null;
            }
        }

        private void OnSpectrogramUpdated(Spectrogram spectrogram) => RequestChanged();

        /// <summary>Raises <see cref="Changed"/> soon, on the UI thread; from any thread.</summary>
        private void RequestChanged()
        {
            if (Interlocked.Exchange(ref _changePending, 1) != 0)
                return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed)
                    _changed.Start();
            });
        }

        private void RaiseChanged()
        {
            _changed.Stop();
            Volatile.Write(ref _changePending, 0);
            if (!_disposed)
                Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>A source's audio as the analysis reads it, and the key its spectrogram is kept under.</summary>
        private sealed record Prepared(AnalysisKey Key, AnalysisInput Input);

        /// <summary>An analysis's onsets at one sensitivity, and their times.</summary>
        private sealed record PickedOnsets(OnsetAnalysis? Analysis, double Sensitivity, IReadOnlyList<DetectedOnset> Onsets, double[] Times)
        {
            public static PickedOnsets None { get; } = new(null, 0, [], []);
        }
    }
}
