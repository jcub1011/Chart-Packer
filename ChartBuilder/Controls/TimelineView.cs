using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using ChartBuilder.Controls.Editing;
using ChartBuilder.ViewModels;
using ChartShared.Audio.Waveforms;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Validation;
using ChartShared.UI.Controls;
using ChartShared.UI.Controls.Highway;

namespace ChartBuilder.Controls
{
    /// <summary>A point of the star power meter over the song, as the meter ribbon draws it.</summary>
    public readonly record struct StarPowerMeterPoint(double Seconds, double Meter, bool Active);

    /// <summary>A hard passage and whether the kept star power can be activated going into it.</summary>
    public readonly record struct StarPowerSpikeMark(double Seconds, bool Charged);

    /// <summary>
    /// What the generation window's timeline adds to the editor's: for star power, the meter ribbon and the hard passages;
    /// and which proposed region is selected.
    /// </summary>
    public sealed record StarPowerTimelineExtras(
        IReadOnlyList<StarPowerMeterPoint> Meter,
        IReadOnlyList<StarPowerSpikeMark> Spikes,
        int? SelectedProposal,
        double ActivationThreshold = 0.5);

    /// <summary>
    /// The editor's timeline: second marks, the whole mix's waveform, the chart's beat and measure lines, its tempo and
    /// time signature changes, and a playhead that clicking or dragging moves (scrubbing), and the wheel moves a bar at a
    /// time. It's also the overview strip (charter-spec C3 item 2): the sections, the notes, faded, star power and solos
    /// of the lane in focus, and a box round the span the lanes show.
    /// </summary>
    /// <remarks>
    /// <para>The playhead is drawn by a layer of its own over the rest, so it moves without the ruler, waveform and beat
    /// lines being drawn again. While <see cref="IsPlaying"/>, it follows <see cref="Clock"/>, which is smoother than
    /// <see cref="Position"/>; otherwise <see cref="Position"/>. It's drawn again only when it, or the span box, moves to
    /// another pixel: each time it is, the screen under it is drawn again too, the timeline's picture included.</para>
    /// <para>The ruler, waveform, overview and beat lines are drawn once into a picture at the screen's pixels, and drawn
    /// again only when what they show or the timeline's size changes, so the screen under the playhead is one picture to
    /// draw again rather than thousands of lines.</para>
    /// <para>Beat lines closer than a few pixels are left out, then measure lines, so a long song doesn't turn into a block.</para>
    /// </remarks>
    public sealed class TimelineView : Control
    {
        private const double RulerHeight = 18;
        private const double MinLabelSpacing = 56;
        private const double MinBeatSpacing = 6;
        private const double MinMeasureSpacing = 3;

        // The beat and measure lines, see-through, so the waveform and notes show through them.
        private const double BeatOpacity = 0.5;

        // Under the ruler, a row of its own for the sections' names, so nothing is drawn behind them; everything else is
        // drawn from under it (BodyTop).
        private const double SectionRowHeight = 14;
        private const double BodyTop = RulerHeight + SectionRowHeight;

        // A section's name is drawn this far right of its line and stops this far short of the next one; with less room
        // than this, it isn't drawn.
        private const double SectionLabelPadding = 3;
        private const double MinSectionLabelWidth = 10;

        // The overview's marks: a band at the top for each row of the phrase rail, in the rail's colours, and under them the
        // lane's notes, faded, in its rows and colours: a short gem each, a fainter bar for a sustain, and a faint line down
        // every row for a kick or an open note.
        private const double BandHeight = 3;
        private const double BandGap = 1;
        private const byte BandAlpha = 0xC0;
        private const double NoteWidth = 2;
        private const double NoteOpacity = 0.75;
        private const double SustainOpacity = 0.35;
        private const double AcrossOpacity = 0.4;

        // Each phrase rail row's band, and each note colour at each opacity, made the first time it's drawn: rendering is on
        // the UI thread only.
        private static readonly Dictionary<RailKind, IBrush> BandBrushes = [];
        private static readonly Dictionary<(uint Color, double Opacity), IBrush> NoteBrushes = [];
        private static readonly IPen SectionPen = new ImmutablePen(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF).ToUInt32(), 1);

        // The chart's tempo and time signature changes, in the tempo markers' amber, down the timeline.
        private static readonly IPen TempoPen = new ImmutablePen(Color.FromArgb(0xC0, 0xFF, 0xB0, 0x40).ToUInt32(), 1);

        // The loop region's start and end, as lines down the timeline, in the brush it's given.
        private const double LoopLineThickness = 2;

