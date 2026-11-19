using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ChartBuilder.ViewModels;

namespace ChartBuilder.Controls
{
    /// <summary>
    /// The bottom timeline's lyric lens: while the pointer is over the timeline of a song with lyrics, whichever lane is in
    /// focus, a window above it, at the pointer, onto the lyrics written out as running text, as a magnifying glass slides
    /// along a line of a book, so a part of the song is found by its words. The whole song's syllables are one line, words joined as they're sung and a slash between lines; the time
    /// under the pointer is a place in that line, within a syllable as far through it as through its note, and between two
    /// as far through the rest between them, so the text glides under the glass as the pointer moves. That place is under
    /// the lens's pointer mark; the syllable being sung there is in the accent, its line in full and the others muted.
    /// </summary>
    /// <remarks>
    /// An adorner of the timeline, drawn above it unclipped and never hit, so the pointer stays the timeline's. Its
    /// coordinates are the timeline's: it's arranged over it.
    /// </remarks>
    internal sealed class LyricLens : Control
    {
        private const double LensWidth = 440;
        private const double LensHeight = 30;
        private const double CornerRadius = 6;
        // How far above the timeline the lens's mark points down from, and how big the mark is.
        private const double Gap = 7;
        private const double MarkSize = 5;
        private const double FontSize = 15;
        // How wide the lens's ends are where the text fades into it.
        private const double FadeWidth = 56;
        private const string LineBreak = "/";

        /// <summary>How far above the timeline the lens reaches, so a tip over the timeline can be placed above it.</summary>
        public const double Reach = Gap + LensHeight;

        private static readonly BoxShadows Shadow = BoxShadows.Parse("0 4 14 0 #A0000000");

        private IReadOnlyList<OverviewLyric>? _lyrics;
        private double _seconds;
        private double _x;

        // The line laid out: each syllable's text, where it starts and ends along the line, and which line it's in; where
        // each slash between lines is, and the slash; and how tall the text is. Each syllable's brush, and the slash's, is
        // set as it's drawn, so a new theme's colours show without laying the line out again.
        private FormattedText[] _texts = [];
        private double[] _lefts = [];
        private double[] _rights = [];
        private int[] _lines = [];
        private readonly List<double> _breaks = [];
        private FormattedText? _break;
        private double _textHeight;

        private IBrush _fadeLeft = Brushes.Transparent;
        private IBrush _fadeRight = Brushes.Transparent;
        private Color? _fadeColor;

        public LyricLens()
        {
            IsHitTestVisible = false;
            ClipToBounds = false;
        }

        /// <summary>The lens's ground.</summary>
        public IBrush? Background { get; set; }

        /// <summary>The edge round the lens.</summary>
        public IBrush? BorderBrush { get; set; }

        /// <summary>The line being sung.</summary>
        public IBrush? TextBrush { get; set; }

        /// <summary>The other lines, and the slashes between lines.</summary>
        public IBrush? MutedBrush { get; set; }

        /// <summary>The syllable being sung, and the lens's mark.</summary>
        public IBrush? AccentBrush { get; set; }

        /// <summary>
        /// Shows the lens at <paramref name="x"/> along the timeline, onto <paramref name="lyrics"/> where
        /// <paramref name="seconds"/> into the audio is; laid out again only when they're another list.
        /// </summary>
        public void Show(IReadOnlyList<OverviewLyric> lyrics, double seconds, double x)
        {
            if (!ReferenceEquals(lyrics, _lyrics))
            {
                _lyrics = lyrics;
                _texts = [];
            }

            _seconds = seconds;
            _x = x;
            IsVisible = true;
            InvalidateVisual();
        }

        public void Hide() => IsVisible = false;

        public override void Render(DrawingContext context)
        {
            if (_lyrics is not { Count: > 0 } lyrics || Bounds.Width <= 0)
                return;
            if (_texts.Length != lyrics.Count)
                Layout(lyrics);

            // The lens over the pointer, kept over the timeline, and its mark pointing down at the pointer.
            double width = Math.Min(LensWidth, Bounds.Width);
            double x = Math.Clamp(_x, 0, Bounds.Width);
            double left = Math.Clamp(x - (width / 2), 0, Bounds.Width - width);
            var lens = new Rect(left, -Gap - LensHeight, width, LensHeight);
            var rounded = new RoundedRect(lens, CornerRadius);
            var background = Background ?? Brushes.Black;
            var border = new Pen(BorderBrush ?? Brushes.Gray, 1);
            context.DrawRectangle(background, border, rounded, Shadow);
            DrawMark(context, x, lens.Bottom);

            double place = PlaceOf(_seconds);
            int sung = SungAt(lyrics, _seconds);
            int line = LineAt(place);
            double shift = x - place;
            double top = lens.Top + ((LensHeight - _textHeight) / 2);
            using (context.PushClip(rounded))
            {
                // Only the syllables the lens shows.
                var text = TextBrush ?? Brushes.White;
                var muted = MutedBrush ?? Brushes.Gray;
                var accent = AccentBrush ?? Brushes.CornflowerBlue;
                for (int i = FirstEndingAfter(lens.Left - shift); i < _texts.Length && _lefts[i] + shift < lens.Right; i++)
                {
                    _texts[i].SetForegroundBrush(i == sung ? accent : _lines[i] == line ? text : muted);
                    context.DrawText(_texts[i], new Point(_lefts[i] + shift, top));
                }

                if (_break is { } slash)
                {
                    slash.SetForegroundBrush(muted);
                    foreach (double at in _breaks)
                    {
                        if (at + shift > lens.Left - slash.Width && at + shift < lens.Right)
                            context.DrawText(slash, new Point(at + shift, top));
                    }
                }

                // The text fades into the lens's ends, so it reads as passing under the glass.
                if (background is ISolidColorBrush { Color: var color })
                {
                    Fades(color);
                    context.FillRectangle(_fadeLeft, new Rect(lens.Left, lens.Top, FadeWidth, LensHeight));
                    context.FillRectangle(_fadeRight, new Rect(lens.Right - FadeWidth, lens.Top, FadeWidth, LensHeight));
                }
            }

            context.DrawRectangle(null, border, rounded);
        }

