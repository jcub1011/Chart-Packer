using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ChartShared.Audio.Waveforms;
using ChartShared.Core.Charts;
using ChartShared.UI.Controls;
using ChartShared.UI.Controls.Highway;

namespace ChartPacker.Controls
{
    /// <summary>A part's notes as <see cref="AlignmentTimelineView"/> draws them: when each starts and ends, its row and its colour, in start order.</summary>
    public sealed class TimelineNotes
    {
        private TimelineNotes(string label, int rows, List<(double Start, double End, int Row, Color Color)> notes)
        {
            notes.Sort((a, b) => a.Start.CompareTo(b.Start));
            Label = label;
            Rows = Math.Max(1, rows);
            Starts = [.. notes.Select(n => n.Start)];
            Ends = [.. notes.Select(n => n.End)];
            RowOf = [.. notes.Select(n => n.Row)];
            Colors = [.. notes.Select(n => n.Color)];
        }

        /// <summary>What the notes are, e.g. <c>Guitar (Expert)</c>.</summary>
        public string Label { get; }

        /// <summary>How many rows the notes are drawn in: a fret, pad or column each.</summary>
        public int Rows { get; }

        /// <summary>When each note starts, in seconds of its song's own audio.</summary>
        public double[] Starts { get; }

        /// <summary>When each ends: when it starts, for a note without a sustain.</summary>
        public double[] Ends { get; }

        /// <summary>The row each is drawn in, from the top; -1 for one across them all, as a kick or an open note.</summary>
        public int[] RowOf { get; }

        public Color[] Colors { get; }

        /// <summary>When the first note starts; 0 for none.</summary>
        public double FirstStart => Starts.Length > 0 ? Starts[0] : 0;

        /// <summary>When the last note ends; 0 for none.</summary>
        public double LastEnd => Ends.Length > 0 ? Ends.Max() : 0;

        /// <summary>A highway's notes, in its rows and colours.</summary>
        public static TimelineNotes Of(HighwayLane lane)
        {
            ArgumentNullException.ThrowIfNull(lane);
            int rows = lane.Kind == ChartInstrumentKind.SixFret ? 3 : lane.LaneCount;
            var notes = new List<(double, double, int, Color)>(lane.Notes.Length);
            for (int i = 0; i < lane.Notes.Length; i++)
            {
                var note = lane.Notes[i];
                int row = note.Lane == ChartNote.OpenLane ? -1 : HighwayPalette.Column(lane.Kind, lane.LaneCount, note.Lane);
                notes.Add((lane.Starts[i], lane.Ends[i], row, HighwayPalette.NoteColor(lane.Kind, lane.LaneCount, note)));
            }

            return new TimelineNotes(lane.Label, rows, notes);
        }

        /// <summary>A vocal part's notes, in one row.</summary>
        /// <param name="delay">How much later than its ticks say the chart plays against its audio.</param>
        public static TimelineNotes OfVocals(string label, VocalPart part, TempoMap tempo, TimeSpan delay)
        {
            ArgumentNullException.ThrowIfNull(part);
            ArgumentNullException.ThrowIfNull(tempo);
            var notes = part.Notes
                .Select(n => (tempo.SecondsAt(n.Tick) + delay.TotalSeconds, tempo.SecondsAt(n.Tick + n.Length) + delay.TotalSeconds, 0, VocalColor))
                .ToList();
            return new TimelineNotes(label, 1, notes);
        }

        private static readonly Color VocalColor = Color.FromRgb(0x7F, 0xE9, 0xFF);
    }

    /// <summary>
    /// Two songs on one time axis, to line one up with the other by eye: the kept audio's song's waveform and notes, and the
    /// other song's moved by <see cref="OffsetSeconds"/>, each under the first's.
    /// </summary>
    /// <remarks>
    /// <para>Dragging the other song's lanes moves them, and the offset with them, as far as the pointer goes; with Ctrl held
    /// a drag is <see cref="FineFactor"/> times finer and holds the pointer still, so it can go on as far as it needs. Dragging
    /// anywhere else moves along the song. The wheel zooms about the pointer, and with Shift held moves along the song.</para>
    /// <para>The view starts a second before the first notes, showing <see cref="StartSeconds"/>, once there's something to show.</para>
    /// </remarks>
    public sealed class AlignmentTimelineView : Control
    {
        public static readonly StyledProperty<Waveform?> ReferenceWaveformProperty =
            AvaloniaProperty.Register<AlignmentTimelineView, Waveform?>(nameof(ReferenceWaveform));

