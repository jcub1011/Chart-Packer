using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ChartBuilder.ViewModels;
using ChartPacker.Audio.Waveforms;
using ChartPacker.Core.Charting;

namespace ChartBuilder.Controls
{
    /// <summary>A beat line on the timeline, in audio time (charter-spec §4.4.1).</summary>
    public readonly record struct TimelineBeat(double Seconds, bool IsMeasure);

    /// <summary>
    /// The editor's timeline: second marks, the whole mix's waveform, the chart's beat and measure lines, and a playhead
    /// that clicking or dragging moves (scrubbing). It's also the overview strip (charter-spec C3 item 2): the sections,
    /// the note density, star power and solos of the lane in focus, and a box round the span the lanes show.
    /// </summary>
    /// <remarks>
    /// <para>The playhead is drawn by a layer of its own over the rest, so it moves without the ruler, waveform and beat
    /// lines being drawn again. While <see cref="IsPlaying"/>, it's drawn every frame at <see cref="Clock"/>, which is
    /// smoother than <see cref="Position"/>; otherwise at <see cref="Position"/>.</para>
    /// <para>Beat lines closer than a few pixels are left out, then measure lines, so a long song doesn't turn into a block.</para>
    /// </remarks>
    public sealed class TimelineView : Control
    {
        private const double RulerHeight = 18;
        private const double MinLabelSpacing = 56;
        private const double MinBeatSpacing = 6;
        private const double MinMeasureSpacing = 3;

