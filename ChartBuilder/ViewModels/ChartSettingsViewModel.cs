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
using ChartPacker.Services;
using ChartPacker.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChartBuilder.ViewModels
{
    /// <summary>A resolution Chart settings offers.</summary>
    public sealed record ResolutionChoice(int Value, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>A drum variant Chart settings offers.</summary>
    public sealed record DrumsVariantChoice(DrumsVariant Value, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>A part Chart settings can tick.</summary>
    public sealed partial class ChartPartChoice(ChartInstrument instrument, string label, bool isChecked) : ObservableObject
    {
        public ChartInstrument Instrument { get; } = instrument;

        public string Label { get; } = label;

        [ObservableProperty]
        public partial bool IsChecked { get; set; } = isChecked;

        public bool IsDrums => Instrument == ChartInstrument.Drums;
    }

    /// <summary>
    /// Chart settings (charter-spec C1), a tab in the Editor's left panel: the chart's timing and parts. Each change is
    /// applied as it's made, as one undoable command, so the tempo can be found by ear while the song plays; a number
    /// being typed is applied once the typing pauses. It works the same on a new chart and on a song opened.
    /// </summary>
    public sealed partial class ChartSettingsViewModel : ViewModelBase
    {
        // A BPM or beats per measure is applied this long after the last keystroke or spin, as one undo step.
        private static readonly TimeSpan TypingDelay = TimeSpan.FromMilliseconds(600);

        private readonly BuilderSettingsService _settings;
        private readonly IConfirmationService _confirmations;
        private readonly EditableChart _chart;
        private readonly Action<IChartCommand> _apply;
        private readonly DispatcherTimer _typingTimer;
        private long _chartMicroseconds;
        private decimal _chartBpm;

        // While the fields are being set from the chart, or a change put back, which applies nothing.
        private bool _loading;

        // While a change is being applied, which may be waiting on a confirmation; a change meanwhile is applied after.
        private bool _applying;
        private bool _applyAgain;

        /// <param name="settings">Whether a tempo change keeps ticks or times.</param>
        /// <param name="confirmations">Asks before a part with notes is removed, and before a resolution change that moves items.</param>
        /// <param name="chart">The chart open in the Editor.</param>
        /// <param name="apply">Applies a change to <paramref name="chart"/>, as one undo step.</param>
        public ChartSettingsViewModel(BuilderSettingsService settings, IConfirmationService confirmations, EditableChart chart, Action<IChartCommand> apply)
        {
            ArgumentNullException.ThrowIfNull(chart);
            ArgumentNullException.ThrowIfNull(apply);
            _settings = settings;
            _confirmations = confirmations;
            _chart = chart;
            _apply = apply;
            _typingTimer = new DispatcherTimer(TypingDelay, DispatcherPriority.Background, (_, _) => ApplyNow());
            _typingTimer.Stop();

            // Only this changes the resolution and the time signature, so the chart's can only be these or what it had.
            var values = ChartSettingsValues.Of(chart);
            Resolutions = [.. NewChartSettings.Resolutions.Append(values.Resolution).Distinct().Order().Select(r => new ResolutionChoice(r, BuilderSettingsCatalog.ResolutionLabel(r)))];
            Denominators = [.. NewChartSettings.Denominators.Append(values.Denominator).Distinct().Order()];
            Parts = [.. NewChartSettings.Instruments.Select(i => new ChartPartChoice(i, PartName(i), values.Parts.Contains(i)))];
            foreach (var part in Parts)
                part.PropertyChanged += OnPartChanged;
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

        // Parts.

        public IReadOnlyList<ChartPartChoice> Parts { get; }

        public IReadOnlyList<DrumsVariantChoice> DrumsVariants { get; } =
        [
            new(DrumsVariant.FourLane, Strings.DrumsFourLane),
            new(DrumsVariant.Pro, Strings.DrumsPro),
            new(DrumsVariant.FiveLane, Strings.DrumsFiveLane),
        ];

        [ObservableProperty]
        public partial DrumsVariantChoice SelectedDrums { get; set; }

        /// <summary>Whether drums are ticked, so their variant is offered.</summary>
        public bool HasDrums => Parts.Any(p => p.IsDrums && p.IsChecked);

        /// <summary>Whether the drum variant can be changed: not while the drums have notes, until their conversion comes.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DrumsToolTip))]
        public partial bool CanChangeDrums { get; private set; }

        public string? DrumsToolTip => CanChangeDrums ? null : Strings.ChartSettingsDrumsLockedToolTip;

        /// <summary>What stops a change being applied, or why the last one wasn't; null when nothing does.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasProblem))]
        public partial string? Problem { get; private set; }

        public bool HasProblem => Problem is not null;

        /// <summary>
        /// Shows what the chart has, after it changed: by this, or by an undo or redo. A number still being typed is
        /// dropped.
        /// </summary>
        public void Reload() => Load();

        partial void OnSelectedResolutionChanged(ResolutionChoice value) => ApplyNow();

        partial void OnBpmChanged(decimal? value) => ApplySoon();

        partial void OnNumeratorChanged(decimal? value) => ApplySoon();

        partial void OnDenominatorChanged(int value) => ApplyNow();

        partial void OnSelectedDrumsChanged(DrumsVariantChoice value) => ApplyNow();

        private async void OnPartChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ChartPartChoice.IsChecked) || sender is not ChartPartChoice part)
                return;

            if (part.IsDrums)
                OnPropertyChanged(nameof(HasDrums));
            if (_loading)
                return;

            try
            {
                // Unticking a part that has notes asks first, as Remove part does (charter-spec C1, Parts).
                if (!part.IsChecked && _chart.PartOf(part.Instrument) is { HasNotes: true } && !await _confirmations.ConfirmAsync(
                        Strings.RemovePartTitle,
                        string.Format(CultureInfo.CurrentCulture, Strings.RemovePartMessageFormat, part.Label),
                        Strings.RemovePartConfirm))
                {
                    Restore(() => part.IsChecked = true);
                    return;
                }
            }
            catch (Exception ex)
            {
                // Nothing awaits this, so anything not caught here would take the app down.
                Trace.TraceError($"Asking to remove a part failed: {ex}");
                Restore(() => part.IsChecked = true);
                return;
            }

            ApplyNow();
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

        /// <summary>Sets the fields from the chart.</summary>
        [MemberNotNull(nameof(SelectedResolution), nameof(SelectedDrums))]
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
                Numerator = values.Numerator;
                Denominator = values.Denominator;
                foreach (var part in Parts)
                    part.IsChecked = values.Parts.Contains(part.Instrument);
                SelectedDrums = DrumsVariants.First(d => d.Value == values.Drums);
                CanChangeDrums = _chart.PartOf(ChartInstrument.Drums)?.HasNotes != true;
            }
            finally
            {
                _loading = false;
            }

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
            return ChartSettingsValues.Of(_chart) with
            {
                Resolution = SelectedResolution.Value,
                MicrosecondsPerQuarter = microseconds,
                Numerator = (int)Numerator!.Value,
                Denominator = Denominator,
                // Parts Chart Builder doesn't edit aren't offered, so they're kept as they are.
                Parts = Parts.Where(p => p.IsChecked).Select(p => p.Instrument).ToHashSet(),
                Drums = SelectedDrums.Value,
            };
        }

        /// <summary>Works out what stops a change being applied: a BPM or time signature out of range.</summary>
        private void Refresh()
        {
            if (Bpm is not { } bpm || bpm < MinBpm || bpm > MaxBpm)
                Problem = string.Format(CultureInfo.CurrentCulture, Strings.ChartSettingsBadBpmFormat, NewChartSettings.MinBpm, NewChartSettings.MaxBpm);
            else if (Numerator is not { } numerator || numerator < 1 || numerator > MaxNumerator || numerator != Math.Floor(numerator))
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
    }
}
