using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ChartPacker.Controls;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Merging;
using ChartPacker.Core.Metadata;
using ChartPacker.Resources;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartPacker.ViewModels
{
    /// <summary>
    /// The lyrics row under the album art: the one selected song's vocals, as its draft would save
    /// them, drawn as a karaoke lane in step with the audio, with an offset to move them.
    /// </summary>
    /// <remarks>
    /// What's drawn is the chart <see cref="VocalsEditor"/> makes from the draft's vocals change,
    /// read back, so it's what saving writes. The offset moves every syllable by the same song time,
    /// so it's drawn by moving the lane rather than rewriting the chart for every step of a drag.
    /// The row shows while the song has a vocals track to sing along to, vocals in its chart, or a
    /// change to them to undo.
    /// </remarks>
    public sealed partial class LyricsViewModel(Func<TimeSpan> clock) : ViewModelBase
    {
        private HighwaySong? _song;
        private bool _hasVocalStem;

        // The chart being shown, by where it's read from, and its bytes once read.
        private string? _chartKey;
        private string? _chartName;
        private byte[]? _bytes;

        // What the latest build was of: the chart, the vocals change and the metadata that places them.
        private string? _builtKey;

        // Bumped for each load and build, so only the latest one is applied.
        private int _version;

        // The vocals the draft's change leaves the chart with, before the offset, and where they're sung.
        private VocalChart? _shown;
        private TimeSpan _delay;

        // Set while the offset is changed from here, so that doesn't read as the user changing it.
        private bool _isSyncing;

        // The charts vocals were chosen from, by path, read once.
        private readonly Dictionary<string, VocalsSource> _sources = new(StringComparer.Ordinal);

        /// <summary>Raised when the offset is set in the draft of the song shown.</summary>
        public event EventHandler? OffsetChanged;

        /// <summary>Where the audio is.</summary>
        public Func<TimeSpan> Clock { get; } = clock;

        /// <summary>What's drawn, or <see langword="null"/> when there are no vocals to show.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasLyrics))]
        [NotifyPropertyChangedFor(nameof(ReplaceToolTip))]
        [NotifyPropertyChangedFor(nameof(CanNudge))]
        [NotifyCanExecuteChangedFor(nameof(NudgeCommand))]
        [NotifyCanExecuteChangedFor(nameof(RevertOffsetCommand))]
        public partial LyricScene? Scene { get; set; }

        /// <summary>Shown in the lane instead of the vocals, e.g. "No lyrics" or "Lyrics will be removed".</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasNote))]
        public partial string? Note { get; set; }

        /// <summary>A caveat about what's shown: a <c>.chart</c> keeps the words but not the notes.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasWarning))]
        public partial string? Warning { get; set; }

        /// <summary>Why the offset can't be saved: it moves the vocals before the start of the song.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasProblem))]
        public partial string? Problem { get; set; }

        /// <summary>The song's chart as stored has vocals, so the row shows even once they're removed.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsVisible))]
        public partial bool ChartHasVocals { get; set; }

        /// <summary>The song will have a chart, for vocals to go in.</summary>
        [ObservableProperty]
        public partial bool HasChart { get; set; }

        /// <summary>Where the vocals shown come from: the chart chosen for them, or the song's own.</summary>
        [ObservableProperty]
        public partial string? SourceName { get; set; }

        /// <summary>How far the vocals are moved, in milliseconds, later when positive; the offset box.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasOffset))]
        [NotifyCanExecuteChangedFor(nameof(RevertOffsetCommand))]
        public partial decimal? Offset { get; set; } = 0;

        /// <summary>Whether the offset can be changed: one song is shown, and it can be edited.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanNudge))]
        [NotifyCanExecuteChangedFor(nameof(NudgeCommand))]
        [NotifyCanExecuteChangedFor(nameof(RevertOffsetCommand))]
        public partial bool CanEditOffset { get; set; }

        public bool IsVisible => _song is not null && (_hasVocalStem || ChartHasVocals || IsChanged);

        /// <summary>There are vocals to draw once the draft is saved.</summary>
        public bool HasLyrics => Scene is not null;

        public bool HasNote => Note is not null;

        public bool HasWarning => Warning is not null;

        public bool HasProblem => Problem is not null;

        public bool HasOffset => Offset is { } offset && offset != 0;

        /// <summary>There are vocals to move, and they can be moved.</summary>
        public bool CanNudge => CanEditOffset && HasLyrics;

        /// <summary>Whether the song shown has a change to its vocals to save: others in their place, or an offset.</summary>
        public bool IsChanged => _song?.Draft is { } draft && (draft.Vocals is not null || draft.HasVocalsOffset);

        public string? Marker => IsChanged ? "*" : null;

        public string ReplaceToolTip => HasLyrics ? Strings.LyricsReplaceToolTip : Strings.LyricsAddToolTip;

        /// <summary>Keeps <paramref name="source"/>, read for <paramref name="path"/>, so showing its vocals doesn't read it again.</summary>
        public void AddSource(string path, VocalsSource source) => _sources[path] = source;

        /// <summary>Forgets the song and its chart, so the next <see cref="Refresh"/> reads it afresh, as after saving.</summary>
        public void Unload()
        {
            _song = null;
            _hasVocalStem = false;
            Clear(null);
        }

        /// <summary>
        /// Shows <paramref name="song"/>'s vocals, with its draft's change and offset; <see langword="null"/>
        /// shows nothing, as for no selection or several songs. The chart is read again only when it's a
        /// different file.
        /// </summary>
        /// <param name="hasVocalStem">The song will have a vocals audio track.</param>
        public void Refresh(HighwaySong? song, bool hasVocalStem)
        {
            _song = song;
            _hasVocalStem = hasVocalStem;
            SyncOffset();
            if (song is null || DraftChart.Of(song.Draft, song.StoredFiles) is not { } chart)
            {
                Clear(song is null ? null : Strings.LyricsNoChart);
                return;
            }

            HasChart = true;
            string key = chart.StagedPath ?? $"{song.Entry.FullPath}|{chart.Name}";
            if (key != _chartKey)
            {
                Clear(null);
                HasChart = true;
                _chartKey = key;
                _ = LoadAsync(song, chart, key);
            }
            else if (_bytes is not null && BuildKey(song) != _builtKey)
            {
                _ = BuildAsync();
            }
            else
            {
                // Only the offset may have changed, as when the draft's offsets are cleared.
                BuildScene();
            }

            RefreshState();
        }

        [RelayCommand(CanExecute = nameof(CanNudge))]
        private void Nudge(string? amount)
        {
            if (int.TryParse(amount, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int ms))
                Offset = (Offset ?? 0) + ms;
        }

        private bool CanRevertOffset() => HasOffset && CanNudge;

        [RelayCommand(CanExecute = nameof(CanRevertOffset))]
        private void RevertOffset() => Offset = 0;

        partial void OnOffsetChanged(decimal? value)
        {
            if (_isSyncing || _song is not { } song)
                return;

            song.Draft.SetVocalsOffset((int)Math.Round(Math.Clamp(value ?? 0, int.MinValue, int.MaxValue)));
            BuildScene();
            RefreshState();
            OffsetChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Shows the draft's offset in the box, without that counting as the user setting it.</summary>
        private void SyncOffset()
        {
            _isSyncing = true;
            try
            {
                Offset = _song?.Draft.VocalsOffset ?? 0;
            }
            finally
            {
                _isSyncing = false;
            }
        }

        private void Clear(string? note)
        {
            _version++;
            _chartKey = null;
            _chartName = null;
            _bytes = null;
            _builtKey = null;
            _shown = null;
            Scene = null;
            Warning = null;
            Problem = null;
            SourceName = null;
            ChartHasVocals = false;
            HasChart = false;
            Note = note;
            RefreshState();
        }

        private void RefreshState()
        {
            OnPropertyChanged(nameof(IsVisible));
            OnPropertyChanged(nameof(IsChanged));
            OnPropertyChanged(nameof(Marker));
        }

        private async Task LoadAsync(HighwaySong song, DraftChart chart, string key)
        {
            int version = _version;
            byte[]? bytes;
            try
            {
                bytes = await Task.Run(() => chart.ReadAsync(song.Entry));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (version == _version)
                    Note = string.Format(CultureInfo.CurrentCulture, Strings.LyricsUnreadableFormat, ex.Message);
                return;
            }

            if (version != _version || key != _chartKey)
                return;
            if (bytes is null)
            {
                Note = Strings.LyricsNoChart;
                return;
            }

            _bytes = bytes;
            _chartName = chart.Name;
            await BuildAsync();
        }

        /// <summary>
        /// What a build depends on besides the chart: the vocals change, and the <c>delay</c> and
        /// <c>pro_drums</c> that place and read it. Not the offset, which <see cref="BuildScene"/> applies.
        /// </summary>
        private static string BuildKey(HighwaySong song) =>
            string.Join('|', song.Draft.Vocals switch { null => "=", { SourcePath: null } => "-", { SourcePath: { } path } => "+" + path },
                First(song.Metadata, "delay"), ChartReadOptions.From(song.Metadata).ProDrums);

        private static string? First(IReadOnlyList<KeyValuePair<string, string>> pairs, string key) =>
            pairs.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

        /// <summary>Reads the vocals with the draft's change applied, off the UI thread, then shows them.</summary>
        private async Task BuildAsync()
        {
            if (_song is not { } song || _bytes is not { } bytes || _chartName is not { } name)
                return;

            int version = ++_version;
            _builtKey = BuildKey(song);
            var change = song.Draft.Vocals;
            var metadata = song.Metadata;
            var cached = change?.SourcePath is { } path ? _sources.GetValueOrDefault(path) : null;

            Built result;
            try
            {
                result = await Task.Run(async () =>
                {
                    var options = ChartReadOptions.From(metadata);
                    var original = VocalReader.Read(bytes, name);
                    if (change is null)
                        return new Built(original, original, SongChartSettings.DelayOf(metadata, ChartReader.Read(bytes, name, options)), false, null, null);

                    try
                    {
                        var source = change.Value.SourcePath is { } from ? cached ?? await VocalsSource.LoadAsync(from) : null;
                        var edited = VocalsEditor.Apply(bytes, name, metadata, source?.Chart);
                        var shown = VocalReader.Read(edited.Bytes, name);
                        var delay = SongChartSettings.DelayOf(metadata, ChartReader.Read(edited.Bytes, name, options));
                        return new Built(original, shown, delay, edited.LyricsOnly, source, null);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // ChartFormatException is an IOException: the vocals chosen can't go in this chart.
                        return new Built(original, null, TimeSpan.Zero, false, null, ex.Message);
                    }
                });
            }
            catch (ChartFormatException ex)
            {
                if (version == _version)
                {
                    _shown = null;
                    Scene = null;
                    ChartHasVocals = false;
                    Note = string.Format(CultureInfo.CurrentCulture, Strings.LyricsUnreadableFormat, ex.Message);
                }

                return;
            }

            // A different chart, or none, bumps the version; the draft may have moved on since.
            if (version != _version || _song is null)
                return;

            if (change?.SourcePath is { } chosen && result.Source is { } loaded)
                _sources[chosen] = loaded;

            ChartHasVocals = result.Original.HasVocals;
            Warning = result.LyricsOnly ? Strings.LyricsChartKeepsWordsOnly : null;
            SourceName = change?.SourcePath is { } picked ? Path.GetFileName(picked) : result.Original.HasVocals ? name : null;
            _shown = result.Shown is { HasVocals: true } shownVocals ? shownVocals : null;
            _delay = result.Delay;
            BuildScene();
            Note = result.Error is { } error ? string.Format(CultureInfo.CurrentCulture, Strings.LyricsChangeFailedFormat, error)
                : Scene is not null ? null
                : change is { IsRemoval: true } ? Strings.LyricsWillBeRemoved
                : Strings.NoLyrics;
            RefreshState();
        }

        /// <summary>Draws the vocals shown, moved by the draft's offset; says so when that moves them before the start.</summary>
        private void BuildScene()
        {
            if (_shown?.Main is not { } part)
            {
                Scene = null;
                Problem = null;
                return;
            }

            int offset = _song?.Draft.VocalsOffset ?? 0;
            var scene = new LyricScene(part, _shown.Tempo, _delay + TimeSpan.FromMilliseconds(offset));
            Scene = scene.IsEmpty ? null : scene;

            // Saving places each event by its tick, which can't go before the first; see VocalsEditor.
            Problem = offset < 0 && FirstSeconds(_shown) + (offset / 1000.0) < 0
                ? string.Format(CultureInfo.CurrentCulture, Strings.LyricsOffsetBeforeStartFormat, -offset)
                : null;
        }

        /// <summary>When the first event of any vocal track is, in song time from the first tick.</summary>
        private static double FirstSeconds(VocalChart vocals)
        {
            long first = vocals.Parts
                .SelectMany(p => p.Notes.Select(n => n.Tick).Concat(p.Lyrics.Select(l => l.Tick)).Concat(p.Phrases.Select(f => f.Tick)))
                .DefaultIfEmpty()
                .Min();
            return vocals.Tempo.SecondsAt(first);
        }

        /// <param name="Original">The vocals of the chart as it is.</param>
        /// <param name="Shown">The vocals it will have once the change is saved, or <see langword="null"/> when it can't be made.</param>
        /// <param name="Source">The chart the vocals were chosen from, read for this build.</param>
        /// <param name="Error">Why the change can't be made.</param>
        private sealed record Built(VocalChart Original, VocalChart? Shown, TimeSpan Delay, bool LyricsOnly, VocalsSource? Source, string? Error);
    }
}
