using System;
using System.Globalization;
using System.Linq;
using ChartBuilder.Resources;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charting.Layout;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChartBuilder.ViewModels
{
    // The selected notes and phrases of a vocal part (charter-spec C3 item 7): where each note is, its pitch, length and
    // lyric, whether it's a slide or spoken, and Type lyrics, which gives a typed line's syllables to the notes in turn.
    public sealed partial class PropertiesViewModel
    {
        // The names of the pitch classes, from C.
        private static readonly string[] PitchClasses = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsEmpty))]
        public partial bool HasVocalNotes { get; private set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsEmpty))]
        public partial bool HasVocalPhrases { get; private set; }

        [ObservableProperty]
        public partial string VocalNotesHeading { get; private set; } = "";

        [ObservableProperty]
        public partial string VocalTickText { get; set; } = "";

        [ObservableProperty]
        public partial string VocalTimeText { get; private set; } = "";

        [ObservableProperty]
        public partial string VocalMeasureBeatText { get; private set; } = "";

        /// <summary>The notes' MIDI pitch, which a note name such as F#3 also sets.</summary>
        [ObservableProperty]
        public partial string PitchText { get; set; } = "";

        /// <summary>The notes' pitch by name, e.g. C4, or a spoken note's word for it.</summary>
        [ObservableProperty]
        public partial string PitchNameText { get; private set; } = "";

        [ObservableProperty]
        public partial string VocalLengthText { get; set; } = "";

        /// <summary>The notes' lyric as the track has it, symbols and all; blank for none.</summary>
        [ObservableProperty]
        public partial string LyricText { get; set; } = "";

        [ObservableProperty]
        public partial bool? IsSlide { get; set; }

        [ObservableProperty]
        public partial bool? IsSpoken { get; set; }

        /// <summary>A line of lyrics, typed to give the selected notes one syllable each (Type lyrics).</summary>
        [ObservableProperty]
        public partial string TypedLyricsText { get; set; } = "";

        [ObservableProperty]
        public partial string VocalPhrasesHeading { get; private set; } = "";

        [ObservableProperty]
        public partial string VocalPhraseStartText { get; set; } = "";

        [ObservableProperty]
        public partial string VocalPhraseEndText { get; set; } = "";

        [ObservableProperty]
        public partial string VocalPhraseTimeText { get; private set; } = "";

        /// <summary>
        /// Gives the syllables of the typed line to the selected notes, from the earliest, a slide skipped, as one step,
        /// then clears the line. Syllables left over are said in the status.
        /// </summary>
        [RelayCommand]
        private void ApplyTypedLyrics()
        {
            if (Session.Tools.SpreadVocalLyrics(TypedLyricsText) is not { } left)
                return;
            TypedLyricsText = "";
            if (left > 0)
                Session.Status = string.Format(CultureInfo.CurrentCulture, Strings.PropertiesTypedLyricsLeftFormat, left);
            Refresh();
        }

        /// <summary>The name of <paramref name="pitch"/>, with its octave as MIDI numbers them from middle C, C4 (60).</summary>
        internal static string PitchName(int pitch) => string.Create(CultureInfo.InvariantCulture, $"{PitchClasses[pitch % 12]}{(pitch / 12) - 1}");

        /// <summary>The pitch <paramref name="text"/> says: a MIDI number, or a note name such as C4, F#3 or Bb2; null for neither.</summary>
        internal static int? ParsePitch(string text)
        {
            text = text.Trim();
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int number))
                return number;
            if (text.Length < 2)
                return null;
            int letter = "C D EF G A B".IndexOf(char.ToUpperInvariant(text[0]), StringComparison.Ordinal);
            if (letter < 0)
                return null;
            int at = 1;
            if (text[at] == '#')
            {
                letter++;
                at++;
            }
            else if (text[at] is 'b' or 'B' && text.Length > at + 1 && text[at + 1] is '-' or (>= '0' and <= '9'))
            {
                letter--;
                at++;
            }

            return int.TryParse(text.AsSpan(at), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int octave)
                ? ((octave + 1) * 12) + letter
                : null;
        }

        /// <summary>The vocal note fields (charter-spec C3 item 7).</summary>
        private void RefreshVocalNotes()
        {
            var selection = Session.Selection;
            var notes = selection.VocalNotes;
            HasVocalNotes = notes.Count > 0 && selection.Vocal is not null;
            if (!HasVocalNotes || Session.VocalContentOf(selection.Vocal!.Value) is not { } content)
            {
                HasVocalNotes = false;
                return;
            }

            var map = Session.TempoMap;
            string part = LaneMenuItem.TitleOf(LanePart.Vocals(selection.Vocal!.Value));
            VocalNotesHeading = notes.Count == 1
                ? string.Format(CultureInfo.CurrentCulture, Strings.PropertiesVocalNoteHeadingFormat, part)
                : string.Format(CultureInfo.CurrentCulture, Strings.PropertiesVocalNotesHeadingFormat, notes.Count, part);
            VocalTickText = Same(notes, n => n.Tick.ToString(CultureInfo.CurrentCulture));
            VocalTimeText = Same(notes, n => SongTime.Format(TimeSpan.FromSeconds(Math.Max(0, Session.AudioAt(n.Tick))), decimals: 3));
            VocalMeasureBeatText = Same(notes, n => MeasureBeat(map, n.Tick));
            PitchText = Same(notes, n => n.Pitch.ToString(CultureInfo.CurrentCulture));
            PitchNameText = Same(notes, n => VocalEdits.IsSpoken(content, n.Tick) ? Strings.PropertiesSpokenPitch : PitchName(n.Pitch));
            VocalLengthText = Same(notes, n => n.Length.ToString(CultureInfo.CurrentCulture));
            LyricText = Same(notes, n => VocalEdits.LyricAt(content, n.Tick) ?? "");
            var list = notes.ToList();
            IsSlide = AllOrNone(list, n => VocalEdits.IsSlide(content, n.Tick));
            IsSpoken = AllOrNone(list, n => VocalEdits.IsSpoken(content, n.Tick));
        }

        /// <summary>The vocal phrase fields: start and end.</summary>
        private void RefreshVocalPhrases()
        {
            var selection = Session.Selection;
            var phrases = selection.VocalPhrases;
            HasVocalPhrases = phrases.Count > 0 && selection.Vocal is not null;
            if (!HasVocalPhrases)
                return;

            string part = LaneMenuItem.TitleOf(LanePart.Vocals(selection.Vocal!.Value));
            VocalPhrasesHeading = phrases.Count == 1
                ? string.Format(CultureInfo.CurrentCulture, Strings.PropertiesVocalPhraseHeadingFormat, part)
                : string.Format(CultureInfo.CurrentCulture, Strings.PropertiesVocalPhrasesHeadingFormat, phrases.Count, part);
            VocalPhraseStartText = Same(phrases, p => p.Tick.ToString(CultureInfo.CurrentCulture));
            VocalPhraseEndText = Same(phrases, p => p.EndTick.ToString(CultureInfo.CurrentCulture));
            VocalPhraseTimeText = Same(phrases, p => SongTime.Format(TimeSpan.FromSeconds(Math.Max(0, Session.AudioAt(p.Tick))), decimals: 3));
        }

        partial void OnVocalTickTextChanged(string value)
        {
            if (_loading)
                return;
            var notes = Session.Selection.VocalNotes;
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out long tick) && tick >= 0
                && notes.Count > 0 && notes.All(n => n.Tick == notes[0].Tick) && tick != notes[0].Tick)
                Session.Tools.SetVocalTick(tick);
            Refresh();
        }

        partial void OnPitchTextChanged(string value)
        {
            if (_loading)
                return;
            if (ParsePitch(value) is { } pitch)
                Session.Tools.SetVocalPitch(pitch);
            else if (value != Multiple)
                Session.Status = Strings.PropertiesBadPitch;
            Refresh();
        }

        partial void OnVocalLengthTextChanged(string value)
        {
            if (_loading)
                return;
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out long length) && length >= 0)
                Session.Tools.SetVocalLength(length);
            Refresh();
        }

        partial void OnLyricTextChanged(string value)
        {
            if (_loading || value == Multiple)
                return;
            Session.Tools.SetVocalLyric(value);
            Refresh();
        }

        partial void OnIsSlideChanged(bool? value)
        {
            if (_loading || value is not { } on)
                return;
            Session.Tools.SetVocalSlide(on);
            Refresh();
        }

        partial void OnIsSpokenChanged(bool? value)
        {
            if (_loading || value is not { } on)
                return;
            Session.Tools.SetVocalSpoken(on);
            Refresh();
        }

        partial void OnVocalPhraseStartTextChanged(string value)
        {
            if (_loading)
                return;
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out long tick) && tick >= 0)
                Session.Tools.SetVocalPhraseStart(tick);
            Refresh();
        }

        partial void OnVocalPhraseEndTextChanged(string value)
        {
            if (_loading)
                return;
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out long tick) && tick >= 0)
                Session.Tools.SetVocalPhraseEnd(tick);
            Refresh();
        }
    }
}