        // The synchronized region, outlined, and the passages synced with it, filled, behind the other marks.
        private static readonly IBrush SyncedBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x60, 0xFF, 0x7A, 0x1A));
        private static readonly IBrush SyncRegionBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x30, 0xFF, 0x7A, 0x1A));
        private static readonly IPen SyncRegionPen = new ImmutablePen(Color.FromArgb(0xE0, 0xFF, 0x7A, 0x1A).ToUInt32(), 1);

        // The passages Find found, as the synced passages are but in light blue, the one the cursor is at outlined like the region.
        private static readonly IBrush FoundBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x60, 0x7F, 0xD0, 0xFF));
        private static readonly IBrush FoundCurrentBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x30, 0x7F, 0xD0, 0xFF));
        private static readonly IPen FoundCurrentPen = new ImmutablePen(Color.FromArgb(0xE0, 0x7F, 0xD0, 0xFF).ToUInt32(), 1);
        // A synchronized region nothing matched, hashed in yellow instead.
        private static readonly IBrush SyncUnmatchedBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x22, 0xE6, 0xC8, 0x00));
        private static readonly IPen SyncUnmatchedPen = new ImmutablePen(Color.FromArgb(0xE0, 0xE6, 0xC8, 0x00).ToUInt32(), 1);
        private static readonly IPen SyncHashPen = new ImmutablePen(Color.FromArgb(0x90, 0xE6, 0xC8, 0x00).ToUInt32(), 1);

        // How far apart a region nothing matched's hash lines are drawn, in pixels.
        private const double SyncHashGap = 5;

        // The ghosts of a generator's proposals, over everything else, so they're easy to find: a kept one's a faint amber
        // column the height of the overview, capped solid; a dropped one's only a faint outline.
        private const double ProposalCap = 4;
        private const double ProposalMinWidth = 2;
        private static readonly IBrush KeptProposalBrush = new ImmutableSolidColorBrush(HighwayPalette.WithAlpha(PhraseColors.Proposal, 0x40));
        private static readonly IBrush ProposalCapBrush = new ImmutableSolidColorBrush(PhraseColors.Proposal);
        private static readonly IPen DroppedProposalPen = new ImmutablePen(HighwayPalette.WithAlpha(PhraseColors.Proposal, 0x60).ToUInt32(), 1);

        // Star power the window is replacing, drawn muted.
        private static readonly IBrush ReplacedBandBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x40, 0x9E, 0x9E, 0x9E));

        // The lint marks: a short tick up from the ruler's foot, coloured by severity, errors drawn last so they show.
        private const double MarkHeight = 6;
        private static readonly IBrush ErrorMarkBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
        private static readonly IBrush WarningMarkBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
        private static readonly IBrush InfoMarkBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xC0, 0x93, 0xC5, 0xFD));

        // The generation window's extras, for star power: the meter ribbon along the bottom, and the hard passages as
        // triangles at the top. A fills window has neither, and uses them only to outline the selected proposal.
        private const double MeterHeight = 20;
        private static readonly IBrush MeterBackgroundBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xB0, 0x08, 0x08, 0x10));
        private static readonly IBrush MeterFillBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x80, 0x7F, 0xE9, 0xFF));
        private static readonly IPen MeterPen = new ImmutablePen(Color.FromArgb(0xE0, 0x7F, 0xE9, 0xFF).ToUInt32(), 1);
        private static readonly IPen MeterThresholdPen = new ImmutablePen(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF).ToUInt32(), 1, new ImmutableDashStyle([3, 3], 0));
        private static readonly IBrush SpikeChargedBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x40));
        private static readonly IBrush SpikeUnchargedBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xB0, 0x40));
        private static readonly IPen SpikePen = new ImmutablePen(Color.FromRgb(0xFF, 0xB0, 0x40).ToUInt32(), 1);
        private static readonly IPen SelectedProposalPen = new ImmutablePen(Colors.White.ToUInt32(), 2);

        private readonly PlayheadLayer _playhead;
        private Pen? _tickPen;
        private Pen? _beatPen;
        private Pen? _measurePen;
        private Pen? _waveformPen;
        private Pen? _loopPen;
        private bool _frameRequested;

        // The wheel turn not yet taken as a notch.
        private double _wheel;

        // Each section's stretch of the row under the ruler, from its line to the next, as last drawn; the section whose
        // name is the tip now, null for none; the tip the timeline was given, shown again off a section; whether the tip
        // open now was opened by Avalonia for that tip, so is left open off a section; and whether the timeline is setting
        // the tip itself, so its own change isn't taken for a new tip.
        private readonly List<(double Left, double Right, string Name)> _sectionSpans = [];
        private string? _tipSection;
        private object? _ownTip;
        private bool _ownTipOpen;
        private bool _settingTip;
        private DispatcherTimer? _tipTimer;

        // Where the pointer is over the timeline, in its own coordinates; null while it's elsewhere. A tip opens above the
        // timeline at this x, so it never covers the waveform and playhead being scrubbed.
        private Point? _pointer;

        // The picture of everything but the playhead, the size and scaling it was drawn at, and whether what it shows has
        // changed since.
        private RenderTargetBitmap? _content;
        private Size _contentSize;
        private double _contentScaling;
        private bool _isContentStale = true;

        public static readonly StyledProperty<double> PositionProperty =
            AvaloniaProperty.Register<TimelineView, double>(nameof(Position), defaultBindingMode: BindingMode.TwoWay);

        public static readonly StyledProperty<double> LengthProperty =
            AvaloniaProperty.Register<TimelineView, double>(nameof(Length), 1);

        public static readonly StyledProperty<bool> IsPlayingProperty =
            AvaloniaProperty.Register<TimelineView, bool>(nameof(IsPlaying));

        public static readonly StyledProperty<Func<TimeSpan>?> ClockProperty =
            AvaloniaProperty.Register<TimelineView, Func<TimeSpan>?>(nameof(Clock));

        public static readonly StyledProperty<bool> WheelUpIsLaterProperty =
            AvaloniaProperty.Register<TimelineView, bool>(nameof(WheelUpIsLater), true);

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

        public static readonly StyledProperty<IReadOnlyList<FindingMark>?> FindingMarksProperty =
            AvaloniaProperty.Register<TimelineView, IReadOnlyList<FindingMark>?>(nameof(FindingMarks));

        public static readonly StyledProperty<LoopRegion?> LoopProperty =
            AvaloniaProperty.Register<TimelineView, LoopRegion?>(nameof(Loop));

        public static readonly StyledProperty<IBrush?> LoopBrushProperty =
            AvaloniaProperty.Register<TimelineView, IBrush?>(nameof(LoopBrush));

        public static readonly StyledProperty<StarPowerTimelineExtras?> StarPowerExtrasProperty =
            AvaloniaProperty.Register<TimelineView, StarPowerTimelineExtras?>(nameof(StarPowerExtras));

        // What the picture of the ruler, waveform, overview, beat lines and loop lines shows: when one changes, it's drawn again.
        private static readonly AvaloniaProperty[] ContentProperties =
        [
            LengthProperty, WaveformProperty, BeatsProperty, WaveformBrushProperty, TickBrushProperty, BeatBrushProperty, MeasureBrushProperty,
            OverviewProperty, FindingMarksProperty, LoopProperty, LoopBrushProperty, StarPowerExtrasProperty,
        ];

        static TimelineView()
        {
            AffectsRender<TimelineView>(ContentProperties);
            ClipToBoundsProperty.OverrideDefaultValue<TimelineView>(true);
        }

        public TimelineView()
        {
            _playhead = new PlayheadLayer(this);
            VisualChildren.Add(_playhead);
            ToolTip.SetPlacement(this, PlacementMode.Custom);
            ToolTip.SetCustomPopupPlacementCallback(this, placement => TipPlacement.PlaceAbove(placement, this, _pointer));
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

        /// <summary>Whether the wheel turned up moves the playhead later, as along the lanes; otherwise turned down does.</summary>
        public bool WheelUpIsLater
        {
            get => GetValue(WheelUpIsLaterProperty);
            set => SetValue(WheelUpIsLaterProperty, value);
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

        /// <summary>The loop region, marked with blue lines at its start and end (charter-spec C10 item 2); null for none.</summary>
        public LoopRegion? Loop
        {
            get => GetValue(LoopProperty);
            set => SetValue(LoopProperty, value);
        }

        /// <summary>The line drawn at each end of the loop region.</summary>
        public IBrush? LoopBrush
        {
            get => GetValue(LoopBrushProperty);
            set => SetValue(LoopBrushProperty, value);
        }

        /// <summary>The meter ribbon, hard passages and selected proposal the star power window adds, or null for none.</summary>
        public StarPowerTimelineExtras? StarPowerExtras
        {
            get => GetValue(StarPowerExtrasProperty);
            set => SetValue(StarPowerExtrasProperty, value);
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
            else if (change.Property == LoopBrushProperty)
                _loopPen = null;

            // A tip given while a section's name is showing is the one to show again off it; the name stays for now.
            if (change.Property == ToolTip.TipProperty && _tipSection is not null && !_settingTip)
            {
                _ownTip = change.NewValue;
                SetTip(_tipSection);
            }

            if (Array.IndexOf(ContentProperties, change.Property) >= 0)
                _isContentStale = true;

            if (change.Property == LengthProperty || change.Property == PlayheadBrushProperty || change.Property == SpanBrushProperty)
                _playhead.InvalidateVisual();
            else if (change.Property == PositionProperty || change.Property == VisibleStartProperty || change.Property == VisibleEndProperty)
                InvalidatePlayheadIfMoved();
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

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _content?.Dispose();
            _content = null;
            _isContentStale = true;
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
            if (Length <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
                return;

            double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
            if (_isContentStale || _content is null || _contentSize != bounds.Size || _contentScaling != scaling)
                DrawContent(bounds, scaling);

            // Pixel for pixel, as it was drawn at the screen's pixels: smoothing would only blur it.
            var pixels = _content!.PixelSize;
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
                context.DrawImage(_content, new Rect(0, 0, pixels.Width, pixels.Height), new Rect(0, 0, pixels.Width / scaling, pixels.Height / scaling));
        }

        /// <summary>Draws the ruler, waveform, overview, beat lines and lint marks into the picture again, at the screen's pixels.</summary>
        private void DrawContent(Rect bounds, double scaling)
        {
            // A frame already drawn with the old one keeps what it needs of it.
            _content?.Dispose();
            var pixels = new PixelSize(Math.Max(1, (int)Math.Ceiling(bounds.Width * scaling)), Math.Max(1, (int)Math.Ceiling(bounds.Height * scaling)));
            _content = new RenderTargetBitmap(pixels, new Vector(96, 96));
            _contentSize = bounds.Size;
            _contentScaling = scaling;
            _isContentStale = false;
            _sectionSpans.Clear();

            using var context = _content.CreateDrawingContext();
            using (context.PushTransform(Matrix.CreateScale(scaling, scaling)))
            {
                DrawWaveform(context, bounds);
                DrawOverview(context, bounds);
                DrawStarPowerExtras(context, bounds);
                DrawBeats(context, bounds);
                DrawLoop(context, bounds);
                DrawRuler(context, bounds);
                DrawFindingMarks(context, bounds);
            }
        }

        /// <summary>The lint findings of the lane in focus, and the song-wide ones (charter-spec C3 item 2), as marks on the ruler.</summary>
        public IReadOnlyList<FindingMark>? FindingMarks
        {
            get => GetValue(FindingMarksProperty);
            set => SetValue(FindingMarksProperty, value);
        }

        private void DrawFindingMarks(DrawingContext context, Rect bounds)
        {
            if (FindingMarks is not { Count: > 0 } marks)
                return;

            foreach (var severity in new[] { Severity.Info, Severity.Warning, Severity.Error })
            {
                var brush = severity switch
                {
                    Severity.Error => ErrorMarkBrush,
                    Severity.Warning => WarningMarkBrush,
                    _ => InfoMarkBrush,
                };
                double lastX = double.MinValue;
                foreach (var mark in marks)
                {
                    if (mark.Severity != severity)
                        continue;
                    double x = Math.Round(XOf(mark.Seconds, bounds.Width));
                    if (x == lastX)
                        continue;
                    context.FillRectangle(brush, new Rect(x - 1, RulerHeight - MarkHeight, 2, MarkHeight));
                    lastX = x;
                }
            }
        }

        /// <summary>
        /// The lane in focus over the whole song: its notes, a band of its phrases for each row of its phrase rail, the
        /// chart's tempo and time signature changes, and the song's sections; the synchronized region and the passages
        /// synced with it; and over them all, the ghosts of a generation window's proposals.
        /// </summary>
        private void DrawOverview(DrawingContext context, Rect bounds)
        {
            if (Overview is not { } overview)
                return;

            double width = bounds.Width;
            double top = BodyTop;
            foreach (var (start, end, isRegion, unmatched) in overview.Synced)
            {
                var band = new Rect(XOf(start, width), top, Math.Max(1, XOf(end, width) - XOf(start, width)), bounds.Bottom - top);
                if (!isRegion)
                    context.FillRectangle(SyncedBrush, band);
                else if (unmatched)
                    DrawSyncHash(context, band);
                else
                    context.DrawRectangle(SyncRegionBrush, SyncRegionPen, band.Deflate(0.5));
            }

            foreach (var (start, end, current) in overview.Found)
            {
                var band = new Rect(XOf(start, width), top, Math.Max(1, XOf(end, width) - XOf(start, width)), bounds.Bottom - top);
                context.FillRectangle(FoundBrush, band);
                if (current)
                    context.DrawRectangle(FoundCurrentBrush, FoundCurrentPen, band.Deflate(0.5));
            }

            DrawNotes(context, overview, width, top + (overview.Rows.Count * (BandHeight + BandGap)), bounds.Bottom);

            foreach (var phrase in overview.Phrases)
            {
                int row = IndexOf(overview.Rows, phrase.Row);
                if (row < 0)
                    continue;
                double y = top + (row * (BandHeight + BandGap));
                context.FillRectangle(phrase.Replaced ? ReplacedBandBrush : BandBrush(phrase.Row), new Rect(XOf(phrase.Start, width), y, Math.Max(1, XOf(phrase.End, width) - XOf(phrase.Start, width)), BandHeight));
            }

            DrawTempoChanges(context, overview, width, bounds);

            // Each section's line, from the row under the ruler down through the rest, and its name in that row, cut short
            // with an ellipsis before the next section's line; its whole name is the tip over its stretch of the row.
            var labelBrush = TickBrush ?? Brushes.Gray;
            var sections = overview.Sections;
            for (int i = 0; i < sections.Count; i++)
            {
                double x = Math.Round(XOf(sections[i].Seconds, width)) + 0.5;
                double next = i + 1 < sections.Count ? Math.Round(XOf(sections[i + 1].Seconds, width)) + 0.5 : width;
                context.DrawLine(SectionPen, new Point(x, RulerHeight), new Point(x, bounds.Bottom));
                _sectionSpans.Add((x, next, sections[i].Name));

                double room = next - x - (2 * SectionLabelPadding);
                if (room < MinSectionLabelWidth)
                    continue;
                var text = new FormattedText(sections[i].Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 10, labelBrush)
                {
                    MaxTextWidth = room,
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis,
                };
                context.DrawText(text, new Point(x + SectionLabelPadding, RulerHeight + ((SectionRowHeight - text.Height) / 2)));
            }

            // The ghosts last, kept ones over the dropped, each wide enough to see on a long song.
            foreach (bool kept in new[] { false, true })
            {
                foreach (var proposal in overview.Proposals)
                {
                    if (proposal.Kept != kept)
                        continue;
                    double x = XOf(proposal.Start, width);
                    double w = Math.Max(ProposalMinWidth, XOf(proposal.End, width) - x);
                    var column = new Rect(x, top, w, bounds.Bottom - top);
                    if (kept)
                    {
                        context.FillRectangle(KeptProposalBrush, column);
                        context.FillRectangle(ProposalCapBrush, new Rect(x, top, w, ProposalCap));
                    }
                    else
                    {
                        context.DrawRectangle(null, DroppedProposalPen, column.Deflate(0.5));
                    }
                }
            }
        }

        /// <summary>
        /// The generation window's extras: for star power, the meter ribbon along the bottom with its activation threshold
        /// and the hard passages as triangles along the top of the overview; and the selected proposal outlined.
        /// </summary>
        private void DrawStarPowerExtras(DrawingContext context, Rect bounds)
        {
            if (StarPowerExtras is not { } extras)
                return;

            double width = bounds.Width;

            // The meter ribbon, filled under its curve.
            if (extras.Meter.Count > 1)
            {
                double top = bounds.Bottom - MeterHeight;

                // The ribbon is drawn over the meter's samples, so they can be fewer than a pixel apart.
                context.FillRectangle(MeterBackgroundBrush, new Rect(0, top, width, MeterHeight));
                var area = new StreamGeometry();
                using (var geometry = area.Open())
                {
                    geometry.BeginFigure(new Point(XOf(extras.Meter[0].Seconds, width), bounds.Bottom), true);
                    foreach (var sample in extras.Meter)
                        geometry.LineTo(new Point(XOf(sample.Seconds, width), top + (MeterHeight * (1 - sample.Meter))));
                    geometry.LineTo(new Point(XOf(extras.Meter[^1].Seconds, width), bounds.Bottom));
                    geometry.EndFigure(true);
                }

                context.DrawGeometry(MeterFillBrush, null, area);
                for (int i = 1; i < extras.Meter.Count; i++)
                {
                    var previous = extras.Meter[i - 1];
                    var sample = extras.Meter[i];
                    context.DrawLine(MeterPen,
                        new Point(XOf(previous.Seconds, width), top + (MeterHeight * (1 - previous.Meter))),
                        new Point(XOf(sample.Seconds, width), top + (MeterHeight * (1 - sample.Meter))));
                }

                double threshold = top + (MeterHeight * (1 - extras.ActivationThreshold));
                context.DrawLine(MeterThresholdPen, new Point(0, threshold), new Point(width, threshold));
            }

            // Hard passages: filled when charged, hollow when the kept star power falls short.
            foreach (var spike in extras.Spikes)
            {
                double x = Math.Round(XOf(spike.Seconds, width));
                var triangle = new StreamGeometry();
                using (var geometry = triangle.Open())
                {
                    geometry.BeginFigure(new Point(x, BodyTop + 7), true);
                    geometry.LineTo(new Point(x - 4, BodyTop));
                    geometry.LineTo(new Point(x + 4, BodyTop));
                    geometry.EndFigure(true);
                }

                context.DrawGeometry(spike.Charged ? SpikeChargedBrush : SpikeUnchargedBrush, SpikePen, triangle);
            }

            // The selected proposal, outlined so it's found in the whole song.
            if (extras.SelectedProposal is { } selected && Overview is { } overview && selected >= 0 && selected < overview.Proposals.Count)
            {
                var proposal = overview.Proposals[selected];
                double x = XOf(proposal.Start, width);
                double w = Math.Max(ProposalMinWidth, XOf(proposal.End, width) - x);
                context.DrawRectangle(null, SelectedProposalPen, new Rect(x, BodyTop, w, bounds.Bottom - BodyTop).Deflate(0.5));
            }
        }

        /// <summary>
        /// The loop region's start and end, as blue lines from the ruler down through the timeline (charter-spec C10 item
        /// 2), so the stretch the song repeats is found at a glance.
        /// </summary>
        private void DrawLoop(DrawingContext context, Rect bounds)
        {
            if (Loop is not { } loop || LoopBrush is not { } brush)
                return;
            var pen = _loopPen ??= new Pen(brush, LoopLineThickness);
            foreach (double seconds in (ReadOnlySpan<double>)[loop.Start, loop.End])
            {
                if (seconds < 0 || seconds > Length)
                    continue;
                double x = Math.Round(XOf(seconds, bounds.Width)) + 0.5;
                context.DrawLine(pen, new Point(x, RulerHeight), new Point(x, bounds.Bottom));
            }
        }

        /// <summary>
        /// The chart's tempo and time signature changes, as lines in the tempo markers' amber from the ruler down through
        /// the overview, so the whole song's timing can be found at a glance.
        /// </summary>
        private void DrawTempoChanges(DrawingContext context, OverviewData overview, double width, Rect bounds)
        {
            foreach (double seconds in overview.TempoChanges)
            {
                double x = Math.Round(XOf(seconds, width)) + 0.5;
                context.DrawLine(TempoPen, new Point(x, RulerHeight), new Point(x, bounds.Bottom));
            }
        }

        /// <summary>A synchronized region nothing matched, hashed in yellow.</summary>
        private static void DrawSyncHash(DrawingContext context, Rect band)
        {
            context.DrawRectangle(SyncUnmatchedBrush, SyncUnmatchedPen, band.Deflate(0.5));
            using (context.PushClip(band))
            {
                for (double x = band.Left - band.Height; x < band.Right; x += SyncHashGap)
                    context.DrawLine(SyncHashPen, new Point(x, band.Bottom), new Point(x + band.Height, band.Top));
            }
        }

        /// <summary>
        /// The lane's notes, faded, between <paramref name="top"/> and <paramref name="bottom"/> in a row each for its frets,
        /// pads or columns; those across every row behind the rest. On a long song only the first gem at each pixel of a
        /// row is drawn, and of a half-height one (a six-fret black fret played with a white one) of its top half, as the
        /// rest would cover it; and a sustain only where the row's sustains before it didn't reach, so faded ones don't
        /// pile up.
        /// </summary>
        private void DrawNotes(DrawingContext context, OverviewData overview, double width, double top, double bottom)
        {
            int rows = overview.NoteRows;
            if (rows <= 0 || overview.Notes.Count == 0 || bottom - top < rows)
                return;

            double rowHeight = (bottom - top) / rows;
            double inset = Math.Min(1, rowHeight / 4);
            double gemHeight = rowHeight - (2 * inset);

            // Where a gem was last drawn in each row, and across them all at the end; where a top-half gem was last drawn
            // in each row; and how far right each row's sustains reach.
            var lastX = new double[rows + 1];
            var lastTopX = new double[rows];
            var tailEnd = new double[rows];
            foreach (bool across in (ReadOnlySpan<bool>)[true, false])
            {
                Array.Fill(lastX, double.MinValue);
                Array.Fill(lastTopX, double.MinValue);
                Array.Fill(tailEnd, double.MinValue);
                foreach (var note in overview.Notes)
                {
                    if ((note.Row < 0) != across || note.Row >= rows)
                        continue;

                    double x = Math.Round(XOf(note.Start, width));
                    if (across)
                    {
                        if (x != lastX[rows])
                            context.FillRectangle(BrushOf(note.Color, AcrossOpacity), new Rect(x, top, 1, bottom - top));
                        lastX[rows] = x;
                        continue;
                    }

                    double y = top + (note.Row * rowHeight) + inset;
                    double end = XOf(note.End, width);
                    if (end - x >= NoteWidth)
                    {
                        double from = Math.Max(x, tailEnd[note.Row]);
                        if (end - from >= 1)
                        {
                            context.FillRectangle(BrushOf(note.Color, SustainOpacity), new Rect(from, y + (gemHeight / 4), end - from, gemHeight / 2));
                            tailEnd[note.Row] = end;
                        }
                    }

                    var last = note.Top ? lastTopX : lastX;
                    if (x != last[note.Row])
                    {
                        double height = note.Top ? gemHeight / 2 : gemHeight;
                        context.FillRectangle(BrushOf(note.Color, NoteOpacity), new Rect(x - (NoteWidth / 2), y, NoteWidth, height));
                    }

                    last[note.Row] = x;
                }
            }
        }

        /// <summary>A brush of <paramref name="color"/> at <paramref name="opacity"/>, made once for each, as a lane has only a few.</summary>
        private static IBrush BrushOf(Color color, double opacity)
        {
            var key = (color.ToUInt32(), opacity);
            if (!NoteBrushes.TryGetValue(key, out var brush))
                NoteBrushes[key] = brush = new ImmutableSolidColorBrush(color, opacity);
            return brush;
        }

        private static IBrush BandBrush(RailKind row)
        {
            if (!BandBrushes.TryGetValue(row, out var brush))
            {
                brush = new ImmutableSolidColorBrush(HighwayPalette.WithAlpha(PhraseColors.Of(row), BandAlpha));
                BandBrushes[row] = brush;
            }

            return brush;
        }

        private static int IndexOf(IReadOnlyList<RailKind> rows, RailKind row)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i] == row)
                    return i;
            }

            return -1;
        }

        /// <summary>Each pixel column's lowest and highest level, and how loud it is, scaled so the loudest fills the height.</summary>
        private void DrawWaveform(DrawingContext context, Rect bounds)
        {
            if (WaveformBrush is not { } brush || Waveform is not { ReadCount: > 0 } waveform)
                return;

            double top = BodyTop;
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

            using (context.PushOpacity(BeatOpacity))
            {
                foreach (var beat in beats)
                {
                    var pen = beat.IsMeasure ? (showMeasures ? _measurePen : null) : (showBeats ? _beatPen : null);
                    if (pen is null)
                        continue;

                    double x = Math.Round(XOf(beat.Seconds, bounds.Width)) + 0.5;
                    context.DrawLine(pen, new Point(x, BodyTop), new Point(x, bounds.Height));
                }
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
                InvalidatePlayheadIfMoved();
                RequestFrame();
            });
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            ShowSectionTip(null); // Scrubbing, the name's tip goes, and comes again as the pointer next moves over a section.
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;

            e.Pointer.Capture(this);
            Scrub(e);
            e.Handled = true;
        }

        protected override void OnPointerEntered(PointerEventArgs e)
        {
            base.OnPointerEntered(e);
            _pointer = e.GetPosition(this);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            _pointer = e.GetPosition(this);
            if (ReferenceEquals(e.Pointer.Captured, this))
                Scrub(e);
            else
                ShowSectionTip(SectionAt(e.GetPosition(this)));
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            _pointer = null;
            ShowSectionTip(null);
        }

        /// <summary>The name of the section whose stretch of the row under the ruler <paramref name="at"/> is in, or null.</summary>
        private string? SectionAt(Point at)
        {
            if (at.Y < RulerHeight || at.Y >= BodyTop)
                return null;
            foreach (var (left, right, name) in _sectionSpans)
            {
                if (at.X >= left && at.X < right)
                    return name;
            }

            return null;
        }

        /// <summary>
        /// Has the tip show <paramref name="section"/>'s whole name, or the timeline's own tip again for null.
        /// </summary>
        /// <remarks>
        /// Avalonia's tooltip service only times a tip in as the pointer comes onto a control with one, and a click puts it
        /// away until the pointer leaves, so the timeline, always being clicked, opens the name's tip itself, after the
        /// usual delay. An open tip just shows the new name. Off the sections, a tip it opened is put away, and one Avalonia
        /// opened for the timeline's own tip shows that again, as Avalonia wouldn't open it again until the pointer leaves.
        /// </remarks>
        private void ShowSectionTip(string? section)
        {
            if (section == _tipSection)
                return;
            if (_tipSection is null)
            {
                _ownTip = ToolTip.GetTip(this);
                _ownTipOpen = ToolTip.GetIsOpen(this);
            }

            _tipSection = section;
            SetTip(section ?? _ownTip);

            _tipTimer?.Stop();
            if (section is null)
            {
                if (!_ownTipOpen)
                    ToolTip.SetIsOpen(this, false);
                return;
            }

            if (ToolTip.GetIsOpen(this))
                return;
            if (_tipTimer is null)
            {
                _tipTimer = new DispatcherTimer();
                _tipTimer.Tick += OnTipTimer;
            }

            _tipTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(0, ToolTip.GetShowDelay(this)));
            _tipTimer.Start();
        }

        private void OnTipTimer(object? sender, EventArgs e)
        {
            _tipTimer?.Stop();
            if (_tipSection is not null && IsPointerOver)
            {
                _ownTipOpen = false;
                ToolTip.SetIsOpen(this, true);
            }
        }

        private void SetTip(object? tip)
        {
            _settingTip = true;
            try
            {
                ToolTip.SetTip(this, tip);
            }
            finally
            {
                _settingTip = false;
            }
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

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);

            // A bar a notch, later the way WheelUpIsLater says, as along the lanes; four with Shift held, which some
            // platforms report as a sideways scroll. At most one notch per wheel event.
            bool fast = (e.KeyModifiers & KeyModifiers.Shift) != 0;
            double raw = e.Delta.Y != 0 ? e.Delta.Y : (fast ? e.Delta.X : 0);
            int bars = WheelSteps.TakeScrollStep(ref _wheel, WheelSteps.TowardLater(raw, WheelUpIsLater), fast);
            if (bars != 0 && Beats is { } beats && TimelineBeats.StepBars(beats, Position, bars) is { } seconds)
                Position = Math.Clamp(seconds, 0, Length);

            e.Handled = true;
        }

        /// <summary>Where the playhead is drawn: at the clock while playing, otherwise at <see cref="Position"/>.</summary>
        private double PlayheadSeconds => IsPlaying && Clock is { } clock ? clock().TotalSeconds : Position;

        /// <summary>Has the playhead layer drawn again if the playhead or the span box is now at another pixel.</summary>
        private void InvalidatePlayheadIfMoved()
        {
            if (Bounds.Width <= 0 || Length <= 0 || PlaceOfPlayhead(Bounds.Width) != _playhead.Drawn)
                _playhead.InvalidateVisual();
        }

        /// <summary>Where the playhead's line is drawn, and the span box's edges, in whole pixels across <paramref name="width"/>.</summary>
        private (double Line, double SpanLeft, double SpanRight) PlaceOfPlayhead(double width) =>
            (Math.Round(XOf(Math.Clamp(PlayheadSeconds, 0, Length), width)) + 0.5,
             Math.Round(XOf(Math.Clamp(VisibleStart, 0, Length), width)),
             Math.Round(XOf(Math.Clamp(VisibleEnd, 0, Length), width)));

        private double XOf(double seconds, double width) => seconds / Length * width;

        private double TimeOf(double x, double width) => x / width * Length;

        /// <summary>The playhead's line, over the rest, which its owner redraws only when it moves.</summary>
        private sealed class PlayheadLayer(TimelineView owner) : Control
        {
            // Immutable, so drawing them doesn't make them resources of the frame.
            private IPen? _pen;
            private IBrush? _penBrush;
            private IBrush? _span;
            private IBrush? _spanBrush;

            /// <summary>Where the playhead and the span box were last drawn: nowhere, before they've been drawn.</summary>
            public (double Line, double SpanLeft, double SpanRight) Drawn { get; private set; } = (double.NaN, double.NaN, double.NaN);

            public override void Render(DrawingContext context)
            {
                double width = Bounds.Width;
                if (owner.PlayheadBrush is not { } brush || owner.Length <= 0 || width <= 0)
                {
                    Drawn = (double.NaN, double.NaN, double.NaN);
                    return;
                }

                if (!ReferenceEquals(_penBrush, brush))
                {
                    _pen = new ImmutablePen(brush.ToImmutable(), 1.5);
                    _penBrush = brush;
                }

                var place = owner.PlaceOfPlayhead(width);
                Drawn = place;

                // The span the lanes show, boxed, so the overview says where in the song the editor is.
                if (owner.SpanBrush is { } span && owner.VisibleEnd > owner.VisibleStart)
                {
                    if (!ReferenceEquals(_spanBrush, span))
                    {
                        _span = span.ToImmutable();
                        _spanBrush = span;
                    }

                    context.FillRectangle(_span!, new Rect(place.SpanLeft, 0, Math.Max(2, place.SpanRight - place.SpanLeft), Bounds.Height));
                }

                context.DrawLine(_pen!, new Point(place.Line, 0), new Point(place.Line, Bounds.Height));
            }
        }
    }
}
