using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Threading;
using ChartBuilder.Resources;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// A row of the Tempo map tab (charter-spec C4): a tick where the tempo or the time signature changes, with fields
    /// that change, add or remove either there. A field with nothing placed at the tick shows what's in force there, greyed.
    /// </summary>
    public sealed class TempoChangeItem : ObservableObject
    {
        private readonly TempoMapViewModel _owner;
        private decimal? _bpm;
        private decimal? _tickValue;
        private string _where;
        private bool _isCurrent;
        private decimal? _editNumerator;
        private int _editDenominator;

        internal TempoChangeItem(TempoMapViewModel owner, TempoMapRow row, string where)
        {
            _owner = owner;
            Row = row;
            _where = where;
            Fill(force: true, old: null);
            GoToCommand = new RelayCommand(() => owner.GoTo(this));
            RemoveCommand = new RelayCommand(() => owner.Remove(this));
            RemoveTempoCommand = new RelayCommand(() => owner.RemoveTempo(this));
            RemoveSignatureCommand = new RelayCommand(() => owner.RemoveSignature(this));
            RemoveBothCommand = new RelayCommand(() => owner.RemoveBoth(this));
            ToggleAnchorCommand = new RelayCommand(() => owner.ToggleAnchor(this));
            MoveToPlayheadCommand = new RelayCommand(() => owner.MoveToPlayhead(this));
            ApplySignatureCommand = new RelayCommand(() => owner.ApplySignature(this));
            ChooseSignatureCommand = new RelayCommand<string>(text => owner.ChooseSignature(this, text));
        }

        /// <summary>The change, and what's in force there.</summary>
        internal TempoMapRow Row { get; private set; }

        /// <summary>Whether its fields are filled again at the next refresh whatever's typed in them, after an edit of its own.</summary>
        internal bool IsStale { get; set; }

        /// <summary>Whether a BPM typed or scrubbed in it is still to be applied, so a refresh leaves it alone.</summary>
        internal bool IsBpmPending { get; set; }

        /// <summary>Whether its tick scrubbed or typed is still to be applied, so a refresh leaves it alone.</summary>
        internal bool IsTickPending { get; set; }

        /// <summary>Shows the tick the chart has again, taking back one scrubbed or typed but not applied.</summary>
        internal void RestoreTick()
        {
            _tickValue = Row.Tick;
            OnPropertyChanged(nameof(TickValue));
        }

        public long Tick => Row.Tick;

        public bool HasTempo => Row.HasTempo;

        public bool HasSignature => Row.HasSignature;

        /// <summary>Whether the tempo marker here is anchored, for the anchor button.</summary>
        public bool IsAnchored => Row.IsAnchored;

        /// <summary>Whether there's anything here that can be removed: not at the song's start.</summary>
        public bool CanRemove => Row.Tick > 0 && (Row.HasTempo || Row.HasSignature);

        /// <summary>Whether ✕ removes one thing straight away, rather than asking which.</summary>
        public bool RemovesOne => CanRemove && !(Row.HasTempo && Row.HasSignature);

        /// <summary>Whether ✕ asks which of the tempo and the time signature to remove.</summary>
        public bool RemovesEither => CanRemove && Row.HasTempo && Row.HasSignature;

        /// <summary>The tempo marker's BPM, edited to change it; null when there's none here.</summary>
        public decimal? Bpm
        {
            get => _bpm;
            set
            {
                if (value == _bpm)
                    return;
                _bpm = value;
                OnPropertyChanged();
                _owner.SetBpm(this, value);
            }
        }

        /// <summary>The BPM in force here, shown greyed in the BPM field when there's no tempo marker.</summary>
        public string BpmInForce => TempoMapViewModel.BpmText(Row.TempoInForce);

        /// <summary>The time signature here, or the one in force here, e.g. "7/8", on the button that opens its editor.</summary>
        public string SignatureText
        {
            get
            {
                (int numerator, int denominator) = Row.Signature is { } placed
                    ? (placed.Numerator, placed.Denominator)
                    : (Row.SignatureInForce.Numerator, Row.SignatureInForce.Denominator);
                return string.Format(CultureInfo.CurrentCulture, "{0}/{1}", numerator, denominator);
            }
        }

        /// <summary>The time signatures the editor offers in one click.</summary>
        public IReadOnlyList<string> SignaturePresets => TempoMapViewModel.SignaturePresets;

        /// <summary>The beats in the time signature's editor, applied with <see cref="ApplySignatureCommand"/>.</summary>
        public decimal? EditNumerator
        {
            get => _editNumerator;
            set => SetProperty(ref _editNumerator, value);
        }

        /// <summary>The note value in the time signature's editor, applied with <see cref="ApplySignatureCommand"/>.</summary>
        public int EditDenominator
        {
            get => _editDenominator;
            set => SetProperty(ref _editDenominator, value);
        }

        /// <summary>Whether the time signature's editor offers to remove it: there's one here, and not at the song's start.</summary>
        public bool CanRemoveSignature => HasSignature && Row.Tick > 0;

        /// <summary>The note values a beat can be, the one in force among them.</summary>
        public IReadOnlyList<int> Denominators { get; private set; } = [];

        /// <summary>Its tick, which is scrubbed or typed to move it.</summary>
        public decimal? TickValue
        {
            get => _tickValue;
            set
            {
                if (value == _tickValue)
                    return;
                _tickValue = value;
                OnPropertyChanged();
                _owner.SetTick(this, value);
            }
        }

        /// <summary>Where it is: its time in the song, and its measure and beat.</summary>
        public string Where
        {
            get => _where;
            private set => SetProperty(ref _where, value);
        }

        /// <summary>Whether it's the change in force at the cursor.</summary>
        public bool IsCurrent
        {
            get => _isCurrent;
            internal set => SetProperty(ref _isCurrent, value);
        }

        public IRelayCommand GoToCommand { get; }

        /// <summary>Removes what's here, when that's one thing.</summary>
        public IRelayCommand RemoveCommand { get; }

        public IRelayCommand RemoveTempoCommand { get; }

        public IRelayCommand RemoveSignatureCommand { get; }

        public IRelayCommand RemoveBothCommand { get; }

        public IRelayCommand ToggleAnchorCommand { get; }

        /// <summary>Moves the change to the playhead, on the grid, as Add tempo change places one there.</summary>
        public IRelayCommand MoveToPlayheadCommand { get; }

        /// <summary>Places the time signature in the editor's fields here, or changes the one here to it.</summary>
        public IRelayCommand ApplySignatureCommand { get; }

        /// <summary>Places one of <see cref="SignaturePresets"/> here, given as "7/8".</summary>
        public IRelayCommand<string> ChooseSignatureCommand { get; }

        /// <summary>Fills the time signature's editor with the signature here, or the one in force here, as it opens.</summary>
        public void BeginSignatureEdit()
        {
            if (Row.Signature is { } placed)
            {
                EditNumerator = placed.Numerator;
                EditDenominator = placed.Denominator;
            }
            else
            {
                EditNumerator = Row.SignatureInForce.Numerator;
                EditDenominator = Row.SignatureInForce.Denominator;
            }
        }

        /// <summary>Takes up the tick an edit of its own has moved it to, so the next refresh finds this row for it.</summary>
        internal void Rekey(long tick) => Row = Row with { Tick = tick };

        /// <summary>
        /// Takes up <paramref name="row"/>, the change as the tempo map now has it. A field is filled again only when what
        /// it shows has changed, or after an edit of its own, so what's being typed in it isn't lost to someone else's edit.
        /// </summary>
        internal void Update(TempoMapRow row, string where)
        {
            var old = Row;
            bool force = IsStale;
            Row = row;
            IsStale = false;
            Fill(force, old);
            Where = where;
        }

        private void Fill(bool force, TempoMapRow? old)
        {
            var row = Row;
            if (old is not { } was || was.HasTempo != row.HasTempo || was.HasSignature != row.HasSignature)
            {
                OnPropertyChanged(nameof(HasTempo));
                OnPropertyChanged(nameof(HasSignature));
                OnPropertyChanged(nameof(CanRemove));
                OnPropertyChanged(nameof(RemovesOne));
                OnPropertyChanged(nameof(RemovesEither));
                OnPropertyChanged(nameof(CanRemoveSignature));
            }

            // Always, so the anchor button shows the marker again after a refused toggle.
            OnPropertyChanged(nameof(IsAnchored));
            if (old is not { } previous || previous.Tick != row.Tick)
                OnPropertyChanged(nameof(Tick));
            OnPropertyChanged(nameof(BpmInForce));
            OnPropertyChanged(nameof(SignatureText));

            decimal? bpm = row.Tempo is { } tempo ? TempoMapViewModel.BpmValueOf(tempo.MicrosecondsPerQuarter) : null;
            if (!IsBpmPending && (force || bpm != _bpm))
                SetField(ref _bpm, bpm, nameof(Bpm));

            // The time signature editor's note values, with the one here among them.
            int denominator = row.Signature?.Denominator ?? row.SignatureInForce.Denominator;
            if (!Denominators.Contains(denominator))
            {
                Denominators = [.. NewChartSettings.Denominators.Append(denominator).Distinct().Order()];
                OnPropertyChanged(nameof(Denominators));
            }

            decimal tickValue = row.Tick;
            if (!IsTickPending && (force || tickValue != _tickValue))
                SetField(ref _tickValue, tickValue, nameof(TickValue));
        }

        // Sets a field and says so, without the edit its setter makes.
        private void SetField<T>(ref T field, T value, string name)
        {
            field = value;
            OnPropertyChanged(name);
        }
    }

    /// <summary>Which field of a row the keyboard is put in, once the row's listed.</summary>
    public enum TempoField
    {
        Bpm,
        Numerator,
    }

    /// <summary>A row whose field the keyboard is put in, once it's listed.</summary>
    public sealed record TempoFieldRequest(TempoChangeItem Item, TempoField Field);

    /// <summary>
    /// The Tempo map tab (charter-spec C4): every tick where the tempo or the time signature changes, in time order, each
    /// with its time and its measure and beat. Each can be gone to, have its BPM, time signature and anchor changed, be
    /// moved by its tick and be removed, each as one undoable step. <b>Add tempo change</b> adds a tempo marker at the
    /// cursor, on the grid; <b>Add time signature</b> puts one at the start of the measure the cursor is in.
    /// </summary>
    /// <remarks>
    /// The rows follow the chart (an edit, an undo or redo, or a drag's preview) and the song's delay, and are worked out
    /// once the changes of a moment are in. A row whose tick is still a change is kept, with whatever's typed in its
    /// fields. The row in force at the cursor is marked as the cursor moves, playing or not.
    /// </remarks>
    public sealed partial class TempoMapViewModel : ViewModelBase, IDisposable
    {
        /// <summary>The time signatures Add time signature offers, before Custom….</summary>
        private static readonly (int Numerator, int Denominator)[] CommonSignatures = [(2, 4), (3, 4), (4, 4), (5, 4), (6, 8), (7, 8), (12, 8)];

        // How long a field typed or scrubbed is left alone before it's applied, so a drag is one undoable step.
        private static readonly TimeSpan FieldTypingDelay = TimeSpan.FromMilliseconds(600);

        /// <summary>The common time signatures as they're written, e.g. "6/8", which a row's time signature editor offers.</summary>
        internal static readonly IReadOnlyList<string> SignaturePresets =
            [.. CommonSignatures.Select(s => string.Format(CultureInfo.InvariantCulture, "{0}/{1}", s.Numerator, s.Denominator))];

        private readonly ChartEditorViewModel _editor;
        private List<TempoChangeItem> _items = [];
        private List<TempoMapRow> _rows = [];
        private TempoChangeItem? _current;
        private bool _scheduled;
        private bool _disposed;

        // A BPM scrubbed or typed, shown on the chart at once, and applied once it's been left alone.
        private readonly DispatcherTimer _bpmApply;
        private (TempoChangeItem Item, decimal? Bpm)? _pendingBpm;
        private bool _bpmPreviewing;

        // A tick scrubbed or typed, shown on the chart at once, and applied once it's been left alone.
        private readonly DispatcherTimer _tickApply;
        private (TempoChangeItem Item, decimal? Tick)? _pendingTick;
        private bool _tickPreviewing;

        // A change added to be typed into, whose field is edited once it's listed.
        private (long Tick, TempoField Field)? _editNext;

        // Each tick's time and measure and beat, kept while the tempo map and the delay are the ones they were worked out
        // with.
        private readonly Dictionary<long, string> _whereByTick = [];
        private TempoMap? _whereMap;
        private TimeSpan _whereDelay;

        /// <param name="chartSettings">The chart's resolution and the audio's delay, and Detect from audio, which the tab's top has.</param>
        public TempoMapViewModel(ChartEditorViewModel editor, ChartSettingsViewModel chartSettings)
        {
            _editor = editor;
            ChartSettings = chartSettings;
            _bpmApply = new DispatcherTimer(FieldTypingDelay, DispatcherPriority.Background, (_, _) => ApplyBpm());
            _bpmApply.Stop();
            _tickApply = new DispatcherTimer(FieldTypingDelay, DispatcherPriority.Background, (_, _) => ApplyTick());
            _tickApply.Stop();
            AddSignatureMenu =
            [
                .. CommonSignatures.Select(s => LaneMenuItem.Choice(
                    string.Format(CultureInfo.CurrentCulture, "{0}/{1}", s.Numerator, s.Denominator),
                    false,
                    () => AddSignature(s.Numerator, s.Denominator))),
                LaneMenuItem.Choice(Strings.TempoMapCustomSignature, false, AddCustomSignature),
            ];
            Session.ChartChanged += OnChartChanged;
            Session.SongSettingsChanged += OnSongSettingsChanged;
            Session.Viewport.Changed += OnCursorChanged;
            Refresh();
        }

        /// <summary>Timing: the resolution and the delay, and Detect from audio (charter-spec C1).</summary>
        public ChartSettingsViewModel ChartSettings { get; }

        /// <summary>From the audio: a proposed tempo map to review, and Find first downbeat (charter-spec C14).</summary>
        public AudioAssistViewModel AudioAssist => _editor.AudioAssist;

        /// <summary>The list's rows: a <see cref="TempoChangeItem"/> for each change, in time order.</summary>
        public AvaloniaList<TempoChangeItem> Rows { get; } = [];

        /// <summary><b>Add time signature</b>'s menu: the common signatures, then Custom….</summary>
        public IReadOnlyList<LaneMenuItem> AddSignatureMenu { get; }

        /// <summary>The tempo and time signature at the cursor, e.g. "140 BPM · 3/4".</summary>
        [ObservableProperty]
        public partial string AtCursorText { get; private set; } = "";

        /// <summary>Raised when a change added to be typed into is listed, for the view to put the keyboard in its field.</summary>
        public event EventHandler<TempoFieldRequest>? EditRequested;

        private EditorSession Session => _editor.Session;

        /// <summary>A tempo in beats per minute, to three decimals at most, e.g. "120" or "96.5".</summary>
        internal static string BpmText(long microsecondsPerQuarter) =>
            EditableTempoMap.BpmOf(microsecondsPerQuarter).ToString("0.###", CultureInfo.CurrentCulture);

        /// <summary>
        /// A tempo in beats per minute for the BPM box, to three decimals as it's shown, e.g. 120 or 96.5.
        /// </summary>
        internal static decimal BpmValueOf(long microsecondsPerQuarter) =>
            Math.Round((decimal)EditableTempoMap.BpmOf(microsecondsPerQuarter), 3, MidpointRounding.AwayFromZero);

        /// <summary>
        /// The slowest tempo the BPM box offers, in beats per minute: a beat as long as MIDI can hold, rounded up to the
        /// three decimals it shows, so that what it holds can always be applied.
        /// </summary>
        public static decimal MinBpm =>
            Math.Ceiling((decimal)(60_000_000.0 / EditableTempoMap.MaxMicrosecondsPerQuarter * 1000)) / 1000;

        public void Dispose()
        {
            ClearBpmPreview();
            ClearTickPreview();
            _disposed = true;
            _bpmApply.Stop();
            _pendingBpm = null;
            _tickApply.Stop();
            _pendingTick = null;
            Session.ChartChanged -= OnChartChanged;
            Session.SongSettingsChanged -= OnSongSettingsChanged;
            Session.Viewport.Changed -= OnCursorChanged;
        }

        [RelayCommand]
        private void AddTempoChange()
        {
            if (Session.Tools.AddTempoChangeAtCursor() is { } tick)
                EditNext(tick, TempoField.Bpm);
        }

        private void AddSignature(int numerator, int denominator) => Session.Tools.PlaceSignatureAtCursor(numerator, denominator);

        // The signature in force at the start of the cursor's measure, placed there to be typed over.
        private void AddCustomSignature()
        {
            var map = Session.TempoMap;
            long start = MeasureBeat.MeasureStart(map, (long)Math.Round(Math.Max(0, Session.TickAt(Session.Viewport.Cursor))));
            if (!Session.Chart.TempoMap.TimeSignatures.At(start).IsEmpty)
            {
                EditNext(start, TempoField.Numerator);
                return;
            }

            var inForce = map.TimeSignatures.Last(s => s.Tick <= start);
            if (Session.Tools.PlaceSignatureAtCursor(inForce.Numerator, inForce.Denominator) is { } tick)
                EditNext(tick, TempoField.Numerator);
        }

        private void EditNext(long tick, TempoField field)
        {
            _editNext = (tick, field);
            Schedule();
        }

        /// <summary>Moves the cursor to <paramref name="item"/>'s change, and selects its tempo marker when it has one.</summary>
        internal void GoTo(TempoChangeItem item)
        {
            FlushPending();
            Session.MoveCursorTo(item.Tick);
            if (item.HasTempo)
                Session.Selection.SelectTempos([item.Tick]);
        }

        internal void Remove(TempoChangeItem item)
        {
            FlushPending();
            if (item.HasTempo)
                RemoveTempo(item);
            else if (item.HasSignature)
                RemoveSignature(item);
        }

        internal void RemoveTempo(TempoChangeItem item)
        {
            FlushPending();
            long tick = item.Tick;
            Edit(item, blocked => TempoMapEdits.RemoveMarker(Session.Chart, tick, Session.Delay, Session.KeepTimes, blocked));
        }

        internal void RemoveBoth(TempoChangeItem item)
        {
            FlushPending();
            long tick = item.Tick;
            Edit(item, blocked => TempoMapEdits.RemoveChange(Session.Chart, tick, Session.Delay, Session.KeepTimes, blocked));
        }

        internal void RemoveSignature(TempoChangeItem item)
        {
            FlushPending();
            Execute(item, TempoMapEdits.RemoveSignature(Session.Chart, item.Tick));
        }

        internal void ToggleAnchor(TempoChangeItem item)
        {
            FlushPending();
            Execute(item, TempoMapEdits.SetAnchored(Session.Chart, item.Tick, !item.IsAnchored, Session.Delay));
        }

        /// <summary>
        /// Takes back the BPM typed at <paramref name="item"/> but not yet applied, showing the tempo the chart has.
        /// </summary>
        internal void RevertBpm(TempoChangeItem item) => CancelPendingBpm(item);

        // Forgets a BPM scrubbed or typed in this row, showing the tempo the chart has again. Another row's is left alone.
        private void CancelPendingBpm(TempoChangeItem item)
        {
            if (_pendingBpm is { } pending && ReferenceEquals(pending.Item, item))
            {
                _pendingBpm = null;
                _bpmApply.Stop();
                item.IsBpmPending = false;
                ClearBpmPreview();
            }

            item.IsStale = true;
            Schedule();
        }

        /// <summary>
        /// Takes up the BPM scrubbed or typed at <paramref name="item"/>'s row: the chart shows it at once, and it's put on
        /// the undo stack once it's been left alone, so a drag or a burst of typing is one undoable tempo edit. A row
        /// changed before it's applied takes its own BPM.
        /// </summary>
        internal void SetBpm(TempoChangeItem item, decimal? bpm)
        {
            if (_pendingTick is not null)
                ApplyTick();
            if (_pendingBpm is { } pending && !ReferenceEquals(pending.Item, item))
                ApplyBpm();

            _pendingBpm = (item, bpm);
            item.IsBpmPending = true;
            PreviewBpm(item, bpm);
            _bpmApply.Stop();
            _bpmApply.Start();
        }

        // Shows the tempo the BPM makes on the chart, without putting it on the undo stack, so a drag is seen as it's made.
        private void PreviewBpm(TempoChangeItem item, decimal? bpm)
        {
            ClearBpmPreview();
            TempoEdit edit;
            long tick = item.Tick;
            if (bpm is not { } value)
            {
                if (Session.Chart.TempoMap.Tempos.At(tick).IsEmpty)
                    return;
                edit = TempoMapEdits.RemoveMarker(Session.Chart, tick, Session.Delay, Session.KeepTimes);
            }
            else
            {
                long tempo;
                try
                {
                    tempo = EditableTempoMap.MicrosecondsPerQuarterOf((double)Math.Round(value, 3));
                }
                catch (ArgumentOutOfRangeException)
                {
                    Session.Status = Strings.PropertiesBadBpm;
                    return;
                }

                if (!Session.Chart.TempoMap.Tempos.At(tick).IsEmpty && TempoMapEdits.TempoAt(Session.Chart.TempoMap, tick) == tempo)
                    return;
                edit = TempoMapEdits.PlaceBpm(Session.Chart, tick, tempo, Session.Delay, Session.KeepTimes);
            }

            if (edit.IsRefused)
            {
                // An anchor it can't keep is offered a way out once the value settles; anything else says why now.
                if (edit.Blocking is null)
                    Session.Status = edit.Refusal;
                return;
            }

            if (Session.Preview(edit.Command))
                _bpmPreviewing = true;
        }

        // Takes the shown BPM off the chart again, leaving the tempo it had.
        private void ClearBpmPreview()
        {
            if (!_bpmPreviewing)
                return;
            _bpmPreviewing = false;
            Session.Preview(null);
        }

        // Applies the BPM last typed or scrubbed: adding a marker when there's none, changing it when there is, and
        // removing it when the field is emptied.
        private void ApplyBpm()
        {
            _bpmApply.Stop();
            if (_pendingBpm is not { } pending)
                return;
            _pendingBpm = null;
            if (_disposed)
                return;

            var item = pending.Item;
            item.IsBpmPending = false;

            // The value shown is the one settled on: put it on the undo stack as one step.
            if (_bpmPreviewing)
            {
                _bpmPreviewing = false;
                if (Session.CommitPreview() is not null)
                {
                    Edited(item);
                    return;
                }
            }

            long tick = item.Tick;
            if (pending.Bpm is not { } bpm)
            {
                if (!Session.Chart.TempoMap.Tempos.At(tick).IsEmpty)
                    RemoveTempo(item);
                else
                    Edited(item);
                return;
            }

            long tempo;
            try
            {
                tempo = EditableTempoMap.MicrosecondsPerQuarterOf((double)Math.Round(bpm, 3));
            }
            catch (ArgumentOutOfRangeException)
            {
                Session.Status = Strings.PropertiesBadBpm;
                Edited(item);
                return;
            }

            if (!Session.Chart.TempoMap.Tempos.At(tick).IsEmpty && TempoMapEdits.TempoAt(Session.Chart.TempoMap, tick) == tempo)
            {
                Edited(item);
                return;
            }

            Edit(item, blocked => TempoMapEdits.PlaceBpm(Session.Chart, tick, tempo, Session.Delay, Session.KeepTimes, blocked));
        }

        private void PlaceSignature(TempoChangeItem item, int numerator, int denominator)
        {
            FlushPending();
            var signature = item.Row.Signature;
            if (signature is { } placed && placed.Numerator == numerator && placed.Denominator == denominator)
            {
                Edited(item);
                return;
            }

            Execute(item, TempoMapEdits.PlaceSignature(Session.Chart, item.Tick, numerator, denominator));
        }

        /// <summary>Places the time signature in <paramref name="item"/>'s editor at its tick.</summary>
        internal void ApplySignature(TempoChangeItem item)
        {
            if (item.EditNumerator is not { } beats || beats < 1 || beats != Math.Floor(beats) || beats > int.MaxValue || item.EditDenominator <= 0)
            {
                Session.Status = Strings.TempoMapBadNumerator;
                return;
            }

            PlaceSignature(item, (int)beats, item.EditDenominator);
        }

        /// <summary>Places <paramref name="text"/>, a time signature written as "7/8", at <paramref name="item"/>'s tick.</summary>
        internal void ChooseSignature(TempoChangeItem item, string? text)
        {
            string[] parts = text?.Split('/') ?? [];
            if (parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int numerator)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int denominator))
                PlaceSignature(item, numerator, denominator);
        }

        /// <summary>
        /// Moves <paramref name="item"/>'s tempo marker and time signature to the playhead, on the grid: the tick Add tempo
        /// change would place a marker at.
        /// </summary>
        internal void MoveToPlayhead(TempoChangeItem item)
        {
            FlushPending();
            long to = Session.CursorTick;
            if (!item.CanRemove || to == item.Tick)
                return;
            _ = MoveAsync(item, to);
        }

        /// <summary>
        /// Takes up the tick scrubbed or typed at <paramref name="item"/>'s row, to apply once it's been left alone, so a
        /// drag is one undoable step. A row changed before it's applied takes its own tick.
        /// </summary>
        internal void SetTick(TempoChangeItem item, decimal? tick)
        {
            if (_pendingBpm is not null)
                ApplyBpm();
            if (_pendingTick is { } pending && !ReferenceEquals(pending.Item, item))
                ApplyTick();

            _pendingTick = (item, tick);
            item.IsTickPending = true;
            PreviewTick(item, tick);
            _tickApply.Stop();
            _tickApply.Start();
        }

        // Shows the change at the tick scrubbed or typed on the chart, without putting it on the undo stack.
        private void PreviewTick(TempoChangeItem item, decimal? tick)
        {
            ClearTickPreview();
            if (tick is not { } value || value < 0 || value > long.MaxValue || value != Math.Floor(value))
                return;

            long to = (long)value;
            if (to == item.Tick || !item.CanRemove)
                return;

            var edit = TempoMapEdits.MoveChange(Session.Chart, item.Tick, to, Session.Delay, Session.KeepTimes);
            if (edit.IsRefused)
            {
                // An anchor it can't keep is offered a way out once the value settles; anything else says why now.
                if (edit.Blocking is null)
                    Session.Status = edit.Refusal;
                return;
            }

            if (Session.Preview(edit.Command))
                _tickPreviewing = true;
        }

        // Takes the shown tick off the chart again, leaving the change where it was.
        private void ClearTickPreview()
        {
            if (!_tickPreviewing)
                return;
            _tickPreviewing = false;
            Session.Preview(null);
        }

        // Moves the marker and time signature to the tick scrubbed or typed: anything else is put back.
        private void ApplyTick()
        {
            _tickApply.Stop();
            if (_pendingTick is not { } pending)
                return;
            _pendingTick = null;
            if (_disposed)
                return;

            var item = pending.Item;
            item.IsTickPending = false;
            if (pending.Tick is not { } value || value < 0 || value > long.MaxValue || value != Math.Floor(value))
            {
                ClearTickPreview();
                Edited(item);
                return;
            }

            long to = (long)value;
            if (to == item.Tick || !item.CanRemove)
            {
                ClearTickPreview();
                Edited(item);
                return;
            }

            // The tick shown is the one settled on: put it on the undo stack as one step.
            if (_tickPreviewing)
            {
                _tickPreviewing = false;
                if (Session.CommitPreview() is not null)
                {
                    item.Rekey(to);
                    Edited(item);
                    return;
                }
            }

            _ = MoveAsync(item, to);
        }

        /// <summary>Takes back a tick scrubbed or typed but not yet applied, showing the tick the chart has.</summary>
        internal void RevertTick(TempoChangeItem item) => CancelPendingTick(item);

        // Forgets a tick scrubbed or typed in this row, so a button pressed next doesn't apply it afterwards. The list is
        // refreshed as it then is, as a refresh asked for while the tick was pending was left to this.
        private void CancelPendingTick(TempoChangeItem item)
        {
            if (_pendingTick is not { } pending || !ReferenceEquals(pending.Item, item))
                return;
            _pendingTick = null;
            _tickApply.Stop();
            item.IsTickPending = false;
            ClearTickPreview();
            item.RestoreTick();
            Schedule();
        }

        // Applies a BPM or tick scrubbed or typed but not yet settled, as one undo step, before another change is made:
        // the change shown is put on the undo stack over the chart as it is then, so a commit can't take back what's
        // been done meanwhile.
        private void FlushPending()
        {
            if (_pendingTick is not null)
                ApplyTick();
            if (_pendingBpm is not null)
                ApplyBpm();
        }

        private async Task MoveAsync(TempoChangeItem item, long to)
        {
            long tick = item.Tick;
            bool moved = await _editor.ApplyTempoEditAsync(blocked => TempoMapEdits.MoveChange(Session.Chart, tick, to, Session.Delay, Session.KeepTimes, blocked));
            if (moved)
                item.Rekey(to);
            Edited(item);
        }

        // A tempo edit, with the offer to unanchor or add a marker when it can't keep an anchor.
        private void Edit(TempoChangeItem item, Func<BlockedAnchor, TempoEdit> make) => _ = EditAsync(item, make);

        private async Task EditAsync(TempoChangeItem item, Func<BlockedAnchor, TempoEdit> make)
        {
            await _editor.ApplyTempoEditAsync(make);
            Edited(item);
        }

        // An edit that never meets an anchor.
        private void Execute(TempoChangeItem item, TempoEdit edit)
        {
            if (edit.IsRefused)
                Session.Status = edit.Refusal;
            else
                Session.Execute(edit.Command!);
            Edited(item);
        }

        // Done or not, the row shows the change as it then is.
        private void Edited(TempoChangeItem item)
        {
            item.IsStale = true;
            Schedule();
        }

        private void OnChartChanged(object? sender, EditorChartChangedEventArgs e)
        {
            // A tick being scrubbed moves the row, which is put back in order once it settles; a refresh asked for
            // meanwhile is made then, or when the tick is taken back.
            if (_pendingTick is not null)
                return;
            if (e.Affected.TempoMapChanged)
                Schedule();
        }

        // The delay moves every change's time.
        private void OnSongSettingsChanged(object? sender, EventArgs e) => Schedule();

        private void OnCursorChanged(object? sender, EventArgs e) => MarkCurrent();

        // Once the changes of a moment are in, so a drag's preview or a run of edits is refreshed once.
        private void Schedule()
        {
            if (_scheduled)
                return;
            _scheduled = true;
            Dispatcher.UIThread.Post(Refresh, DispatcherPriority.Background);
        }

        private void Refresh()
        {
            _scheduled = false;
            // A tick being scrubbed is put back in order once it settles, or when it's taken back, which asks again.
            if (_disposed || _pendingTick is not null)
                return;

            var map = Session.TempoMap;
            if (!ReferenceEquals(map, _whereMap) || Session.Delay != _whereDelay || _whereByTick.Count > 4096)
            {
                _whereByTick.Clear();
                _whereMap = map;
                _whereDelay = Session.Delay;
            }

            _rows = [.. TempoMapRows.Of(Session.Chart.TempoMap)];
            _items = Sync(_items, _rows, map);
            _current = null;
            ListRows.Replace(Rows, _items);
            MarkCurrent();

            if (_editNext is { } wanted)
            {
                _editNext = null;
                if (_items.FirstOrDefault(i => i.Tick == wanted.Tick) is { } item)
                    EditRequested?.Invoke(this, new TempoFieldRequest(item, wanted.Field));
            }
        }

        /// <summary>
        /// The rows of <paramref name="rows"/>, keeping each of <paramref name="items"/> whose tick is still a change, so the
        /// rows kept keep their fields and what's typed in them.
        /// </summary>
        private List<TempoChangeItem> Sync(List<TempoChangeItem> items, List<TempoMapRow> rows, TempoMap map)
        {
            var unused = new Dictionary<long, TempoChangeItem>(items.Count);
            foreach (var item in items)
                unused.TryAdd(item.Tick, item);

            var next = new List<TempoChangeItem>(rows.Count);
            foreach (var row in rows)
            {
                string where = WhereOf(row.Tick, map);
                if (unused.Remove(row.Tick, out var item))
                {
                    item.Update(row, where);
                    next.Add(item);
                }
                else
                {
                    next.Add(new TempoChangeItem(this, row, where));
                }
            }

            return next;
        }

        // Marks the row in force at the cursor, and says what's in force there.
        private void MarkCurrent()
        {
            if (_disposed || _items.Count == 0)
                return;
            double tick = Session.TickAt(Session.Viewport.Cursor);
            int index = TempoMapRows.IndexAt(_rows, tick);
            var current = index >= 0 && index < _items.Count ? _items[index] : null;
            if (ReferenceEquals(current, _current))
                return;

            foreach (var item in _items)
                item.IsCurrent = ReferenceEquals(item, current);
            _current = current;
            if (current is not null)
            {
                var row = current.Row;
                AtCursorText = string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.TempoMapAtCursorValueFormat,
                    BpmText(row.TempoInForce),
                    row.SignatureInForce.Numerator,
                    row.SignatureInForce.Denominator);
            }
        }

        /// <summary>Where <paramref name="tick"/> is: its time in the song, and its measure and beat.</summary>
        private string WhereOf(long tick, TempoMap map)
        {
            if (!_whereByTick.TryGetValue(tick, out string? where))
            {
                where = string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.SectionsWhereFormat,
                    SongTime.Format(TimeSpan.FromSeconds(Math.Max(0, Session.AudioAt(tick))), decimals: 2),
                    PropertiesViewModel.MeasureBeat(map, tick));
                _whereByTick[tick] = where;
            }

            return where;
        }
    }
}
