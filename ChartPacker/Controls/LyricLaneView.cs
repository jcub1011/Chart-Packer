using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ChartPacker.Audio.Waveforms;
using ChartPacker.Core.Charts;

namespace ChartPacker.Controls
{
    /// <summary>What <see cref="LyricLaneView"/> draws: one vocal part's notes, words and phrases, with their song times worked out.</summary>
    public sealed class LyricScene
    {
        /// <param name="delay">How much later than its ticks say the chart plays against the audio.</param>
        public LyricScene(VocalPart part, TempoMap tempo, TimeSpan delay)
        {
            double Seconds(long tick) => tempo.SecondsAt(tick) + delay.TotalSeconds;
            var syllables = part.Lyrics
                .GroupBy(l => l.Tick)
                .ToDictionary(g => g.Key, g => LyricSyllable.Of(g.First().Text));

            var notes = part.Notes;
            NoteStarts = new double[notes.Count];
            NoteEnds = new double[notes.Count];
            Pitches = new int[notes.Count];
            IsUnpitched = new bool[notes.Count];
            IsSlide = new bool[notes.Count];
            for (int i = 0; i < notes.Count; i++)
            {
                NoteStarts[i] = Seconds(notes[i].Tick);
                NoteEnds[i] = Seconds(notes[i].Tick + notes[i].Length);
                Pitches[i] = notes[i].Pitch;
                var syllable = syllables.GetValueOrDefault(notes[i].Tick);
                IsUnpitched[i] = syllable.IsUnpitched;
                IsSlide[i] = syllable.IsSlide;
                MaxNoteLength = Math.Max(MaxNoteLength, NoteEnds[i] - NoteStarts[i]);
            }

            var pitched = notes.Where((_, i) => !IsUnpitched[i]).Select(n => n.Pitch).ToList();
            (LowestPitch, HighestPitch) = pitched.Count > 0 ? (pitched.Min(), pitched.Max()) : (60, 60);

            var words = part.Lyrics
                .Select(l => (l.Tick, Syllable: LyricSyllable.Of(l.Text)))
                .Where(w => !w.Syllable.IsSlide && w.Syllable.Text.Length > 0)
                .ToList();
            WordTimes = [.. words.Select(w => Seconds(w.Tick))];
            Words = [.. words.Select(w => w.Syllable.Text)];
            JoinsNext = [.. words.Select(w => w.Syllable.JoinsNext)];

            PhraseStarts = [.. part.Phrases.Select(p => Seconds(p.Tick))];
            PhraseEnds = [.. part.Phrases.Select(p => Seconds(p.Tick + p.Length))];
        }

        /// <summary>Each note's song time, in seconds.</summary>
        public double[] NoteStarts { get; }

        /// <summary>When each note ends, in seconds.</summary>
        public double[] NoteEnds { get; }

        public int[] Pitches { get; }

        /// <summary>Each note is spoken rather than sung, so it has no pitch to draw at.</summary>
        public bool[] IsUnpitched { get; }

        /// <summary>Each note carries the note before it on to its pitch, drawn joined to it.</summary>
        public bool[] IsSlide { get; }

        /// <summary>The longest note, in seconds, so notes still being sung can be found.</summary>
        public double MaxNoteLength { get; }

        /// <summary>The range of the pitched notes, which the lane's height is spread over.</summary>
        public int LowestPitch { get; }

        public int HighestPitch { get; }

        /// <summary>Each shown syllable's song time, in seconds.</summary>
        public double[] WordTimes { get; }

        public string[] Words { get; }

        /// <summary>Each syllable runs on into the next, without a space.</summary>
        public bool[] JoinsNext { get; }

        public double[] PhraseStarts { get; }

        public double[] PhraseEnds { get; }

        public bool IsEmpty => NoteStarts.Length == 0 && WordTimes.Length == 0;
    }

    /// <summary>
    /// Draws a vocal part as a karaoke strip: time runs right to left past a line near the left edge,
    /// the notes are bars at their pitch, and the words run along the bottom, lit as they're sung.
    /// Each frame the screen is drawn it reads <see cref="Clock"/>, so the lane follows the audio
    /// whether it's playing, paused or being dragged. <see cref="TimeShift"/> moves the whole part in
    /// time without the scene being built again. Behind the notes it can draw a track's <see cref="Waveform"/>.
    /// </summary>
    /// <example><c>&lt;ctl:LyricLaneView Scene="{Binding Lyrics.Scene}" Clock="{Binding Lyrics.Clock}" TimeShift="{Binding Lyrics.TimeShift}" Waveform="{Binding Lyrics.Waveform}" /&gt;</c></example>
    public sealed class LyricLaneView : Control
    {
        public static readonly StyledProperty<LyricScene?> SceneProperty =
            AvaloniaProperty.Register<LyricLaneView, LyricScene?>(nameof(Scene));

