using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Collections;
using ChartPacker.Audio.Waveforms;
using ChartPacker.Controls;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Library;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Scanning;
using ChartPacker.Resources;
using ChartPacker.Services;
using ChartPacker.UI.Controls.Highway;
using ChartPacker.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartPacker.ViewModels
{
    /// <summary>The one selected song, as the note highway needs it.</summary>
    /// <param name="Metadata">Its metadata with the draft applied, for <c>delay</c> and <c>pro_drums</c>.</param>
    /// <param name="StoredFiles">The names of its files as stored.</param>
    /// <param name="ChartShifts">
    /// How far to draw each instrument difficulty moved on song time, in milliseconds, later when positive, on top of the draft's
    /// offsets, without that being an offset of the song's: the merge preview lining its songs up live, from the chart it last read.
    /// </param>
    /// <param name="VocalsShift">How far to draw the vocals moved, as <paramref name="ChartShifts"/> moves the instruments.</param>
    public sealed record HighwaySong(
        SongEntry Entry,
        MetadataDraft Draft,
        IReadOnlyList<KeyValuePair<string, string>> Metadata,
        IReadOnlyList<string> StoredFiles,
        IReadOnlyDictionary<ChartTrackKey, int>? ChartShifts = null,
        int VocalsShift = 0);

    /// <summary>A difficulty in an instrument's picker, labelled with its pending offset if it has one.</summary>
    public sealed record DifficultyOption(ChartDifficulty Difficulty, string Label);

    /// <summary>
    /// The note highway, drawn over the background tile or in its own row below the lyrics: the one selected song's chart, as its draft
    /// would save it, with a highway for each of its instruments, each at its own difficulty.
    /// </summary>
    /// <remarks>
    /// Offsets are edited per instrument difficulty straight into the song's <see cref="MetadataDraft"/>,
    /// and <see cref="OffsetsChanged"/> tells the details panel so. What's drawn is the chart
    /// <see cref="ChartShifter"/> makes from the draft, read back, so it's exactly what saving writes.
    /// Every instrument is shown until it's unchecked, which lasts for that song until the app closes.
    /// Which difficulties are shown, and the note speed, are app settings. Each highway draws the waveform
    /// of the track its instrument is played against, or of one picked in its place, which likewise lasts
    /// for that song until the app closes.
    /// </remarks>
    public sealed partial class HighwayViewModel : ViewModelBase
    {
        // How far ahead of the strike line the highway shows at the normal note speed, in seconds.
        private const double BaseLookAhead = 1.4;

        // The notes stepped through without a chart: 120 BPM from the start, as a chart is before its first tempo.
        private static readonly TempoMap NoChartTempo = new(192, [], []);

        private readonly SettingsService _settings;
        private HighwaySong? _song;

        // The parts of a song without a chart; one instance, so setting it again doesn't notify.
        private static readonly IReadOnlyDictionary<string, ChartPart> NoParts = new Dictionary<string, ChartPart>();

        // The chart being shown, by where it's read from, and its bytes once read.
        private string? _sourceKey;
        private string? _chartName;
        private byte[]? _bytes;

        // Set by ReloadChart: the next chart read keeps what's drawn until it's rebuilt from what's read.
        private bool _keepScene;

        // The chart as stored, which the rows are built from, and as the draft moves it, which is drawn.
        private ChartDocument? _original;
        private (byte[] Bytes, ChartReadOptions Options)? _originalOf;
        private ChartDocument? _shown;
        private TimeSpan _delay;

        // Bumped for each load and rebuild, so only the latest one is applied.
        private int _version;

        // What the latest rebuild was of, so a draft change that touches neither doesn't rebuild.
        private Dictionary<ChartTrackKey, int>? _builtOffsets;
        private ChartReadOptions? _builtOptions;

        // One rebuild runs at a time; changes while it runs make one more, of the draft as it is then.
        private bool _rebuilding;
        private bool _rebuildPending;

        // Set while the rows are changed from here, so that doesn't read as the user changing them.
        private bool _isSyncing;

        // The instruments unchecked for each song, by its path. Never saved.
        private readonly Dictionary<string, HashSet<ChartInstrument>> _hidden = new(StringComparer.Ordinal);

        // The track picked for each instrument's waveform in place of its own, for each song, by its path. Never saved.
        private readonly Dictionary<string, Dictionary<ChartInstrument, string>> _waveformStems = new(StringComparer.Ordinal);

        // The instrument of each of the scene's highways, in order.
        private List<ChartInstrument> _laneInstruments = [];

        // The stems each of the scene's highways draws the waveform of, in order; empty while none are drawn.
        private List<IReadOnlyList<string>> _laneStems = [];

        // Plays the song, has read its tracks' waveforms, and says whether they're drawn.
        private readonly PlayerViewModel _player;

        /// <param name="player">Plays the song, and has read its tracks' waveforms.</param>
        public HighwayViewModel(SettingsService settings, PlayerViewModel player)
        {
            _settings = settings;
            _player = player;
            Clock = () => player.DrawnPosition;
            IsEnabled = settings.Settings.ShowNoteHighway;
            Placement = settings.Settings.HighwayPlacement;
            NoteSpeed = settings.Settings.HighwayNoteSpeed;
            player.TrackWaveformsChanged += (_, e) =>
            {
                // More read of a track only changes what's drawn, and only for the highways drawing it.
                if (e.Stems is null)
                    RefreshWaveforms();
                else if (e.Touches(_laneStems.SelectMany(s => s)))
                    RefreshLaneWaveforms();
            };
            player.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PlayerViewModel.AreLaneWaveformsShown))
                    RefreshLaneWaveforms();
            };
        }

        /// <summary>Raised when an offset is set in the draft of the song shown.</summary>
        public event EventHandler? OffsetsChanged;

        /// <summary>Where the audio is.</summary>
        public Func<TimeSpan> Clock { get; }

        /// <summary>One row per instrument the chart has, in display order.</summary>
        public AvaloniaList<HighwayRowViewModel> Rows { get; } = [];

        /// <summary>Whether the highway is drawn; the caption's toggle.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsShown))]
        [NotifyPropertyChangedFor(nameof(IsShownOverBackground))]
        [NotifyPropertyChangedFor(nameof(IsTileShown))]
        public partial bool IsEnabled { get; set; }

        /// <summary>Where the highway is drawn: over the background, or in its own row below the lyrics.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsOverBackground))]
        [NotifyPropertyChangedFor(nameof(IsInRow))]
        [NotifyPropertyChangedFor(nameof(IsShownOverBackground))]
        public partial HighwayPlacement Placement { get; set; }

        /// <summary>How fast the notes fall, as a multiple of the normal speed.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LookAhead))]
        public partial double NoteSpeed { get; set; }

        /// <summary>What's drawn, or <see langword="null"/> for nothing.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsShown))]
        [NotifyPropertyChangedFor(nameof(IsShownOverBackground))]
        public partial HighwayScene? Scene { get; set; }

        /// <summary>
        /// The waveform drawn behind each of the <see cref="Scene"/>'s highways, in order, or <see langword="null"/> for
        /// one without; the whole list is <see langword="null"/> while lane waveforms aren't shown.
        /// </summary>
        [ObservableProperty]
        public partial IReadOnlyList<Waveform?>? LaneWaveforms { get; private set; }

        /// <summary>Why there's no chart to show, if there isn't.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasNote))]
        public partial string? Note { get; set; }

        /// <summary>Whether the offsets can be changed; not while the song is saved, or when it can't be.</summary>
        [ObservableProperty]
        public partial bool CanEditOffsets { get; set; }

        /// <summary>A problem with the offsets: they can't be applied, or they change notes' markings.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasProblem))]
        public partial string? Problem { get; set; }

        /// <summary>
        /// The parts the song's chart has, by <see cref="Core.Merging.MidiPart.Id"/>, with the difficulties
        /// each has notes for and how many: empty when it has no chart, and <see langword="null"/> while that isn't known,
        /// as with no song or several, while the chart is read, or when it can't be.
        /// </summary>
        [ObservableProperty]
        public partial IReadOnlyDictionary<string, ChartPart>? ChartParts { get; private set; }

        /// <summary>
        /// The offset the chart carries itself, which plays as the <c>delay</c> while song.ini sets none: zero for a
        /// chart without one, and <see langword="null"/> while there's no chart read, as with no song or several.
        /// </summary>
        [ObservableProperty]
        public partial TimeSpan? ChartOffset { get; private set; }

        /// <summary>How far ahead of the strike line the highway shows, in seconds.</summary>
        public double LookAhead => BaseLookAhead / Math.Clamp(NoteSpeed, AppSettings.MinHighwayNoteSpeed, AppSettings.MaxHighwayNoteSpeed);

        public bool HasChart => _original is not null;

        public bool HasNote => Note is not null;

        public bool HasProblem => Problem is not null;

        public bool IsShown => IsEnabled && Scene is { Lanes.Count: > 0 };

        public bool IsOverBackground => Placement == HighwayPlacement.Background;

        public bool IsInRow => Placement == HighwayPlacement.Row;

        /// <summary>Whether the highway is drawn over the background tile now.</summary>
        public bool IsShownOverBackground => IsShown && IsOverBackground;

        /// <summary>Whether the highway's own row has its tile: while there's a chart and it's switched on.</summary>
        public bool IsTileShown => IsEnabled && HasChart;

        /// <summary>Whether the song shown has an offset to save.</summary>
        public bool IsChanged => _song?.Draft.HasChartOffsets == true;

        public string? Marker => IsChanged ? "*" : null;

        public string ToggleToolTip => HasChart ? Strings.HighwayToggleToolTip : Strings.HighwayNoChartToolTip;

        /// <summary>
        /// The song time of the note <paramref name="steps"/> on from <paramref name="seconds"/>, or back when negative, on a
        /// grid of notes <paramref name="noteQuarters"/> quarter notes long by the chart shown as it plays against the audio;
        /// without one, at 120 BPM.
        /// </summary>
        public double StepNotes(double seconds, int steps, double noteQuarters)
        {
            if (_shown is not { } chart)
                return NoChartTempo.StepNotes(seconds, steps, noteQuarters);

            double delay = _delay.TotalSeconds;
            return chart.Tempo.StepNotes(seconds - delay, steps, noteQuarters) + delay;
        }

        /// <summary>Forgets the song and its chart, so the next <see cref="Refresh"/> reads it afresh, as after saving.</summary>
        public void Unload()
        {
            _song = null;
            Clear(null);
        }

        /// <summary>
        /// Has the next <see cref="Refresh"/> read the chart afresh, as after it was written again in place, while what's
        /// drawn stays until the chart read is rebuilt: the merge preview staged again, so it doesn't blink.
        /// </summary>
        public void ReloadChart()
        {
            _version++;
            _sourceKey = null;
            _chartName = null;
            _bytes = null;
            _builtOffsets = null;
            _keepScene = _original is not null;
        }

        /// <summary>
        /// Shows <paramref name="song"/>'s chart, with its draft's offsets; <see langword="null"/> shows
        /// nothing, as for no selection or several songs. The chart is read again only when it's a
        /// different file: the draft adding one, or a different song, or after <see cref="ReloadChart"/>.
        /// </summary>
        public void Refresh(HighwaySong? song)
        {
            _song = song;
            if (song is null)
            {
                Clear(null);
                return;
            }

            if (DraftChart.Of(song.Draft, song.StoredFiles) is not { } chart)
            {
                Clear(Strings.HighwayNoChart);
                ChartParts = NoParts;
                return;
            }

            string key = chart.StagedPath ?? $"{song.Entry.FullPath}|{chart.Name}";
            if (key != _sourceKey)
            {
                // Read again in place, what's drawn stays until it's rebuilt; anything else starts afresh.
                if (_keepScene)
                    _version++;
                else
                    Clear(null);
                _keepScene = false;
                _sourceKey = key;
                _ = LoadAsync(song.Entry, chart, key);
            }
            else if (_bytes is not null)
            {
                // Most draft changes are to metadata, of which only the delay changes what's drawn.
                if (IsBuiltFor(song))
                    RefreshDelay();
                else
                    RequestRebuild();
            }

            RefreshState();
        }

        /// <summary>Takes up the settings again, as after another highway sharing them changed them: whether it's shown, where, the speed and the difficulties.</summary>
        public void ReloadSettings()
        {
            IsEnabled = _settings.Settings.ShowNoteHighway;
            Placement = _settings.Settings.HighwayPlacement;
            NoteSpeed = _settings.Settings.HighwayNoteSpeed;
            if (_original is { } chart)
            {
                BuildRows(chart);
                SyncRows();
                BuildScene();
            }
        }

        partial void OnIsEnabledChanged(bool value) => _settings.Settings.ShowNoteHighway = value;

        partial void OnPlacementChanged(HighwayPlacement value) => _settings.Settings.HighwayPlacement = value;

        partial void OnNoteSpeedChanged(double value) => _settings.Settings.HighwayNoteSpeed = value;

        partial void OnCanEditOffsetsChanged(bool value)
        {
            foreach (var row in Rows)
                row.OnCanEditChanged();
        }

        private void Clear(string? note)
        {
            _version++;
            _sourceKey = null;
            _chartName = null;
            _bytes = null;
            _keepScene = false;
            _original = null;
            _originalOf = null;
            _shown = null;
            _builtOffsets = null;
            ChartParts = null;
            ChartOffset = null;
            Rows.Clear();
            Scene = null;
            _laneInstruments = [];
            _laneStems = [];
            LaneWaveforms = null;
            Problem = null;
            Note = note;
            RefreshState();
        }

        private async Task LoadAsync(SongEntry entry, DraftChart chart, string key)
        {
            int version = _version;
            byte[]? bytes;
            try
            {
                bytes = await Task.Run(() => chart.ReadAsync(entry));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (version == _version)
                    ShowUnread(key, string.Format(CultureInfo.CurrentCulture, Strings.HighwayUnreadableFormat, ex.Message));
                return;
            }

            if (version != _version || key != _sourceKey)
                return;
            if (bytes is null)
            {
                ShowUnread(key, Strings.HighwayNoChart);
                ChartParts = NoParts;
                return;
            }

            _bytes = bytes;
            _chartName = chart.Name;
            RequestRebuild();

            // Read alongside the rebuild; the parts don't depend on the offsets or pro_drums. The rebuild
            // bumps the version, so it's these bytes still being shown that says the parts are current.
            var parts = await Task.Run(() => ChartContents.ReadParts(bytes, chart.Name));
            if (key == _sourceKey && ReferenceEquals(bytes, _bytes))
                ChartParts = parts;
        }

        /// <summary>
        /// Shows <paramref name="note"/> for the chart at <paramref name="key"/>, which wasn't read: nothing drawn before it was
        /// read again stays. It stays known as read, so it isn't read again until it's a different one.
        /// </summary>
        private void ShowUnread(string key, string note)
        {
            Clear(note);
            _sourceKey = key;
        }

        /// <summary>
        /// Whether what's shown, or the rebuild under way, is of <paramref name="song"/>'s offsets and the
        /// settings it's read with: <c>pro_drums</c>, <c>sustain_cutoff_threshold</c> and <c>hopo_frequency</c>.
        /// </summary>
        private bool IsBuiltFor(HighwaySong song)
        {
            var offsets = DisplayOffsets(song);
            return _builtOffsets is { } built
                && _builtOptions == ChartReadOptions.From(song.Metadata)
                && built.Count == offsets.Count
                && built.All(p => offsets.TryGetValue(p.Key, out int ms) && ms == p.Value);
        }

        /// <summary>The draft's offsets with the song's <see cref="HighwaySong.ChartShifts"/> on top: how far each track is drawn moved.</summary>
        private static Dictionary<ChartTrackKey, int> DisplayOffsets(HighwaySong song)
        {
            var offsets = new Dictionary<ChartTrackKey, int>(song.Draft.ChartOffsets);
            if (song.ChartShifts is { } shifts)
            {
                foreach (var (key, ms) in shifts)
                    offsets[key] = offsets.GetValueOrDefault(key) + ms;
            }

            return offsets;
        }

        /// <summary>Draws the chart shown against the song's <c>delay</c> as drafted now.</summary>
        private void RefreshDelay()
        {
            if (_song is not { } song || _original is not { } original)
                return;

            var delay = SongChartSettings.DelayOf(song.Metadata, original);
            if (delay != _delay)
            {
                _delay = delay;
                BuildScene();
            }
        }

        private void RequestRebuild()
        {
            _rebuildPending = true;
            if (!_rebuilding)
                _ = RebuildWhilePendingAsync();
        }

        private async Task RebuildWhilePendingAsync()
        {
            _rebuilding = true;
            try
            {
                while (_rebuildPending)
                {
                    _rebuildPending = false;
                    await RebuildAsync();
                }
            }
            finally
            {
                _rebuilding = false;
            }
        }

        /// <summary>Reads the chart with the draft's offsets applied, off the UI thread, then shows it.</summary>
        private async Task RebuildAsync()
        {
            if (_song is not { } song || _bytes is not { } bytes || _chartName is not { } name)
                return;

            int version = ++_version;
            var offsets = DisplayOffsets(song);
            var options = ChartReadOptions.From(song.Metadata);
            _builtOffsets = offsets;
            _builtOptions = options;
            var cached = _originalOf is { } of && ReferenceEquals(of.Bytes, bytes) && of.Options == options ? _original : null;

            // Markings a shift changes are a problem only where the song has an offset of its own: a shift is just how it's drawn.
            var ownOffsets = song.ChartShifts is { Count: > 0 } ? song.Draft.ChartOffsets.Keys.ToHashSet() : null;

            (ChartDocument Original, ChartDocument Shown, string? Problem) result;
            try
            {
                result = await Task.Run(() =>
                {
                    var original = cached ?? ChartReader.Read(bytes, name, options);
                    try
                    {
                        var shifted = ChartShifter.Shift(bytes, name, offsets, options);
                        var changed = shifted.ChangedMarkings.Where(k => ownOffsets?.Contains(k) != false).ToList();
                        string? problem = changed.Count == 0 ? null : string.Format(
                            CultureInfo.CurrentCulture, Strings.HighwayMarkingsChangedFormat, string.Join(", ", changed.Select(TrackName)));
                        return (original, shifted.Document, problem);
                    }
                    catch (ChartFormatException ex)
                    {
                        return (original, original, (string?)string.Format(CultureInfo.CurrentCulture, Strings.HighwayOffsetErrorFormat, ex.Message));
                    }
                });
            }
            catch (ChartFormatException ex)
            {
                // The chart stays known as read, so it isn't read again until it's a different one.
                if (version == _version)
                {
                    string? key = _sourceKey;
                    Clear(string.Format(CultureInfo.CurrentCulture, Strings.HighwayUnreadableFormat, ex.Message));
                    _sourceKey = key;
                }

                return;
            }

            // A different chart, or none, bumps the version; the song's metadata may have moved on since.
            if (version != _version || _song is not { } current)
                return;

            _shown = result.Shown;
            _delay = SongChartSettings.DelayOf(current.Metadata, result.Original);
            Problem = result.Problem;
            Note = null;
            if (!ReferenceEquals(result.Original, _original))
            {
                // Read again with the same instruments, as when the merge preview is staged again, the rows stay as they are.
                if (!HasRowsFor(result.Original))
                    BuildRows(result.Original);
                _original = result.Original;
                _originalOf = (bytes, options);
            }

            ChartOffset = result.Original.Offset ?? TimeSpan.Zero;

            SyncRows();
            BuildScene();
            RefreshState();
        }

        /// <summary>
        /// A row per instrument, checked unless it was unchecked for this song; each at the difficulty
        /// last chosen for it, or the nearest the chart has.
        /// </summary>
        private void BuildRows(ChartDocument chart)
        {
            var hidden = _song is { } song ? _hidden.GetValueOrDefault(song.Entry.FullPath) : null;

            _isSyncing = true;
            Rows.Clear();
            foreach (var instrument in chart.Instruments)
            {
                var difficulties = chart.DifficultiesOf(instrument);
                var wanted = _settings.Settings.HighwayDifficulties?.GetValueOrDefault(instrument.ToString()) is { } saved
                    && Enum.TryParse<ChartDifficulty>(saved, out var d) ? d : ChartDifficulty.Expert;
                var difficulty = difficulties.Contains(wanted) ? wanted : difficulties.OrderBy(x => Math.Abs((int)x - (int)wanted)).ThenByDescending(x => x).First();
                Rows.Add(new HighwayRowViewModel(this, instrument, difficulties, difficulty, hidden?.Contains(instrument) != true));
            }

            _isSyncing = false;
        }

        /// <summary>Whether there's a row for each of <paramref name="chart"/>'s instruments, in order, with the difficulties it has.</summary>
        private bool HasRowsFor(ChartDocument chart) =>
            Rows.Count == chart.Instruments.Count
            && Rows.Zip(chart.Instruments).All(p => p.First.Instrument == p.Second && p.First.HasDifficulties(chart.DifficultiesOf(p.Second)));

        /// <summary>Brings each row's offset and difficulty labels up to date with the draft.</summary>
        private void SyncRows()
        {
            if (_song is not { } song)
                return;

            _isSyncing = true;
            foreach (var row in Rows)
                row.Sync(song.Draft.ChartOffsets);
            _isSyncing = false;
        }

        private void BuildScene()
        {
            var lanes = new List<HighwayLane>();
            var instruments = new List<ChartInstrument>();
            if (_shown is { } chart)
            {
                foreach (var row in Rows.Where(r => r.IsChecked))
                {
                    if (!chart.Tracks.TryGetValue(row.Key, out var track))
                        continue;

                    var kind = ChartInstruments.KindOf(row.Instrument);
                    lanes.Add(new HighwayLane(kind, HighwayLane.LaneCountOf(kind, chart.IsFiveLaneDrums), TrackName(row.Key), track.Notes, chart.Tempo, _delay));
                    instruments.Add(row.Instrument);
                }
            }

            _laneInstruments = instruments;
            Scene = lanes.Count > 0 && _shown is { } drawn ? new HighwayScene(lanes, drawn.Tempo, _delay) : null;
            RefreshWaveforms();
        }

        /// <summary>
        /// Brings each row's waveform picker up to date with the song's tracks, and picks the waveform drawn behind
        /// each highway: none unless lane waveforms are shown and the player has this song's tracks.
        /// </summary>
        private void RefreshWaveforms()
        {
            // The song's stems once for every row: only which is each row's own differs.
            var stems = LaneWaveformChoice.StemsOf(LaneWaveformChoice.TracksOf(_player, _song?.Entry));
            var chosen = ChosenStems();
            foreach (var row in Rows)
                row.WaveformPicker.Sync(LaneWaveformChoice.OptionsFor(stems, LaneStems.For(row.Instrument)), chosen?.GetValueOrDefault(row.Instrument));

            RefreshLaneWaveforms();
        }

        /// <summary>Picks the waveform drawn behind each highway, as the pickers stand: none unless lane waveforms are shown and the player has this song's tracks.</summary>
        private void RefreshLaneWaveforms()
        {
            var chosen = ChosenStems();
            bool isShown = LaneWaveformChoice.HasTracksOf(_player, _song?.Entry) && _player.AreLaneWaveformsShown && Scene is not null;
            _laneStems = isShown ? [.. _laneInstruments.Select(i => LaneWaveformChoice.StemsFor(_player, LaneStems.For(i), chosen?.GetValueOrDefault(i)))] : [];
            LaneWaveforms = isShown ? [.. _laneStems.Select(stems => LaneWaveformChoice.Resolve(_player, stems))] : null;
        }

        /// <summary>The tracks picked in place of the instruments' own for the song shown, if any were.</summary>
        private Dictionary<ChartInstrument, string>? ChosenStems() =>
            _song is { } song ? _waveformStems.GetValueOrDefault(song.Entry.FullPath) : null;

        internal void OnRowWaveformChanged(HighwayRowViewModel row)
        {
            if (_isSyncing || _song is not { } song)
                return;

            if (!_waveformStems.TryGetValue(song.Entry.FullPath, out var chosen))
                _waveformStems[song.Entry.FullPath] = chosen = [];
            if (row.WaveformPicker.Stem is { } stem)
                chosen[row.Instrument] = stem;
            else
                chosen.Remove(row.Instrument);
            RefreshLaneWaveforms();
        }

        internal void OnRowChecked(HighwayRowViewModel row)
        {
            if (_isSyncing || _song is not { } song)
                return;

            if (!_hidden.TryGetValue(song.Entry.FullPath, out var hidden))
                _hidden[song.Entry.FullPath] = hidden = [];
            if (row.IsChecked)
                hidden.Remove(row.Instrument);
            else
                hidden.Add(row.Instrument);
            BuildScene();
        }

        internal void OnRowDifficultyChanged(HighwayRowViewModel row)
        {
            if (_isSyncing)
                return;

            var difficulties = _settings.Settings.HighwayDifficulties ??= [];
            difficulties[row.Instrument.ToString()] = row.Difficulty.ToString();
            SyncRows();
            BuildScene();
        }

        internal void SetOffset(HighwayRowViewModel row, int milliseconds)
        {
            if (_isSyncing || _song is not { } song)
                return;

            // The box is disabled then; this puts back a value that got in anyway.
            if (!CanEditOffsets)
            {
                SyncRows();
                return;
            }

            song.Draft.SetChartOffset(row.Key, milliseconds);
            SyncRows();
            RequestRebuild();
            RefreshState();
            OffsetsChanged?.Invoke(this, EventArgs.Empty);
        }

        private void RefreshState()
        {
            OnPropertyChanged(nameof(HasChart));
            OnPropertyChanged(nameof(IsTileShown));
            OnPropertyChanged(nameof(IsChanged));
            OnPropertyChanged(nameof(Marker));
            OnPropertyChanged(nameof(ToggleToolTip));
        }

        private static string TrackName(ChartTrackKey key) =>
            string.Format(CultureInfo.CurrentCulture, Strings.HighwayTrackFormat, ChartLabels.DifficultyName(key.Difficulty), ChartLabels.InstrumentName(key.Instrument));
    }

    /// <summary>An instrument in the highway's options: whether it's shown, at which difficulty, and that difficulty's offset.</summary>
    public sealed partial class HighwayRowViewModel : ViewModelBase
    {
        private readonly HighwayViewModel _owner;
        private readonly IReadOnlyList<ChartDifficulty> _difficulties;

        internal HighwayRowViewModel(HighwayViewModel owner, ChartInstrument instrument, IReadOnlyList<ChartDifficulty> difficulties, ChartDifficulty difficulty, bool isChecked)
        {
            _owner = owner;
            _difficulties = difficulties;
            Instrument = instrument;
            Name = ChartLabels.InstrumentName(instrument);
            Difficulties = [.. difficulties.Select(d => new DifficultyOption(d, ChartLabels.DifficultyName(d)))];
            DifficultyIndex = Math.Max(0, IndexOf(difficulty));
            IsChecked = isChecked;
            WaveformPicker = new LaneWaveformPickerViewModel(() => owner.OnRowWaveformChanged(this));
        }

        public ChartInstrument Instrument { get; }

        public string Name { get; }

        /// <summary>The difficulties the chart has for the instrument, easiest first.</summary>
        [ObservableProperty]
        public partial IReadOnlyList<DifficultyOption> Difficulties { get; set; }

        [ObservableProperty]
        public partial int DifficultyIndex { get; set; }

        [ObservableProperty]
        public partial bool IsChecked { get; set; }

        /// <summary>The picked difficulty's offset in milliseconds, later when positive.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasOffset))]
        [NotifyCanExecuteChangedFor(nameof(RevertOffsetCommand))]
        public partial decimal? Offset { get; set; } = 0;

        public bool HasOffset => Offset is { } offset && offset != 0;

        /// <summary>Whether the offset can be changed; see <see cref="HighwayViewModel.CanEditOffsets"/>.</summary>
        public bool CanEdit => _owner.CanEditOffsets;

        public ChartDifficulty Difficulty => _difficulties[Math.Clamp(DifficultyIndex, 0, _difficulties.Count - 1)];

        public ChartTrackKey Key => new(Instrument, Difficulty);

        /// <summary>Which track the highway's waveform is drawn from: the instrument's own, or another picked for this song.</summary>
        public LaneWaveformPickerViewModel WaveformPicker { get; }

        private bool CanRevertOffset() => HasOffset && CanEdit;

        [RelayCommand(CanExecute = nameof(CanRevertOffset))]
        private void RevertOffset() => Offset = 0;

        internal void OnCanEditChanged()
        {
            OnPropertyChanged(nameof(CanEdit));
            RevertOffsetCommand.NotifyCanExecuteChanged();
        }

        /// <summary>Shows <paramref name="offsets"/>: the picked difficulty's in the box, and each difficulty's in its label.</summary>
        internal void Sync(IReadOnlyDictionary<ChartTrackKey, int> offsets)
        {
            var options = _difficulties
                .Select(d => new DifficultyOption(d, offsets.TryGetValue(new ChartTrackKey(Instrument, d), out int ms)
                    ? string.Format(CultureInfo.CurrentCulture, Strings.HighwayDifficultyOffsetFormat, ChartLabels.DifficultyName(d), ms)
                    : ChartLabels.DifficultyName(d)))
                .ToList();
            if (!options.SequenceEqual(Difficulties))
            {
                // A new list resets the picker's selection, so it's put back.
                int index = DifficultyIndex;
                Difficulties = options;
                DifficultyIndex = index;
            }

            Offset = offsets.GetValueOrDefault(Key);
        }

        /// <summary>Whether the row offers exactly <paramref name="difficulties"/>.</summary>
        internal bool HasDifficulties(IEnumerable<ChartDifficulty> difficulties) => _difficulties.SequenceEqual(difficulties);

        private int IndexOf(ChartDifficulty difficulty)
        {
            for (int i = 0; i < _difficulties.Count; i++)
            {
                if (_difficulties[i] == difficulty)
                    return i;
            }

            return -1;
        }

        partial void OnIsCheckedChanged(bool value) => _owner.OnRowChecked(this);

        partial void OnDifficultyIndexChanged(int value)
        {
            if (value >= 0)
                _owner.OnRowDifficultyChanged(this);
        }

        // No limit of its own, as a chart can be off by any amount; only what the offset, in whole milliseconds, can hold.
        partial void OnOffsetChanged(decimal? value) =>
            _owner.SetOffset(this, (int)Math.Round(Math.Clamp(value ?? 0, int.MinValue, int.MaxValue)));
    }
}