        public static readonly StyledProperty<Waveform?> SongWaveformProperty =
            AvaloniaProperty.Register<AlignmentTimelineView, Waveform?>(nameof(SongWaveform));

        public static readonly StyledProperty<TimelineNotes?> ReferenceNotesProperty =
            AvaloniaProperty.Register<AlignmentTimelineView, TimelineNotes?>(nameof(ReferenceNotes));

        public static readonly StyledProperty<TimelineNotes?> SongNotesProperty =
            AvaloniaProperty.Register<AlignmentTimelineView, TimelineNotes?>(nameof(SongNotes));

        public static readonly StyledProperty<double> OffsetSecondsProperty =
            AvaloniaProperty.Register<AlignmentTimelineView, double>(nameof(OffsetSeconds), defaultBindingMode: BindingMode.TwoWay);

        public static readonly StyledProperty<string?> ReferenceNameProperty =
            AvaloniaProperty.Register<AlignmentTimelineView, string?>(nameof(ReferenceName));

        public static readonly StyledProperty<string?> SongNameProperty =
            AvaloniaProperty.Register<AlignmentTimelineView, string?>(nameof(SongName));

        public static readonly StyledProperty<double> FineFactorProperty =
            AvaloniaProperty.Register<AlignmentTimelineView, double>(nameof(FineFactor), LaneScrub.DefaultFineFactor);

        /// <summary>How many seconds the view shows when it starts.</summary>
        public const double StartSeconds = 12;

        private const double RulerHeight = 22;
        private const double WaveformHeight = 64;
        private const double NoteRowHeight = 9;
        private const double NotePadding = 4;
        private const double LaneGap = 6;
        private const double BarWidth = 2;
        private const double BarGap = 1;
        private const double MinBarHeight = 1;
        private const double PeakOpacity = 0.35;
        private const double ZoomStep = 1.25;
        private const double PanPixels = 80;

        // The finest the view zooms in to: a fifth of a millisecond a pixel.
        private const double MinSecondsPerPixel = 0.0002;

        // Ruler steps, in seconds, the first that leaves this much room between labels.
        private const double MinStepPixels = 80;
        private static readonly double[] Steps = [0.005, 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300];

