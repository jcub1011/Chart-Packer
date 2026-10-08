using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using ChartPacker.Audio.Waveforms;
using ChartPacker.Controls;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Merging;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Scanning;
using ChartPacker.Resources;
using ChartPacker.UI.ViewModels;
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
    /// Vocals that would start before the song does are read back moved later just enough to fit,
    /// and the lane moved back by as much, so none are drawn stacked on the first tick.
    /// The row shows while the song has a vocals track to sing along to, vocals in its chart, or a
    /// change to them to undo. The lane draws the vocals track's waveform behind the notes, or the
    /// waveform of a track picked in its place, which lasts for that song until the app closes.
    /// </remarks>
    public sealed partial class LyricsViewModel : ViewModelBase
    {
        private HighwaySong? _song;
        private bool _hasVocalStem;

        // The chart being shown, by where it's read from, and its bytes once read.
        private string? _chartKey;
        private string? _chartName;
        private byte[]? _bytes;

        // Set by ReloadChart: the next chart read keeps what's drawn until it's built from what's read.
        private bool _keepScene;

        // What the latest build was of: the chart, the vocals change and the metadata that places them.
        private string? _builtKey;

        // Bumped for each load and build, so only the latest one is applied.
        private int _version;

        // The vocals the draft's change leaves the chart with, before the offset, and where they're sung: by the
        // delay they were built with, which the chart's own offset stands in for while song.ini sets none.
        private VocalChart? _shown;
        private TimeSpan _delay;
        private TimeSpan? _chartOffset;

        // The chart _shown was read from, with the metadata that places it, for exporting what's drawn.
        private byte[]? _shownBytes;
        private IReadOnlyList<KeyValuePair<string, string>> _shownMetadata = [];

        // How much later than the change places them _shown was built, so they fit in the song; the lane takes it back off.
        private TimeSpan _lead;

        // When the first vocal event is sung before the offset, from the chart's first tick; saving needs it at or after zero.
        private TimeSpan? _start;

        // Set while the offset is changed from here, so that doesn't read as the user changing it.
        private bool _isSyncing;

        // The track picked for the lane's waveform in place of the vocals, for each song, by its path. Never saved.
        private readonly Dictionary<string, string> _waveformStems = new(StringComparer.Ordinal);

        // The stems the lane draws the waveform of; empty while it draws none.
        private IReadOnlyList<string> _laneStems = [];

        // Plays the song, has read its tracks' waveforms, and says whether they're drawn.
        private readonly PlayerViewModel _player;

        /// <param name="player">Plays the song, and has read its tracks' waveforms.</param>
        public LyricsViewModel(PlayerViewModel player)
        {
            _player = player;
            Clock = () => player.DrawnPosition;
            WaveformPicker = new LaneWaveformPickerViewModel(OnWaveformPicked);
            player.TrackWaveformsChanged += (_, e) =>
            {
                // More read of a track only changes what's drawn, and only when the lane draws it.
                if (e.Stems is null)
                    RefreshWaveform();
                else if (e.Touches(_laneStems))
                    RefreshLaneWaveform();
            };
            player.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PlayerViewModel.AreLaneWaveformsShown))
                    RefreshLaneWaveform();
            };
        }

        /// <summary>Raised when the offset is set in the draft of the song shown.</summary>
        public event EventHandler? OffsetChanged;

        /// <summary>Where the audio is.</summary>
        public Func<TimeSpan> Clock { get; }

        /// <summary>The waveform drawn behind the vocals, or <see langword="null"/> for none, as while lane waveforms aren't shown.</summary>
        [ObservableProperty]
        public partial Waveform? Waveform { get; private set; }

        /// <summary>Which track the lane's waveform is drawn from: the vocals' own, or another picked for this song.</summary>
        public LaneWaveformPickerViewModel WaveformPicker { get; }

        /// <summary>What's drawn, or <see langword="null"/> when there are no vocals to show.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasLyrics))]
        [NotifyPropertyChangedFor(nameof(ReplaceToolTip))]
        [NotifyPropertyChangedFor(nameof(CanExport))]
        [NotifyPropertyChangedFor(nameof(CanSetOffset))]
        [NotifyCanExecuteChangedFor(nameof(RevertOffsetCommand))]
        public partial LyricScene? Scene { get; set; }

        /// <summary>
        /// How much later than <see cref="Scene"/> says the lane draws the vocals: the offset, less any lead they were
        /// built with, plus how far the song's <c>delay</c> has moved since.
        /// </summary>
        [ObservableProperty]
        public partial TimeSpan TimeShift { get; set; }

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
        [NotifyPropertyChangedFor(nameof(CanSetOffset))]
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
        public bool CanSetOffset => CanEditOffset && HasLyrics;

        /// <summary>Whether the song shown has a change to its vocals to save: others in their place, or an offset.</summary>
        public bool IsChanged => _song?.Draft is { } draft && (draft.Vocals is not null || draft.HasVocalsOffset);

        public string? Marker => IsChanged ? "*" : null;

        /// <summary>The vocals drawn can be exported: there are some, as the draft's change leaves them.</summary>
        public bool CanExport => Scene is not null && _shownBytes is not null;

        /// <summary>
        /// Works out what's drawn as a <c>notes.mid</c> of its own: the vocals as the draft's change
        /// leaves them, moved by the offset, with the song's delay worked into their ticks. Safe to
        /// run off the UI thread; <see langword="null"/> when there's nothing to export.
        /// </summary>
        /// <remarks>The function throws a <see cref="ChartFormatException"/> when the vocals can't be written, e.g. moved before the song starts.</remarks>
        public Func<byte[]>? ExportSource()
        {
            if (!CanExport || _shownBytes is not { } bytes || _chartName is not { } name)
                return null;

            var metadata = _shownMetadata;
            var shift = TimeShift;
            return () => LyricsExport.ToMidi(bytes, name, metadata, shift);
        }

        public string ReplaceToolTip => HasLyrics ? Strings.LyricsReplaceToolTip : Strings.LyricsAddToolTip;

        /// <summary>Forgets the song and its chart, so the next <see cref="Refresh"/> reads it afresh, as after saving.</summary>
        public void Unload()
        {
            _song = null;
            _hasVocalStem = false;
            Clear(null);
            RefreshWaveform();
        }

        /// <summary>
        /// Has the next <see cref="Refresh"/> read the chart afresh, as after it was written again in place, while what's
        /// drawn stays until the chart read is built: the merge preview staged again, so it doesn't blink.
        /// </summary>
        public void ReloadChart()
        {
            _version++;
            _chartKey = null;
            _chartName = null;
            _bytes = null;
            _builtKey = null;
            _keepScene = _shown is not null || Scene is not null;
        }

        /// <summary>
        /// Shows <paramref name="song"/>'s vocals, with its draft's change and offset; <see langword="null"/>
        /// shows nothing, as for no selection or several songs. The chart is read again only when it's a
        /// different file, or after <see cref="ReloadChart"/>. The song's <see cref="HighwaySong.VocalsShift"/> moves the
        /// lane as the offset does, without being one.
        /// </summary>
        /// <param name="hasVocalStem">The song will have a vocals audio track.</param>
        public void Refresh(HighwaySong? song, bool hasVocalStem)
        {
            _song = song;
            _hasVocalStem = hasVocalStem;
            SyncOffset();
            RefreshWaveform();
            if (song is null || DraftChart.Of(song.Draft, song.StoredFiles) is not { } chart)
            {
                Clear(song is null ? null : Strings.LyricsNoChart);
                return;
            }

            HasChart = true;
            string key = chart.StagedPath ?? $"{song.Entry.FullPath}|{chart.Name}";
            if (key != _chartKey)
            {
                // Read again in place, what's drawn stays until it's built; anything else starts afresh.
                if (_keepScene)
                    _version++;
                else
                    Clear(null);
                _keepScene = false;
                HasChart = true;
                _chartKey = key;
                _ = LoadAsync(song, chart, key);
            }
            else if (_bytes is not null && BuildKey(song) != _builtKey)
            {
                _ = BuildAsync();
            }
            else if (_bytes is not null)
            {
                // Only the offset may have changed, as when the draft's offsets are cleared. Until the chart is read
                // there's nothing of it to move; what's still drawn of the chart read before stays as it was.
                ApplyOffset();
            }

            RefreshState();
        }

        private bool CanRevertOffset() => HasOffset && CanSetOffset;

        [RelayCommand(CanExecute = nameof(CanRevertOffset))]
        private void RevertOffset() => Offset = 0;

        partial void OnOffsetChanged(decimal? value)
        {
            if (_isSyncing || _song is not { } song)
                return;

            song.Draft.SetVocalsOffset((int)Math.Round(Math.Clamp(value ?? 0, int.MinValue, int.MaxValue)));
            ApplyOffset();
            RefreshState();
            OffsetChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnWaveformPicked()
        {
            if (_song is not { } song)
                return;

            if (WaveformPicker.Stem is { } stem)
                _waveformStems[song.Entry.FullPath] = stem;
            else
                _waveformStems.Remove(song.Entry.FullPath);
            RefreshLaneWaveform();
        }

        /// <summary>Brings the waveform picker up to date with the song's tracks, and picks the waveform drawn behind the vocals.</summary>
        private void RefreshWaveform()
        {
            var stems = LaneWaveformChoice.StemsOf(LaneWaveformChoice.TracksOf(_player, _song?.Entry));
            WaveformPicker.Sync(LaneWaveformChoice.OptionsFor(stems, LaneStems.Vocals), ChosenStem());
            RefreshLaneWaveform();
        }

        /// <summary>Picks the waveform drawn behind the vocals, as the picker stands: none unless lane waveforms are shown and the player has this song's tracks.</summary>
        private void RefreshLaneWaveform()
        {
            bool isShown = LaneWaveformChoice.HasTracksOf(_player, _song?.Entry) && _player.AreLaneWaveformsShown;
            _laneStems = isShown ? LaneWaveformChoice.StemsFor(_player, LaneStems.Vocals, ChosenStem()) : [];
            Waveform = isShown ? LaneWaveformChoice.Resolve(_player, _laneStems) : null;
        }

        /// <summary>The track picked in place of the vocals for the song shown, if one was.</summary>
        private string? ChosenStem() => _song is { } song ? _waveformStems.GetValueOrDefault(song.Entry.FullPath) : null;

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
            _keepScene = false;
            _builtKey = null;
            _shown = null;
            _shownBytes = null;
            _chartOffset = null;
            _lead = TimeSpan.Zero;
            _start = null;
            Scene = null;
            TimeShift = TimeSpan.Zero;
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
                    ShowUnread(key, string.Format(CultureInfo.CurrentCulture, Strings.LyricsUnreadableFormat, ex.Message));
                return;
            }

            if (version != _version || key != _chartKey)
                return;
            if (bytes is null)
            {
                ShowUnread(key, Strings.LyricsNoChart);
                return;
            }

            _bytes = bytes;
            _chartName = chart.Name;
            await BuildAsync();
        }

        /// <summary>
        /// Shows <paramref name="note"/> for the chart at <paramref name="key"/>, which wasn't read: nothing drawn before it was
        /// read again stays. It stays known as read, so it isn't read again until it's a different one.
        /// </summary>
        private void ShowUnread(string key, string note)
        {
            Clear(note);
            HasChart = true;
            _chartKey = key;
        }

        /// <summary>
        /// What a build depends on besides the chart: the vocals change, and the <c>pro_drums</c> that reads it, and
        /// for vocals taken from elsewhere the <c>delay</c> that places them. Not the offset, nor the delay of the
        /// chart's own vocals, which move every syllable alike, so <see cref="ApplyOffset"/> applies them, as the
        /// audio offset's drag sets the delay. The source counts by identity, so choosing the same file again reads its vocals afresh.
        /// </summary>
        private static string BuildKey(HighwaySong song) =>
            string.Join('|', song.Draft.Vocals switch { null => "=", { Source: null } => "-", { Source: { } source } => $"+{source.FilePath}#{RuntimeHelpers.GetHashCode(source)}" },
                song.Draft.Vocals is { Source: not null } ? First(song.Metadata, "delay") : null, ChartReadOptions.From(song.Metadata).ProDrums);

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

            Built result;
            try
            {
                result = await Task.Run(() =>
                {
                    // Changing the vocals leaves the tempo map and the delay as they are, so one read serves for both.
                    var chart = ChartReader.Read(bytes, name, ChartReadOptions.From(metadata));
                    var delay = SongChartSettings.DelayOf(metadata, chart);
                    var original = VocalReader.Read(bytes, name, chart.Tempo);
                    if (change is null)
                        return new Built(original, original, bytes, delay, chart.Offset, TimeSpan.Zero, VocalsEditor.StartOf(original, delay, delay), false, null);

                    try
                    {
                        // Vocals that would start before the song are built later just enough to fit, as saving can't place them.
                        var source = change.Value.Source;
                        var start = source is null ? null : VocalsEditor.StartOf(source.Vocals, source.Chart.Delay, delay);
                        var lead = start is { } early && early < TimeSpan.Zero ? TimeSpan.FromMilliseconds(Math.Ceiling(-early.TotalMilliseconds)) : TimeSpan.Zero;
                        var edited = VocalsEditor.Apply(bytes, name, metadata, source, lead);
                        return new Built(original, VocalReader.Read(edited.Bytes, name, chart.Tempo), edited.Bytes, delay, chart.Offset, lead, start, edited.LyricsOnly, null);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // ChartFormatException is an IOException: the vocals chosen can't go in this chart.
                        return new Built(original, null, null, delay, chart.Offset, TimeSpan.Zero, null, false, ex.Message);
                    }
                });
            }
            catch (ChartFormatException ex)
            {
                if (version == _version)
                {
                    _shown = null;
                    _shownBytes = null;
                    _start = null;
                    Scene = null;
                    Problem = null;
                    ChartHasVocals = false;
                    Note = string.Format(CultureInfo.CurrentCulture, Strings.LyricsUnreadableFormat, ex.Message);
                }

                return;
            }

            // A different chart, or none, bumps the version; the draft may have moved on since.
            if (version != _version || _song is null)
                return;

            ChartHasVocals = result.Original.HasVocals;
            Warning = result.LyricsOnly ? Strings.LyricsChartKeepsWordsOnly : null;
            SourceName = change?.SourcePath is { } picked ? Path.GetFileName(picked) : result.Original.HasVocals ? name : null;
            _shown = result.Shown is { HasVocals: true } shownVocals ? shownVocals : null;
            _shownBytes = _shown is null ? null : result.ShownBytes;
            _shownMetadata = metadata;
            _delay = result.Delay;
            _chartOffset = result.ChartOffset;
            _lead = result.Lead;
            _start = result.Start;
            BuildScene();
            ApplyOffset();
            Note = result.Error is { } error ? string.Format(CultureInfo.CurrentCulture, Strings.LyricsChangeFailedFormat, error)
                : Scene is not null ? null
                : change is { IsRemoval: true } ? Strings.LyricsWillBeRemoved
                : Strings.NoLyrics;
            RefreshState();
        }

        /// <summary>Draws the vocals shown, as the draft's change places them; the offset is <see cref="ApplyOffset"/>'s.</summary>
        private void BuildScene()
        {
            var scene = _shown?.Main is { } part ? new LyricScene(part, _shown.Tempo, _delay) : null;
            Scene = scene is { IsEmpty: false } ? scene : null;
        }

        /// <summary>
        /// Moves the lane by the draft's offset and the song's shift, and by how far the delay has moved since the vocals were built; says
        /// so when the offset puts the vocals before the start. The delay leaves that alone: it moves the song's first tick with them.
        /// </summary>
        private void ApplyOffset()
        {
            int offset = (_song?.Draft.VocalsOffset ?? 0) + (_song?.VocalsShift ?? 0);
            var delayMoved = _song is { } song && _shown is not null ? SongChartSettings.DelayOf(song.Metadata, _chartOffset) - _delay : TimeSpan.Zero;
            TimeShift = TimeSpan.FromMilliseconds(offset) - _lead + delayMoved;

            // Saving places each event by its tick, which can't go before the first; see VocalsEditor.
            double startsAt = _shown is not null && _start is { } start ? start.TotalMilliseconds + offset : 0;
            Problem = startsAt < 0
                ? string.Format(CultureInfo.CurrentCulture, Strings.LyricsOffsetBeforeStartFormat, Math.Ceiling(-startsAt))
                : null;
        }

        /// <param name="Original">The vocals of the chart as it is.</param>
        /// <param name="Shown">The vocals it will have once the change is saved, or <see langword="null"/> when it can't be made.</param>
        /// <param name="Lead">How much later than the change places them <paramref name="Shown"/> was built.</param>
        /// <param name="Start">When their first event is sung, from the chart's first tick, before the offset.</param>
        /// <param name="ShownBytes">The chart <paramref name="Shown"/> was read from.</param>
        /// <param name="ChartOffset">The chart's own offset, which places it while song.ini sets no <c>delay</c>.</param>
        /// <param name="Error">Why the change can't be made.</param>
        private sealed record Built(VocalChart Original, VocalChart? Shown, byte[]? ShownBytes, TimeSpan Delay, TimeSpan? ChartOffset, TimeSpan Lead, TimeSpan? Start, bool LyricsOnly, string? Error);
    }
}
