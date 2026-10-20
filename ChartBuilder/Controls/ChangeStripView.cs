using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ChartShared.Core.Charting;

namespace ChartBuilder.Controls
{
    /// <summary>What a mark on the <see cref="ChangeStripView"/> stands for, which gives its colour.</summary>
    public enum ChangeStripKind
    {
        Removal,
        Reshape,
        Sustain,
        Forcing,
        FindingInfo,
        FindingWarning,
        FindingError,
    }

    /// <summary>A mark on the <see cref="ChangeStripView"/>: where, in seconds of audio, on which of its rows, and of what.</summary>
    public readonly record struct ChangeStripMark(double Seconds, int Row, ChangeStripKind Kind);

    /// <summary>The colours of the <see cref="ChangeStripView"/>'s marks, which the Reduce dialog's filters show beside them.</summary>
    public static class ChangeStripPalette
    {
        public static readonly IBrush Removal = new ImmutableSolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x96));
        public static readonly IBrush Reshape = new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0x4F, 0xA3));
        public static readonly IBrush Sustain = new ImmutableSolidColorBrush(Color.FromRgb(0x2D, 0xD4, 0xBF));
        public static readonly IBrush Forcing = new ImmutableSolidColorBrush(Color.FromRgb(0xA7, 0x8B, 0xFA));
        public static readonly IBrush FindingInfo = new ImmutableSolidColorBrush(Color.FromArgb(0xC0, 0x93, 0xC5, 0xFD));
        public static readonly IBrush FindingWarning = new ImmutableSolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
        public static readonly IBrush FindingError = new ImmutableSolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));

        public static IBrush Of(ChangeStripKind kind) => kind switch
        {
            ChangeStripKind.Removal => Removal,
            ChangeStripKind.Reshape => Reshape,
            ChangeStripKind.Sustain => Sustain,
            ChangeStripKind.Forcing => Forcing,
            ChangeStripKind.FindingWarning => FindingWarning,
            ChangeStripKind.FindingError => FindingError,
            _ => FindingInfo,
        };
    }

    /// <summary>
    /// The Reduce dialog's change strip: a row of marks for each draft where it changed, and one for the lint findings, over
    /// a stretch of the song with its measure lines, and a playhead that clicking or dragging moves and the wheel moves a bar
    /// at a time. A press on a mark puts the playhead <see cref="Lead"/> before it, so its notes are about to reach the
    /// strike line.
    /// </summary>
    /// <remarks>The playhead is drawn by a layer of its own over the rest, so it moves without the marks being drawn again.</remarks>
    public sealed class ChangeStripView : Control
    {
        private const double RulerHeight = 14;
        private const double RowHeight = 12;
        private const double RowGap = 3;
        private const double GutterWidth = 58;
        private const double MinLabelSpacing = 56;
        private const double MinMeasureSpacing = 4;

        // How near a mark, in pixels, a press is taken as on it.
        private const double MarkTolerance = 4;

        // How near a bar line the playhead counts as on it, so one put there by the wheel doesn't stick to it.
        private const double BarTolerance = 1e-3;

        private readonly PlayheadLayer _playhead;
        private bool _frameRequested;

        // The wheel turn not yet taken as a notch.
        private double _wheel;

        public static readonly StyledProperty<double> StartProperty =
            AvaloniaProperty.Register<ChangeStripView, double>(nameof(Start));

        public static readonly StyledProperty<double> EndProperty =
            AvaloniaProperty.Register<ChangeStripView, double>(nameof(End), 1);

        public static readonly StyledProperty<double> PositionProperty =
            AvaloniaProperty.Register<ChangeStripView, double>(nameof(Position), defaultBindingMode: BindingMode.TwoWay);

        public static readonly StyledProperty<bool> IsPlayingProperty =
            AvaloniaProperty.Register<ChangeStripView, bool>(nameof(IsPlaying));

        public static readonly StyledProperty<Func<TimeSpan>?> ClockProperty =
            AvaloniaProperty.Register<ChangeStripView, Func<TimeSpan>?>(nameof(Clock));

        public static readonly StyledProperty<bool> WheelUpIsLaterProperty =
            AvaloniaProperty.Register<ChangeStripView, bool>(nameof(WheelUpIsLater), true);

        public static readonly StyledProperty<IReadOnlyList<TimelineBeat>?> BeatsProperty =
            AvaloniaProperty.Register<ChangeStripView, IReadOnlyList<TimelineBeat>?>(nameof(Beats));

        public static readonly StyledProperty<IReadOnlyList<ChangeStripMark>?> MarksProperty =
            AvaloniaProperty.Register<ChangeStripView, IReadOnlyList<ChangeStripMark>?>(nameof(Marks));

        public static readonly StyledProperty<IReadOnlyList<string>?> RowLabelsProperty =
            AvaloniaProperty.Register<ChangeStripView, IReadOnlyList<string>?>(nameof(RowLabels));

        public static readonly StyledProperty<double> CurrentProperty =
            AvaloniaProperty.Register<ChangeStripView, double>(nameof(Current), double.NaN);

        public static readonly StyledProperty<double> LeadProperty =
            AvaloniaProperty.Register<ChangeStripView, double>(nameof(Lead));

        public static readonly StyledProperty<IBrush?> TickBrushProperty =
            AvaloniaProperty.Register<ChangeStripView, IBrush?>(nameof(TickBrush));

        public static readonly StyledProperty<IBrush?> MeasureBrushProperty =
            AvaloniaProperty.Register<ChangeStripView, IBrush?>(nameof(MeasureBrush));

        public static readonly StyledProperty<IBrush?> RowBrushProperty =
            AvaloniaProperty.Register<ChangeStripView, IBrush?>(nameof(RowBrush));

        public static readonly StyledProperty<IBrush?> PlayheadBrushProperty =
            AvaloniaProperty.Register<ChangeStripView, IBrush?>(nameof(PlayheadBrush));

        static ChangeStripView()
        {
            AffectsRender<ChangeStripView>(StartProperty, EndProperty, BeatsProperty, MarksProperty, RowLabelsProperty, TickBrushProperty, MeasureBrushProperty, RowBrushProperty);
            AffectsMeasure<ChangeStripView>(RowLabelsProperty);
            ClipToBoundsProperty.OverrideDefaultValue<ChangeStripView>(true);
        }

        public ChangeStripView()
        {
            _playhead = new PlayheadLayer(this);
            VisualChildren.Add(_playhead);
        }

        /// <summary>Where the strip starts, in seconds of audio.</summary>
        public double Start
        {
            get => GetValue(StartProperty);
            set => SetValue(StartProperty, value);
        }

        /// <summary>Where the strip ends, in seconds of audio.</summary>
        public double End
        {
            get => GetValue(EndProperty);
            set => SetValue(EndProperty, value);
        }

        /// <summary>The playhead while stopped, and where scrubbing puts it, in seconds of audio.</summary>
        public double Position
        {
            get => GetValue(PositionProperty);
            set => SetValue(PositionProperty, value);
        }

        /// <summary>Whether the song is playing, so the playhead follows <see cref="Clock"/> every frame.</summary>
        public bool IsPlaying
        {
            get => GetValue(IsPlayingProperty);
            set => SetValue(IsPlayingProperty, value);
        }

        /// <summary>Where the audio is heard to be now: read every frame while playing.</summary>
        public Func<TimeSpan>? Clock
        {
            get => GetValue(ClockProperty);
            set => SetValue(ClockProperty, value);
        }

        /// <summary>Whether the wheel turned up moves the playhead later, as along the lanes; otherwise turned down does.</summary>
        public bool WheelUpIsLater
        {
            get => GetValue(WheelUpIsLaterProperty);
            set => SetValue(WheelUpIsLaterProperty, value);
        }

        /// <summary>The chart's beat and measure lines, in order; only the measures are drawn.</summary>
        public IReadOnlyList<TimelineBeat>? Beats
        {
            get => GetValue(BeatsProperty);
            set => SetValue(BeatsProperty, value);
        }

        /// <summary>The marks, each on one of the rows <see cref="RowLabels"/> names.</summary>
        public IReadOnlyList<ChangeStripMark>? Marks
        {
            get => GetValue(MarksProperty);
            set => SetValue(MarksProperty, value);
        }

        /// <summary>Each row's name, top to bottom.</summary>
        public IReadOnlyList<string>? RowLabels
        {
            get => GetValue(RowLabelsProperty);
            set => SetValue(RowLabelsProperty, value);
        }

        /// <summary>Where the change last gone to is, in seconds of audio, marked on the ruler; NaN for none.</summary>
        public double Current
        {
            get => GetValue(CurrentProperty);
            set => SetValue(CurrentProperty, value);
        }

        /// <summary>How far before a mark a press on it puts the playhead, in seconds.</summary>
        public double Lead
        {
            get => GetValue(LeadProperty);
            set => SetValue(LeadProperty, value);
        }

        /// <summary>The ruler, its times and the row names.</summary>
        public IBrush? TickBrush
        {
            get => GetValue(TickBrushProperty);
            set => SetValue(TickBrushProperty, value);
        }

        public IBrush? MeasureBrush
        {
            get => GetValue(MeasureBrushProperty);
            set => SetValue(MeasureBrushProperty, value);
        }

        /// <summary>Each row's band, behind its marks.</summary>
        public IBrush? RowBrush
        {
            get => GetValue(RowBrushProperty);
            set => SetValue(RowBrushProperty, value);
        }

        public IBrush? PlayheadBrush
        {
            get => GetValue(PlayheadBrushProperty);
            set => SetValue(PlayheadBrushProperty, value);
        }

        /// <summary>
        /// The index of the mark on <paramref name="row"/> nearest <paramref name="seconds"/>, no further than
        /// <paramref name="tolerance"/> seconds from it; -1 for none.
        /// </summary>
        internal static int MarkNear(IReadOnlyList<ChangeStripMark> marks, int row, double seconds, double tolerance)
        {
            int found = -1;
            double nearest = tolerance;
            for (int i = 0; i < marks.Count; i++)
            {
                if (marks[i].Row != row)
                    continue;
                double distance = Math.Abs(marks[i].Seconds - seconds);
                if (distance <= nearest)
                {
                    nearest = distance;
                    found = i;
                }
            }

            return found;
        }

        protected override Size MeasureOverride(Size availableSize) =>
            new(0, RulerHeight + (Math.Max(1, RowLabels?.Count ?? 1) * (RowHeight + RowGap)) + RowGap);

        protected override Size ArrangeOverride(Size finalSize)
        {
            _playhead.Arrange(new Rect(finalSize));
            return finalSize;
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == StartProperty || change.Property == EndProperty || change.Property == PlayheadBrushProperty
                || change.Property == PositionProperty || change.Property == CurrentProperty)
                _playhead.InvalidateVisual();
            if (change.Property == IsPlayingProperty)
            {
                _playhead.InvalidateVisual();
                RequestFrame();
            }
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            RequestFrame();
        }

        public override void Render(DrawingContext context)
        {
            var bounds = new Rect(Bounds.Size);
            context.FillRectangle(Brushes.Transparent, bounds); // Hit-testable everywhere, for scrubbing.
            if (End <= Start || bounds.Width <= GutterWidth)
                return;

            var labels = RowLabels ?? [];
            if (RowBrush is { } rowBrush)
            {
                for (int row = 0; row < labels.Count; row++)
                    context.FillRectangle(rowBrush, new Rect(GutterWidth, RowTop(row), bounds.Width - GutterWidth, RowHeight));
            }

            DrawMeasures(context, bounds);
            DrawRuler(context, bounds);
            if (TickBrush is { } brush)
            {
                for (int row = 0; row < labels.Count; row++)
                {
                    var text = new FormattedText(labels[row], CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 10, brush)
                    {
                        MaxTextWidth = GutterWidth - 6,
                        MaxLineCount = 1,
                        Trimming = TextTrimming.CharacterEllipsis,
                    };
                    context.DrawText(text, new Point(2, RowTop(row) + ((RowHeight - text.Height) / 2)));
                }
            }

            DrawMarks(context, bounds, labels.Count);
        }

        // Each row's marks, one to a pixel column of each kind, findings and removals first so the changes show over them.
        private void DrawMarks(DrawingContext context, Rect bounds, int rows)
        {
            if (Marks is not { Count: > 0 } marks)
                return;

            foreach (var kind in (ReadOnlySpan<ChangeStripKind>)
                [ChangeStripKind.FindingInfo, ChangeStripKind.FindingWarning, ChangeStripKind.FindingError, ChangeStripKind.Removal, ChangeStripKind.Sustain, ChangeStripKind.Forcing, ChangeStripKind.Reshape])
            {
                var brush = ChangeStripPalette.Of(kind);
                var drawn = new HashSet<(int Row, double X)>();
                foreach (var mark in marks)
                {
                    if (mark.Kind != kind || mark.Row < 0 || mark.Row >= rows || mark.Seconds < Start || mark.Seconds > End)
                        continue;
                    double x = Math.Round(XOf(mark.Seconds, bounds.Width));
                    if (drawn.Add((mark.Row, x)))
                        context.FillRectangle(brush, new Rect(x - 1, RowTop(mark.Row) + 1, 2, RowHeight - 2));
                }
            }
        }

        private void DrawMeasures(DrawingContext context, Rect bounds)
        {
            if (MeasureBrush is not { } brush || Beats is not { Count: > 0 } beats)
                return;

            int measures = 0;
            foreach (var beat in beats)
            {
                if (beat.IsMeasure && beat.Seconds >= Start && beat.Seconds <= End)
                    measures++;
            }

            // Too close together, they'd turn the strip into a block.
            if (measures > 1 && (bounds.Width - GutterWidth) / (measures - 1) < MinMeasureSpacing)
                return;

            var pen = new ImmutablePen(brush.ToImmutable(), 1);
            foreach (var beat in beats)
            {
                if (!beat.IsMeasure || beat.Seconds < Start || beat.Seconds > End)
                    continue;
                double x = Math.Round(XOf(beat.Seconds, bounds.Width)) + 0.5;
                context.DrawLine(pen, new Point(x, RulerHeight), new Point(x, bounds.Height));
            }
        }

        private void DrawRuler(DrawingContext context, Rect bounds)
        {
            if (TickBrush is not { } brush)
                return;

            var pen = new ImmutablePen(brush.ToImmutable(), 1);
            context.DrawLine(pen, new Point(GutterWidth, RulerHeight - 0.5), new Point(bounds.Width, RulerHeight - 0.5));

            // A labelled mark at a round step that leaves room for its label.
            double length = End - Start;
            double pixelsPerSecond = (bounds.Width - GutterWidth) / length;
            double step = 3600;
            foreach (double candidate in (ReadOnlySpan<double>)[1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600])
            {
                if (candidate * pixelsPerSecond >= MinLabelSpacing)
                {
                    step = candidate;
                    break;
                }
            }

            for (double t = Math.Ceiling(Math.Max(0, Start) / step) * step; t <= End; t += step)
            {
                double x = Math.Round(XOf(t, bounds.Width)) + 0.5;
                context.DrawLine(pen, new Point(x, RulerHeight - 6), new Point(x, RulerHeight));
                var label = new FormattedText(SongTime.Format(TimeSpan.FromSeconds(t)), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 10, brush);
                context.DrawText(label, new Point(x + 3, 0));
            }
        }

        private static double RowTop(int row) => RulerHeight + RowGap + (row * (RowHeight + RowGap));

        /// <summary>Has the playhead drawn again every frame while playing.</summary>
        private void RequestFrame()
        {
            if (_frameRequested || !IsPlaying || TopLevel.GetTopLevel(this) is not { } top)
                return;

            _frameRequested = true;
            top.RequestAnimationFrame(_ =>
            {
                _frameRequested = false;
                if (Math.Round(XOf(PlayheadSeconds, Bounds.Width)) != _playhead.Drawn)
                    _playhead.InvalidateVisual();
                RequestFrame();
            });
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || End <= Start || Bounds.Width <= GutterWidth)
                return;

            // A press on a mark goes to it; anywhere else, the playhead goes where it's pressed and follows a drag.
            var point = e.GetPosition(this);
            int row = (int)Math.Floor((point.Y - RulerHeight - RowGap) / (RowHeight + RowGap));
            double tolerance = MarkTolerance / PixelsPerSecond(Bounds.Width);
            if (point.X >= GutterWidth && row >= 0 && Marks is { } marks && MarkNear(marks, row, TimeOf(point.X, Bounds.Width), tolerance) is int found and >= 0)
            {
                Position = Math.Max(0, marks[found].Seconds - Lead);
                e.Handled = true;
                return;
            }

            e.Pointer.Capture(this);
            Scrub(e);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (ReferenceEquals(e.Pointer.Captured, this))
                Scrub(e);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (ReferenceEquals(e.Pointer.Captured, this))
                e.Pointer.Capture(null);
        }

        private void Scrub(PointerEventArgs e) => Position = Math.Max(0, Math.Clamp(TimeOf(e.GetPosition(this).X, Bounds.Width), Start, End));

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);

            // A bar a notch, later the way WheelUpIsLater says, as along the lanes; four with Shift held, as on the timeline.
            bool fast = (e.KeyModifiers & KeyModifiers.Shift) != 0;
            double raw = e.Delta.Y != 0 ? e.Delta.Y : (fast ? e.Delta.X : 0);
            int bars = Editing.EditorCanvas.TakeScrollStep(ref _wheel, Editing.EditorCanvas.TowardLater(raw, WheelUpIsLater), fast);
            if (bars != 0 && Beats is { } beats && TimelineView.StepBars(beats, Position, bars) is { } seconds)
                Position = Math.Max(0, seconds);

            e.Handled = true;
        }

        /// <summary>Where the playhead is drawn: at the clock while playing, otherwise at <see cref="Position"/>.</summary>
        private double PlayheadSeconds => IsPlaying && Clock is { } clock ? clock().TotalSeconds : Position;

        private double PixelsPerSecond(double width) => Math.Max(1e-6, (width - GutterWidth) / Math.Max(1e-6, End - Start));

        private double XOf(double seconds, double width) => GutterWidth + ((seconds - Start) * PixelsPerSecond(width));

        private double TimeOf(double x, double width) => Start + ((x - GutterWidth) / PixelsPerSecond(width));

        /// <summary>The playhead's line, and a caret on the ruler at the change last gone to, over the rest.</summary>
        private sealed class PlayheadLayer(ChangeStripView owner) : Control
        {
            private static readonly IBrush CaretBrush = ChangeStripPalette.Reshape;
            private IPen? _pen;
            private IBrush? _penBrush;

            /// <summary>The pixel column the playhead was last drawn at; NaN before it has been.</summary>
            public double Drawn { get; private set; } = double.NaN;

            public override void Render(DrawingContext context)
            {
                double width = Bounds.Width;
                Drawn = double.NaN;
                if (owner.PlayheadBrush is not { } brush || owner.End <= owner.Start || width <= GutterWidth)
                    return;

                if (!ReferenceEquals(_penBrush, brush))
                {
                    _pen = new ImmutablePen(brush.ToImmutable(), 1.5);
                    _penBrush = brush;
                }

                if (!double.IsNaN(owner.Current) && owner.Current >= owner.Start && owner.Current <= owner.End)
                {
                    double at = Math.Round(owner.XOf(owner.Current, width)) + 0.5;
                    var caret = new StreamGeometry();
                    using (var g = caret.Open())
                    {
                        g.BeginFigure(new Point(at - 4, RulerHeight - 6), true);
                        g.LineTo(new Point(at + 4, RulerHeight - 6));
                        g.LineTo(new Point(at, RulerHeight));
                        g.EndFigure(true);
                    }

                    context.DrawGeometry(CaretBrush, null, caret);
                }

                double seconds = owner.PlayheadSeconds;
                if (seconds < owner.Start || seconds > owner.End)
                    return;
                Drawn = Math.Round(owner.XOf(seconds, width));
                context.DrawLine(_pen!, new Point(Drawn + 0.5, 0), new Point(Drawn + 0.5, Bounds.Height));
            }
        }
    }
}
