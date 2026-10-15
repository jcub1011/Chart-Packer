using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ChartPacker.Audio.Waveforms;
using ChartPacker.Core.Charts;
using ChartPacker.UI.Controls;

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
            EndSeconds = Math.Max(0, NoteEnds.Concat(PhraseEnds).Concat(WordTimes).DefaultIfEmpty().Max());
        }

        /// <summary>When the last note, phrase or word ends, in seconds.</summary>
        public double EndSeconds { get; }

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
    /// <para>Pressing on it and dragging scrubs: from <see cref="ScrubStarted"/> to <see cref="ScrubEnded"/>,
    /// <see cref="Scrubbed"/> says where to, and the owner seeks when it ends, as it does for <see cref="WaveformView"/>. A drag
    /// grabs the words: left moves the audio later, as far as the words scroll for it, as if pulling them along. With Ctrl
    /// held it's fine, <see cref="FineScrubFactor"/> times less far. Either way the pointer is held still, so a drag can
    /// go on as far as it needs, and a press leaves the audio where it is.</para>
    /// <para>While Shift is held (and not Ctrl) over it, it fades to the whole part instead, squeezed into its width from the
    /// start at the left to <see cref="LengthSeconds"/> at the right, with the phrase under the pointer written out, and a
    /// press or a drag follows the pointer across it.</para>
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

        public static readonly StyledProperty<double> LengthSecondsProperty =
            AvaloniaProperty.Register<LyricLaneView, double>(nameof(LengthSeconds));

        public static readonly StyledProperty<double> FineScrubFactorProperty =
            AvaloniaProperty.Register<LyricLaneView, double>(nameof(FineScrubFactor), LaneScrub.DefaultFineFactor);

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

        // The phrase under the pointer when the whole part is shown, lit above the others.
        private static readonly IBrush HoveredPhraseBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x2C, 0xFF, 0xFF, 0xFF));

        private readonly Dictionary<(int Index, int State), FormattedText> _words = [];
        private double _spaceWidth = -1;
        private bool _isFrameRequested;
        private Rect? _viewport;
        private TimeSpan _drawnAt = TimeSpan.MinValue;

        // How far across the lane the pointer is, where the whole part is scrubbed to while Shift is held; NaN for not over it.
        private double _hoverX = double.NaN;
        private bool _isScrubbing;

        // Whether Ctrl is held over the lane, or while scrubbing, making a drag fine, or Shift, showing the whole part.
        private readonly LaneScrubMode _mode;

        // How far the lane has faded to the whole part.
        private readonly OverviewFade _fade = new();

        // The whole part's notes as last laid out, and the words of the phrase last written out under the pointer.
        private OverviewMarks? _overview;
        private (int Index, double Width, FormattedText Text)? _phraseText;

        // A spoken note's diamond, a unit across from its middle, scaled and moved to each drawn.
        private static StreamGeometry? s_diamond;

        static LyricLaneView()
        {
            AffectsRender<LyricLaneView>(SceneProperty, TimeShiftProperty, WaveformProperty, WindowSecondsProperty, LengthSecondsProperty);
        }

        /// <summary>Raised when the lane is pressed, before the first <see cref="Scrubbed"/>.</summary>
        public event EventHandler? ScrubStarted;

        /// <summary>Raised as the pointer moves while the lane is pressed, with the time it's at.</summary>
        public event EventHandler<ScrubEventArgs>? Scrubbed;

        /// <summary>Raised when the lane is let go, or the press is lost.</summary>
        public event EventHandler? ScrubEnded;

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

        /// <summary>
        /// How long the song is, in seconds: what the lane shows of it while the pointer is over it, and scrubs through. At
        /// zero, the part's own end is taken instead.
        /// </summary>
        public double LengthSeconds
        {
            get => GetValue(LengthSecondsProperty);
            set => SetValue(LengthSecondsProperty, value);
        }

        /// <summary>How many times less far a drag with Ctrl held moves the lyrics than a plain drag; see <see cref="LaneScrub.DefaultFineFactor"/>.</summary>
        public double FineScrubFactor
        {
            get => GetValue(FineScrubFactorProperty);
            set => SetValue(FineScrubFactorProperty, value);
        }

        public LyricLaneView()
        {
            _mode = new LaneScrubMode(this, () => !double.IsNaN(_hoverX) || _isScrubbing);
            EffectiveViewportChanged += OnEffectiveViewportChanged;
        }

        // Whether any of the control is in view: scrolled out of sight, it isn't drawn as the clock moves. Not yet laid
        // out (hidden, say), it has no size to check, so it's taken to be, lest it's left undrawn once shown in place.
        private bool IsInViewport =>
            _viewport is not { } viewport || Bounds.Width <= 0 || Bounds.Height <= 0 || viewport.Intersects(new Rect(Bounds.Size));

        private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
        {
            bool wasInViewport = IsInViewport;
            _viewport = e.EffectiveViewport;
            if (IsInViewport && !wasInViewport)
                InvalidateVisual();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == SceneProperty)
            {
                _words.Clear();
                _overview = null;
                _phraseText = null;
            }
            if (change.Property == SceneProperty || change.Property == IsVisibleProperty)
                RequestFrame();

            // Shown again, it scrolls with the audio until the pointer is over it again.
            if (change.Property == IsVisibleProperty && !IsVisible && !_isScrubbing)
                SetHover(double.NaN);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            RequestFrame();

            // Shift pressed or let go with the pointer over the lane, or while scrubbing, shows the whole part at once or it scrolling.
            _mode.Attach();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _mode.Detach();
        }

        public override void Render(DrawingContext context)
        {
            var bounds = new Rect(Bounds.Size);
            if (Scene is not { IsEmpty: false } scene || bounds.Width <= 0 || bounds.Height <= 0)
                return;

            // The time read for this frame, rather than reading the clock again.
            var clock = _drawnAt != TimeSpan.MinValue ? _drawnAt : Clock?.Invoke() ?? TimeSpan.Zero;
            double wordBand = scene.WordTimes.Length > 0 ? WordFontSize * 1.9 : 0;

            // Fading between them, each is drawn see-through, so one shows through the other.
            double overview = _fade.Opacity;
            if (overview < 1)
            {
                using (context.PushOpacity(1 - overview))
                    DrawScrolling(context, scene, bounds, clock, wordBand);
            }

            if (overview > 0)
            {
                using (context.PushOpacity(overview))
                    DrawOverview(context, scene, bounds, clock.TotalSeconds, wordBand);
            }
        }

        /// <summary>Whether the lane is to show the whole part: Shift is held over it, or while it's scrubbed.</summary>
        private bool IsOverviewShown => !double.IsNaN(_hoverX) && _mode.IsOverview;

        /// <summary>The lane as it scrolls with the audio, at <paramref name="clock"/>: its phrases, notes and words, and the now line.</summary>
        private void DrawScrolling(DrawingContext context, LyricScene scene, Rect bounds, TimeSpan clock, double wordBand)
        {
            // Drawing the part later is drawing it at an earlier moment of the scene's own time.
            double now = (clock - TimeShift).TotalSeconds;
            double nowX = bounds.Width * NowFraction;
            double perSecond = (bounds.Width - nowX) / Math.Max(0.5, WindowSeconds);
            double X(double time) => nowX + ((time - now) * perSecond);
            double from = now - (nowX / perSecond);
            double to = now + ((bounds.Width - nowX) / perSecond);

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
                        from + shift, to + shift, time => X(time - shift), WaveformPeakBrush, WaveformLoudBrush, LaneWaveformPainter.FullScaleGain(waveform), 1 / (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1));
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
            double thickness = Math.Clamp((bottom - top) / (Math.Max(1, scene.HighestPitch - scene.LowestPitch) + 2), 3, 8);
            double Y(int i) => NoteY(scene, i, top, bottom, thickness);

            // A faint line an octave apart, so the pitches have something to be read against.
            for (int pitch = scene.LowestPitch - (scene.LowestPitch % 12) + 12; pitch <= scene.HighestPitch; pitch += 12)
            {
                double at = PitchY(scene, pitch, top, bottom, thickness);
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
                    using (context.PushTransform(new Matrix(half, 0, 0, half, left, y)))
                        context.DrawGeometry(brush, null, Diamond());
                }
                else
                {
                    context.DrawRectangle(brush, null, new RoundedRect(new Rect(left, y - (thickness / 2), right - left, thickness), thickness / 2));
                }
            }
        }

        /// <summary>Where a note at <paramref name="pitch"/> is drawn between <paramref name="top"/> and <paramref name="bottom"/>, through its middle.</summary>
        private static double PitchY(LyricScene scene, int pitch, double top, double bottom, double thickness) =>
            bottom - ((pitch - scene.LowestPitch) / (double)Math.Max(1, scene.HighestPitch - scene.LowestPitch) * (bottom - top - thickness)) - (thickness / 2);

        /// <summary>Where the scene's <paramref name="index"/>th note is drawn, through its middle: at its pitch, or halfway up when it's spoken.</summary>
        private static double NoteY(LyricScene scene, int index, double top, double bottom, double thickness) =>
            scene.IsUnpitched[index] ? (top + bottom) / 2 : PitchY(scene, scene.Pitches[index], top, bottom, thickness);

        /// <summary>How much of the song the whole part covers, in seconds: the song's length, or else the part's, as heard.</summary>
        private double TimelineOf(LyricScene scene) => LengthSeconds > 0 ? LengthSeconds : scene.EndSeconds + Math.Max(0, TimeShift.TotalSeconds) + 1;

        /// <summary>
        /// The whole part, from the start at the left to the end of the song at the right, as it's heard: its phrases, its
        /// notes as thin bars at their pitch, and the words of the phrase under the pointer; what's been played shaded, a line
        /// where the audio is, and one where the pointer is, or was last, so it fades out with the rest once the pointer has gone.
        /// </summary>
        /// <param name="now">Where the audio is, in seconds: <see cref="TimeShift"/> not taken off.</param>
        private void DrawOverview(DrawingContext context, LyricScene scene, Rect bounds, double now, double wordBand)
        {
            double timeline = Math.Max(0.1, TimelineOf(scene));
            double shift = TimeShift.TotalSeconds;
            double X(double sceneTime) => (sceneTime + shift) / timeline * bounds.Width;
            double noteTop = Padding;
            double noteBottom = bounds.Height - wordBand - Padding;
            double hover = double.IsNaN(_fade.PointerAt) ? 0 : Math.Clamp(_fade.PointerAt, 0, bounds.Width);
            int phrase = PhraseAt(scene, TimeAt(hover) - shift);

            using (context.PushClip(bounds))
            {
                context.FillRectangle(LaneBrush, bounds);
                for (int i = 0; i < scene.PhraseStarts.Length; i++)
                {
                    var block = new Rect(new Point(X(scene.PhraseStarts[i]), 0), new Point(Math.Max(X(scene.PhraseEnds[i]), X(scene.PhraseStarts[i]) + 1), bounds.Height));
                    context.FillRectangle(i == phrase ? HoveredPhraseBrush : PhraseBrush, block);
                }

                if (scene.NoteStarts.Length > 0 && noteBottom - noteTop > 6)
                {
                    foreach (var bar in OverviewBarsOf(scene, bounds.Size, noteTop, noteBottom, timeline, shift))
                        context.FillRectangle(NoteBrush, bar);
                }

                if (wordBand > 0 && phrase >= 0)
                {
                    var text = PhraseText(scene, phrase, bounds.Width - (Padding * 2));
                    context.DrawText(text, new Point(Padding, bounds.Height - wordBand + ((wordBand - text.Height) / 2)));
                }

                double playhead = Math.Clamp(now / timeline * bounds.Width, 0, bounds.Width);
                context.FillRectangle(LaneScrub.PlayedBrush, new Rect(0, 0, playhead, bounds.Height));
                context.DrawLine(LaneScrub.PlayheadPen, new Point(playhead, 0), new Point(playhead, bounds.Height));
                context.DrawLine(LaneScrub.HoverPen, new Point(hover, 0), new Point(hover, bounds.Height));
                LaneScrub.DrawTimeTag(context, TimeAt(hover), bounds, hover, isAcross: false);
            }
        }

        /// <summary>
        /// The whole part's notes as thin bars in a lane of <paramref name="size"/> covering <paramref name="timeline"/> seconds,
        /// each run of notes at one height that touch on screen as one bar, so a long part is a few hundred rectangles. Laid out
        /// again only when one of those, or the shift, changes.
        /// </summary>
        private List<Rect> OverviewBarsOf(LyricScene scene, Size size, double top, double bottom, double timeline, double shift)
        {
            if (_overview is { } known && ReferenceEquals(known.Scene, scene) && known.Size == size && known.Timeline == timeline && known.Shift == shift)
                return known.Bars;

            double thickness = Math.Clamp((bottom - top) / (Math.Max(1, scene.HighestPitch - scene.LowestPitch) + 2), 2, 4);
            double X(double sceneTime) => (sceneTime + shift) / timeline * size.Width;
            var bars = new List<Rect>();

            // The bar being built at each height; notes are in time order, so each starts at or after the one before.
            var runs = new Dictionary<double, (double Left, double Right)>();
            for (int i = 0; i < scene.NoteStarts.Length; i++)
            {
                double y = NoteY(scene, i, top, bottom, thickness);
                double left = X(scene.NoteStarts[i]);
                double right = Math.Max(X(scene.NoteEnds[i]), left + 1.5);
                if (runs.TryGetValue(y, out var run))
                {
                    if (left <= run.Right + 1)
                    {
                        runs[y] = (run.Left, Math.Max(run.Right, right));
                        continue;
                    }

                    bars.Add(new Rect(run.Left, y - (thickness / 2), run.Right - run.Left, thickness));
                }

                runs[y] = (left, right);
            }

            foreach (var (y, run) in runs)
                bars.Add(new Rect(run.Left, y - (thickness / 2), run.Right - run.Left, thickness));

            _overview = new OverviewMarks(scene, size, timeline, shift, bars);
            return bars;
        }

        /// <summary>The index of the phrase being sung at <paramref name="time"/> of the scene's own time, or -1.</summary>
        private static int PhraseAt(LyricScene scene, double time)
        {
            int i = LowerBound(scene.PhraseStarts, time + 1e-9) - 1;
            return i >= 0 && time < scene.PhraseEnds[i] ? i : -1;
        }

        /// <summary>The words of the scene's <paramref name="phrase"/>th phrase as one line, cut short to <paramref name="width"/>.</summary>
        private FormattedText PhraseText(LyricScene scene, int phrase, double width)
        {
            width = Math.Max(1, width);
            if (_phraseText is { } known && known.Index == phrase)
            {
                if (known.Width != width)
                {
                    known.Text.MaxTextWidth = width;
                    _phraseText = (phrase, width, known.Text);
                }

                return known.Text;
            }

            var line = new System.Text.StringBuilder();
            for (int i = LowerBound(scene.WordTimes, scene.PhraseStarts[phrase]); i < scene.WordTimes.Length && scene.WordTimes[i] < scene.PhraseEnds[phrase]; i++)
            {
                line.Append(scene.Words[i]);
                if (!scene.JoinsNext[i])
                    line.Append(' ');
            }

            var text = new FormattedText(line.ToString().TrimEnd(), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, WordTypeface, WordFontSize, PhraseWordBrush)
            {
                MaxTextWidth = width,
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            _phraseText = (phrase, width, text);
            return text;
        }

        /// <summary>The song time at <paramref name="x"/> across the whole part.</summary>
        private double TimeAt(double x) =>
            Scene is { } scene && Bounds.Width > 0 ? Math.Clamp(x / Bounds.Width, 0, 1) * TimelineOf(scene) : 0;

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var point = e.GetPosition(this);
            _mode.Set(e.KeyModifiers);
            SetHover(point.X);
            if (_isScrubbing)
                Scrubbed?.Invoke(this, new ScrubEventArgs(ScrubTimeAt(point)));
        }

        /// <summary>
        /// Where a drag to <paramref name="point"/> moves the audio: with the whole part shown, the time there across it;
        /// otherwise on from where the audio is by how far left the pointer has gone since it last moved, as far as the lane
        /// scrolls for it, or while it's fine, <see cref="FineScrubFactor"/> times less far; either way holding the pointer still.
        /// </summary>
        private double ScrubTimeAt(Point point)
        {
            if (_mode.IsOverview || Scene is not { } scene)
            {
                _mode.Stop();
                return TimeAt(point.X);
            }

            double now = (Clock?.Invoke() ?? TimeSpan.Zero).TotalSeconds;
            double perPixel = _mode.IsFine ? GrabSecondsPerPixel / Math.Max(1, FineScrubFactor) : GrabSecondsPerPixel;
            return Math.Clamp(now - (_mode.Move(point).X * perPixel), 0, TimelineOf(scene));
        }

        /// <summary>How far the lane scrolls in a pixel, in seconds, so a drag moves the words as far as the pointer goes.</summary>
        private double GrabSecondsPerPixel => Bounds.Width > 0 ? Math.Max(0.5, WindowSeconds) / (Bounds.Width * (1 - NowFraction)) : 0;

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            if (!_isScrubbing)
                SetHover(double.NaN);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Scene is not { IsEmpty: false })
                return;

            var point = e.GetPosition(this);
            _mode.Set(e.KeyModifiers);
            SetHover(point.X);
            _isScrubbing = true;
            e.Pointer.Capture(this);
            ScrubStarted?.Invoke(this, EventArgs.Empty);

            // A press on the whole part moves the audio there; otherwise it starts a drag from where the audio is.
            if (_mode.IsOverview)
                Scrubbed?.Invoke(this, new ScrubEventArgs(TimeAt(point.X)));
            else
                _mode.Start(point);
            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (!_isScrubbing)
                return;

            var point = e.GetPosition(this);
            _mode.Set(e.KeyModifiers);
            Scrubbed?.Invoke(this, new ScrubEventArgs(ScrubTimeAt(point)));
            e.Pointer.Capture(null);
            EndScrub();

            // Let go off the lane, it stops showing the whole part.
            SetHover(new Rect(Bounds.Size).Contains(point) ? point.X : double.NaN);
            e.Handled = true;
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            EndScrub();
        }

        private void EndScrub()
        {
            if (!_isScrubbing)
                return;

            _isScrubbing = false;
            _mode.Stop();
            ScrubEnded?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Takes the pointer to be <paramref name="x"/> across the lane, which shows the whole part while Shift is held; NaN for not over it.</summary>
        private void SetHover(double x)
        {
            if (x == _hoverX || (double.IsNaN(x) && double.IsNaN(_hoverX)))
                return;

            if (double.IsNaN(x) != double.IsNaN(_hoverX))
                Cursor = double.IsNaN(x) ? null : LaneScrub.HandCursor;
            _hoverX = x;
            if (!double.IsNaN(x))
                _fade.PointerAt = x;
            InvalidateVisual();
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

        /// <summary>A diamond reaching a unit from the origin each way, made once.</summary>
        private static StreamGeometry Diamond()
        {
            if (s_diamond is { } diamond)
                return diamond;

            diamond = new StreamGeometry();
            using (var g = diamond.Open())
            {
                g.BeginFigure(new Point(0, -1), true);
                g.LineTo(new Point(1, 0));
                g.LineTo(new Point(0, 1));
                g.LineTo(new Point(-1, 0));
                g.EndFigure(true);
            }

            return s_diamond = diamond;
        }

        /// <summary>Asks to be called back when the screen is next drawn, while there's a part to draw.</summary>
        private void RequestFrame()
        {
            if (_isFrameRequested || Scene is not { IsEmpty: false } || !IsVisible || TopLevel.GetTopLevel(this) is not { } topLevel)
                return;

            _isFrameRequested = true;
            topLevel.RequestAnimationFrame(OnFrame);
        }

        private void OnFrame(TimeSpan frame)
        {
            _isFrameRequested = false;
            if (TopLevel.GetTopLevel(this) is null)
                return;

            // Paused, nothing moves, so nothing is redrawn; nor is it while it's scrolled out of view, where a fade to or
            // from the whole part is put where it's going.
            if (!IsEffectivelyVisible || !IsInViewport)
            {
                _fade.Finish(IsOverviewShown);
                RequestFrame();
                return;
            }

            bool isFading = _fade.Step(IsOverviewShown, frame);
            var clock = Clock?.Invoke() ?? TimeSpan.Zero;
            if (clock != _drawnAt || isFading)
            {
                _drawnAt = clock;
                InvalidateVisual();
            }

            RequestFrame();
        }

        /// <summary>The whole part's note bars, and the scene, size, length of song and shift they were laid out for.</summary>
        private sealed record OverviewMarks(LyricScene Scene, Size Size, double Timeline, double Shift, List<Rect> Bars);
    }
}
