using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ChartBuilder.Resources;
using ChartBuilder.Services;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charts;
using ChartPacker.Services;
using ChartPacker.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>A section of Chart settings, listed on the left; choosing one scrolls to it.</summary>
    /// <param name="Id">Names the section's heading in the view: <c>&lt;Id&gt;Section</c>.</param>
    public sealed record ChartSettingsSection(string Id, string Title);

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
    /// Chart settings (charter-spec C1), opened from the Editor's ⚙ menu: the chart's timing and parts, which OK changes as
    /// one undoable command. It works the same on a new chart and on a song opened.
    /// </summary>
    public sealed partial class ChartSettingsViewModel : ViewModelBase
    {
        private readonly BuilderSettingsService _settings;
        private readonly IConfirmationService _confirmations;
        private readonly EditableChart _chart;
        private readonly long _startMicroseconds;
        private readonly decimal _startBpm;
        private bool _restoringPart;

        /// <param name="settings">Whether a tempo change keeps ticks or times.</param>
        /// <param name="confirmations">Asks before a part with notes is removed, and before a resolution change that moves items.</param>
        /// <param name="chart">The chart open in the Editor.</param>
        public ChartSettingsViewModel(BuilderSettingsService settings, IConfirmationService confirmations, EditableChart chart)
        {
            ArgumentNullException.ThrowIfNull(chart);
            _settings = settings;
            _confirmations = confirmations;
            _chart = chart;

            var values = ChartSettingsValues.Of(chart);
            Resolutions = [.. NewChartSettings.Resolutions.Append(values.Resolution).Distinct().Order().Select(r => new ResolutionChoice(r, BuilderSettingsCatalog.ResolutionLabel(r)))];
            SelectedResolution = Resolutions.First(r => r.Value == values.Resolution);

            _startMicroseconds = values.MicrosecondsPerQuarter;
            _startBpm = Math.Round((decimal)EditableTempoMap.BpmOf(_startMicroseconds), 3);
            Bpm = _startBpm;
            Numerator = values.Numerator;
            Denominators = [.. NewChartSettings.Denominators.Append(values.Denominator).Distinct().Order()];
            Denominator = values.Denominator;

            Parts = [.. NewChartSettings.Instruments.Select(i => new ChartPartChoice(i, PartName(i), values.Parts.Contains(i)))];
            foreach (var part in Parts)
                part.PropertyChanged += OnPartChanged;
            SelectedDrums = DrumsVariants.First(d => d.Value == values.Drums);
            CanChangeDrums = chart.PartOf(ChartInstrument.Drums)?.HasNotes != true;
            Refresh();
        }

        /// <summary>Asks the window to close, once OK or Cancel is done.</summary>
        public event EventHandler? CloseRequested;

        public IReadOnlyList<ChartSettingsSection> Sections { get; } =
        [
            new("Timing", Strings.ChartSettingsSectionTiming),
            new("Parts", Strings.ChartSettingsSectionParts),
        ];

        [ObservableProperty]
        public partial ChartSettingsSection? SelectedSection { get; set; }

        /// <summary>The command OK made, once it has; null when nothing changed, or it was cancelled.</summary>
        public IChartCommand? Command { get; private set; }

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
        public bool CanChangeDrums { get; }

        public string? DrumsToolTip => CanChangeDrums ? null : Strings.ChartSettingsDrumsLockedToolTip;

        // The footer.

        /// <summary>What stops OK, or why it last failed; null when nothing does.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasProblem))]
        [NotifyCanExecuteChangedFor(nameof(OkCommand))]
        public partial string? Problem { get; private set; }

        public bool HasProblem => Problem is not null;

        [RelayCommand(CanExecute = nameof(CanOk))]
        private async Task OkAsync()
        {
            var values = Values();
            if (values.Resolution != _chart.Resolution)
            {
                ResolutionChangeReport report;
                try
                {
                    report = ResolutionChangeReport.Analyze(_chart, values.Resolution);
                }
                catch (ChartFormatException ex)
                {
                    Problem = string.Format(CultureInfo.CurrentCulture, Strings.ResolutionChangeFailedFormat, ex.Message);
                    return;
                }

                // Worked out before anything changes, and said, so the user can go back (charter-spec C1).
                if (report.HasWarning && !await _confirmations.ConfirmAsync(
                        Strings.ResolutionWarningTitle, WarningText(report), Strings.ResolutionWarningConfirm))
                    return;
            }

            Command = ChartSettingsEdit.CommandFor(_chart, values, _settings.Settings.TempoEdits == TempoEditMode.KeepTimes);
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        private bool CanOk() => Problem is null;

        [RelayCommand]
        private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

        partial void OnBpmChanged(decimal? value) => Refresh();

        partial void OnNumeratorChanged(decimal? value) => Refresh();

        private async void OnPartChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ChartPartChoice.IsChecked) || sender is not ChartPartChoice part)
                return;

            if (part.IsDrums)
                OnPropertyChanged(nameof(HasDrums));

            // Unticking a part that has notes asks first, as Remove part does (charter-spec C1, Parts).
            if (_restoringPart || part.IsChecked || _chart.PartOf(part.Instrument) is not { HasNotes: true })
                return;

            try
            {
                bool remove = await _confirmations.ConfirmAsync(
                    Strings.RemovePartTitle,
                    string.Format(CultureInfo.CurrentCulture, Strings.RemovePartMessageFormat, part.Label),
                    Strings.RemovePartConfirm);
                if (!remove)
                {
                    _restoringPart = true;
                    part.IsChecked = true;
                    _restoringPart = false;
                }
            }
            catch (Exception ex)
            {
                // Nothing awaits this, so anything not caught here would take the app down.
                Trace.TraceError($"Asking to remove a part failed: {ex}");
            }
        }

        private ChartSettingsValues Values()
        {
            // The BPM shown is rounded, so one left as it was keeps the tempo the chart has.
            long microseconds = Bpm == _startBpm ? _startMicroseconds : EditableTempoMap.MicrosecondsPerQuarterOf((double)Bpm!.Value);
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

        /// <summary>Works out what stops OK: a BPM or time signature out of range.</summary>
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