        public static readonly StyledProperty<Func<TimeSpan>?> ClockProperty =
            AvaloniaProperty.Register<LyricLaneView, Func<TimeSpan>?>(nameof(Clock));

        public static readonly StyledProperty<TimeSpan> TimeShiftProperty =
            AvaloniaProperty.Register<LyricLaneView, TimeSpan>(nameof(TimeShift));

        public static readonly StyledProperty<Waveform?> WaveformProperty =
            AvaloniaProperty.Register<LyricLaneView, Waveform?>(nameof(Waveform));

        public static readonly StyledProperty<double> WindowSecondsProperty =
            AvaloniaProperty.Register<LyricLaneView, double>(nameof(WindowSeconds), 4);

        // Where the now line sits, as a fraction of the width from the left.
        private const double NowFraction = 0.2;

        private const double WordFontSize = 14;
        private const double Padding = 8;

        private static readonly IBrush LaneBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xB0, 0x06, 0x06, 0x0C));
        private static readonly IBrush PhraseBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        private static readonly IPen PhrasePen = new ImmutablePen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF).ToUInt32(), 1);
        private static readonly IPen NowPen = new ImmutablePen(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF).ToUInt32(), 2);
        private static readonly IPen PitchLinePen = new ImmutablePen(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF).ToUInt32(), 1);

        // The theme's accent: the notes to come, and a brighter tint for what's being and been sung.
        private static readonly Color NoteColor = Color.FromRgb(0x8B, 0x5C, 0xF6);
        private static readonly Color SingingColor = Color.FromRgb(0xE9, 0xE3, 0xFF);
        private static readonly Color SungColor = Color.FromRgb(0xC4, 0xB5, 0xFD);

        private static readonly IBrush NoteBrush = new ImmutableSolidColorBrush(NoteColor);
        private static readonly IBrush SingingBrush = new ImmutableSolidColorBrush(SingingColor);
        private static readonly IBrush SungNoteBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x90, SungColor.R, SungColor.G, SungColor.B));
        private static readonly IPen SlidePen = new ImmutablePen(Color.FromArgb(0xA0, NoteColor.R, NoteColor.G, NoteColor.B).ToUInt32(), 2);

        private static readonly IBrush SungWordBrush = new ImmutableSolidColorBrush(SungColor);
        private static readonly IBrush PhraseWordBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF6));
        private static readonly IBrush OtherWordBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x7C, 0x7F, 0x96));

        // The track's waveform behind the notes, in a faint tint of what's been sung, so the notes stand out in front of it.
        private static readonly IBrush WaveformPeakBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x1C, SungColor.R, SungColor.G, SungColor.B));
        private static readonly IBrush WaveformLoudBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x3C, SungColor.R, SungColor.G, SungColor.B));

        private static readonly Typeface WordTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

        private readonly Dictionary<(int Index, int State), FormattedText> _words = [];
        private double _spaceWidth = -1;
        private bool _isFrameRequested;
        private TimeSpan _drawnAt = TimeSpan.MinValue;

        static LyricLaneView()
        {
            AffectsRender<LyricLaneView>(SceneProperty, TimeShiftProperty, WaveformProperty, WindowSecondsProperty);
        }

        /// <summary>The part to draw, or <see langword="null"/> for none.</summary>
        public LyricScene? Scene
        {
            get => GetValue(SceneProperty);
            set => SetValue(SceneProperty, value);
        }

        /// <summary>Where the audio is; zero when not set.</summary>
        public Func<TimeSpan>? Clock
        {
            get => GetValue(ClockProperty);
            set => SetValue(ClockProperty, value);
        }

        /// <summary>How much later than <see cref="Scene"/> says the part is drawn; earlier when negative.</summary>
        public TimeSpan TimeShift
        {
            get => GetValue(TimeShiftProperty);
            set => SetValue(TimeShiftProperty, value);
        }

        /// <summary>The audio drawn behind the notes, at the time it's heard, which <see cref="TimeShift"/> doesn't move; <see langword="null"/> for none.</summary>
        public Waveform? Waveform
        {
            get => GetValue(WaveformProperty);
            set => SetValue(WaveformProperty, value);
        }

        /// <summary>How much of the song shows ahead of the now line, in seconds.</summary>
        public double WindowSeconds
        {
            get => GetValue(WindowSecondsProperty);
            set => SetValue(WindowSecondsProperty, value);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == SceneProperty)
                _words.Clear();
            if (change.Property == SceneProperty || change.Property == IsVisibleProperty)
                RequestFrame();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            RequestFrame();
        }

        public override void Render(DrawingContext context)
        {
            var bounds = new Rect(Bounds.Size);
            if (Scene is not { IsEmpty: false } scene || bounds.Width <= 0 || bounds.Height <= 0)
                return;

            // Drawing the part later is drawing it at an earlier moment of the scene's own time.
            double now = ((Clock?.Invoke() ?? TimeSpan.Zero) - TimeShift).TotalSeconds;
            double nowX = bounds.Width * NowFraction;
            double perSecond = (bounds.Width - nowX) / Math.Max(0.5, WindowSeconds);
            double X(double time) => nowX + ((time - now) * perSecond);
            double from = now - (nowX / perSecond);
            double to = now + ((bounds.Width - nowX) / perSecond);

            double wordBand = scene.WordTimes.Length > 0 ? WordFontSize * 1.9 : 0;
            double noteTop = Padding;
            double noteBottom = bounds.Height - wordBand - Padding;

            using (context.PushClip(bounds))
            {
                context.FillRectangle(LaneBrush, bounds);

                // The track as it's heard, where the notes are, so they can be lined up with it: at the audio's own
                // time, which the shift doesn't move. Scaled so its loudest peak fills the lane, so quiet vocals still show.
                if (Waveform is { } waveform && noteBottom - noteTop > 6)
                {
                    double shift = TimeShift.TotalSeconds;
                    LaneWaveformPainter.Draw(context, waveform, new Rect(0, noteTop, bounds.Width, noteBottom - noteTop), isVertical: false,
                        from + shift, to + shift, time => X(time - shift), WaveformPeakBrush, WaveformLoudBrush, LaneWaveformPainter.FullScaleGain(waveform));
                }

                int phrase = DrawPhrases(context, scene, bounds, now, from, to, X);
                if (scene.NoteStarts.Length > 0 && noteBottom - noteTop > 6)
                    DrawNotes(context, scene, bounds, noteTop, noteBottom, now, from, to, X);
                if (wordBand > 0)
                    DrawWords(context, scene, bounds, bounds.Height - wordBand, wordBand, now, from, to, phrase, X);
                context.DrawLine(NowPen, new Point(nowX, 0), new Point(nowX, bounds.Height));
            }
        }

        /// <summary>Shades the phrase being sung and marks where each starts.</summary>
        /// <returns>The index of the phrase being sung, or -1.</returns>
        private static int DrawPhrases(DrawingContext context, LyricScene scene, Rect bounds, double now, double from, double to, Func<double, double> x)
        {
            int current = -1;
            for (int i = Math.Max(0, LowerBound(scene.PhraseStarts, from) - 1); i < scene.PhraseStarts.Length && scene.PhraseStarts[i] <= to; i++)
            {
                double start = scene.PhraseStarts[i], end = scene.PhraseEnds[i];
                if (end < from)
                    continue;

                if (start <= now && now < end)
                {
                    current = i;
                    context.FillRectangle(PhraseBrush, new Rect(new Point(x(start), 0), new Point(x(end), bounds.Height)));
                }

                context.DrawLine(PhrasePen, new Point(x(start), 0), new Point(x(start), bounds.Height));
            }

            return current;
        }

        private static void DrawNotes(DrawingContext context, LyricScene scene, Rect bounds, double top, double bottom, double now, double from, double to, Func<double, double> x)
        {
            int range = Math.Max(1, scene.HighestPitch - scene.LowestPitch);
            double thickness = Math.Clamp((bottom - top) / (range + 2), 3, 8);
            double Y(int i) => scene.IsUnpitched[i]
                ? (top + bottom) / 2
                : bottom - ((scene.Pitches[i] - scene.LowestPitch) / (double)range * (bottom - top - thickness)) - (thickness / 2);

            // A faint line an octave apart, so the pitches have something to be read against.
            for (int pitch = scene.LowestPitch - (scene.LowestPitch % 12) + 12; pitch <= scene.HighestPitch; pitch += 12)
            {
                double at = bottom - ((pitch - scene.LowestPitch) / (double)range * (bottom - top - thickness)) - (thickness / 2);
                context.DrawLine(PitchLinePen, new Point(bounds.Left, at), new Point(bounds.Right, at));
            }

            for (int i = LowerBound(scene.NoteStarts, from - scene.MaxNoteLength); i < scene.NoteStarts.Length && scene.NoteStarts[i] <= to; i++)
            {
                double start = scene.NoteStarts[i], end = scene.NoteEnds[i];
                if (end < from)
                    continue;

                double y = Y(i);
                double left = x(start), right = Math.Max(x(end), left + thickness);
                var brush = end < now ? SungNoteBrush : start <= now ? SingingBrush : NoteBrush;

                if (scene.IsSlide[i] && i > 0)
                    context.DrawLine(SlidePen, new Point(x(scene.NoteEnds[i - 1]), Y(i - 1)), new Point(left, y));

                if (scene.IsUnpitched[i])
                {
                    // A spoken note is a diamond at its start, with a thin tail for its length.
                    double half = thickness * 0.9;
                    context.FillRectangle(brush, new Rect(left, y - 1, right - left, 2));
                    var diamond = new StreamGeometry();
                    using (var g = diamond.Open())
                    {
                        g.BeginFigure(new Point(left, y - half), true);
                        g.LineTo(new Point(left + half, y));
                        g.LineTo(new Point(left, y + half));
                        g.LineTo(new Point(left - half, y));
                        g.EndFigure(true);
                    }

                    context.DrawGeometry(brush, null, diamond);
                }
                else
                {
                    context.DrawRectangle(brush, null, new RoundedRect(new Rect(left, y - (thickness / 2), right - left, thickness), thickness / 2));
                }
            }
        }

        /// <summary>
        /// Writes the syllables along the bottom, each at its note, or pushed on past the one before
        /// so none overlap; a syllable that runs on sits against the next.
        /// </summary>
        private void DrawWords(DrawingContext context, LyricScene scene, Rect bounds, double top, double height, double now, double from, double to, int phrase, Func<double, double> x)
        {
            if (_spaceWidth < 0)
                _spaceWidth = Text(" ", OtherWordBrush).WidthIncludingTrailingWhitespace;

            // Lay out from the start of a phrase, so a word's place doesn't change as the words before it scroll away.
            double layoutFrom = from - 2;
            int phraseAt = LowerBound(scene.PhraseStarts, from) - 1;
            if (phraseAt >= 0 && scene.PhraseEnds[phraseAt] > from)
                layoutFrom = Math.Min(layoutFrom, scene.PhraseStarts[phraseAt]);

            double phraseStart = phrase >= 0 ? scene.PhraseStarts[phrase] : double.NaN;
            double phraseEnd = phrase >= 0 ? scene.PhraseEnds[phrase] : double.NaN;
            double right = double.NegativeInfinity;
            for (int i = LowerBound(scene.WordTimes, layoutFrom); i < scene.WordTimes.Length; i++)
            {
                double time = scene.WordTimes[i];
                double left = Math.Max(x(time), right);
                if (left > bounds.Right)
                    break;

                int state = time <= now ? 0 : time >= phraseStart && time < phraseEnd ? 1 : 2;
                var text = Word(scene, i, state);
                if (left + text.Width >= bounds.Left)
                    context.DrawText(text, new Point(left, top + ((height - text.Height) / 2)));

                right = left + text.Width + (scene.JoinsNext[i] ? 0 : _spaceWidth);
            }
        }

        private FormattedText Word(LyricScene scene, int index, int state)
        {
            if (!_words.TryGetValue((index, state), out var text))
                _words[(index, state)] = text = Text(scene.Words[index], state switch { 0 => SungWordBrush, 1 => PhraseWordBrush, _ => OtherWordBrush });
            return text;
        }

        private static FormattedText Text(string text, IBrush brush) =>
            new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, WordTypeface, WordFontSize, brush);

        /// <summary>The index of the first of <paramref name="times"/>, which are in order, at or after <paramref name="time"/>.</summary>
        private static int LowerBound(double[] times, double time)
        {
            int lo = 0, hi = times.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (times[mid] < time)
                    lo = mid + 1;
                else
                    hi = mid;
            }

            return lo;
        }

        /// <summary>Asks to be called back when the screen is next drawn, while there's a part to draw.</summary>
        private void RequestFrame()
        {
            if (_isFrameRequested || Scene is not { IsEmpty: false } || !IsVisible || TopLevel.GetTopLevel(this) is not { } topLevel)
                return;

            _isFrameRequested = true;
            topLevel.RequestAnimationFrame(OnFrame);
        }

        private void OnFrame(TimeSpan _)
        {
            _isFrameRequested = false;
            if (TopLevel.GetTopLevel(this) is null)
                return;

            // Paused, nothing moves, so nothing is redrawn.
            var clock = Clock?.Invoke() ?? TimeSpan.Zero;
            if (clock != _drawnAt && IsEffectivelyVisible)
            {
                _drawnAt = clock;
                InvalidateVisual();
            }

            RequestFrame();
        }
    }
}
