using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ChartBuilder.Resources;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charting.Editing;
using ChartPacker.Core.Charts;
using ChartPacker.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// The Properties panel (charter-spec C3 item 4): the selected notes, tempo markers and span. A field shows
    /// <see cref="Multiple"/> when the items differ, and editing it sets every item, as one undoable step.
    /// </summary>
    public sealed partial class PropertiesViewModel : ViewModelBase
    {
        /// <summary>What a field shows when the selected items' values differ, as the metadata editor shows it.</summary>
        public const string Multiple = "<<multiple>>";

        private readonly ChartEditorViewModel _editor;

        // While the fields are filled from the selection, so filling them isn't taken as an edit.
        private bool _loading;

        internal PropertiesViewModel(ChartEditorViewModel editor)
        {
            _editor = editor;
        }

        private EditorSession Session => _editor.Session;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsEmpty))]
        public partial bool HasNotes { get; private set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsEmpty))]
        public partial bool HasTempos { get; private set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsEmpty))]
        public partial bool HasSpan { get; private set; }

        public bool IsEmpty => !HasNotes && !HasTempos && !HasSpan;

        [ObservableProperty]
        public partial string NotesHeading { get; private set; } = "";

        [ObservableProperty]
        public partial string TickText { get; set; } = "";

        [ObservableProperty]
        public partial string TimeText { get; private set; } = "";

        [ObservableProperty]
        public partial string MeasureBeatText { get; private set; } = "";

        [ObservableProperty]
        public partial string LaneText { get; private set; } = "";

        [ObservableProperty]
        public partial string LengthText { get; set; } = "";

        /// <summary>Whether the notes' chords can be forced or tapped: not drums.</summary>
        [ObservableProperty]
        public partial bool CanForce { get; private set; }

        [ObservableProperty]
        public partial bool? IsForcedHopo { get; set; }

        [ObservableProperty]
        public partial bool? IsForcedStrum { get; set; }

        [ObservableProperty]
        public partial bool? IsTap { get; set; }

        [ObservableProperty]
        public partial string TemposHeading { get; private set; } = "";

        [ObservableProperty]
        public partial string BpmText { get; set; } = "";

        [ObservableProperty]
        public partial string NumeratorText { get; set; } = "";

        [ObservableProperty]
        public partial string DenominatorText { get; set; } = "";

        [ObservableProperty]
        public partial bool? IsAnchored { get; set; }

        [ObservableProperty]
        public partial string SpanText { get; private set; } = "";

        /// <summary>How many beats the selected span lasts, for Set BPM from selection (charter-spec C4 item 6).</summary>
        [ObservableProperty]
        public partial string BeatsText { get; set; } = "4";

        /// <summary>Fills the fields from the selection, as it is now.</summary>
        public void Refresh()
        {
            _loading = true;
            try
            {
                RefreshNotes();
                RefreshTempos();
                RefreshSpan();
            }
            finally
            {
                _loading = false;
            }
        }

        [RelayCommand]
        private void SetBpmFromSpan()
        {
            if (Session.Selection.Span is not { } span)
                return;
            if (!double.TryParse(BeatsText, NumberStyles.Float, CultureInfo.CurrentCulture, out double beats) || beats <= 0)
            {
                Session.Status = Strings.PropertiesBadBeats;
                return;
            }

            _ = _editor.ApplyTempoEditAsync(blocked => TempoMapEdits.BpmFromRange(Session.Chart, span.From, span.To, beats, Session.Delay, Session.KeepTimes, blocked));
        }

        partial void OnTickTextChanged(string value)
        {
            if (_loading || Session.Selection.Track is not { } key || !long.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out long tick))
                return;
            var notes = Session.Selection.Notes;
            if (notes.Count == 0 || notes.Any(n => n.Tick != notes[0].Tick) || tick == notes[0].Tick || tick < 0)
            {
                Refresh();
                return;
            }

            var move = new MoveNotes(key.Instrument, key.Difficulty, notes, tick - notes[0].Tick, 0);
            if (Session.Execute(move))
                Session.Selection.Select(key, move.Moved);
            Refresh();
        }

        partial void OnLengthTextChanged(string value)
        {
            if (_loading || Session.Selection.Track is not { } key || !long.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out long length))
                return;
            var notes = Session.Selection.Notes.Where(n => n.Length != Math.Max(EditableNote.PlainLength, length)).ToList();
            if (notes.Count > 0 && length >= 0)
                Session.Execute(new SetSustain(key.Instrument, key.Difficulty, notes, Math.Max(EditableNote.PlainLength, length)));
            Refresh();
        }

        partial void OnIsForcedHopoChanged(bool? value) => SetChordFlag(ChordFlags.ForceHopo, value);

        partial void OnIsForcedStrumChanged(bool? value) => SetChordFlag(ChordFlags.ForceStrum, value);

        partial void OnIsTapChanged(bool? value) => SetChordFlag(ChordFlags.Tap, value);

        partial void OnBpmTextChanged(string value)
        {
            if (_loading)
                return;
            var ticks = Session.Selection.TempoTicks.ToList();
            if (ticks.Count == 0 || !double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out double bpm))
            {
                Refresh();
                return;
            }

            long tempo;
            try
            {
                tempo = EditableTempoMap.MicrosecondsPerQuarterOf(Math.Round(bpm, 3));
            }
            catch (ArgumentOutOfRangeException)
            {
                Session.Status = Strings.PropertiesBadBpm;
                Refresh();
                return;
            }

            var map = Session.Chart.TempoMap;
            ticks = [.. ticks.Where(t => TempoMapEdits.TempoAt(map, t) != tempo)];
            if (ticks.Count == 1)
            {
                long tick = ticks[0];
                _ = _editor.ApplyTempoEditAsync(blocked => TempoMapEdits.SetBpm(Session.Chart, tick, tempo, Session.Delay, Session.KeepTimes, blocked));
            }
            else if (ticks.Count > 1)
            {
                Session.ExecuteAsOne(ticks.Select<long, Func<(IChartCommand?, string?)>>(tick => () =>
                {
                    var edit = TempoMapEdits.SetBpm(Session.Chart, tick, tempo, Session.Delay, Session.KeepTimes);
                    return (edit.Command, edit.Refusal);
                }));
            }

            Refresh();
        }

        partial void OnNumeratorTextChanged(string value) => SetSignature();

        partial void OnDenominatorTextChanged(string value) => SetSignature();

        partial void OnIsAnchoredChanged(bool? value)
        {
            if (_loading || value is not { } anchored)
                return;
            var ticks = Session.Selection.TempoTicks.Where(t => Session.Chart.TempoMap.Anchors.At(t).IsEmpty == anchored).ToList();
            Session.ExecuteAsOne(ticks.Select<long, Func<(IChartCommand?, string?)>>(tick => () =>
            {
                var edit = TempoMapEdits.SetAnchored(Session.Chart, tick, anchored, Session.Delay);
                return (edit.Command, edit.Refusal);
            }));
            Refresh();
        }

        private void SetChordFlag(ChordFlags flag, bool? value)
        {
            if (_loading || value is null || Session.Selection.Track is not { } key || Session.TrackOf(key) is not { } track)
                return;

            // The key toggles: on when not every chord has it, off when every one does.
            var ticks = Session.Selection.Notes.Select(n => n.Tick).Distinct().ToList();
            bool all = ticks.Count > 0 && ticks.All(t => track.FlagsAt(t).HasFlag(flag));
            if (all != value)
                Session.Tools.ToggleChordFlag(flag);
            Refresh();
        }

        private void SetSignature()
        {
            if (_loading)
                return;
            var ticks = Session.Selection.TempoTicks.ToList();
            if (ticks.Count == 0
                || !int.TryParse(NumeratorText, NumberStyles.Integer, CultureInfo.CurrentCulture, out int numerator)
                || !int.TryParse(DenominatorText, NumberStyles.Integer, CultureInfo.CurrentCulture, out int denominator))
            {
                return;
            }

            var map = Session.Chart.TempoMap;
            var commands = new List<IChartCommand>();
            foreach (long tick in ticks)
            {
                var at = map.TimeSignatures.At(tick);
                if (at.IsEmpty)
                    commands.Add(new AddTimeSignature(new TimeSignatureMarker(tick, numerator, denominator)));
                else if (at[^1].Numerator != numerator || at[^1].Denominator != denominator)
                    commands.Add(new SetTimeSignature(at[^1], numerator, denominator));
            }

            if (commands.Count > 0)
                Session.Execute(commands.Count == 1 ? commands[0] : new CompositeCommand(commands));
            Refresh();
        }

        private void RefreshNotes()
        {
            var selection = Session.Selection;
            var notes = selection.Notes;
            HasNotes = notes.Count > 0 && selection.Track is not null;
            if (!HasNotes)
                return;

            var key = selection.Track!.Value;
            var track = Session.TrackOf(key)!;
            var map = Session.TempoMap;
            NotesHeading = string.Format(CultureInfo.CurrentCulture, notes.Count == 1 ? Strings.PropertiesNoteHeading : Strings.PropertiesNotesHeadingFormat, notes.Count);
            TickText = Same(notes, n => n.Tick.ToString(CultureInfo.CurrentCulture));
            TimeText = Same(notes, n => SongTime.Format(TimeSpan.FromSeconds(Math.Max(0, Session.AudioAt(n.Tick))), decimals: 3));
            MeasureBeatText = Same(notes, n => MeasureBeat(map, n.Tick));
            LaneText = Same(notes, n => LaneName(track.Kind, n, Session.IsFiveLaneDrums));
            LengthText = Same(notes, n => n.Length.ToString(CultureInfo.CurrentCulture));
            CanForce = track.Kind != ChartInstrumentKind.Drums;
            var chords = notes.Select(n => track.FlagsAt(n.Tick)).ToList();
            IsForcedHopo = AllOrNone(chords, ChordFlags.ForceHopo);
            IsForcedStrum = AllOrNone(chords, ChordFlags.ForceStrum);
            IsTap = AllOrNone(chords, ChordFlags.Tap);
        }

        private void RefreshTempos()
        {
            var ticks = Session.Selection.TempoTicks.ToList();
            HasTempos = ticks.Count > 0;
            if (!HasTempos)
                return;

            var map = Session.Chart.TempoMap;
            var tidied = map.ToTempoMap();
            TemposHeading = string.Format(CultureInfo.CurrentCulture, ticks.Count == 1 ? Strings.PropertiesTempoHeading : Strings.PropertiesTemposHeadingFormat, ticks.Count);
            BpmText = Same(ticks, t => EditableTempoMap.BpmOf(TempoMapEdits.TempoAt(map, t)).ToString("0.###", CultureInfo.CurrentCulture));
            NumeratorText = Same(ticks, t => SignatureAt(tidied, t).Numerator.ToString(CultureInfo.CurrentCulture));
            DenominatorText = Same(ticks, t => SignatureAt(tidied, t).Denominator.ToString(CultureInfo.CurrentCulture));
            var anchored = ticks.Select(t => !map.Anchors.At(t).IsEmpty).Distinct().ToList();
            IsAnchored = anchored.Count == 1 ? anchored[0] : null;
        }

        private void RefreshSpan()
        {
            var selection = Session.Selection;
            HasSpan = selection.Range is { } range && range.To > range.From;
            if (selection.Range is { } span)
            {
                SpanText = string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.PropertiesSpanFormat,
                    SongTime.Format(TimeSpan.FromSeconds(Math.Max(0, Session.AudioAt(span.From))), decimals: 3),
                    SongTime.Format(TimeSpan.FromSeconds(Math.Max(0, Session.AudioAt(span.To))), decimals: 3));
            }
        }

        /// <summary>The value every item has, or <see cref="Multiple"/>.</summary>
        private static string Same<T>(IEnumerable<T> items, Func<T, string> value)
        {
            var values = items.Select(value).Distinct().Take(2).ToList();
            return values.Count == 1 ? values[0] : Multiple;
        }

        private static bool? AllOrNone(List<ChordFlags> chords, ChordFlags flag) =>
            chords.All(c => c.HasFlag(flag)) ? true : chords.Any(c => c.HasFlag(flag)) ? null : false;

        private static TimeSignature SignatureAt(TempoMap map, long tick) => map.TimeSignatures.Last(s => s.Tick <= tick);

        /// <summary>Where <paramref name="tick"/> is in the song's measures, e.g. "m41 · beat 3", counting both from 1.</summary>
        internal static string MeasureBeat(TempoMap map, long tick)
        {
            int measure = 0;
            long measureStart = 0;
            foreach (var line in map.BeatLines(0, tick))
            {
                if (line.IsMeasure)
                {
                    measure++;
                    measureStart = line.Tick;
                }
            }

            var signature = SignatureAt(map, tick);
            double beatTicks = map.Resolution * 4.0 / signature.Denominator;
            double beat = 1 + ((tick - measureStart) / beatTicks);
            return string.Format(CultureInfo.CurrentCulture, Strings.PropertiesMeasureBeatFormat, Math.Max(1, measure), beat.ToString("0.##", CultureInfo.CurrentCulture));
        }

        /// <summary>A note's lane, in words: Green, Open, Kick, White 2…</summary>
        internal static string LaneName(ChartInstrumentKind kind, EditableNote note, bool fiveLaneDrums)
        {
            if (kind == ChartInstrumentKind.Drums)
            {
                if (note.Lane == 0)
                    return note.DoubleKick ? Strings.LaneNameDoubleKick : Strings.LaneNameKick;
                return note.Lane switch
                {
                    1 => Strings.LaneNameRed,
                    2 => Strings.LaneNameYellow,
                    3 => Strings.LaneNameBlue,
                    4 when fiveLaneDrums => Strings.LaneNameOrange,
                    _ => Strings.LaneNameGreen,
                };
            }

            if (note.Lane == ChartNote.OpenLane)
                return Strings.LaneNameOpen;
            if (kind == ChartInstrumentKind.SixFret)
                return string.Format(CultureInfo.CurrentCulture, note.Lane < 3 ? Strings.LaneNameWhiteFormat : Strings.LaneNameBlackFormat, (note.Lane % 3) + 1);
            return note.Lane switch
            {
                0 => Strings.LaneNameGreen,
                1 => Strings.LaneNameRed,
                2 => Strings.LaneNameYellow,
                3 => Strings.LaneNameBlue,
                _ => Strings.LaneNameOrange,
            };
        }
    }
}
