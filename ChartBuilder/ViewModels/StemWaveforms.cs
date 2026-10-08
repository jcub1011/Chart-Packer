using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ChartPacker.Audio.Playback;
using ChartPacker.Audio.Waveforms;
using ChartPacker.Core.Scanning;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// The open chart's waveforms (charter-spec C12): every stem's peaks, read once in the background, and the waveform
    /// of any <see cref="AudioSource"/> from them, a stem's own or several mixed, in mono or by channel.
    /// </summary>
    /// <remarks>
    /// <para>Peaks are read at <see cref="WaveformResolution.High"/> (5 ms) and drawn at whatever the zoom needs, a
    /// power of two of buckets to a bar, so one read serves every zoom. A stem's channels are only read the first time a
    /// lane asks for it in stereo; until then it's drawn in mono.</para>
    /// <para>Several stems are mixed in the background, and the mix kept until their peaks change, so drawing never waits
    /// for it. Its members are for the UI thread, and <see cref="Changed"/> is raised there, at most every
    /// <see cref="ChangedInterval"/>.</para>
    /// </remarks>
    public sealed class StemWaveforms : IDisposable
    {
        public static readonly WaveformResolution Resolution = WaveformResolution.High;

        private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan ChangedInterval = TimeSpan.FromMilliseconds(150);

        private readonly SongEntry? _entry;
        private readonly IReadOnlyList<SongTrack> _tracks;
        private readonly CancellationTokenSource _cancel = new();
        private readonly DispatcherTimer _changed;

        // Under the lock: each track's peaks, how many times they've changed, and the tracks whose channels are being read.
        private readonly Lock _lock = new();
        private readonly Dictionary<SongTrack, StemPeaks> _peaks = [];
        private readonly Dictionary<SongTrack, int> _versions = [];
        private readonly HashSet<SongTrack> _readingChannels = [];

        // On the UI thread: the mixes made, by source and whether by channel, and those being made.
        private readonly Dictionary<(string Key, bool Stereo), Mixed> _mixes = [];
        private bool _changePending;
        private bool _disposed;

        public StemWaveforms(SongEntry? entry, IReadOnlyList<SongTrack> tracks)
        {
            _entry = entry;
            _tracks = tracks;
            _changed = new DispatcherTimer(DispatcherPriority.Background) { Interval = ChangedInterval };
            _changed.Tick += (_, _) => RaiseChanged();
        }

        /// <summary>Raised on the UI thread when some waveform may have changed: peaks read, or a mix made.</summary>
        public event EventHandler? Changed;

        /// <summary>Starts reading every track's peaks, from the cache where it has them.</summary>
        public void Start()
        {
            if (_tracks.Count > 0)
                Read(_tracks, channels: false);
        }

        /// <summary><paramref name="source"/>'s waveform, its channels combined, as much as has been read; null until any has been.</summary>
        public Waveform? Mono(AudioSource source)
        {
            ArgumentNullException.ThrowIfNull(source);
            var peaks = PeaksOf(source.Tracks, channels: false);
            if (peaks.Count == 0)
                return null;
            if (peaks.Count == 1 && !source.IsMix)
                return WaveformPeaks.Of(peaks[0].Peaks);
            return (Waveform?)MixOf(source, peaks, stereo: false)?.Mono;
        }

        /// <summary>
        /// <paramref name="source"/>'s left and right waveforms; null until its stems' channels have been read, which the
        /// first call starts.
        /// </summary>
        public (Waveform Left, Waveform Right)? Stereo(AudioSource source)
        {
            ArgumentNullException.ThrowIfNull(source);
            var peaks = PeaksOf(source.Tracks, channels: true);
            if (peaks.Count < source.Tracks.Count(t => HasPeaks(t)))
                return null;
            if (peaks.Count == 0)
                return null;
            if (peaks.Count == 1 && !source.IsMix)
                return (WaveformPeaks.Of(peaks[0].Peaks.Left), WaveformPeaks.Of(peaks[0].Peaks.Right));
            return MixOf(source, peaks, stereo: true)?.Channels;
        }

        public void Dispose()
        {
            _disposed = true;
            _cancel.Cancel();
            _changed.Stop();
            _mixes.Clear();
        }

        private bool HasPeaks(SongTrack track)
        {
            lock (_lock)
                return _peaks.ContainsKey(track);
        }

        /// <summary>
        /// The peaks there are of <paramref name="tracks"/>, with their versions; by channel, only those read so, and the
        /// others' channels are read.
        /// </summary>
        private List<(SongTrack Track, StemPeaks Peaks, int Version)> PeaksOf(IReadOnlyList<SongTrack> tracks, bool channels)
        {
            var found = new List<(SongTrack, StemPeaks, int)>();
            var missing = new List<SongTrack>();
            lock (_lock)
            {
                foreach (var track in tracks)
                {
                    if (!_peaks.TryGetValue(track, out var peaks))
                        continue;
                    if (channels && !peaks.HasChannels)
                    {
                        if (_readingChannels.Add(track))
                            missing.Add(track);
                        continue;
                    }

                    found.Add((track, peaks, _versions[track]));
                }
            }

            if (missing.Count > 0)
                Read(missing, channels: true);
            return found;
        }

        /// <summary>The mix of <paramref name="peaks"/> made last, starting a new one when they've changed since.</summary>
        private Mixed? MixOf(AudioSource source, List<(SongTrack Track, StemPeaks Peaks, int Version)> peaks, bool stereo)
        {
            var key = (source.Key, stereo);
            string versions = string.Join(",", peaks.Select(p => p.Version));
            _mixes.TryGetValue(key, out var mixed);
            if (mixed?.Versions == versions || mixed?.Making == versions)
                return mixed?.Made is null ? null : mixed;

            mixed ??= new Mixed();
            mixed.Making = versions;
            _mixes[key] = mixed;
            var stems = peaks.Select(p => new AudibleStem(p.Peaks, IsMuted: false, IsSolo: false)).ToList();
            var token = _cancel.Token;
            _ = Task.Run(() =>
            {
                object made = stereo ? WaveformPeaks.MixChannels(stems) : WaveformPeaks.Mix(stems);
                Dispatcher.UIThread.Post(() =>
                {
                    if (token.IsCancellationRequested || mixed.Making != versions)
                        return;
                    mixed.Made = made;
                    mixed.Versions = versions;
                    mixed.Making = null;
                    RequestChanged();
                });
            }, token);
            return mixed.Made is null ? null : mixed;
        }

        /// <summary>Reads <paramref name="tracks"/>' peaks in the background, from the cache where it has them.</summary>
        private void Read(IReadOnlyList<SongTrack> tracks, bool channels)
        {
            var token = _cancel.Token;
            _ = Task.Run(() =>
            {
                try
                {
                    // Each key is taken before its file is read, so what's kept under it is never older than the file it names.
                    var keys = new Dictionary<SongTrack, StemPeaksKey?>();
                    foreach (var track in tracks)
                    {
                        var key = StemPeaksCache.KeyOf(_entry, track);
                        if (key is { } found && StemPeaksCache.Shared.TryGet(found, Resolution, channels, out var cached))
                            Show(track, cached);
                        else
                            keys[track] = key;
                    }

                    if (keys.Count == 0)
                        return;

                    // Separate from the player's own sources, which are for the audio thread alone.
                    using var audio = SongAudio.Open(_entry, [.. keys.Keys], token);
                    ChunkedPeaksRead.Read(
                        audio,
                        Resolution,
                        completed: (opened, peaks) =>
                        {
                            Show(opened.Track, peaks);
                            if (keys[opened.Track] is { } key)
                                StemPeaksCache.Shared.Add(key, peaks);
                        },
                        failed: opened => Forget(opened.Track, channels),
                        // Read again by channel, the averaged peaks the track has stand until the read is done.
                        progress: channels ? null : (opened, partial) => Show(opened.Track, partial),
                        ProgressInterval,
                        token,
                        channels);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    // No waveform; the song still plays.
                    if (ex is not OperationCanceledException)
                        Trace.TraceWarning($"Reading the waveform failed: {ex.Message}");
                }
            }, token);
        }

        private void Show(SongTrack track, StemPeaks peaks)
        {
            lock (_lock)
            {
                // Peaks by channel aren't replaced by a mono read's.
                if (_peaks.TryGetValue(track, out var had) && had.HasChannels && !peaks.HasChannels)
                    return;
                _peaks[track] = peaks;
                _versions[track] = _versions.GetValueOrDefault(track) + 1;
                if (peaks.HasChannels)
                    _readingChannels.Remove(track);
            }

            Dispatcher.UIThread.Post(RequestChanged);
        }

        private void Forget(SongTrack track, bool channels)
        {
            lock (_lock)
            {
                // A failed read by channel leaves the averaged peaks as they were, and isn't tried again.
                if (channels)
                    return;
                _peaks.Remove(track);
                _versions[track] = _versions.GetValueOrDefault(track) + 1;
            }

            Dispatcher.UIThread.Post(RequestChanged);
        }

        private void RequestChanged()
        {
            if (_disposed || _changePending)
                return;
            _changePending = true;
            _changed.Start();
        }

        private void RaiseChanged()
        {
            _changed.Stop();
            _changePending = false;
            if (!_disposed)
                Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>A source's mix: the one made last and the versions of the peaks it's of, and the versions of one being made.</summary>
        private sealed class Mixed
        {
            public object? Made { get; set; }

            public string? Versions { get; set; }

            public string? Making { get; set; }

            public Waveform? Mono => Made as Waveform;

            public (Waveform Left, Waveform Right)? Channels => Made is ValueTuple<Waveform, Waveform> pair ? pair : null;
        }
    }
}