        private static readonly IBrush LaneBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x18, 0x19, 0x22));
        private static readonly IBrush SongLaneBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x1D, 0x1B, 0x2C));
        private static readonly IBrush ReferenceWaveBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x8A, 0x8E, 0xAE));
        private static readonly IBrush SongWaveBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xC4, 0xB5, 0xFD));
        private static readonly IPen GridPen = new ImmutablePen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF).ToUInt32(), 1);
        private static readonly IPen RowPen = new ImmutablePen(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF).ToUInt32(), 1);
        private static readonly IBrush RulerTextBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
        private static readonly IBrush LabelBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF));
        private static readonly IBrush LabelBackBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xB0, 0x0E, 0x0F, 0x14));
        private static readonly IBrush TagBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xD0, 0x00, 0x00, 0x00));
        private static readonly IBrush TagTextBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0xE6, 0x80));
        private static readonly Typeface Typeface = new(FontFamily.Default);
        private static readonly Typeface BoldTypeface = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
        private static readonly Cursor MoveCursor = new(StandardCursorType.SizeWestEast);

        private readonly FineDrag _fine;

        // What the view shows: from _start seconds, _secondsPerPixel to a pixel. Set once there's something to show.
        private double _start;
        private double _secondsPerPixel = StartSeconds / 1000;
        private bool _hasView;

        // A drag under way: of the other song's lanes, or along the song; and where the pointer was last.
        private DragKind _drag;
        private double _lastX;
        private double _hoverX = double.NaN;

        static AlignmentTimelineView()
        {
            AffectsRender<AlignmentTimelineView>(ReferenceWaveformProperty, SongWaveformProperty, ReferenceNotesProperty, SongNotesProperty,
                OffsetSecondsProperty, ReferenceNameProperty, SongNameProperty);
            AffectsMeasure<AlignmentTimelineView>(ReferenceWaveformProperty, SongWaveformProperty, ReferenceNotesProperty, SongNotesProperty);
            FocusableProperty.OverrideDefaultValue<AlignmentTimelineView>(false);
            ClipToBoundsProperty.OverrideDefaultValue<AlignmentTimelineView>(true);
        }

        public AlignmentTimelineView() => _fine = new FineDrag(this);

        private enum DragKind
        {
            None,
            Offset,
            Pan,
        }

        /// <summary>The kept audio's song's audio, mixed.</summary>
        public Waveform? ReferenceWaveform
        {
            get => GetValue(ReferenceWaveformProperty);
            set => SetValue(ReferenceWaveformProperty, value);
        }

        /// <summary>The other song's audio, mixed, drawn <see cref="OffsetSeconds"/> later.</summary>
        public Waveform? SongWaveform
        {
            get => GetValue(SongWaveformProperty);
            set => SetValue(SongWaveformProperty, value);
        }

        /// <summary>The kept audio's song's notes of a part.</summary>
        public TimelineNotes? ReferenceNotes
        {
            get => GetValue(ReferenceNotesProperty);
            set => SetValue(ReferenceNotesProperty, value);
        }

        /// <summary>The other song's notes of a part, drawn <see cref="OffsetSeconds"/> later.</summary>
        public TimelineNotes? SongNotes
        {
            get => GetValue(SongNotesProperty);
            set => SetValue(SongNotesProperty, value);
        }

        /// <summary>How much later the other song is drawn, in seconds; dragging its lanes changes it.</summary>
        public double OffsetSeconds
        {
            get => GetValue(OffsetSecondsProperty);
            set => SetValue(OffsetSecondsProperty, value);
        }

        public string? ReferenceName
        {
            get => GetValue(ReferenceNameProperty);
            set => SetValue(ReferenceNameProperty, value);
        }

        public string? SongName
        {
            get => GetValue(SongNameProperty);
            set => SetValue(SongNameProperty, value);
        }

        /// <summary>How many times less far a drag with Ctrl held moves the other song than a plain drag.</summary>
        public double FineFactor
        {
            get => GetValue(FineFactorProperty);
            set => SetValue(FineFactorProperty, value);
        }

        /// <summary>Shows half as much, about the middle.</summary>
        public void ZoomIn() => Zoom(1 / (ZoomStep * ZoomStep), Bounds.Width / 2);

        /// <summary>Shows twice as much, about the middle.</summary>
        public void ZoomOut() => Zoom(ZoomStep * ZoomStep, Bounds.Width / 2);

        /// <summary>Shows both songs from end to end.</summary>
        public void ShowAll()
        {
            if (Bounds.Width <= 0)
                return;

            var (first, last) = Extent();
            _start = first;
            _secondsPerPixel = Math.Max(MinSecondsPerPixel, (last - first) / Bounds.Width);
            _hasView = true;
            InvalidateVisual();
        }

        /// <summary>Shows <see cref="StartSeconds"/> from a second before the first notes, or from the start.</summary>
        public void ShowStart()
        {
            if (Bounds.Width <= 0)
                return;

            SetStartView(Bounds.Width);
            InvalidateVisual();
        }

        /// <summary>Sets the view <see cref="ShowStart"/> shows, for a control <paramref name="width"/> wide, without drawing it again.</summary>
        private void SetStartView(double width)
        {
            double? first = ReferenceNotes is { Starts.Length: > 0 } reference ? reference.FirstStart
                : SongNotes is { Starts.Length: > 0 } song ? song.FirstStart + OffsetSeconds
                : null;
            _secondsPerPixel = StartSeconds / width;
            _start = first is { } at ? at - 1 : 0;
            _hasView = true;
        }

        protected override Size MeasureOverride(Size availableSize) =>
            new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, Lanes().Select(l => l.Bottom).DefaultIfEmpty(RulerHeight).Max());

        public override void Render(DrawingContext context)
        {
            double width = Bounds.Width;
            double height = Bounds.Height;
            if (width <= 0 || height <= 0)
                return;

            // It's being drawn already, so the view is only set.
            if (!_hasView && (ReferenceWaveform is not null || SongWaveform is not null || ReferenceNotes is not null || SongNotes is not null))
                SetStartView(width);

            // Transparent, so the whole of it takes the pointer.
            context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

            var lanes = Lanes();
            foreach (var lane in lanes)
                context.DrawRectangle(lane.IsSong ? SongLaneBrush : LaneBrush, null, lane.Rect(width), 4, 4);

            DrawRuler(context, width, height);

            foreach (var lane in lanes)
            {
                var rect = lane.Rect(width);
                using (context.PushClip(rect))
                {
                    double shift = lane.IsSong ? OffsetSeconds : 0;
                    if (lane.Waveform is { } waveform)
                        DrawWaveform(context, rect, waveform, shift, lane.IsSong ? SongWaveBrush : ReferenceWaveBrush);
                    else if (lane.Notes is { } notes)
                        DrawNotes(context, rect, notes, shift);
                    DrawLabel(context, rect, lane.Label);
                }
            }

            if (!double.IsNaN(_hoverX))
            {
                context.DrawLine(LaneScrub.HoverPen, new Point(_hoverX, RulerHeight), new Point(_hoverX, height));
                DrawTag(context, TimeAt(_hoverX), _hoverX, width);
            }
        }

        private void DrawRuler(DrawingContext context, double width, double height)
        {
            double step = Steps.FirstOrDefault(s => s / _secondsPerPixel >= MinStepPixels, Steps[^1]);
            int decimals = step >= 1 ? 0 : step >= 0.1 ? 1 : step >= 0.01 ? 2 : 3;
            for (double t = Math.Floor(_start / step) * step; t <= _start + (width * _secondsPerPixel); t += step)
            {
                double x = Math.Round((t - _start) / _secondsPerPixel) + 0.5;
                context.DrawLine(GridPen, new Point(x, RulerHeight - 6), new Point(x, height));
                var text = new FormattedText(FormatTime(t, decimals), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface, 10.5, RulerTextBrush);
                context.DrawText(text, new Point(x + 3, 3));
            }
        }

        private void DrawWaveform(DrawingContext context, Rect rect, Waveform waveform, double shift, IBrush brush)
        {
            double middle = rect.Center.Y;
            double half = (rect.Height / 2) - 2;
            double columnSeconds = (BarWidth + BarGap) * _secondsPerPixel;
            int columns = (int)Math.Ceiling(rect.Width / (BarWidth + BarGap));

            for (int pass = 0; pass < 2; pass++)
            {
                // The peaks first, faintly; then the loudness over them.
                using (context.PushOpacity(pass == 0 ? PeakOpacity : 1))
                {
                    for (int column = 0; column < columns; column++)
                    {
                        double x = column * (BarWidth + BarGap);
                        double from = _start + (x * _secondsPerPixel) - shift;
                        if (from + columnSeconds <= 0 || from >= waveform.Seconds)
                            continue;

                        var (low, high, rms) = WaveformLevels.Of(waveform, Math.Max(0, from), from + columnSeconds);
                        double above = pass == 0 ? high : WaveformLevels.Loudness(low, high, rms);
                        double below = pass == 0 ? -low : above;
                        double top = middle - (Math.Clamp(above, 0, 1) * half);
                        double bottom = middle + (Math.Clamp(below, 0, 1) * half);
                        if (bottom - top < MinBarHeight)
                        {
                            top = middle - (MinBarHeight / 2);
                            bottom = middle + (MinBarHeight / 2);
                        }

                        context.DrawRectangle(brush, null, new Rect(rect.X + x, top, BarWidth, bottom - top));
                    }
                }
            }
        }

        private void DrawNotes(DrawingContext context, Rect rect, TimelineNotes notes, double shift)
        {
            double rowsTop = rect.Y + NotePadding;
            for (int row = 1; row < notes.Rows; row++)
            {
                double y = rowsTop + (row * NoteRowHeight) - 0.5;
                context.DrawLine(RowPen, new Point(rect.X, y), new Point(rect.Right, y));
            }

            double viewEnd = _start + (rect.Width * _secondsPerPixel);
            for (int i = 0; i < notes.Starts.Length; i++)
            {
                double start = notes.Starts[i] + shift;
                double end = notes.Ends[i] + shift;
                if (end < _start || start > viewEnd)
                    continue;

                double x = rect.X + ((start - _start) / _secondsPerPixel);
                double length = (end - start) / _secondsPerPixel;
                var color = notes.Colors[i];
                if (notes.RowOf[i] < 0)
                {
                    // Across every row, behind the others, as a kick is.
                    context.DrawRectangle(BrushOf(color, 0.55), null, new Rect(x - 1, rowsTop, 2, notes.Rows * NoteRowHeight));
                    continue;
                }

                double y = rowsTop + (notes.RowOf[i] * NoteRowHeight) + 1;
                if (length >= 2)
                    context.DrawRectangle(BrushOf(color, 0.4), null, new Rect(x, y + 2, length, NoteRowHeight - 6));
                context.DrawRectangle(BrushOf(color, 1), null, new Rect(x - 1.5, y, 3, NoteRowHeight - 2), 1, 1);
            }
        }

        /// <summary>A brush of <paramref name="color"/> at <paramref name="opacity"/>, made once for each, as a chart has only a few.</summary>
        private static IBrush BrushOf(Color color, double opacity)
        {
            var key = (color.ToUInt32(), opacity);
            if (!NoteBrushes.TryGetValue(key, out var brush))
                NoteBrushes[key] = brush = new ImmutableSolidColorBrush(color, opacity);
            return brush;
        }

        // Only drawn on the UI thread, so it needn't be locked.
        private static readonly Dictionary<(uint Color, double Opacity), IBrush> NoteBrushes = [];

        private static void DrawLabel(DrawingContext context, Rect rect, string label)
        {
            var text = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, BoldTypeface, 11, LabelBrush);
            var back = new Rect(rect.X + 4, rect.Y + 3, text.Width + 8, text.Height + 2);
            context.DrawRectangle(LabelBackBrush, null, back, 3, 3);
            context.DrawText(text, new Point(back.X + 4, back.Y + 1));
        }

        private static void DrawTag(DrawingContext context, double seconds, double x, double width)
        {
            var text = new FormattedText(FormatTime(seconds, 3), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, BoldTypeface, 10.5, TagTextBrush);
            var size = new Size(text.Width + 6, text.Height + 2);
            double left = x + 4 + size.Width <= width ? x + 4 : x - 4 - size.Width;
            var tag = new Rect(new Point(left, RulerHeight + 2), size);
            context.DrawRectangle(TagBrush, null, tag, 3, 3);
            context.DrawText(text, new Point(tag.X + 3, tag.Y + 1));
        }

        /// <summary><paramref name="seconds"/> as <c>m:ss</c>, with <paramref name="decimals"/> places of a second; negative before the start.</summary>
        internal static string FormatTime(double seconds, int decimals)
        {
            double rounded = Math.Round(Math.Abs(seconds), decimals, MidpointRounding.AwayFromZero);
            long minutes = (long)(rounded / 60);
            double rest = rounded - (minutes * 60);
            string format = decimals > 0 ? "00." + new string('0', decimals) : "00";
            string text = minutes.ToString(CultureInfo.InvariantCulture) + ":" + rest.ToString(format, CultureInfo.InvariantCulture);
            return seconds < 0 && rounded > 0 ? "-" + text : text;
        }

        /// <summary>The lanes there are, top to bottom: each song's audio, then each one's notes, the kept audio's first of each pair.</summary>
        private List<Lane> Lanes()
        {
            var lanes = new List<Lane>();
            double top = RulerHeight;
            void Add(bool isSong, string label, Waveform? waveform, TimelineNotes? notes)
            {
                double height = waveform is not null ? WaveformHeight : (notes!.Rows * NoteRowHeight) + (NotePadding * 2) + 12;
                lanes.Add(new Lane(isSong, label, waveform, notes, top, height));
                top += height + LaneGap;
            }

            string reference = ReferenceName ?? "";
            string song = SongName ?? "";
            string offset = OffsetLabel();
            if (ReferenceWaveform is { } referenceWaveform)
                Add(false, reference, referenceWaveform, null);
            if (SongWaveform is { } songWaveform)
                Add(true, song + offset, songWaveform, null);
            if (ReferenceNotes is { } referenceNotes)
                Add(false, reference + " · " + referenceNotes.Label, null, referenceNotes);
            if (SongNotes is { } songNotes)
                Add(true, song + " · " + songNotes.Label + offset, null, songNotes);
            return lanes;
        }

        private string OffsetLabel() =>
            "  " + Math.Round(OffsetSeconds * 1000).ToString("+0;-0;0", CultureInfo.CurrentCulture) + " ms";

        /// <summary>The time both songs cover, the other moved by its offset, from the earlier start to the later end.</summary>
        private (double First, double Last) Extent()
        {
            double offset = OffsetSeconds;
            var starts = new List<double> { 0 };
            var ends = new List<double> { 1 };
            if (ReferenceWaveform is { } a)
                ends.Add(a.Seconds);
            if (SongWaveform is { } b)
            {
                starts.Add(offset);
                ends.Add(b.Seconds + offset);
            }

            if (ReferenceNotes is { Starts.Length: > 0 } x)
            {
                starts.Add(x.FirstStart);
                ends.Add(x.LastEnd);
            }

            if (SongNotes is { Starts.Length: > 0 } y)
            {
                starts.Add(y.FirstStart + offset);
                ends.Add(y.LastEnd + offset);
            }

            return (starts.Min(), ends.Max());
        }

        private double TimeAt(double x) => _start + (x * _secondsPerPixel);

        /// <summary>Zooms by <paramref name="factor"/> about <paramref name="x"/>, keeping the time there where it is.</summary>
        private void Zoom(double factor, double x)
        {
            if (Bounds.Width <= 0)
                return;

            var (first, last) = Extent();
            double at = TimeAt(x);
            double max = Math.Max(MinSecondsPerPixel, (last - first) * 2 / Bounds.Width);
            _secondsPerPixel = Math.Clamp(_secondsPerPixel * factor, MinSecondsPerPixel, max);
            _start = at - (x * _secondsPerPixel);
            _hasView = true;
            InvalidateVisual();
        }

        private Lane? LaneAt(Point at) => Lanes().FirstOrDefault(l => at.Y >= l.Top && at.Y < l.Bottom);

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var point = e.GetCurrentPoint(this);
            if (!point.Properties.IsLeftButtonPressed)
                return;

            _drag = LaneAt(point.Position) is { IsSong: true } ? DragKind.Offset : DragKind.Pan;
            _lastX = point.Position.X;
            e.Pointer.Capture(this);
            if (LaneScrub.IsFine(e.KeyModifiers))
                _fine.Start(point.Position);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var at = e.GetPosition(this);
            if (_drag == DragKind.None)
            {
                Cursor = LaneAt(at) is { IsSong: true } ? MoveCursor : LaneScrub.HandCursor;
                _hoverX = at.X;
                InvalidateVisual();
                return;
            }

            // Fine, the pointer is held and the drag goes by how far it would have moved, the finer for it.
            double moved;
            if (LaneScrub.IsFine(e.KeyModifiers))
            {
                _fine.Start(at);
                moved = _fine.Move(at).X / Math.Max(1, FineFactor);
            }
            else
            {
                _fine.Stop();
                moved = at.X - _lastX;
                _hoverX = at.X;
            }

            _lastX = at.X;
            if (_drag == DragKind.Offset)
                OffsetSeconds += moved * _secondsPerPixel;
            else
                _start -= moved * _secondsPerPixel;
            _hasView = true;
            InvalidateVisual();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (_drag == DragKind.None)
                return;

            e.Pointer.Capture(null);
            EndDrag();
            e.Handled = true;
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            EndDrag();
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            if (_drag != DragKind.None)
                return;
            _hoverX = double.NaN;
            InvalidateVisual();
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            double x = e.GetPosition(this).X;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.Delta.X != 0)
            {
                double pan = e.Delta.X != 0 ? e.Delta.X : e.Delta.Y;
                _start -= pan * PanPixels * _secondsPerPixel;
                _hasView = true;
                InvalidateVisual();
            }
            else
            {
                Zoom(Math.Pow(ZoomStep, -e.Delta.Y), x);
            }

            e.Handled = true;
        }

        private void EndDrag()
        {
            _drag = DragKind.None;
            _fine.Stop();
        }

        /// <summary>One lane: a song's audio or notes, from <paramref name="Top"/> for <paramref name="Height"/>.</summary>
        private sealed record Lane(bool IsSong, string Label, Waveform? Waveform, TimelineNotes? Notes, double Top, double Height)
        {
            public double Bottom => Top + Height;

            public Rect Rect(double width) => new(0, Top, width, Height);
        }
    }
}
