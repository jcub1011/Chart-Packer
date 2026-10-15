using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Threading;
using ChartBuilder.Resources;
using ChartBuilder.Services;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Metadata;
using ChartPacker.Resources;
using ChartPacker.UI.Services;
using ChartPacker.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>A resolution Chart settings offers.</summary>
    public sealed record ResolutionChoice(int Value, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>A part chip in Chart settings' Parts: selected when the chart has it, with its difficulty rating picked from a
    /// flyout as Chart Packer's inspector picks one. Drums are three chips sharing one part: 4-lane, Pro and 5-lane.
    /// </summary>
    public sealed partial class ChartPartChipViewModel : ObservableObject
    {
        internal ChartPartChipViewModel(ChartSettingsViewModel owner, ChartInstrument instrument, DrumsVariant? variant, string label, string difficultyKey, bool isChecked)
        {
            Instrument = instrument;
            Variant = variant;
            Label = label;
            DifficultyKey = difficultyKey;
            Rating = new ChartRatingViewModel(owner, difficultyKey, label);
            IsChecked = isChecked;
        }

        public ChartInstrument Instrument { get; }

        /// <summary>Which drums layout the chip stands for; null for a normal part.</summary>
        public DrumsVariant? Variant { get; }

        public bool IsDrumsChip => Variant is not null;

        public string Label { get; }

        /// <summary>The <c>song.ini</c> key of the rating the chip shows, e.g. <c>diff_guitar</c>.</summary>
        public string DifficultyKey { get; }

        [ObservableProperty]
        public partial bool IsChecked { get; set; }

        /// <summary>The rating the chip shows.</summary>
        public ChartRatingViewModel Rating { get; }

        /// <summary><c>*</c> while the rating has a pending change, as an edited metadata row has.</summary>
        [ObservableProperty]
        public partial string? Marker { get; internal set; }
    }

    /// <summary>
    /// A part's rating in Chart settings: a number, unset as <c>?</c>, that opens a flyout as Chart Packer's inspector's
    /// instrument icons do (<see cref="ChartPacker.UI.Views.DifficultyPicker"/>). <c>?</c> clears it, 0 to 6 set it, and Other sets any other
    /// tier, each at once as one undo step.
    /// </summary>
    public sealed partial class ChartRatingViewModel : ObservableObject, IDifficultyPicker
    {
        /// <summary>The tiers offered as buttons; any other is typed.</summary>
        private const int MaxPresetTier = 6;

        private readonly ChartSettingsViewModel _owner;

        internal ChartRatingViewModel(ChartSettingsViewModel owner, string key, string name)
        {
            _owner = owner;
            Key = key;
            Name = name;
            var set = new RelayCommand<int?>(tier => _owner.SetDifficulty(Key, tier));
            Presets =
            [
                new DifficultyPresetViewModel(null, SharedStrings.DifficultyUnsetBadge, set),
                .. Enumerable.Range(0, MaxPresetTier + 1).Select(t => new DifficultyPresetViewModel(t, t.ToString(CultureInfo.CurrentCulture), set)),
            ];
        }

        public string Name { get; }

        /// <summary>The rating's <c>song.ini</c> key, e.g. <c>diff_guitar</c>.</summary>
        public string Key { get; }

        /// <summary>None: the parts' rows don't count notes.</summary>
        public IReadOnlyList<NoteCountRow> NoteCounts => [];

        public bool HasNoteCounts => false;

        public IReadOnlyList<DifficultyPresetViewModel> Presets { get; }

        [ObservableProperty]
        public partial decimal? OtherValue { get; set; }

        /// <summary>The tier as the next save leaves it; null while it's unrated, or what's kept isn't a tier.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Text))]
        public partial int? Tier { get; private set; }

        /// <summary>The tier as the rating's button shows it: its number, or <c>?</c> while it's unrated.</summary>
        public string Text => Tier is { } tier ? tier.ToString(CultureInfo.CurrentCulture) : SharedStrings.DifficultyUnsetBadge;

        /// <summary>Never: an undo puts a rating back.</summary>
        public bool IsEdited => false;

        public IRelayCommand RevertCommand { get; } = new RelayCommand(static () => { }, static () => false);

        /// <summary>Takes the tier the song has now.</summary>
        internal void Update(int? tier)
        {
            Tier = tier;
            OtherValue = tier;
            foreach (var preset in Presets)
                preset.IsCurrent = preset.Tier == tier;
        }

        [RelayCommand]
        private void ApplyOther()
        {
            if (OtherValue is { } value && value >= 0 && value <= int.MaxValue)
                _owner.SetDifficulty(Key, (int)decimal.Truncate(value));
        }
    }

    /// <summary>
    /// Chart settings (charter-spec C1), a tab in the Editor's left panel: the chart's timing and the audio's delay, its
    /// parts and their ratings. Each change is applied as it's made, as one undoable command, so the tempo can be found by
    /// ear while the song plays; a number being typed is applied once the typing pauses. It works the same on a new chart
    /// and on a song opened. The tab's Metadata and Tracks are <see cref="ChartMetadataViewModel"/> and
    /// <see cref="EditorTracksViewModel"/>.
    /// </summary>
    public sealed partial class ChartSettingsViewModel : ViewModelBase, IDisposable
    {
        // A BPM, beats per measure or delay is applied this long after the last keystroke or spin, as one undo step.
        private static readonly TimeSpan TypingDelay = TimeSpan.FromMilliseconds(600);

        private readonly BuilderSettingsService _settings;
        private readonly IConfirmationService _confirmations;
        private readonly EditorSong _song;
        private readonly EditableChart _chart;
        private readonly Action<IChartCommand> _apply;
        private readonly Func<ITempoDetector?>? _detector;
        private readonly DispatcherTimer _typingTimer;
        private readonly DispatcherTimer _songTypingTimer;

        // Whether the delay is being typed: applied once the typing pauses, and not shown over meanwhile.
        private bool _delayTyped;
        private long _chartMicroseconds;
        private decimal _chartBpm;
        private int _chartNumerator;

        // While the fields are being set from the chart, or a change put back, which applies nothing.
        private bool _loading;

        // While a change is being applied, which may be waiting on a confirmation; a change meanwhile is applied after.
        private bool _applying;
        private bool _applyAgain;

        /// <param name="settings">Whether a tempo change keeps ticks or times.</param>
        /// <param name="confirmations">Asks before a part with notes is removed, and before a resolution change that moves items.</param>
        /// <param name="song">The song open in the Editor, whose chart and ratings these are.</param>
        /// <param name="apply">Applies a change to the song, as one undo step.</param>
        /// <param name="detector">Finds the tempo in the song's audio, for Detect from audio; asked each time, as the editor comes after.</param>
        public ChartSettingsViewModel(BuilderSettingsService settings, IConfirmationService confirmations, EditorSong song, Action<IChartCommand> apply, Func<ITempoDetector?>? detector = null)
        {
            ArgumentNullException.ThrowIfNull(song);
            ArgumentNullException.ThrowIfNull(apply);
            _settings = settings;
            _confirmations = confirmations;
            _song = song;
            _chart = song.Chart;
            _apply = apply;
            _detector = detector;
            _typingTimer = new DispatcherTimer(TypingDelay, DispatcherPriority.Background, (_, _) => ApplyNow());
            _typingTimer.Stop();
            _songTypingTimer = new DispatcherTimer(TypingDelay, DispatcherPriority.Background, (_, _) => ApplyTyped());
            _songTypingTimer.Stop();

            // Only this changes the resolution and the time signature, so the chart's can only be these or what it had.
            var values = ChartSettingsValues.Of(_chart);
            Resolutions = [.. NewChartSettings.Resolutions.Append(values.Resolution).Distinct().Order().Select(r => new ResolutionChoice(r, BuilderSettingsCatalog.ResolutionLabel(r)))];
            Denominators = [.. NewChartSettings.Denominators.Append(values.Denominator).Distinct().Order()];
            PartChips = BuildChips(values);
            foreach (var chip in PartChips)
                chip.PropertyChanged += OnChipChanged;
            _song.Changed += OnSongChanged;
            Load();
        }

        // Timing.

        public IReadOnlyList<ResolutionChoice> Resolutions { get; }

        [ObservableProperty]
        public partial ResolutionChoice SelectedResolution { get; set; }

        public static decimal MinBpm => (decimal)NewChartSettings.MinBpm;

        public static decimal MaxBpm => (decimal)NewChartSettings.MaxBpm;

        public static decimal MaxNumerator => NewChartSettings.MaxNumerator;

        [ObservableProperty]
        public partial decimal? Bpm { get; set; }

        [ObservableProperty]
        public partial decimal? Numerator { get; set; }

        public IReadOnlyList<int> Denominators { get; }

        [ObservableProperty]
        public partial int Denominator { get; set; }

        /// <summary>
        /// The <c>delay</c> in milliseconds, how much later than its notes the chart plays against the audio, as the box
        /// shows it: the song's as the next save leaves it, or what's being typed.
        /// </summary>
        [ObservableProperty]
        public partial decimal? DelayMs { get; set; }

        /// <summary>Whether a save is running, which the ratings and the delay, the song's own, can't be changed during.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanEditSong))]
        public partial bool IsSaving { get; set; }

        /// <summary>Whether the ratings and the delay can be changed now: not while the song is being saved.</summary>
        public bool CanEditSong => !IsSaving;

        public static decimal MinDelayMs => -86_399_999;

        public static decimal MaxDelayMs => 86_399_999;

        // Parts.

        public IReadOnlyList<ChartPartChipViewModel> PartChips { get; }

        /// <summary>Whether any drums chip is on, so the drums hint is offered.</summary>
        public bool HasDrums => PartChips.Any(p => p.IsDrumsChip && p.IsChecked);

        /// <summary>What stops a change being applied, or why the last one wasn't; null when nothing does.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasProblem))]
        public partial string? Problem { get; private set; }

        public bool HasProblem => Problem is not null;

        /// <summary>Whether the song has audio to detect the tempo from (charter-spec C1, Detect from audio).</summary>
        public bool CanDetect => _detector?.Invoke()?.CanDetect == true;

        /// <summary>Whether the tempo is being detected.</summary>
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(DetectFromAudioCommand))]
        public partial bool IsDetecting { get; private set; }

        /// <summary>Why Detect from audio set nothing, or null.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasDetectStatus))]
        public partial string? DetectStatus { get; private set; }

        public bool HasDetectStatus => DetectStatus is not null;

        /// <summary>
        /// Detect from audio (charter-spec C1): finds the tempo in the mix, sets the starting BPM to it as one undoable step,
        /// and proposes a tempo map in the editor to review.
        /// </summary>
        [RelayCommand(CanExecute = nameof(CanDetectNow))]
        private async Task DetectFromAudioAsync()
        {
            if (_detector?.Invoke() is not { CanDetect: true } detector)
                return;

            IsDetecting = true;
            DetectStatus = null;
            try
            {
                if (await detector.DetectAsync() is not { } bpm)
                {
                    DetectStatus = Strings.DetectFromAudioNoBeat;
                    return;
                }

                Bpm = Math.Clamp((decimal)bpm, MinBpm, MaxBpm);
                ApplyNow();
            }
            finally
            {
                IsDetecting = false;
            }
        }

        private bool CanDetectNow() => !IsDetecting;

        /// <summary>
        /// Shows what the chart has, after it changed: by this, or by an undo or redo. A number still being typed is
        /// dropped.
        /// </summary>
        public void Reload() => Load();

        partial void OnSelectedResolutionChanged(ResolutionChoice value) => ApplyNow();

        partial void OnBpmChanged(decimal? value) => ApplySoon();

        partial void OnNumeratorChanged(decimal? value) => ApplySoon();

        partial void OnDenominatorChanged(int value) => ApplyNow();

        partial void OnDelayMsChanged(decimal? value)
        {
            if (_loading)
                return;
            _delayTyped = true;
            _songTypingTimer.Stop();
            _songTypingTimer.Start();
        }

        public void Dispose()
        {
            _typingTimer.Stop();
            _songTypingTimer.Stop();
            _song.Changed -= OnSongChanged;
        }

        /// <summary>
        /// Sets the rating <paramref name="key"/> to <paramref name="tier"/>, or unrated for null, at once, as one undo step.
        /// While Pro drums are on, <c>diff_drums</c> and <c>diff_drums_real</c> are kept together, as the Pro chip only
        /// shows <c>diff_drums_real</c>.
        /// </summary>
        internal void SetDifficulty(string key, int? tier)
        {
            if (IsSaving)
                return;
            string drumsKey = SongIniPartKeys.DifficultyKeysOf(ChartInstrument.Drums, null)[0];
            string proKey = SongIniPartKeys.ProDrumsDifficulty;
            bool proOn = PartChips.Any(c => c.Variant == DrumsVariant.Pro && c.IsChecked);
            if (key.Equals(drumsKey, StringComparison.OrdinalIgnoreCase) && proOn)
            {
                if (SongCommands.SetValues(_song, new Dictionary<string, string?>
                    {
                        [drumsKey] = tier?.ToString(CultureInfo.InvariantCulture) ?? MetadataDifficulty.UnsetValue,
                        [proKey] = tier?.ToString(CultureInfo.InvariantCulture) ?? MetadataDifficulty.UnsetValue,
                    }) is { } both)
                    _apply(both);
            }
            else if (key.Equals(proKey, StringComparison.OrdinalIgnoreCase) && proOn)
            {
                if (SongCommands.SetValues(_song, new Dictionary<string, string?>
                    {
                        [proKey] = tier?.ToString(CultureInfo.InvariantCulture) ?? MetadataDifficulty.UnsetValue,
                        [drumsKey] = tier?.ToString(CultureInfo.InvariantCulture) ?? MetadataDifficulty.UnsetValue,
                    }) is { } both)
                    _apply(both);
            }
            else if (SongCommands.SetDifficulty(_song, key, tier) is { } command)
                _apply(command);
        }

        /// <summary>Applies the delay typed, as one undo step.</summary>
        private void ApplyTyped()
        {
            _songTypingTimer.Stop();
            if (IsSaving)
            {
                // Typed just as a save began: applied once it's done.
                _songTypingTimer.Start();
                return;
            }

            bool typed = _delayTyped;
            _delayTyped = false;
            if (typed && DelayMs is { } ms && SongCommands.SetDelay(_song, (long)decimal.Round(ms, MidpointRounding.AwayFromZero)) is { } delay)
                _apply(delay);
            else
                LoadSong();
        }

        // The ratings and the delay follow the song: an edit's undo, a save, or another tab's edit.
        private void OnSongChanged(object? sender, EventArgs e) => LoadSong();

        private async void OnChipChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ChartPartChipViewModel.IsChecked) || sender is not ChartPartChipViewModel chip)
                return;

            if (chip.IsDrumsChip)
                OnPropertyChanged(nameof(HasDrums));
            if (_loading)
                return;

            // The drums chips share one part, so only one can be on.
            bool syncPro = false;
            if (chip.IsDrumsChip && chip.IsChecked)
            {
                _loading = true;
                try
                {
                    foreach (var other in PartChips)
                    {
                        if (other != chip && other.IsDrumsChip && other.IsChecked)
                            other.IsChecked = false;
                    }
                }
                finally
                {
                    _loading = false;
                }

                OnPropertyChanged(nameof(HasDrums));
                syncPro = chip.Variant == DrumsVariant.Pro;
            }

            try
            {
                // Turning off a part that has notes asks first, as Remove part does (charter-spec C1, Parts).
                // Switching drums variants keeps or converts the notes, so only removing drums asks.
                bool removing = !chip.IsChecked && (!chip.IsDrumsChip || !PartChips.Any(c => c.IsDrumsChip && c.IsChecked));
                ChartInstrument removed = chip.Instrument;
                if (removing && _chart.PartOf(removed) is { HasNotes: true } && !await _confirmations.ConfirmAsync(
                        Strings.RemovePartTitle,
                        string.Format(CultureInfo.CurrentCulture, Strings.RemovePartMessageFormat, chip.Label),
                        Strings.RemovePartConfirm))
                {
                    Restore(() => chip.IsChecked = true);
                    return;
                }
            }
            catch (Exception ex)
            {
                // Nothing awaits this, so anything not caught here would take the app down.
                Trace.TraceError($"Asking to remove a part failed: {ex}");
                Restore(() => chip.IsChecked = true);
                return;
            }

            ApplyNow();

            // Turning Pro on takes the standard drums rating with it, as that's the only rating the Pro chip shows.
            if (syncPro)
                SyncProRatingFromDrums();
        }

        /// <summary>Copies the standard drums rating to Pro drums' own rating, when they differ.</summary>
        private void SyncProRatingFromDrums()
        {
            string drumsKey = SongIniPartKeys.DifficultyKeysOf(ChartInstrument.Drums, null)[0];
            int? tier = MetadataDifficulty.TierOf(_song.EffectiveValue(drumsKey));
            string? current = _song.EffectiveValue(SongIniPartKeys.ProDrumsDifficulty);
            string wanted = tier?.ToString(CultureInfo.InvariantCulture) ?? MetadataDifficulty.UnsetValue;
            if (!string.Equals(current, wanted, StringComparison.Ordinal))
                SetDifficulty(SongIniPartKeys.ProDrumsDifficulty, tier);
        }

        /// <summary>Shows the problem with what's being typed at once, and applies it once the typing pauses.</summary>
        private void ApplySoon()
        {
            if (_loading)
                return;

            Refresh();
            _typingTimer.Stop();
            _typingTimer.Start();
        }

        /// <summary>
        /// Applies every field that differs from the chart, as one undo step, unless one is out of range. A resolution
        /// change that would move items asks first, and is put back if the user doesn't go ahead.
        /// </summary>
        private async void ApplyNow()
        {
            if (_loading)
                return;

            _typingTimer.Stop();
            if (_applying)
            {
                _applyAgain = true;
                return;
            }

            Refresh();
            if (Problem is not null)
                return;

            _applying = true;
            try
            {
                var values = Values();
                if (values.Resolution != _chart.Resolution && !await ConfirmResolutionAsync(values.Resolution))
                {
                    Restore(() => SelectedResolution = Resolutions.First(r => r.Value == _chart.Resolution));
                    values = values with { Resolution = _chart.Resolution };
                }

                if (ChartSettingsEdit.CommandFor(_chart, values, _settings.Settings.TempoEdits == TempoEditMode.KeepTimes) is { } command)
                    _apply(command);
            }
            catch (Exception ex)
            {
                // Nothing awaits this, so anything not caught here would take the app down.
                Trace.TraceError($"Applying Chart settings failed: {ex}");
            }
            finally
            {
                _applying = false;
            }

            if (_applyAgain)
            {
                _applyAgain = false;
                ApplyNow();
            }
        }

        /// <summary>
        /// Works out what changing to <paramref name="resolution"/> would move, and says it, so the user can go back
        /// (charter-spec C1).
        /// </summary>
        /// <returns>Whether to go ahead.</returns>
        private async Task<bool> ConfirmResolutionAsync(int resolution)
        {
            ResolutionChangeReport report;
            try
            {
                report = ResolutionChangeReport.Analyze(_chart, resolution);
            }
            catch (ChartFormatException ex)
            {
                Problem = string.Format(CultureInfo.CurrentCulture, Strings.ResolutionChangeFailedFormat, ex.Message);
                return false;
            }

            return !report.HasWarning || await _confirmations.ConfirmAsync(
                Strings.ResolutionWarningTitle, WarningText(report), Strings.ResolutionWarningConfirm);
        }

        /// <summary>
        /// Sets the ratings and the delay from the song as the next save leaves them, but the delay while it's being typed,
        /// and marks each rating with a pending change.
        /// </summary>
        private void LoadSong()
        {
            var pending = _song.PendingChanges().Select(c => c.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _loading = true;
            try
            {
                foreach (var chip in PartChips)
                {
                    chip.Rating.Update(MetadataDifficulty.TierOf(_song.EffectiveValue(chip.DifficultyKey)));
                    chip.Marker = pending.Contains(chip.DifficultyKey) ? "*" : null;
                }

                if (!_delayTyped)
                    DelayMs = (long)Math.Round(_song.Delay.TotalMilliseconds, MidpointRounding.AwayFromZero);
            }
            finally
            {
                _loading = false;
            }
        }

        /// <summary>Sets the fields from the chart and the song.</summary>
        [MemberNotNull(nameof(SelectedResolution))]
        private void Load()
        {
            _typingTimer.Stop();
            var values = ChartSettingsValues.Of(_chart);
            _loading = true;
            try
            {
                SelectedResolution = Resolutions.First(r => r.Value == values.Resolution);
                _chartMicroseconds = values.MicrosecondsPerQuarter;
                _chartBpm = Math.Round((decimal)EditableTempoMap.BpmOf(_chartMicroseconds), 3);
                Bpm = _chartBpm;
                _chartNumerator = values.Numerator;
                Numerator = values.Numerator;
                Denominator = values.Denominator;
                bool hasDrums = values.Parts.Contains(ChartInstrument.Drums);
                foreach (var chip in PartChips)
                {
                    chip.IsChecked = chip.IsDrumsChip
                        ? hasDrums && values.Drums == chip.Variant
                        : values.Parts.Contains(chip.Instrument);
                }

                OnPropertyChanged(nameof(HasDrums));
            }
            finally
            {
                _loading = false;
            }

            LoadSong();
            Refresh();
        }

        /// <summary>Puts a field back as it was, applying nothing.</summary>
        private void Restore(Action restore)
        {
            _loading = true;
            try
            {
                restore();
            }
            finally
            {
                _loading = false;
            }
        }

        private ChartSettingsValues Values()
        {
            // The BPM shown is rounded, so one left as it was keeps the tempo the chart has.
            long microseconds = Bpm == _chartBpm ? _chartMicroseconds : EditableTempoMap.MicrosecondsPerQuarterOf((double)Bpm!.Value);
            var parts = PartChips.Where(p => p.IsChecked).Select(p => p.Instrument).ToHashSet();
            DrumsVariant drums = PartChips.FirstOrDefault(p => p.IsDrumsChip && p.IsChecked)?.Variant
                ?? _chart.DrumsVariant
                ?? DrumsVariant.Pro;
            return ChartSettingsValues.Of(_chart) with
            {
                Resolution = SelectedResolution.Value,
                MicrosecondsPerQuarter = microseconds,
                Numerator = (int)Numerator!.Value,
                Denominator = Denominator,
                // Parts Chart Builder doesn't edit aren't offered, so they're kept as they are.
                Parts = parts,
                Drums = drums,
            };
        }

        /// <summary>
        /// Works out what stops a change being applied: a BPM or time signature out of range. Only one that differs from the
        /// chart's is, as the chart's own starting tempo is edited in the Tempo map tab, so one out of range there doesn't
        /// hold up the resolution, the parts or the drums.
        /// </summary>
        private void Refresh()
        {
            if (Bpm != _chartBpm && (Bpm is not { } bpm || bpm < MinBpm || bpm > MaxBpm))
                Problem = string.Format(CultureInfo.CurrentCulture, Strings.ChartSettingsBadBpmFormat, NewChartSettings.MinBpm, NewChartSettings.MaxBpm);
            else if (Numerator != _chartNumerator && (Numerator is not { } numerator || numerator < 1 || numerator > MaxNumerator || numerator != Math.Floor(numerator)))
                Problem = string.Format(CultureInfo.CurrentCulture, Strings.ChartSettingsBadNumeratorFormat, MaxNumerator);
            else
                Problem = null;
        }

        /// <summary>The warning before a resolution change that would move or leave out items (charter-spec C1).</summary>
        private static string WarningText(ResolutionChangeReport report)
        {
            var text = new StringBuilder();
            if (report.Moved > 0)
            {
                text.AppendFormat(CultureInfo.CurrentCulture, Strings.ResolutionWarningMovesFormat, report.From, report.To, report.Moved, report.LargestMoveMs);
                foreach (var moves in report.Moves)
                {
                    text.AppendLine();
                    text.AppendFormat(CultureInfo.CurrentCulture, Strings.ResolutionWarningGroupFormat, GroupName(moves), moves.Moved);
                }
            }

            if (report.LeftOut.Count > 0)
            {
                if (text.Length > 0)
                    text.AppendLine().AppendLine();
                text.Append(Strings.ResolutionWarningLeftOut);
                foreach (var item in report.LeftOut)
                    text.AppendLine().Append("• ").Append(item);
            }

            return text.ToString();
        }

        private static string GroupName(ResolutionChangeMoves moves) => moves.Group switch
        {
            ResolutionChangeGroup.TempoMap => Strings.ResolutionGroupTempoMap,
            ResolutionChangeGroup.Events => Strings.ResolutionGroupEvents,
            ResolutionChangeGroup.Passthrough => Strings.ResolutionGroupPassthrough,
            _ => PartName(moves.Part!.Value),
        };

        /// <summary>A part as Chart settings names it.</summary>
        public static string PartName(ChartInstrument instrument) => instrument switch
        {
            ChartInstrument.Guitar => Strings.PartLead,
            ChartInstrument.Bass => Strings.PartBass,
            ChartInstrument.Rhythm => Strings.PartRhythm,
            ChartInstrument.GuitarCoop => Strings.PartCoop,
            ChartInstrument.GhlGuitar => Strings.PartGhlGuitar,
            ChartInstrument.GhlBass => Strings.PartGhlBass,
            ChartInstrument.GhlRhythm => Strings.PartGhlRhythm,
            ChartInstrument.GhlCoop => Strings.PartGhlCoop,
            ChartInstrument.Drums => Strings.PartDrums,
            _ => ChartLabels.InstrumentName(instrument),
        };

        private List<ChartPartChipViewModel> BuildChips(ChartSettingsValues values)
        {
            var chips = new List<ChartPartChipViewModel>();
            bool hasDrums = values.Parts.Contains(ChartInstrument.Drums);
            foreach (var instrument in NewChartSettings.Instruments)
            {
                if (instrument == ChartInstrument.Drums)
                    continue;
                chips.Add(new ChartPartChipViewModel(
                    this, instrument, null, PartName(instrument),
                    SongIniPartKeys.DifficultyKeysOf(instrument, null)[0],
                    values.Parts.Contains(instrument)));
            }

            string drumsKey = SongIniPartKeys.DifficultyKeysOf(ChartInstrument.Drums, null)[0];
            chips.Add(new ChartPartChipViewModel(
                this, ChartInstrument.Drums, DrumsVariant.FourLane, Strings.PartDrums, drumsKey,
                hasDrums && values.Drums == DrumsVariant.FourLane));
            chips.Add(new ChartPartChipViewModel(
                this, ChartInstrument.Drums, DrumsVariant.Pro, Strings.ChartSettingsProDrumsDifficultyLabel,
                SongIniPartKeys.ProDrumsDifficulty,
                hasDrums && values.Drums == DrumsVariant.Pro));
            chips.Add(new ChartPartChipViewModel(
                this, ChartInstrument.Drums, DrumsVariant.FiveLane, Strings.ChartSettingsFiveLaneDrumsLabel, drumsKey,
                hasDrums && values.Drums == DrumsVariant.FiveLane));
            return chips;
        }
    }
}