        // The overview's marks: star power and solo bands under the ruler, and the note density along the bottom.
        private const double BandHeight = 3;
        private const double DensityFraction = 0.3;
        private static readonly IBrush StarPowerBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xC0, 0x7F, 0xE9, 0xFF));
        private static readonly IBrush SoloBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xC0, 0xC0, 0x84, 0xFC));
        private static readonly IBrush DensityBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xB0, 0x40));
        private static readonly IPen SectionPen = new ImmutablePen(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF).ToUInt32(), 1);

        private readonly PlayheadLayer _playhead;
        private Pen? _tickPen;
        private Pen? _beatPen;
        private Pen? _measurePen;
        private Pen? _waveformPen;
        private bool _frameRequested;

        public static readonly StyledProperty<double> PositionProperty =
            AvaloniaProperty.Register<TimelineView, double>(nameof(Position), defaultBindingMode: BindingMode.TwoWay);

        public static readonly StyledProperty<double> LengthProperty =
            AvaloniaProperty.Register<TimelineView, double>(nameof(Length), 1);

        public static readonly StyledProperty<bool> IsPlayingProperty =
            AvaloniaProperty.Register<TimelineView, bool>(nameof(IsPlaying));

        public static readonly StyledProperty<Func<TimeSpan>?> ClockProperty =
            AvaloniaProperty.Register<TimelineView, Func<TimeSpan>?>(nameof(Clock));

        public static readonly StyledProperty<Waveform?> WaveformProperty =
            AvaloniaProperty.Register<TimelineView, Waveform?>(nameof(Waveform));

        public static readonly StyledProperty<IReadOnlyList<TimelineBeat>?> BeatsProperty =
            AvaloniaProperty.Register<TimelineView, IReadOnlyList<TimelineBeat>?>(nameof(Beats));

        public static readonly StyledProperty<IBrush?> WaveformBrushProperty =
            AvaloniaProperty.Register<TimelineView, IBrush?>(nameof(WaveformBrush));

        public static readonly StyledProperty<IBrush?> TickBrushProperty =
            AvaloniaProperty.Register<TimelineView, IBrush?>(nameof(TickBrush));

        public static readonly StyledProperty<IBrush?> BeatBrushProperty =
            AvaloniaProperty.Register<TimelineView, IBrush?>(nameof(BeatBrush));

        public static readonly StyledProperty<IBrush?> MeasureBrushProperty =
            AvaloniaProperty.Register<TimelineView, IBrush?>(nameof(MeasureBrush));

        public static readonly StyledProperty<IBrush?> PlayheadBrushProperty =
            AvaloniaProperty.Register<TimelineView, IBrush?>(nameof(PlayheadBrush));

        public static readonly StyledProperty<OverviewData?> OverviewProperty =
            AvaloniaProperty.Register<TimelineView, OverviewData?>(nameof(Overview));

        public static readonly StyledProperty<double> VisibleStartProperty =
            AvaloniaProperty.Register<TimelineView, double>(nameof(VisibleStart));

        public static readonly StyledProperty<double> VisibleEndProperty =
            AvaloniaProperty.Register<TimelineView, double>(nameof(VisibleEnd));

        public static readonly StyledProperty<IBrush?> SpanBrushProperty =
            AvaloniaProperty.Register<TimelineView, IBrush?>(nameof(SpanBrush));

        static TimelineView()
        {
            AffectsRender<TimelineView>(
                LengthProperty, WaveformProperty, BeatsProperty, WaveformBrushProperty, TickBrushProperty, BeatBrushProperty, MeasureBrushProperty, OverviewProperty);
            ClipToBoundsProperty.OverrideDefaultValue<TimelineView>(true);
        }

        public TimelineView()
        {
            _playhead = new PlayheadLayer(this);
            VisualChildren.Add(_playhead);
        }

        /// <summary>The playhead while stopped, and where scrubbing puts it, in seconds of audio.</summary>
        public double Position
        {
            get => GetValue(PositionProperty);
            set => SetValue(PositionProperty, value);
        }

        /// <summary>How many seconds the timeline spans.</summary>
        public double Length
        {
            get => GetValue(LengthProperty);
            set => SetValue(LengthProperty, value);
        }

        /// <summary>Whether the song is playing, so the playhead follows <see cref="Clock"/> every frame.</summary>
        public bool IsPlaying
        {
            get => GetValue(IsPlayingProperty);
            set => SetValue(IsPlayingProperty, value);
        }

        /// <summary>Where the audio is heard to be now, as smoothly as the player can say: read every frame while playing.</summary>
        public Func<TimeSpan>? Clock
        {
            get => GetValue(ClockProperty);
            set => SetValue(ClockProperty, value);
        }

        /// <summary>The mix's waveform, as much as has been read; none until there's audio.</summary>
        public Waveform? Waveform
        {
            get => GetValue(WaveformProperty);
            set => SetValue(WaveformProperty, value);
        }

        /// <summary>The chart's beat and measure lines, in order.</summary>
        public IReadOnlyList<TimelineBeat>? Beats
        {
            get => GetValue(BeatsProperty);
            set => SetValue(BeatsProperty, value);
        }

        public IBrush? WaveformBrush
        {
            get => GetValue(WaveformBrushProperty);
            set => SetValue(WaveformBrushProperty, value);
        }

        /// <summary>The second marks and their labels.</summary>
        public IBrush? TickBrush
        {
            get => GetValue(TickBrushProperty);
            set => SetValue(TickBrushProperty, value);
        }

        public IBrush? BeatBrush
        {
            get => GetValue(BeatBrushProperty);
            set => SetValue(BeatBrushProperty, value);
        }

        public IBrush? MeasureBrush
        {
            get => GetValue(MeasureBrushProperty);
            set => SetValue(MeasureBrushProperty, value);
        }

        public IBrush? PlayheadBrush
        {
            get => GetValue(PlayheadBrushProperty);
            set => SetValue(PlayheadBrushProperty, value);
        }

        /// <summary>What's drawn of the lane in focus over the whole song; none for nothing.</summary>
        public OverviewData? Overview
        {
            get => GetValue(OverviewProperty);
            set => SetValue(OverviewProperty, value);
        }

        /// <summary>Where the span the lanes show starts, in seconds of audio.</summary>
        public double VisibleStart
        {
            get => GetValue(VisibleStartProperty);
            set => SetValue(VisibleStartProperty, value);
        }

        /// <summary>Where the span the lanes show ends, in seconds of audio; no later than its start draws no box.</summary>
        public double VisibleEnd
        {
            get => GetValue(VisibleEndProperty);
            set => SetValue(VisibleEndProperty, value);
        }

        /// <summary>The box round the span the lanes show.</summary>
        public IBrush? SpanBrush
        {
            get => GetValue(SpanBrushProperty);
            set => SetValue(SpanBrushProperty, value);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == TickBrushProperty)
                _tickPen = null;
            else if (change.Property == BeatBrushProperty)
                _beatPen = null;
            else if (change.Property == MeasureBrushProperty)
                _measurePen = null;
            else if (change.Property == WaveformBrushProperty)
                _waveformPen = null;

            if (change.Property == PositionProperty || change.Property == LengthProperty || change.Property == PlayheadBrushProperty
                || change.Property == VisibleStartProperty || change.Property == VisibleEndProperty || change.Property == SpanBrushProperty)
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

        protected override Size ArrangeOverride(Size finalSize)
        {
            _playhead.Arrange(new Rect(finalSize));
            return finalSize;
        }

        public override void Render(DrawingContext context)
        {
            var bounds = new Rect(Bounds.Size);
            context.FillRectangle(Brushes.Transparent, bounds); // Hit-testable everywhere, for scrubbing.
            if (Length <= 0 || bounds.Width <= 0)
                return;

            DrawWaveform(context, bounds);
            DrawOverview(context, bounds);
            DrawBeats(context, bounds);
            DrawRuler(context, bounds);
        }

        /// <summary>The lane in focus over the whole song: its note density, star power and solos, and the song's sections.</summary>
        private void DrawOverview(DrawingContext context, Rect bounds)
        {
            if (Overview is not { } overview)
                return;

            double width = bounds.Width;
            double top = RulerHeight;
            double densityHeight = (bounds.Height - top) * DensityFraction;
            int columns = overview.Density.Count;
            for (int i = 0; i < columns; i++)
            {
                float density = overview.Density[i];
                if (density <= 0)
                    continue;
                double x = (double)i / columns * width;
                double height = Math.Max(1, density * densityHeight);
                context.FillRectangle(DensityBrush, new Rect(x, bounds.Bottom - height, Math.Max(1, width / columns), height));
            }

            foreach (var (start, end) in overview.StarPower)
                context.FillRectangle(StarPowerBrush, new Rect(XOf(start, width), top, Math.Max(1, XOf(end, width) - XOf(start, width)), BandHeight));
            foreach (var (start, end) in overview.Solos)
                context.FillRectangle(SoloBrush, new Rect(XOf(start, width), top + BandHeight + 1, Math.Max(1, XOf(end, width) - XOf(start, width)), BandHeight));

            var labelBrush = TickBrush ?? Brushes.Gray;
            double lastLabelEnd = double.MinValue;
            foreach (var section in overview.Sections)
            {
                double x = Math.Round(XOf(section.Seconds, width)) + 0.5;
                context.DrawLine(SectionPen, new Point(x, top), new Point(x, bounds.Bottom));
                if (x < lastLabelEnd)
                    continue;
                var text = new FormattedText(section.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 10, labelBrush);
                context.DrawText(text, new Point(x + 3, top + (2 * BandHeight) + 2));
                lastLabelEnd = x + text.Width + 6;
            }
        }

        /// <summary>Each pixel column's lowest and highest level, and how loud it is, scaled so the loudest fills the height.</summary>
        private void DrawWaveform(DrawingContext context, Rect bounds)
        {
            if (WaveformBrush is not { } brush || Waveform is not { ReadCount: > 0 } waveform)
                return;

            double top = RulerHeight;
            double height = bounds.Height - top;
            if (height <= 4)
                return;

            var pen = _waveformPen ??= new Pen(brush, 1);
            double middle = top + height / 2;
            double scale = (height / 2 - 1) / Math.Max(waveform.Peak, 1e-3f);
            double bucketsPerPixel = Length * waveform.BucketsPerSecond / bounds.Width;

            using (context.PushOpacity(0.55))
            {
                for (int x = 0; x < (int)bounds.Width; x++)
                {
                    var (low, high, _) = Levels(waveform, x, bucketsPerPixel);
                    if (high - low > 0)
                        context.DrawLine(pen, new Point(x + 0.5, middle - high * scale), new Point(x + 0.5, middle - low * scale + 0.5));
                }
            }

            for (int x = 0; x < (int)bounds.Width; x++)
            {
                var (_, _, rms) = Levels(waveform, x, bucketsPerPixel);
                if (rms > 0)
                    context.DrawLine(pen, new Point(x + 0.5, middle - rms * scale), new Point(x + 0.5, middle + rms * scale + 0.5));
            }
        }

        private static (double Low, double High, double Rms) Levels(Waveform waveform, int x, double bucketsPerPixel)
        {
            int first = (int)Math.Floor(x * bucketsPerPixel);
            int last = Math.Max(first + 1, (int)Math.Floor((x + 1) * bucketsPerPixel));
            return waveform.LevelsOf(first, last);
        }

        private void DrawBeats(DrawingContext context, Rect bounds)
        {
            if (Beats is not { Count: > 0 } beats)
                return;

            // How far apart they are on average: close enough together, the beats go, then the measures.
            int measures = 0;
            foreach (var beat in beats)
            {
                if (beat.IsMeasure)
                    measures++;
            }

            double pixels = XOf(beats[^1].Seconds - beats[0].Seconds, bounds.Width);
            double spacing = beats.Count > 1 ? pixels / (beats.Count - 1) : double.PositiveInfinity;
            double measureSpacing = measures > 1 ? pixels / (measures - 1) : double.PositiveInfinity;
            bool showBeats = spacing >= MinBeatSpacing && BeatBrush is not null;
            bool showMeasures = measureSpacing >= MinMeasureSpacing && MeasureBrush is not null;

            if (showBeats)
                _beatPen ??= new Pen(BeatBrush!, 1);
            if (showMeasures)
                _measurePen ??= new Pen(MeasureBrush!, 1);

            foreach (var beat in beats)
            {
                var pen = beat.IsMeasure ? (showMeasures ? _measurePen : null) : (showBeats ? _beatPen : null);
                if (pen is null)
                    continue;

                double x = Math.Round(XOf(beat.Seconds, bounds.Width)) + 0.5;
                context.DrawLine(pen, new Point(x, RulerHeight), new Point(x, bounds.Height));
            }
        }

        private void DrawRuler(DrawingContext context, Rect bounds)
        {
            if (TickBrush is not { } brush)
                return;

            var pen = _tickPen ??= new Pen(brush, 1);
            context.DrawLine(pen, new Point(0, RulerHeight - 0.5), new Point(bounds.Width, RulerHeight - 0.5));

            // A labelled mark at a round step that leaves room for its label, and a short mark between each.
            double step = LabelStep(bounds.Width);
            for (double t = 0; t <= Length; t += step)
            {
                double x = Math.Round(XOf(t, bounds.Width)) + 0.5;
                context.DrawLine(pen, new Point(x, RulerHeight - 8), new Point(x, RulerHeight));

                double between = Math.Round(XOf(t + step / 2, bounds.Width)) + 0.5;
                context.DrawLine(pen, new Point(between, RulerHeight - 4), new Point(between, RulerHeight));

                var label = new FormattedText(
                    SongTime.Format(TimeSpan.FromSeconds(t)),
                    CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 10, brush);
                context.DrawText(label, new Point(x + 3, 1));
            }
        }

        /// <summary>
        /// The smallest round step, from a second up to an hour, that keeps labels at least
        /// <see cref="MinLabelSpacing"/> apart, or as many whole hours as that takes on a very long timeline.
        /// </summary>
        private double LabelStep(double width)
        {
            double pixelsPerSecond = width / Length;
            foreach (double step in (ReadOnlySpan<double>)[1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600])
            {
                if (step * pixelsPerSecond >= MinLabelSpacing)
                    return step;
            }

            return Math.Ceiling(MinLabelSpacing / pixelsPerSecond / 3600) * 3600;
        }

        /// <summary>Has the playhead drawn again next frame, and every frame after while playing.</summary>
        private void RequestFrame()
        {
            if (_frameRequested || !IsPlaying || TopLevel.GetTopLevel(this) is not { } top)
                return;

            _frameRequested = true;
            top.RequestAnimationFrame(_ =>
            {
                _frameRequested = false;
                _playhead.InvalidateVisual();
                RequestFrame();
            });
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;

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

        private void Scrub(PointerEventArgs e)
        {
            if (Bounds.Width > 0)
                Position = Math.Clamp(TimeOf(e.GetPosition(this).X, Bounds.Width), 0, Length);
        }

        /// <summary>Where the playhead is drawn: at the clock while playing, otherwise at <see cref="Position"/>.</summary>
        private double PlayheadSeconds => IsPlaying && Clock is { } clock ? clock().TotalSeconds : Position;

        private double XOf(double seconds, double width) => seconds / Length * width;

        private double TimeOf(double x, double width) => x / width * Length;

        /// <summary>The playhead's line, over the rest, which its owner redraws only when it moves.</summary>
        private sealed class PlayheadLayer(TimelineView owner) : Control
        {
            private Pen? _pen;
            private IBrush? _penBrush;

            public override void Render(DrawingContext context)
            {
                double width = Bounds.Width;
                if (owner.PlayheadBrush is not { } brush || owner.Length <= 0 || width <= 0)
                    return;

                if (!ReferenceEquals(_penBrush, brush))
                {
                    _pen = new Pen(brush, 1.5);
                    _penBrush = brush;
                }

                // The span the lanes show, boxed, so the overview says where in the song the editor is.
                if (owner.SpanBrush is { } span && owner.VisibleEnd > owner.VisibleStart)
                {
                    double left = owner.XOf(Math.Clamp(owner.VisibleStart, 0, owner.Length), width);
                    double right = owner.XOf(Math.Clamp(owner.VisibleEnd, 0, owner.Length), width);
                    context.FillRectangle(span, new Rect(left, 0, Math.Max(2, right - left), Bounds.Height));
                }

                double x = Math.Round(owner.XOf(Math.Clamp(owner.PlayheadSeconds, 0, owner.Length), width)) + 0.5;
                context.DrawLine(_pen!, new Point(x, 0), new Point(x, Bounds.Height));
            }
        }
    }
}
