using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ChartBuilder.Resources;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Commands;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using ChartShared.UI.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    /// <summary>A difficulty a roll or trill can reach down to, as Properties offers it.</summary>
    public sealed record DifficultyChoice(ChartDifficulty Difficulty, string Label);

    /// <summary>
    /// The Properties panel (charter-spec C3 item 4): the selected notes, phrases, disco flip sections, tempo markers and
    /// span, or a vocal part's notes and phrases (C3 item 7). A field shows <see cref="Multiple"/> when the items differ,
    /// and editing it sets every item, as one undoable step.
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
        public partial bool HasPhrases { get; private set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsEmpty))]
        public partial bool HasDiscoFlips { get; private set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsEmpty))]
        public partial bool HasTempos { get; private set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsEmpty))]
        public partial bool HasSpan { get; private set; }

        public bool IsEmpty => !HasNotes && !HasPhrases && !HasDiscoFlips && !HasTempos && !HasSpan && !HasVocalNotes && !HasVocalPhrases;

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

        /// <summary>Whether the notes are drums, which have cymbals, accents, ghosts and 2× kicks rather than forcing.</summary>
        [ObservableProperty]
        public partial bool IsDrums { get; private set; }

        /// <summary>Whether a selected note can be a cymbal or a tom: a yellow, blue or green note of Pro drums.</summary>
        [ObservableProperty]
        public partial bool CanCymbal { get; private set; }

        /// <summary>Whether the selected yellow, blue and green notes are cymbals; tom markers make them toms (C6 item 3).</summary>
        [ObservableProperty]
        public partial bool? IsCymbal { get; set; }

        /// <summary>Whether a selected note can be an accent or a ghost: one on a pad, not the kick.</summary>
        [ObservableProperty]
        public partial bool CanDynamics { get; private set; }

        [ObservableProperty]
        public partial bool? IsAccent { get; set; }

        [ObservableProperty]
        public partial bool? IsGhost { get; set; }

        /// <summary>Whether a selected note can be a 2× kick: a kick on Expert.</summary>
        [ObservableProperty]
        public partial bool CanDoubleKick { get; private set; }

        [ObservableProperty]
        public partial bool? IsDoubleKick { get; set; }

        [ObservableProperty]
        public partial string PhrasesHeading { get; private set; } = "";

        [ObservableProperty]
        public partial string PhraseKindText { get; private set; } = "";

        /// <summary>Where the phrases start, in ticks.</summary>
        [ObservableProperty]
        public partial string PhraseStartText { get; set; } = "";

        /// <summary>Where the phrases end, in ticks: the tick after the last one they cover.</summary>
        [ObservableProperty]
        public partial string PhraseEndText { get; set; } = "";

        /// <summary>Where the phrases start, in the audio.</summary>
        [ObservableProperty]
        public partial string PhraseTimeText { get; private set; } = "";

        /// <summary>Whether a roll or trill is selected, which reaches down from Expert (charter-spec C6 item 6).</summary>
        [ObservableProperty]
        public partial bool HasLowestDifficulty { get; private set; }

        /// <summary>The lowest difficulty the selected rolls and trills reach, or null when they differ.</summary>
        [ObservableProperty]
        public partial DifficultyChoice? SelectedLowestDifficulty { get; set; }

        /// <summary>The difficulties a roll or trill can reach down to, from Expert only to every one.</summary>
        public IReadOnlyList<DifficultyChoice> LowestDifficultyChoices { get; } =
            [.. new[] { ChartDifficulty.Expert, ChartDifficulty.Hard, ChartDifficulty.Medium, ChartDifficulty.Easy }.Select(d => new DifficultyChoice(d, ChartLabels.DifficultyName(d)))];

        [ObservableProperty]
        public partial string DiscoFlipsHeading { get; private set; } = "";

        /// <summary>Where the disco flip sections start, in ticks.</summary>
        [ObservableProperty]
        public partial string DiscoFlipStartText { get; set; } = "";

        /// <summary>Where the disco flip sections end, in ticks: the tick disco flip goes off. Empty for one that never ends.</summary>
        [ObservableProperty]
        public partial string DiscoFlipEndText { get; set; } = "";

        /// <summary>The difficulty each disco flip section is of, which it doesn't change (charter-spec C6 item 7).</summary>
        [ObservableProperty]
        public partial string DiscoFlipDifficultyText { get; private set; } = "";

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
                RefreshVocalNotes();
                RefreshPhrases();
                RefreshVocalPhrases();
                RefreshDiscoFlips();
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

        partial void OnPhraseStartTextChanged(string value)
        {
            if (_loading)
                return;
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out long tick) && tick >= 0)
                Session.Tools.SetPhraseStart(tick);
            Refresh();
        }

        partial void OnPhraseEndTextChanged(string value)
        {
            if (_loading)
                return;
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out long tick) && tick >= 0)
                Session.Tools.SetPhraseEnd(tick);
            Refresh();
        }

        partial void OnDiscoFlipStartTextChanged(string value)
        {
            if (_loading)
                return;
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out long tick) && tick >= 0)
                Session.Tools.SetDiscoFlipStart(tick);
            Refresh();
        }

        partial void OnDiscoFlipEndTextChanged(string value)
        {
            if (_loading)
                return;
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out long tick) && tick >= 0)
                Session.Tools.SetDiscoFlipEnd(tick);
            Refresh();
        }

        partial void OnSelectedLowestDifficultyChanged(DifficultyChoice? value)
        {
            if (_loading || value is null)
                return;
            Session.Tools.SetLowestDifficulty(value.Difficulty);
            Refresh();
        }

        partial void OnIsForcedHopoChanged(bool? value) => SetChordFlag(ChordFlags.ForceHopo, value);

        partial void OnIsForcedStrumChanged(bool? value) => SetChordFlag(ChordFlags.ForceStrum, value);

        partial void OnIsTapChanged(bool? value) => SetChordFlag(ChordFlags.Tap, value);

        partial void OnIsCymbalChanged(bool? value)
        {
            if (_loading || value is not { } cymbal)
                return;
            Session.Tools.SetTom(!cymbal);
            Refresh();
        }

        partial void OnIsAccentChanged(bool? value) => SetDynamics(NoteDynamics.Accent, value);

        partial void OnIsGhostChanged(bool? value) => SetDynamics(NoteDynamics.Ghost, value);

        partial void OnIsDoubleKickChanged(bool? value)
        {
            if (_loading || value is not { } on)
                return;
            Session.Tools.SetDoubleKick(on);
            Refresh();
        }

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

        /// <summary>Makes the selected pads <paramref name="dynamics"/>, or normal hits when it's unticked.</summary>
        private void SetDynamics(NoteDynamics dynamics, bool? value)
        {
            if (_loading || value is not { } on)
                return;
            Session.Tools.SetDynamics(on ? dynamics : NoteDynamics.None);
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

            // Only the markers whose signature changes, so a run of them that are all set already is no step at all.
            var map = Session.Chart.TempoMap;
            ticks = [.. ticks.Where(t => map.TimeSignatures.At(t) is var at && (at.IsEmpty || at[^1].Numerator != numerator || at[^1].Denominator != denominator))];
            if (ticks.Count > 0)
            {
                Session.ExecuteAsOne(ticks.Select<long, Func<(IChartCommand?, string?)>>(tick => () =>
                {
                    var edit = TempoMapEdits.PlaceSignature(Session.Chart, tick, numerator, denominator);
                    return (edit.Command, edit.Refusal);
                }));
            }

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
            IsDrums = track.Kind == ChartInstrumentKind.Drums;
            CanForce = !IsDrums;
            var chords = notes.Select(n => track.FlagsAt(n.Tick)).ToList();
            IsForcedHopo = AllOrNone(chords, c => c.HasFlag(ChordFlags.ForceHopo));
            IsForcedStrum = AllOrNone(chords, c => c.HasFlag(ChordFlags.ForceStrum));
            IsTap = AllOrNone(chords, c => c.HasFlag(ChordFlags.Tap));
            if (IsDrums)
                RefreshDrums(key, notes);
        }

        /// <summary>The drum fields: cymbal or tom, accent, ghost and 2× kick (charter-spec C6).</summary>
        private void RefreshDrums(ChartTrackKey key, IReadOnlyList<EditableNote> notes)
        {
            var part = Session.Chart.PartOf(key.Instrument)!;
            var pads = notes.Where(static n => n.Lane > 0).ToList();
            // Not a yellow inside a disco flip section, which the games play as red (charter-spec C6 item 7).
            var cymbals = Session.Chart.DrumsVariant == DrumsVariant.Pro
                ? pads.Where(n => Session.Tools.CanToggleCymbal(key, n)).ToList()
                : [];
            var kicks = notes.Where(static n => n.Lane == 0).ToList();

            CanCymbal = cymbals.Count > 0;
            IsCymbal = AllOrNone(cymbals, n => !DrumCymbals.IsTom(part, n.Lane, n.Tick));
            CanDynamics = pads.Count > 0;
            IsAccent = AllOrNone(pads, static n => n.Dynamics == NoteDynamics.Accent);
            IsGhost = AllOrNone(pads, static n => n.Dynamics == NoteDynamics.Ghost);
            CanDoubleKick = kicks.Count > 0 && key.Difficulty == ChartDifficulty.Expert;
            IsDoubleKick = AllOrNone(kicks, static n => n.DoubleKick);
        }

        /// <summary>The phrase fields (charter-spec C3 item 4): kind, start and end, and a roll's or trill's lowest difficulty.</summary>
        private void RefreshPhrases()
        {
            var phrases = Session.Selection.Phrases;
            HasPhrases = phrases.Count > 0;
            if (!HasPhrases)
                return;

            PhrasesHeading = string.Format(CultureInfo.CurrentCulture, phrases.Count == 1 ? Strings.PropertiesPhraseHeading : Strings.PropertiesPhrasesHeadingFormat, phrases.Count);
            PhraseKindText = Same(phrases, p => ChartEditorViewModel.PhraseKindName(p.Kind));
            PhraseStartText = Same(phrases, p => p.Tick.ToString(CultureInfo.CurrentCulture));
            PhraseEndText = Same(phrases, p => (p.Tick + p.Length).ToString(CultureInfo.CurrentCulture));
            PhraseTimeText = Same(phrases, p => SongTime.Format(TimeSpan.FromSeconds(Math.Max(0, Session.AudioAt(p.Tick))), decimals: 3));
            var lowest = phrases.Where(p => Phrase.HasLowestDifficulty(p.Kind)).Select(p => p.LowestDifficulty).Distinct().ToList();
            HasLowestDifficulty = lowest.Count > 0;
            SelectedLowestDifficulty = lowest.Count == 1 ? LowestDifficultyChoices.FirstOrDefault(c => c.Difficulty == lowest[0]) : null;
        }

        /// <summary>The disco flip fields (charter-spec C3 item 4, C6 item 7): start and end, and the difficulty.</summary>
        private void RefreshDiscoFlips()
        {
            var sections = Session.Selection.DiscoFlips;
            HasDiscoFlips = sections.Count > 0;
            if (!HasDiscoFlips)
                return;

            DiscoFlipsHeading = sections.Count == 1
                ? Strings.PropertiesDiscoFlipHeading
                : string.Format(CultureInfo.CurrentCulture, Strings.PropertiesDiscoFlipsHeadingFormat, sections.Count);
            DiscoFlipStartText = Same(sections, s => s.Range.Tick.ToString(CultureInfo.CurrentCulture));
            DiscoFlipEndText = Same(sections, s => s.Range.EndTick?.ToString(CultureInfo.CurrentCulture) ?? "");
            DiscoFlipDifficultyText = Same(sections, s => ChartLabels.DifficultyName(s.Difficulty));
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

        /// <summary>True when every item is so, false when none is (or there are none), and null when some are.</summary>
        private static bool? AllOrNone<T>(List<T> items, Func<T, bool> test) =>
            items.Count > 0 && items.All(test) ? true : items.Any(test) ? null : false;

        private static TimeSignature SignatureAt(TempoMap map, long tick) => map.TimeSignatures.Last(s => s.Tick <= tick);

        /// <summary>
        /// Where <paramref name="tick"/> is in the song's measures, e.g. "m41 · beat 3", counting both from 1. It costs no
        /// more far into the song than near its start (<see cref="ChartShared.Core.Charting.Editing.MeasureBeat"/>).
        /// </summary>
        internal static string MeasureBeat(TempoMap map, long tick)
        {
            var at = ChartShared.Core.Charting.Editing.MeasureBeat.At(map, tick);
            return string.Format(CultureInfo.CurrentCulture, Strings.PropertiesMeasureBeatFormat, at.Measure, at.Beat.ToString("0.##", CultureInfo.CurrentCulture));
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