        // The mark under the lens pointing down at the pointer.
        private void DrawMark(DrawingContext context, double x, double bottom)
        {
            var mark = new StreamGeometry();
            using (var geometry = mark.Open())
            {
                geometry.BeginFigure(new Point(x - MarkSize, bottom), true);
                geometry.LineTo(new Point(x + MarkSize, bottom));
                geometry.LineTo(new Point(x, bottom + MarkSize));
                geometry.EndFigure(true);
            }

            context.DrawGeometry(AccentBrush ?? Brushes.CornflowerBlue, null, mark);
        }

        /// <summary>Lays the syllables out along one line: words joined, a space between them, and a slash between lines.</summary>
        private void Layout(IReadOnlyList<OverviewLyric> lyrics)
        {
            var typeface = Typeface.Default;
            FormattedText Text(string text) => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, FontSize, TextBrush ?? Brushes.White);

            double space = Text(" ").WidthIncludingTrailingWhitespace;
            _break = Text(LineBreak);
            _textHeight = _break.Height;
            _breaks.Clear();

            int count = lyrics.Count;
            _texts = new FormattedText[count];
            _lefts = new double[count];
            _rights = new double[count];
            _lines = new int[count];
            double along = 0;
            int line = 0;
            for (int i = 0; i < count; i++)
            {
                var lyric = lyrics[i];
                var text = Text(lyric.Text);
                _texts[i] = text;
                _lefts[i] = along;
                _rights[i] = along + text.Width;
                _lines[i] = line;
                along = _rights[i];
                if (lyric.EndsLine && i + 1 < count)
                {
                    _breaks.Add(along + space);
                    along += space + _break.Width + space;
                    line++;
                }
                else if (!lyric.JoinsNext)
                {
                    along += space;
                }
            }
        }

        private double PlaceOf(double seconds) => PlaceOf(_lyrics!, _lefts, _rights, seconds);

        /// <summary>
        /// Where <paramref name="seconds"/> is along a line of <paramref name="lyrics"/> laid out from
        /// <paramref name="lefts"/> to <paramref name="rights"/>: as far through a syllable as through its note, and
        /// between two as far through the rest between them; at the first syllable's start before it, and the last's end
        /// after.
        /// </summary>
        internal static double PlaceOf(IReadOnlyList<OverviewLyric> lyrics, IReadOnlyList<double> lefts, IReadOnlyList<double> rights, double seconds)
        {
            int i = LastStartingBy(lyrics, seconds);
            if (i < 0)
                return lefts[0];

            var lyric = lyrics[i];
            if (seconds < lyric.End)
                return lefts[i] + ((rights[i] - lefts[i]) * (seconds - lyric.Start) / (lyric.End - lyric.Start));
            if (i + 1 >= lyrics.Count)
                return rights[i];

            double rest = lyrics[i + 1].Start - lyric.End;
            double through = rest > 0 ? Math.Clamp((seconds - lyric.End) / rest, 0, 1) : 1;
            return rights[i] + ((lefts[i + 1] - rights[i]) * through);
        }

        /// <summary>The syllable of <paramref name="lyrics"/> being sung at <paramref name="seconds"/>, or -1 between them.</summary>
        internal static int SungAt(IReadOnlyList<OverviewLyric> lyrics, double seconds)
        {
            int i = LastStartingBy(lyrics, seconds);
            return i >= 0 && (seconds < lyrics[i].End || seconds == lyrics[i].Start) ? i : -1;
        }

        /// <summary>The line <paramref name="place"/> along the text is in: the next one's, past a line's last syllable.</summary>
        private int LineAt(double place)
        {
            int i = FirstEndingAfter(place);
            return i < _lines.Length ? _lines[i] : _lines[^1];
        }

        // The last syllable starting no later than the time, or -1 for none.
        private static int LastStartingBy(IReadOnlyList<OverviewLyric> lyrics, double seconds)
        {
            int low = 0, high = lyrics.Count;
            while (low < high)
            {
                int middle = (low + high) >>> 1;
                if (lyrics[middle].Start <= seconds)
                    low = middle + 1;
                else
                    high = middle;
            }

            return low - 1;
        }

        // The first syllable ending after the place along the line, or the count for none.
        private int FirstEndingAfter(double place)
        {
            int low = 0, high = _rights.Length;
            while (low < high)
            {
                int middle = (low + high) >>> 1;
                if (_rights[middle] <= place)
                    low = middle + 1;
                else
                    high = middle;
            }

            return low;
        }

        // The ends' fades, from the lens's ground to clear, made again only for a new ground.
        private void Fades(Color color)
        {
            if (_fadeColor == color)
                return;
            _fadeColor = color;
            var clear = Color.FromArgb(0, color.R, color.G, color.B);
            _fadeLeft = Fade(color, clear);
            _fadeRight = Fade(clear, color);
        }

        private static LinearGradientBrush Fade(Color from, Color to) => new()
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = { new GradientStop(from, 0), new GradientStop(to, 1) },
        };
    }
}
