using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Collections;
using ChartPacker.Controls;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Metadata;
using ChartPacker.Core.Scanning;
using ChartPacker.Resources;
using ChartPacker.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartPacker.ViewModels
{
    /// <summary>The one selected song, as the note highway needs it.</summary>
    /// <param name="Metadata">Its metadata with the draft applied, for <c>delay</c> and <c>pro_drums</c>.</param>
    /// <param name="StoredFiles">The names of its files as stored.</param>
    public sealed record HighwaySong(SongEntry Entry, MetadataDraft Draft, IReadOnlyList<KeyValuePair<string, string>> Metadata, IReadOnlyList<string> StoredFiles);

    /// <summary>A difficulty in an instrument's picker, labelled with its pending offset if it has one.</summary>
    public sealed record DifficultyOption(ChartDifficulty Difficulty, string Label);

    /// <summary>
    /// The note highway drawn over the background tile: the one selected song's chart, as its draft
    /// would save it, with up to <see cref="MaxLanes"/> instruments chosen, each at its own difficulty.
    /// </summary>
    /// <remarks>
    /// Offsets are edited per instrument difficulty straight into the song's <see cref="MetadataDraft"/>,
    /// and <see cref="OffsetsChanged"/> tells the details panel so. What's drawn is the chart
    /// <see cref="ChartShifter"/> makes from the draft, read back, so it's exactly what saving writes.
    /// Which instruments and difficulties are shown, and the note speed, are app settings.
    /// </remarks>
    public sealed partial class HighwayViewModel : ViewModelBase
    {
        /// <summary>The most highways shown at once.</summary>
        public const int MaxLanes = 4;

        // How far ahead of the strike line the highway shows at the normal note speed, in seconds.
        private const double BaseLookAhead = 1.4;

        private readonly SettingsService _settings;

        private HighwaySong? _song;

        // The chart being shown, by where it's read from, and its bytes once read.
        private string? _sourceKey;
        private string? _chartName;
        private byte[]? _bytes;

        // The chart as stored, which the rows are built from, and as the draft moves it, which is drawn.
        private ChartDocument? _original;
        private (byte[] Bytes, bool ProDrums)? _originalOf;
        private ChartDocument? _shown;
        private TimeSpan _delay;

        // Bumped for each load and rebuild, so only the latest one is applied.
        private int _version;

        // Set while the rows are changed from here, so that doesn't read as the user changing them.
        private bool _isSyncing;

        public HighwayViewModel(SettingsService settings, Func<TimeSpan> clock)
        {
            _settings = settings;
            Clock = clock;
            IsEnabled = settings.Settings.ShowNoteHighway;
            NoteSpeed = settings.Settings.HighwayNoteSpeed;
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
        public partial bool IsEnabled { get; set; }

        /// <summary>How fast the notes fall, as a multiple of the normal speed.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LookAhead))]
        public partial double NoteSpeed { get; set; }

        /// <summary>What's drawn, or <see langword="null"/> for nothing.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsShown))]
        public partial HighwayScene? Scene { get; set; }

        /// <summary>Why there's no chart to show, if there isn't.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasNote))]
        public partial string? Note { get; set; }

        /// <summary>A problem with the offsets: they can't be applied, or they change notes' markings.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasProblem))]
        public partial string? Problem { get; set; }

        /// <summary>How far ahead of the strike line the highway shows, in seconds.</summary>
        public double LookAhead => BaseLookAhead / Math.Clamp(NoteSpeed, AppSettings.MinHighwayNoteSpeed, AppSettings.MaxHighwayNoteSpeed);

        public bool HasChart => _original is not null;

        public bool HasNote => Note is not null;

        public bool HasProblem => Problem is not null;

        public bool IsShown => IsEnabled && Scene is { Lanes.Count: > 0 };

        /// <summary>Whether the song shown has an offset to save.</summary>
        public bool IsChanged => _song?.Draft.HasChartOffsets == true;

        public string? Marker => IsChanged ? "*" : null;

        public string ToggleToolTip => HasChart ? Strings.HighwayToggleToolTip : Strings.HighwayNoChartToolTip;

        /// <summary>Forgets the song and its chart, so the next <see cref="Refresh"/> reads it afresh, as after saving.</summary>
        public void Unload()
        {
            _song = null;
            Clear(null);
        }

        /// <summary>
        /// Shows <paramref name="song"/>'s chart, with its draft's offsets; <see langword="null"/> shows
        /// nothing, as for no selection or several songs. The chart is read again only when it's a
        /// different file: the draft adding one, or a different song.
        /// </summary>
        public void Refresh(HighwaySong? song)
        {
            _song = song;
            if (song is null)
            {
                Clear(null);
                return;
            }

            // The chart a draft adds replaces the stored one; a stored one the draft removes is gone.
            var added = song.Draft.AddedFiles.Where(f => ChartFiles.IsChartName(f.StoredName)).Cast<AddedFile?>().LastOrDefault();
            string? name = added?.StoredName ?? ChartReader.Pick(song.StoredFiles.Where(f => !song.Draft.IsFileRemoved(f)));
            if (name is null)
            {
                Clear(Strings.HighwayNoChart);
                return;
            }

            string key = added?.SourcePath ?? $"{song.Entry.FullPath}|{name}";
            if (key != _sourceKey)
            {
                Clear(null);
                _sourceKey = key;
                _ = LoadAsync(song.Entry, name, added?.SourcePath, key);
            }
            else if (_bytes is not null)
            {
                _ = RebuildAsync();
            }

            RefreshState();
        }

        partial void OnIsEnabledChanged(bool value) => _settings.Settings.ShowNoteHighway = value;

        partial void OnNoteSpeedChanged(double value) => _settings.Settings.HighwayNoteSpeed = value;

        private void Clear(string? note)
        {
            _version++;
            _sourceKey = null;
            _chartName = null;
            _bytes = null;
            _original = null;
            _originalOf = null;
            _shown = null;
            Rows.Clear();
            Scene = null;
            Problem = null;
            Note = note;
            RefreshState();
        }

        private async Task LoadAsync(SongEntry entry, string name, string? sourcePath, string key)
        {
            int version = _version;
            byte[]? bytes;
            try
            {
                bytes = await Task.Run(async () => sourcePath is not null
                    ? await File.ReadAllBytesAsync(sourcePath)
                    : await SongFile.ReadAsync(entry, name, ChartReader.MaxBytes));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (version == _version)
                    Note = string.Format(CultureInfo.CurrentCulture, Strings.HighwayUnreadableFormat, ex.Message);
                return;
            }

            if (version != _version || key != _sourceKey)
                return;
            if (bytes is null)
            {
                Note = Strings.HighwayNoChart;
                return;
            }

            _bytes = bytes;
            _chartName = name;
            await RebuildAsync();
        }

        /// <summary>Reads the chart with the draft's offsets applied, off the UI thread, then shows it.</summary>
        private async Task RebuildAsync()
        {
            if (_song is not { } song || _bytes is not { } bytes || _chartName is not { } name)
                return;

            int version = ++_version;
            var offsets = new Dictionary<ChartTrackKey, int>(song.Draft.ChartOffsets);
            var options = ChartReadOptions.From(song.Metadata);
            var cached = _originalOf is { } of && ReferenceEquals(of.Bytes, bytes) && of.ProDrums == options.ProDrums ? _original : null;

            (ChartDocument Original, ChartDocument Shown, string? Problem) result;
            try
            {
                result = await Task.Run(() =>
                {
                    var original = cached ?? ChartReader.Read(bytes, name, options);
                    try
                    {
                        var shifted = ChartShifter.Shift(bytes, name, offsets, options);
                        string? problem = shifted.ChangedMarkings.Count == 0 ? null : string.Format(
                            CultureInfo.CurrentCulture, Strings.HighwayMarkingsChangedFormat, string.Join(", ", shifted.ChangedMarkings.Select(TrackName)));
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

            if (version != _version || !ReferenceEquals(song, _song))
                return;

            _shown = result.Shown;
            _delay = SongChartSettings.DelayOf(song.Metadata, result.Original);
            Problem = result.Problem;
            Note = null;
            if (!ReferenceEquals(result.Original, _original))
            {
                _original = result.Original;
                _originalOf = (bytes, options.ProDrums);
                BuildRows(result.Original);
            }

            SyncRows();
            BuildScene();
            RefreshState();
        }

        /// <summary>
        /// A row per instrument: those last chosen checked, or the first four when none of them are
        /// in this chart; each at the difficulty last chosen for it, or the nearest the chart has.
        /// </summary>
        private void BuildRows(ChartDocument chart)
        {
            var available = chart.Instruments;
            var preferred = (_settings.Settings.HighwayInstruments ?? [])
                .Select(n => Enum.TryParse<ChartInstrument>(n, out var i) ? i : (ChartInstrument?)null)
                .OfType<ChartInstrument>()
                .ToHashSet();
            var chosen = available.Where(preferred.Contains).Take(MaxLanes).ToHashSet();
            if (chosen.Count == 0)
                chosen = [.. available.Take(MaxLanes)];

            _isSyncing = true;
            Rows.Clear();
            foreach (var instrument in available)
            {
                var difficulties = chart.DifficultiesOf(instrument);
                var wanted = _settings.Settings.HighwayDifficulties?.GetValueOrDefault(instrument.ToString()) is { } saved
                    && Enum.TryParse<ChartDifficulty>(saved, out var d) ? d : ChartDifficulty.Expert;
                var difficulty = difficulties.Contains(wanted) ? wanted : difficulties.OrderBy(x => Math.Abs((int)x - (int)wanted)).ThenByDescending(x => x).First();
                Rows.Add(new HighwayRowViewModel(this, instrument, difficulties, difficulty, chosen.Contains(instrument)));
            }

            _isSyncing = false;
            RefreshCanCheck();
        }

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
            if (_shown is not { } chart)
            {
                Scene = null;
                return;
            }

            var lanes = new List<HighwayLane>();
            foreach (var row in Rows.Where(r => r.IsChecked).Take(MaxLanes))
            {
                if (!chart.Tracks.TryGetValue(row.Key, out var track))
                    continue;

                var kind = ChartInstruments.KindOf(row.Instrument);
                int laneCount = kind switch
                {
                    ChartInstrumentKind.FiveFret => 5,
                    ChartInstrumentKind.Drums => chart.IsFiveLaneDrums ? 5 : 4,
                    _ => 3,
                };
                lanes.Add(new HighwayLane(kind, laneCount, TrackName(row.Key), track.Notes, chart.Tempo, _delay));
            }

            Scene = lanes.Count > 0 ? new HighwayScene(lanes, chart.Tempo, _delay) : null;
        }

        internal void OnRowChecked(HighwayRowViewModel row)
        {
            if (_isSyncing)
                return;

            // Instruments this chart doesn't have stay chosen for the songs that do.
            var available = Rows.Select(r => r.Instrument).ToHashSet();
            _settings.Settings.HighwayInstruments =
            [
                .. (_settings.Settings.HighwayInstruments ?? []).Where(n => !Enum.TryParse<ChartInstrument>(n, out var i) || !available.Contains(i)),
                .. Rows.Where(r => r.IsChecked).Select(r => r.Instrument.ToString()),
            ];
            RefreshCanCheck();
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

            song.Draft.SetChartOffset(row.Key, milliseconds);
            SyncRows();
            OffsetsChanged?.Invoke(this, EventArgs.Empty);
        }

        private void RefreshCanCheck()
        {
            int count = Rows.Count(r => r.IsChecked);
            foreach (var row in Rows)
                row.CanCheck = row.IsChecked || count < MaxLanes;
        }

        private void RefreshState()
        {
            OnPropertyChanged(nameof(HasChart));
            OnPropertyChanged(nameof(IsChanged));
            OnPropertyChanged(nameof(Marker));
            OnPropertyChanged(nameof(ToggleToolTip));
        }

        internal static string InstrumentName(ChartInstrument instrument) => instrument switch
        {
            ChartInstrument.Guitar => Strings.InstrumentGuitar,
            ChartInstrument.Bass => Strings.InstrumentBass,
            ChartInstrument.Drums => Strings.InstrumentDrums,
            ChartInstrument.Keys => Strings.InstrumentKeys,
            ChartInstrument.Rhythm => Strings.InstrumentRhythm,
            ChartInstrument.GuitarCoop => Strings.InstrumentGuitarCoop,
            ChartInstrument.GhlGuitar => Strings.InstrumentGhlGuitar,
            _ => Strings.InstrumentGhlBass,
        };

        internal static string DifficultyName(ChartDifficulty difficulty) => difficulty switch
        {
            ChartDifficulty.Easy => Strings.DifficultyEasy,
            ChartDifficulty.Medium => Strings.DifficultyMedium,
            ChartDifficulty.Hard => Strings.DifficultyHard,
            _ => Strings.DifficultyExpert,
        };

        private static string TrackName(ChartTrackKey key) =>
            string.Format(CultureInfo.CurrentCulture, Strings.HighwayTrackFormat, DifficultyName(key.Difficulty), InstrumentName(key.Instrument));
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
            Name = HighwayViewModel.InstrumentName(instrument);
            Difficulties = [.. difficulties.Select(d => new DifficultyOption(d, HighwayViewModel.DifficultyName(d)))];
            DifficultyIndex = Math.Max(0, IndexOf(difficulty));
            IsChecked = isChecked;
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

        /// <summary>False while unchecked with <see cref="HighwayViewModel.MaxLanes"/> others checked.</summary>
        [ObservableProperty]
        public partial bool CanCheck { get; set; } = true;

        /// <summary>The picked difficulty's offset in milliseconds, later when positive.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasOffset))]
        [NotifyCanExecuteChangedFor(nameof(RevertOffsetCommand))]
        public partial decimal? Offset { get; set; } = 0;

        public bool HasOffset => Offset is { } offset && offset != 0;

        public ChartDifficulty Difficulty => _difficulties[Math.Clamp(DifficultyIndex, 0, _difficulties.Count - 1)];

        public ChartTrackKey Key => new(Instrument, Difficulty);

        /// <summary>Adds the milliseconds <paramref name="amount"/> gives, e.g. <c>-10</c>, to the offset.</summary>
        [RelayCommand]
        private void Nudge(string? amount)
        {
            if (int.TryParse(amount, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int ms))
                Offset = (Offset ?? 0) + ms;
        }

        [RelayCommand(CanExecute = nameof(HasOffset))]
        private void RevertOffset() => Offset = 0;

        /// <summary>Shows <paramref name="offsets"/>: the picked difficulty's in the box, and each difficulty's in its label.</summary>
        internal void Sync(IReadOnlyDictionary<ChartTrackKey, int> offsets)
        {
            var options = _difficulties
                .Select(d => new DifficultyOption(d, offsets.TryGetValue(new ChartTrackKey(Instrument, d), out int ms)
                    ? string.Format(CultureInfo.CurrentCulture, Strings.HighwayDifficultyOffsetFormat, HighwayViewModel.DifficultyName(d), ms)
                    : HighwayViewModel.DifficultyName(d)))
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
