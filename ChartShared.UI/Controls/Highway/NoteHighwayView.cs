using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ChartShared.Audio.Waveforms;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using ChartShared.UI.Controls;

namespace ChartShared.UI.Controls.Highway
{
    /// <summary>How a note on a highway is marked out from the rest, as the Reduce dialog's preview marks what a draft changed.</summary>
    [Flags]
    public enum HighwayNoteMark : byte
    {
        None = 0,

        /// <summary>Drawn faint: a note left out of what's compared with it.</summary>
        Faded = 1,

        /// <summary>Ringed in <see cref="HighwayPalette.ChangeMark"/>: a note that changed.</summary>
        Flagged = 2,
    }

    /// <summary>The kind of band drawn behind a highway's notes: star power, or a proposed phrase of a generator's window.</summary>
    public enum HighwayBandKind
    {
        /// <summary>A star power phrase.</summary>
        StarPower,

        /// <summary>A proposed fill of Generate fills, in the proposal amber.</summary>
        Proposal,

        /// <summary>A solo.</summary>
        Solo,

        /// <summary>A drum fill, or star-power activation lane.</summary>
        DrumFill,

        /// <summary>A drum roll.</summary>
        Roll,

        /// <summary>A drum trill.</summary>
        Trill,

        /// <summary>A big rock ending.</summary>
        BigRockEnding,

        /// <summary>A section the drums flip in, as the games flip them in disco.</summary>
        DiscoFlip,
    }

    /// <summary>A phrase drawn behind a highway's notes, in song seconds.</summary>
    /// <param name="HasEnd">Whether it ends where <paramref name="End"/> says; one still going on is drawn to the far end of the highway.</param>
    public readonly record struct HighwayBand(double Start, double End, HighwayBandKind Kind = HighwayBandKind.StarPower, bool HasEnd = true)
    {
        /// <summary>The band a phrase of the chart makes, with its ticks timed by <paramref name="tempo"/> and <paramref name="delay"/> as the notes' are.</summary>
        public static HighwayBand Of(PhraseSpan span, TempoMap tempo, TimeSpan delay)
        {
            var kind = span.Kind switch
            {
                null => HighwayBandKind.DiscoFlip,
                PhraseKind.StarPower => HighwayBandKind.StarPower,
                PhraseKind.Solo => HighwayBandKind.Solo,
                PhraseKind.DrumFill => HighwayBandKind.DrumFill,
                PhraseKind.Roll => HighwayBandKind.Roll,
                PhraseKind.Trill => HighwayBandKind.Trill,
                _ => HighwayBandKind.BigRockEnding,
            };
            double start = tempo.SecondsAt(span.Tick) + delay.TotalSeconds;
            return new HighwayBand(start, span.HasEnd ? tempo.SecondsAt(span.EndTick) + delay.TotalSeconds : start, kind, span.HasEnd);
        }

        /// <summary>The colour of the band and how opaque its tint is.</summary>
        internal (Color Color, byte Alpha) Look => Kind switch
        {
            HighwayBandKind.Proposal => (HighwayPalette.Proposal, HighwayChrome.BandTintAlpha),
            HighwayBandKind.StarPower => (HighwayPalette.StarPower, HighwayChrome.BandTintAlpha),
            HighwayBandKind.Solo => (PhraseColors.Solo, PhraseColors.SoloTintAlpha),
            HighwayBandKind.DrumFill => (PhraseColors.DrumFill, PhraseColors.TintAlpha),
            HighwayBandKind.Roll => (PhraseColors.Roll, PhraseColors.TintAlpha),
            HighwayBandKind.Trill => (PhraseColors.Trill, PhraseColors.TintAlpha),
            HighwayBandKind.BigRockEnding => (PhraseColors.BigRockEnding, PhraseColors.TintAlpha),
            _ => (PhraseColors.DiscoFlip, PhraseColors.TintAlpha),
        };
    }

    /// <summary>One highway: an instrument difficulty's notes with their song times worked out.</summary>
    public sealed class HighwayLane
    {
        /// <param name="delay">How much later than its ticks say the chart plays against the audio.</param>
        /// <param name="marks">How each of <paramref name="notes"/> is marked out, in their order, or null for none.</param>
        /// <param name="bands">The phrases to draw behind the notes, in song time, earliest first.</param>
        /// <param name="phrases">The chart's phrases, and drum disco flip sections, to draw behind the notes too, after <paramref name="bands"/>; in ticks, timed as the notes are.</param>
        public HighwayLane(ChartInstrumentKind kind, int laneCount, string label, IReadOnlyList<ChartNote> notes, TempoMap tempo, TimeSpan delay, IReadOnlyList<HighwayNoteMark>? marks = null, IReadOnlyList<HighwayBand>? bands = null, IReadOnlyList<PhraseSpan>? phrases = null)
        {
            if (marks is not null && marks.Count != notes.Count)
                throw new ArgumentException("There must be a mark for each note.", nameof(marks));

            Kind = kind;
            LaneCount = laneCount;
            Slots = LaneSlots.Of(kind, LaneView.Highway, kind == ChartInstrumentKind.Drums && laneCount == 5);
            Label = label;
            Notes = [.. notes];
            Marks = marks is null ? null : [.. marks];
            Bands = [.. bands ?? [], .. (phrases ?? []).Select(p => HighwayBand.Of(p, tempo, delay))];
            Starts = new double[Notes.Length];
            Ends = new double[Notes.Length];
            for (int i = 0; i < Notes.Length; i++)
            {
                Starts[i] = tempo.SecondsAt(Notes[i].Tick) + delay.TotalSeconds;
                Ends[i] = Notes[i].Length > 0 ? tempo.SecondsAt(Notes[i].Tick + Notes[i].Length) + delay.TotalSeconds : Starts[i];
                MaxLength = Math.Max(MaxLength, Ends[i] - Starts[i]);
            }

            if (kind == ChartInstrumentKind.SixFret)
            {
                var whites = Notes.Where(n => n.Lane is >= 0 and < 3).Select(n => (n.Tick, n.Lane)).ToHashSet();
                SharesColumn = Notes.Where(n => n.Lane >= 3 && whites.Contains((n.Tick, n.Lane - 3))).Select(n => (n.Tick, n.Lane - 3)).ToHashSet();
            }
        }

        public ChartInstrumentKind Kind { get; }

        /// <summary>How many lanes a highway of <paramref name="kind"/> has: five frets, four or five pads, or three six-fret columns.</summary>
        public static int LaneCountOf(ChartInstrumentKind kind, bool fiveLaneDrums) => kind switch
        {
            ChartInstrumentKind.FiveFret => 5,
            ChartInstrumentKind.Drums => fiveLaneDrums ? 5 : 4,
            _ => 3,
        };

        /// <summary>How many lanes the highway has: five frets, four or five pads, or three six-fret columns.</summary>
        public int LaneCount { get; }

        /// <summary>The lanes in their columns, as the editor's highway has them.</summary>
        public LaneSlots Slots { get; }

        /// <summary>The instrument and difficulty, shown at the top of the highway.</summary>
        public string Label { get; }

        public ChartNote[] Notes { get; }

        /// <summary>How each note is marked out, in the order of <see cref="Notes"/>; null when none is.</summary>
        public HighwayNoteMark[]? Marks { get; }

        /// <summary>The phrases drawn behind the notes, in song seconds, earliest first.</summary>
        public HighwayBand[] Bands { get; }

        /// <summary>Each note's song time, in seconds.</summary>
        public double[] Starts { get; }

        /// <summary>When each note's sustain ends, in seconds; its start when it has none.</summary>
        public double[] Ends { get; }

        /// <summary>The longest sustain, in seconds, so notes whose tails still show can be found.</summary>
        public double MaxLength { get; }

        /// <summary>For six-fret, the ticks and columns where a black and a white fret are played together.</summary>
        public IReadOnlySet<(long Tick, int Column)> SharesColumn { get; } = new HashSet<(long, int)>();
    }

    /// <summary>What <see cref="NoteHighwayView"/> draws: its highways, left to right, and the beat lines they share.</summary>
    public sealed class HighwayScene
    {
        public HighwayScene(IReadOnlyList<HighwayLane> lanes, TempoMap tempo, TimeSpan delay)
        {
            Lanes = lanes;
            long last = lanes.SelectMany(l => l.Notes).Select(n => n.Tick + n.Length).DefaultIfEmpty().Max() + (tempo.Resolution * 8L);
            var beats = tempo.BeatLines(0, last).ToList();
            BeatTimes = [.. beats.Select(b => tempo.SecondsAt(b.Tick) + delay.TotalSeconds)];
            IsMeasure = [.. beats.Select(b => b.IsMeasure)];
            MaxLaneCount = lanes.Select(l => l.LaneCount).DefaultIfEmpty(1).Max();
            EndSeconds = Math.Max(0, lanes.SelectMany(l => l.Ends).DefaultIfEmpty().Max());
        }

        /// <summary>When the last note of any highway ends, sustain included, in seconds.</summary>
        public double EndSeconds { get; }

        public IReadOnlyList<HighwayLane> Lanes { get; }

        /// <summary>The most lanes any of the highways has.</summary>
        public int MaxLaneCount { get; }

        public double[] BeatTimes { get; }

        public bool[] IsMeasure { get; }
    }

    /// <summary>
    /// Draws up to four flat note highways side by side, notes falling toward a strike line just
    /// above the bottom. Each frame the screen is drawn it reads <see cref="Clock"/>, so the notes
    /// follow the audio whether it's playing, paused or being dragged. Behind each highway's notes it
    /// can draw a track's <see cref="Waveforms"/>, falling with them.
    /// <para>Pressing on a highway and dragging scrubs: from <see cref="ScrubStarted"/> to <see cref="ScrubEnded"/>,
    /// <see cref="Scrubbed"/> says where to, and the owner seeks when it ends, as it does for Chart Packer's waveform. A drag
    /// grabs the notes: down moves the audio later, as far as the notes fall for it, as if pulling them down. With Ctrl held
    /// it's fine, <see cref="FineScrubFactor"/> times less far. Either way the pointer is held still, so a drag can go
    /// on as far as it needs, and a press leaves the audio where it is.</para>
    /// <para>While Shift is held (and not Ctrl) over a highway, that one fades to its whole chart instead, squeezed into its
    /// height from the start at the bottom to <see cref="LengthSeconds"/> at the top, and a press or a drag follows the
    /// pointer up it.</para>
    /// </summary>
    /// <example><c>&lt;ctl:NoteHighwayView Scene="{Binding Highway.Scene}" Clock="{Binding Highway.Clock}" LookAhead="{Binding Highway.LookAhead}" Waveforms="{Binding Highway.LaneWaveforms}" /&gt;</c></example>
    public sealed class NoteHighwayView : Control, IPerspectiveContent
    {
        public static readonly StyledProperty<HighwayScene?> SceneProperty =
            AvaloniaProperty.Register<NoteHighwayView, HighwayScene?>(nameof(Scene));

        public static readonly StyledProperty<Func<TimeSpan>?> ClockProperty =
            AvaloniaProperty.Register<NoteHighwayView, Func<TimeSpan>?>(nameof(Clock));

        public static readonly StyledProperty<double> LookAheadProperty =
            AvaloniaProperty.Register<NoteHighwayView, double>(nameof(LookAhead), 1.4, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

        public static readonly StyledProperty<HighwayLengthMode> LengthModeProperty =
            AvaloniaProperty.Register<NoteHighwayView, HighwayLengthMode>(nameof(LengthMode));

        public static readonly StyledProperty<double> PixelsPerSecondProperty =
            AvaloniaProperty.Register<NoteHighwayView, double>(nameof(PixelsPerSecond), HighwayPixelsPerSecond.Default, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

        public static readonly StyledProperty<double> ShownLookAheadProperty =
            AvaloniaProperty.Register<NoteHighwayView, double>(nameof(ShownLookAhead), 0, defaultBindingMode: Avalonia.Data.BindingMode.OneWayToSource);

        public static readonly StyledProperty<IReadOnlyList<Waveform?>?> WaveformsProperty =
            AvaloniaProperty.Register<NoteHighwayView, IReadOnlyList<Waveform?>?>(nameof(Waveforms));

        public static readonly StyledProperty<bool> AreNotesOutlinedProperty =
            AvaloniaProperty.Register<NoteHighwayView, bool>(nameof(AreNotesOutlined));

        public static readonly StyledProperty<double> LengthSecondsProperty =
            AvaloniaProperty.Register<NoteHighwayView, double>(nameof(LengthSeconds));

        public static readonly StyledProperty<double> FineScrubFactorProperty =
            AvaloniaProperty.Register<NoteHighwayView, double>(nameof(FineScrubFactor), LaneScrub.DefaultFineFactor);

        public static readonly StyledProperty<int?> SelectedPhraseProperty =
            AvaloniaProperty.Register<NoteHighwayView, int?>(nameof(SelectedPhrase));

        // Where the strike line sits, as a fraction of the height from the top; the instrument's name sits below it. In perspective
        // the name stands up beside the plane, and the line is lower, for the plane is long and nothing is drawn below it.
        private const double FlatStrikeFraction = 0.87;
        private const double PerspectiveStrikeFraction = 1 - EditorViewport.PerspectiveStrikeFraction;

        // In perspective, how wide a highway gets at the most, in pixels of the plane, as in the editor's.
        private const double MaxPerspectiveHighway = 480;

        private double StrikeFraction => _camera is null ? FlatStrikeFraction : PerspectiveStrikeFraction;

        // The same line, as the shared mapping measures it: from the bottom, which is where the song starts to show.
        private double StrikeFromEnd => 1 - StrikeFraction;

        // The top of the highway fades out over this fraction of its height, so notes come in rather than pop in.
        private const double FadeFraction = 0.2;

        private static readonly IBrush HighwayBrush = VerticalGradient(
            (0, Color.FromArgb(0x80, 0x06, 0x06, 0x0C)),
            (0.5, Color.FromArgb(0xCC, 0x08, 0x08, 0x10)),
            (1, Color.FromArgb(0xE6, 0x05, 0x05, 0x0A)));

        // Opaque below the fade, so everything drawn on the highway fades in at the top.
        private static readonly IBrush FadeMask = VerticalGradient((0, Colors.Transparent), (FadeFraction, Colors.White), (1, Colors.White));

        private static readonly IBrush RailBrush = VerticalGradient(
            (0, Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF)),
            (0.7, Color.FromArgb(0x70, 0xC8, 0xD2, 0xFF)),
            (1, Color.FromArgb(0xA0, 0xE0, 0xE6, 0xFF)));

        // A highway shown as its whole chart: evenly dark, with no fade, so the marks read the same all the way up.
        private static readonly IBrush OverviewBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xE6, 0x06, 0x06, 0x0C));
        private static readonly IPen OverviewEdgePen = new ImmutablePen(Color.FromArgb(0x50, 0xC8, 0xD2, 0xFF).ToUInt32(), 1);

        private static readonly IBrush LabelBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF));

        // How opaque a faded note is (HighwayNoteMark.Faded), on the highway and in its whole chart.
        private const double FadedOpacity = 0.28;
        private const byte FadedOverviewAlpha = 0x50;

        // The track's waveform behind the notes: faint, so the gems stand out in front of it.
        private static readonly IBrush WaveformPeakBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x24, 0xC8, 0xD2, 0xFF));
        private static readonly IBrush WaveformLoudBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x48, 0xC8, 0xD2, 0xFF));

        private readonly GemPainter _gems = new();
        private readonly NotePainter _notes;
        private readonly UprightGemPainter _upright;
        private readonly NotePainter _uprightNotes;
        private PerspectiveCamera? _camera;
        private readonly List<NoteGlyph> _glyphs = [];
        private readonly List<BandGlyph> _bands = [];
        private readonly LabelCache _labels = new();
        private bool _isFrameRequested;
        private Rect? _viewport;
        private TimeSpan _drawnAt = TimeSpan.MinValue;

        // How brightly each column of the highway being drawn is lit; one for them all, as they're drawn one at a time.
        private double[] _hits = [];

        // The highway the pointer is over, drawn as its whole chart while Shift is held, and how far down it the pointer is;
        // -1 and NaN for none. While scrubbing it stays the highway the press was on.
        private int _hoveredLane = -1;
        private double _hoverY = double.NaN;

        // Whether Ctrl is held over a highway, or while scrubbing, making a drag fine, or Shift, showing its whole chart.
        private readonly ViewportGestures _gestures;

        // How far each highway, by its index, has faded to its whole chart; one fading out as another fades in, both are drawn.
        private readonly List<OverviewFade> _fades = [];

        // Each highway's whole-chart marks, as last laid out, and what they were laid out for.
        private readonly Dictionary<HighwayLane, OverviewMarks> _overviews = [];

        static NoteHighwayView()
        {
            AffectsRender<NoteHighwayView>(SceneProperty, LookAheadProperty, LengthModeProperty, PixelsPerSecondProperty, WaveformsProperty, AreNotesOutlinedProperty, LengthSecondsProperty, SelectedPhraseProperty);
        }

        /// <summary>Raised when a highway is pressed, before the first <see cref="Scrubbed"/>.</summary>
        public event EventHandler? ScrubStarted;

        /// <summary>Raised as the pointer moves while a highway is pressed, with the time it's at.</summary>
        public event EventHandler<ScrubEventArgs>? Scrubbed;

        /// <summary>Raised when the highway is let go, or the press is lost.</summary>
        public event EventHandler? ScrubEnded;

        /// <summary>The highways to draw, or <see langword="null"/> for none.</summary>
        public HighwayScene? Scene
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

        /// <summary>How far ahead of the strike line the highway shows, in seconds: less is faster. Used in <see cref="HighwayLengthMode.LookAhead"/>.</summary>
        public double LookAhead
        {
            get => GetValue(LookAheadProperty);
            set => SetValue(LookAheadProperty, value);
        }

        /// <summary>Whether the highway's length is its <see cref="LookAhead"/> or its <see cref="PixelsPerSecond"/>.</summary>
        public HighwayLengthMode LengthMode
        {
            get => GetValue(LengthModeProperty);
            set => SetValue(LengthModeProperty, value);
        }

        /// <summary>
        /// How many pixels of the highway a second of the song takes up, in <see cref="HighwayLengthMode.PixelsPerSecond"/>: the
        /// notes keep their distance when the highway is resized, and a taller one shows further ahead.
        /// </summary>
        public double PixelsPerSecond
        {
            get => GetValue(PixelsPerSecondProperty);
            set => SetValue(PixelsPerSecondProperty, value);
        }

        /// <summary>
        /// How far ahead of the strike line the highway shows now, in seconds: its <see cref="LookAhead"/>, or in pixels per
        /// second what the distance from the strike line to the top makes of it.
        /// </summary>
        public double EffectiveLookAhead => Math.Max(HighwayLookAhead.ShortestShown, new HighwayLength(LengthMode, LookAhead, PixelsPerSecond).LookAheadFor(Bounds.Height * StrikeFraction));

        /// <summary>
        /// <see cref="EffectiveLookAhead"/> as it was when the highway was last laid out, for something bound to it
        /// (one way to the source): a highway with no size yet leaves it as it was, so it is zero until there is one.
        /// Set by the highway, not by the user of it.
        /// </summary>
        public double ShownLookAhead
        {
            get => GetValue(ShownLookAheadProperty);
            private set => SetCurrentValue(ShownLookAheadProperty, value);
        }

        // Says what the highway shows now, when what it depends on has changed, so it is read without being polled.
        private void ShareShownLookAhead()
        {
            if (Bounds.Height > 0)
                ShownLookAhead = EffectiveLookAhead;
        }

        /// <summary>
        /// The audio drawn behind each of the <see cref="Scene"/>'s highways, in order, at the time it's heard; a
        /// <see langword="null"/> one, or the whole list, draws none.
        /// </summary>
        public IReadOnlyList<Waveform?>? Waveforms
        {
            get => GetValue(WaveformsProperty);
            set => SetValue(WaveformsProperty, value);
        }

        /// <summary>
        /// Whether the notes are drawn as outlines in their colours, with nothing filled in, so the waveform behind
        /// them shows through and a note can be lined up with the sound it's played to.
        /// </summary>
        public bool AreNotesOutlined
        {
            get => GetValue(AreNotesOutlinedProperty);
            set => SetValue(AreNotesOutlinedProperty, value);
        }

        /// <summary>
        /// How long the song is, in seconds: what a highway shows of it while the pointer is over it, and scrubs through. At
        /// zero, the chart's own end is taken instead.
        /// </summary>
        public double LengthSeconds
        {
            get => GetValue(LengthSecondsProperty);
            set => SetValue(LengthSecondsProperty, value);
        }

        /// <summary>How many times less far a drag with Ctrl held moves the notes than a plain drag; see <see cref="LaneScrub.DefaultFineFactor"/>.</summary>
        public double FineScrubFactor
        {
            get => GetValue(FineScrubFactorProperty);
            set => SetValue(FineScrubFactorProperty, value);
        }

        /// <summary>The band drawn selected, or null for none.</summary>
        public int? SelectedPhrase
        {
            get => GetValue(SelectedPhraseProperty);
            set => SetValue(SelectedPhraseProperty, value);
        }

        public NoteHighwayView()
        {
            _notes = new NotePainter(_gems);
            _upright = new UprightGemPainter(_gems);
            _uprightNotes = new NotePainter(_upright);
            _gestures = new ViewportGestures(this, new GestureHost(this), new ScrubBinding(MouseButton.Left, KeyModifiers.None, AllowsOverview: true), WatchesKeys);
            EffectiveViewportChanged += OnEffectiveViewportChanged;
        }

        // Ctrl and Shift are watched while the pointer is over a highway, or one is being scrubbed.
        private bool WatchesKeys() => _hoveredLane >= 0 || _gestures.IsScrubbing;

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == SceneProperty)
            {
                _overviews.Clear();
                _fades.Clear();
            }
            if (change.Property == SceneProperty || change.Property == IsVisibleProperty)
                RequestFrame();
            if (change.Property == BoundsProperty || change.Property == LookAheadProperty || change.Property == LengthModeProperty || change.Property == PixelsPerSecondProperty)
                ShareShownLookAhead();

            // What makes the highway draw again makes what stands up on it draw again.
            if (change.Property == SceneProperty || change.Property == LookAheadProperty || change.Property == LengthModeProperty || change.Property == PixelsPerSecondProperty
                || change.Property == WaveformsProperty || change.Property == AreNotesOutlinedProperty || change.Property == LengthSecondsProperty || change.Property == SelectedPhraseProperty)
            {
                RaiseStanding();
            }

            // Shown again, it shows the notes falling until the pointer is over a highway again.
            if (change.Property == IsVisibleProperty && !IsVisible && !_gestures.IsScrubbing)
                SetHover(-1, double.NaN);
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
                Redraw();
        }


        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            RequestFrame();

            // Shift pressed or let go with the pointer over a highway, or while scrubbing, shows the whole chart or the notes falling.
            _gestures.Mode.Attach();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _gestures.Mode.Detach();
        }

        public override void Render(DrawingContext context)
        {
            if (Scene is not { Lanes.Count: > 0 } scene || Bounds.Width <= 0 || Bounds.Height <= 0)
                return;

            // The time read for this frame, rather than reading the clock again.
            double now = NowSeconds;
            double lookAhead = EffectiveLookAhead;
            double height = Bounds.Height;
            double strike = height * StrikeFraction;

            int count = scene.Lanes.Count;
            double gem = GemSizeOf(strike);

            var waveforms = Waveforms;
            for (int i = 0; i < count; i++)
            {
                var bounds = HighwayRect(scene, i);

                // Transparent, so the whole highway takes the pointer, and the gaps between them don't.
                context.DrawRectangle(Brushes.Transparent, null, bounds);

                // Fading between them, each is drawn see-through, so one shows through the other.
                var fade = FadeOf(i);
                double overview = fade.Opacity;
                if (overview < 1)
                {
                    var waveform = waveforms is not null && i < waveforms.Count ? waveforms[i] : null;

                    // Where the song is drawn down this highway, with what's past the strike line shown for as long as it takes to fall off the bottom.
                    var lane = scene.Lanes[i];
                    var projection = ProjectionOf(lane, bounds, now, lookAhead);
                    using (context.PushOpacity(1 - overview))
                        DrawHighway(context, scene, lane, waveform, bounds, strike, gem, now, projection, i == 0 ? SelectedPhrase : null);
                }

                if (overview > 0)
                {
                    using (context.PushOpacity(overview))
                        DrawOverview(context, scene.Lanes[i], bounds, now, fade.PointerAt);
                }
            }
        }

        /// <summary>Whether the <paramref name="index"/>th highway is to show its whole chart: Shift is held over it, or while it's scrubbed.</summary>
        private bool IsOverviewShown(int index) => index == _hoveredLane && _gestures.Mode.IsOverview;

        /// <summary>How far the <paramref name="index"/>th highway has faded to its whole chart.</summary>
        private OverviewFade FadeOf(int index)
        {
            while (_fades.Count <= index)
                _fades.Add(new OverviewFade());
            return _fades[index];
        }

        /// <summary>
        /// Where the <paramref name="index"/>th of <paramref name="scene"/>'s highways is drawn: as wide as the height allows,
        /// narrower when they don't all fit, with small gaps, centred.
        /// </summary>
        private Rect HighwayRect(HighwayScene scene, int index)
        {
            const double GapFraction = 0.1;
            int count = scene.Lanes.Count;
            double width = Math.Min(_camera is null ? Bounds.Height * 0.45 : MaxPerspectiveHighway, Bounds.Width * 0.96 / (count + ((count - 1) * GapFraction)));
            double gap = width * GapFraction;
            double left = (Bounds.Width - ((count * width) + ((count - 1) * gap))) / 2;
            return new Rect(left + (index * (width + gap)), 0, width, Bounds.Height);
        }

        /// <summary>The highway at <paramref name="point"/>, or -1 when it's over none.</summary>
        private int HighwayAt(Point point)
        {
            if (Scene is not { Lanes.Count: > 0 } scene || Bounds.Width <= 0 || Bounds.Height <= 0)
                return -1;

            for (int i = 0; i < scene.Lanes.Count; i++)
            {
                if (HighwayRect(scene, i).Contains(point))
                    return i;
            }

            return -1;
        }

        /// <summary>How much of the song a highway's whole chart covers, in seconds: the song's length, or else the chart's.</summary>
        private double TimelineOf(HighwayScene scene) => LengthSeconds > 0 ? LengthSeconds : scene.EndSeconds + 1;

        /// <summary>
        /// A highway as its whole chart, from the start at the bottom to the end of the song at the top: the notes as small
        /// marks in their lanes, what's been played shaded, a line where the audio is, and one where the pointer is, or was
        /// last, <paramref name="pointerAt"/> down it.
        /// </summary>
        private void DrawOverview(DrawingContext context, HighwayLane lane, Rect bounds, double now, double pointerAt)
        {
            double timeline = Math.Max(0.1, Scene is { } scene ? TimelineOf(scene) : 0);
            double Y(double time) => bounds.Bottom - (time / timeline * bounds.Height);

            using (context.PushClip(bounds))
            {
                context.FillRectangle(OverviewBrush, bounds);
                double laneWidth = bounds.Width / lane.LaneCount;
                for (int i = 1; i < lane.LaneCount; i++)
                {
                    double x = bounds.X + (i * laneWidth);
                    context.DrawLine(HighwayChrome.LaneDividerPen, new Point(x, bounds.Top), new Point(x, bounds.Bottom));
                }

                using (context.PushTransform(Matrix.CreateTranslation(bounds.X, bounds.Y)))
                {
                    foreach (var (rect, color) in OverviewMarksOf(lane, bounds.Size, timeline))
                        context.FillRectangle(Brush(color), rect);
                }

                double playhead = Math.Clamp(Y(now), bounds.Top, bounds.Bottom);
                context.FillRectangle(LaneScrub.PlayedBrush, new Rect(bounds.X, playhead, bounds.Width, bounds.Bottom - playhead));
                context.DrawLine(LaneScrub.PlayheadPen, new Point(bounds.X, playhead), new Point(bounds.Right, playhead));
                context.DrawRectangle(null, OverviewEdgePen, bounds.Deflate(0.5));

                if (!double.IsNaN(pointerAt))
                {
                    double at = Math.Clamp(pointerAt, bounds.Top, bounds.Bottom);
                    context.DrawLine(LaneScrub.HoverPen, new Point(bounds.X, at), new Point(bounds.Right, at));
                    LaneScrub.DrawTimeTag(context, TimeAt(at), bounds, at, isAcross: true);
                }
            }

            // At the top, out of the way of the time.
            DrawLabel(context, lane.Label, bounds, bounds.Bottom);
        }

        /// <summary>
        /// <paramref name="lane"/>'s notes as marks in a highway of <paramref name="size"/> covering <paramref name="timeline"/>
        /// seconds, from its top left: kick and open bars, then tails, then gems, each a run of the notes in a lane that fall
        /// on the same pixel row or the next one in the same colour, so a dense chart is a few hundred rectangles. Laid out
        /// again only when one of those changes.
        /// </summary>
        private List<(Rect Rect, Color Color)> OverviewMarksOf(HighwayLane lane, Size size, double timeline)
        {
            if (_overviews.TryGetValue(lane, out var known) && known.Size == size && known.Timeline == timeline)
                return known.Marks;

            const double GemHeight = 2;
            double laneWidth = size.Width / lane.LaneCount;
            double Y(double time) => size.Height - (time / timeline * size.Height);

            var bars = new List<(Rect, Color)>();
            var tails = new List<(Rect, Color)>();
            var gems = new List<(Rect, Color)>();

            // The run being built in each lane, by its column (-1 for the bars across), for gems and for tails apart.
            var gemRuns = new Dictionary<int, (double Top, double Bottom, Color Color)>();
            var tailRuns = new Dictionary<int, (double Top, double Bottom, Color Color)>();

            Rect GemMark(int column, double top, double bottom) => column < 0
                ? new Rect(2, top, size.Width - 4, bottom - top)
                : new Rect((column * laneWidth) + (laneWidth * 0.14), top, laneWidth * 0.72, bottom - top);

            Rect TailMark(int column, double top, double bottom)
            {
                double width = Math.Max(1.5, laneWidth * 0.2);
                return new Rect(((column + 0.5) * laneWidth) - (width / 2), top, width, bottom - top);
            }

            // Notes are in time order, so each is at or above the one before in its lane.
            static void Add(Dictionary<int, (double Top, double Bottom, Color Color)> runs, List<(Rect, Color)> marks, Func<int, double, double, Rect> rectOf, int column, double top, double bottom, Color color)
            {
                if (runs.TryGetValue(column, out var run))
                {
                    if (run.Color == color && bottom >= run.Top - 1)
                    {
                        runs[column] = (Math.Min(run.Top, top), run.Bottom, color);
                        return;
                    }

                    marks.Add((rectOf(column, run.Top, run.Bottom), run.Color));
                }

                runs[column] = (top, bottom, color);
            }

            for (int i = 0; i < lane.Notes.Length; i++)
            {
                var note = lane.Notes[i];
                int column = note.Lane == ChartNote.OpenLane ? -1 : Column(lane, note.Lane);

                // A black fret's own colour would be lost on the dark highway. A flagged note is in the mark's colour, so the
                // whole chart is a map of what changed, and a faded one faint.
                var mark = MarkOf(lane, i);
                var color = (mark & HighwayNoteMark.Flagged) != 0 ? HighwayPalette.ChangeMark
                    : IsBlackFret(lane, note) ? HighwayPalette.BlackFretEdge
                    : NoteColor(lane, note);
                if (mark == HighwayNoteMark.Faded)
                    color = HighwayPalette.WithAlpha(color, FadedOverviewAlpha);
                double bottom = Y(lane.Starts[i]);
                Add(gemRuns, column < 0 ? bars : gems, GemMark, column, bottom - GemHeight, bottom, color);
                if (column >= 0 && lane.Ends[i] > lane.Starts[i])
                    Add(tailRuns, tails, TailMark, column, Y(lane.Ends[i]), bottom, HighwayPalette.WithAlpha(color, Math.Min(color.A, (byte)0xA0)));
            }

            foreach (var (column, run) in tailRuns)
                tails.Add((TailMark(column, run.Top, run.Bottom), run.Color));
            foreach (var (column, run) in gemRuns)
                (column < 0 ? bars : gems).Add((GemMark(column, run.Top, run.Bottom), run.Color));

            List<(Rect, Color)> marks = [.. bars, .. tails, .. gems];
            _overviews[lane] = new OverviewMarks(lane, size, timeline, marks);
            return marks;
        }

        /// <summary>The song time at <paramref name="y"/> down a highway's whole chart.</summary>
        private double TimeAt(double y) =>
            Scene is { } scene && Bounds.Height > 0 ? Math.Clamp((Bounds.Height - y) / Bounds.Height, 0, 1) * TimelineOf(scene) : 0;

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var point = e.GetPosition(this);
            _gestures.Mode.Set(e.KeyModifiers);
            if (_gestures.IsScrubbing)
            {
                SetHover(_hoveredLane, point.Y);
                _gestures.Move(e);
                return;
            }

            SetHover(HighwayAt(point), point.Y);
        }

        /// <summary>
        /// The wheel scrolls the highway through the song, a quarter of its look-ahead a notch, so the preview can be moved
        /// without leaving it; with Ctrl held it zooms. The window hears <see cref="Scrubbed"/> and seeks.
        /// </summary>
        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            if (Scene is not null)
                _gestures.Wheel(e);
        }

        /// <summary>How far the notes falling move in a pixel, in seconds, so a drag moves the notes as far as the pointer goes.</summary>
        private double GrabSecondsPerPixel => Bounds.Height > 0 ? EffectiveLookAhead / (Bounds.Height * StrikeFraction) : 0;

        /// <summary>Zooms by <paramref name="steps"/>, in when positive: the highway shows less of the song, as its length setting is kept.</summary>
        private void ZoomBy(int steps)
        {
            if (LengthMode == HighwayLengthMode.PixelsPerSecond)
                SetCurrentValue(PixelsPerSecondProperty, HighwayPixelsPerSecond.Stepped(PixelsPerSecond, steps));
            else
                SetCurrentValue(LookAheadProperty, HighwayLookAhead.Stepped(LookAhead, -steps));
        }

        /// <summary>What the shared gestures move: the highway's clock is the owner's to set, which it's told of by <see cref="Scrubbed"/>.</summary>
        private sealed class GestureHost(NoteHighwayView view) : IViewportGestureHost
        {
            public double Now => (view.Clock?.Invoke() ?? TimeSpan.Zero).TotalSeconds;

            public double SecondsPerPixel => view.GrabSecondsPerPixel;

            public bool IsTimeAxisVertical => true;

            public bool WheelUpIsLater => true;

            public double FineFactor => view.FineScrubFactor;

            public void ScrubStarted() => view.ScrubStarted?.Invoke(view, EventArgs.Empty);

            public void ScrubTo(double seconds)
            {
                if (view.Scene is { } scene)
                    view.Scrubbed?.Invoke(view, new ScrubEventArgs(Math.Clamp(seconds, 0, view.TimelineOf(scene))));
            }

            public void ScrubEnded() => view.ScrubEnded?.Invoke(view, EventArgs.Empty);

            // A quarter of the look-ahead a notch, so the preview can be moved without leaving it.
            public void Scroll(int steps) => ScrubTo(Now + (steps * Math.Clamp(view.EffectiveLookAhead * 0.25, 0.1, 2)));

            public void Zoom(int steps) => view.ZoomBy(steps);

            public double? OverviewTimeAt(Point at) => view.Scene is null ? null : view.TimeAt(at.Y);
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            if (!_gestures.IsScrubbing)
                SetHover(-1, double.NaN);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var point = e.GetPosition(this);
            int lane = HighwayAt(point);
            if (lane < 0 || !_gestures.IsScrubPress(e))
                return;

            SetHover(lane, point.Y);
            _gestures.Press(e, point);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (!_gestures.Release(e))
                return;

            // Let go off the highway, it stops showing the whole chart.
            var point = e.GetPosition(this);
            SetHover(HighwayAt(point), point.Y);
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            _gestures.CaptureLost();
        }

        /// <summary>
        /// Takes the pointer to be over <paramref name="lane"/>, <paramref name="y"/> down it, which shows its whole chart while
        /// Shift is held; -1 for none.
        /// </summary>
        private void SetHover(int lane, double y)
        {
            if (lane < 0)
                y = double.NaN;
            if (lane == _hoveredLane && (y == _hoverY || (double.IsNaN(y) && double.IsNaN(_hoverY))))
                return;

            if (lane != _hoveredLane)
                Cursor = lane >= 0 ? LaneScrub.UpDownCursor : null;
            _hoveredLane = lane;
            _hoverY = y;
            if (lane >= 0)
                FadeOf(lane).PointerAt = y;
            Redraw();
        }

        private void DrawHighway(DrawingContext context, HighwayScene scene, HighwayLane lane, Waveform? waveform, Rect bounds, double strike, double gem, double now, LaneProjection projection, int? selectedBand)
        {
            double from = projection.Start, to = projection.End;
            Func<double, double> y = projection.AlongOf;
            double laneWidth = bounds.Width / lane.LaneCount;
            var hits = HitsAt(lane, now);

            using (context.PushClip(bounds))
            using (context.PushOpacityMask(FadeMask, bounds))
            {
                context.FillRectangle(HighwayBrush, bounds);

                // Star power phrases and proposed fills, behind the notes they charge.
                var bands = _bands;
                bands.Clear();
                for (int i = 0; i < lane.Bands.Length; i++)
                {
                    var band = lane.Bands[i];
                    if (band.Start > to || (band.HasEnd && band.End < from))
                        continue;

                    // One still going on reaches the far end.
                    double top = y(band.HasEnd ? band.End : to), bottom = y(band.Start);
                    double start = Math.Min(top, bottom);
                    bands.Add(new BandGlyph
                    {
                        Start = start,
                        End = start + Math.Max(1, Math.Abs(bottom - top)),
                        Color = band.Look.Color,
                        TintAlpha = band.Look.Alpha,
                        HasEnd = band.HasEnd,
                        IsSelected = i == selectedBand,
                    });
                }

                HighwayChrome.DrawBands(context, _gems, LaneView.Highway, CollectionsMarshal.AsSpan(bands), bounds.X, bounds.Right);

                // The track as it's heard, inside the rails, so a note can be lined up with the sound it's played to. Scaled
                // so its loudest peak fills the highway, so a quiet track's beats show as clearly as a loud one's.
                if (waveform is not null)
                {
                    LaneWaveformPainter.Draw(context, waveform, bounds.Deflate(new Thickness(4, 0)), isVertical: true, from, to, y,
                        WaveformPeakBrush, WaveformLoudBrush, LaneWaveformPainter.FullScaleGain(waveform), 1 / (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1));
                }

                // A beam of light rises from a lane as it's struck.
                for (int column = 0; column < lane.LaneCount; column++)
                {
                    HighwayChrome.DrawLitBeam(context, LaneView.Highway, HitColor(lane, column), hits[column],
                        new Rect(bounds.X + (column * laneWidth), strike * (1 - StrikeGlow.BeamReach), laneWidth, strike * StrikeGlow.BeamReach));
                }

                HighwayChrome.DrawLaneDividers(context, projection, 0, strike);

                for (int i = LowerBound(scene.BeatTimes, from); i < scene.BeatTimes.Length && scene.BeatTimes[i] <= to; i++)
                {
                    double at = y(scene.BeatTimes[i]);
                    if (at < strike)
                        HighwayChrome.DrawBeatLine(context, LaneView.Highway, at, bounds.X, bounds.Right, scene.IsMeasure[i]);
                }

                // The rails down each side, as far as the strike bar.
                double railEnd = strike + (gem * 0.85);
                context.FillRectangle(RailBrush, new Rect(bounds.X, 0, 2, railEnd));
                context.FillRectangle(RailBrush, new Rect(bounds.Right - 2, 0, 2, railEnd));

                DrawStrikeBar(context, lane, projection, bounds, strike, gem, hits);
                // In perspective the notes stand up, in a layer of the host's (PaintGems); only their tails lie on the plane.
                DrawNotes(context, lane, projection, gem, now, from, to, AreNotesOutlined, _notes, _camera is null ? NotePass.All : NotePass.Tails, 0);
            }

            if (_camera is null)
                DrawLabel(context, lane.Label, bounds, strike + (gem * 0.9));
        }

        /// <summary>How brightly each column is lit, from 0 to 1: just struck, or held.</summary>
        private double[] HitsAt(HighwayLane lane, double now)
        {
            if (_hits.Length < lane.LaneCount)
                _hits = new double[lane.LaneCount];
            var hits = _hits;
            Array.Clear(hits, 0, lane.LaneCount);
            for (int i = LowerBound(lane.Starts, now - (lane.MaxLength + StrikeGlow.Lingers)); i < lane.Starts.Length && lane.Starts[i] <= now; i++)
            {
                var note = lane.Notes[i];
                double glow = StrikeGlow.Brightness(now - lane.Starts[i], Math.Max(0, lane.Ends[i] - lane.Starts[i]));
                if (glow <= 0)
                    continue;

                // An open note or kick lights every lane.
                StrikeGlow.Light(hits, lane.LaneCount, note.Lane == ChartNote.OpenLane ? -1 : Column(lane, note.Lane), glow);
            }

            return hits;
        }

        /// <summary>The dark bar across the strike line, with a target per lane that lights as it's struck.</summary>
        private void DrawStrikeBar(DrawingContext context, HighwayLane lane, LaneProjection projection, Rect bounds, double strike, double gem, double[] hits)
        {
            var band = new Rect(bounds.X, strike - (gem * 0.85), bounds.Width, gem * 1.7);
            HighwayChrome.DrawStrikeBand(context, LaneView.Highway, band, HighwayChrome.StrikeBandBrush);

            for (int column = 0; column < lane.LaneCount; column++)
            {
                var color = TargetColor(lane, column);
                var rect = NotePainter.GemRect(projection, column, strike, gem);

                // Dark until struck, then filled in the lane's colour, which fades back to dark.
                HighwayChrome.DrawTarget(context, _gems, rect, color);
                HighwayChrome.DrawLitTarget(context, _gems, rect, color, hits[column]);
            }
        }

        /// <param name="outlined">Whether each note is drawn as its outline only, so what's behind it shows through.</param>
        /// <param name="painter">What draws the notes: flat, or standing up on a perspective highway.</param>
        /// <param name="pass">Which of the notes' tails and the notes themselves to draw.</param>
        /// <param name="fadeLength">How far along the plane, from its far end, the notes fade in over, as the plane does; none for none.</param>
        private void DrawNotes(DrawingContext context, HighwayLane lane, LaneProjection projection, double gem, double now, double from, double to, bool outlined, NotePainter painter, NotePass pass, double fadeLength)
        {
            int first = LowerBound(lane.Starts, from - lane.MaxLength);
            int last = LowerBound(lane.Starts, to + 0.001);

            var glyphs = _glyphs;
            glyphs.Clear();
            for (int i = first; i < last; i++)
            {
                var note = lane.Notes[i];
                bool open = note.Lane == ChartNote.OpenLane;
                var parts = NoteGlyphParts.None;
                double tailFrom = 0, tailTo = 0;

                // A sustain's tail is eaten from the strike line as it's held. A kick has none.
                if (lane.Ends[i] > lane.Starts[i] && lane.Ends[i] > now && !(open && lane.Kind == ChartInstrumentKind.Drums))
                {
                    tailTo = projection.AlongOf(Math.Min(lane.Ends[i], to));
                    tailFrom = projection.AlongOf(Math.Max(lane.Starts[i], now));
                    if (tailFrom > tailTo)
                        parts |= NoteGlyphParts.Tail;
                }

                // A note is struck, and goes, as it reaches the line.
                if (lane.Starts[i] >= now)
                    parts |= NoteGlyphParts.Note;
                if (parts == NoteGlyphParts.None)
                    continue;

                var mark = MarkOf(lane, i);
                if (open)
                    parts |= NoteGlyphParts.Across;

                // Six-fret: a black fret played with a white one covers its top half.
                else if (IsBlackFret(lane, note) && lane.SharesColumn.Contains((note.Tick, note.Lane % 3)))
                    parts |= NoteGlyphParts.Barre;
                if (outlined)
                    parts |= NoteGlyphParts.Outlined | NoteGlyphParts.OutlinedTail;
                if ((mark & HighwayNoteMark.Flagged) != 0)
                    parts |= NoteGlyphParts.Flagged;

                // Standing up, a note fades in at the plane's far end as the plane does, which fades its own.
                double along = projection.AlongOf(lane.Starts[i]);
                double fade = fadeLength > 0 ? Math.Clamp(along / fadeLength, 0, 1) : 1;
                if (fade <= 0.02)
                    continue;

                glyphs.Add(new NoteGlyph
                {
                    Index = i,
                    Note = note,
                    Color = NoteColor(lane, note),
                    Column = open ? -1 : Column(lane, note.Lane),
                    Along = along,
                    TailFrom = tailFrom,
                    TailTo = tailTo,
                    Opacity = ((mark & HighwayNoteMark.Faded) != 0 ? FadedOpacity : 1) * fade,
                    Parts = parts,
                });
            }

            painter.Draw(context, projection, lane.Kind, CollectionsMarshal.AsSpan(glyphs), gem, pass: pass);
        }

        private static HighwayNoteMark MarkOf(HighwayLane lane, int index) => lane.Marks is { } marks ? marks[index] : HighwayNoteMark.None;

        private void DrawLabel(DrawingContext context, string label, Rect bounds, double top)
        {
            // The highway is as wide as the view and the number of lanes allow, which both change.
            var text = _labels.Get(label, 10.5, LabelBrush, Math.Max(1, bounds.Width - 4));

            // Below the strike bar when there's room, otherwise at the top.
            double y = top + 2 + text.Height <= bounds.Bottom ? top + 1 : 4;
            text.Draw(context, new Point(bounds.X + ((bounds.Width - Math.Min(text.Width, bounds.Width)) / 2), y));
        }

        /// <summary>The time the highway is drawn at this frame, in seconds: the clock as it was read for it, not read again.</summary>
        private double NowSeconds => (_drawnAt != TimeSpan.MinValue ? _drawnAt : Clock?.Invoke() ?? TimeSpan.Zero).TotalSeconds;

        /// <summary>
        /// One gem size for every highway: as deep as the highway is long, as in the editor (<see cref="GemDepth"/>), whatever its width.
        /// Flat, it is kept to the room the names below the strike bar leave; in perspective the names stand up beside the plane.
        /// </summary>
        private double GemSizeOf(double strike)
        {
            double depth = GemDepth.For(_camera is { } camera ? camera.HostHeight : Bounds.Height, perspective: _camera is not null);
            return _camera is null ? Math.Max(5, Math.Min(depth, (Bounds.Height - strike - 13) / 0.9)) : depth;
        }

        /// <summary>Where the song is drawn down <paramref name="lane"/>, with what's past the strike line shown for as long as it takes to fall off the bottom.</summary>
        private LaneProjection ProjectionOf(HighwayLane lane, Rect bounds, double now, double lookAhead) =>
            LaneProjection.AtCursor(LaneView.Highway, 0, Bounds.Height, bounds.X, bounds.Width / lane.LaneCount, lane.Slots, now, StrikeFromEnd, lookAhead);

        /// <inheritdoc />
        public PerspectiveCamera? Camera
        {
            get => _camera;
            set
            {
                if (ReferenceEquals(_camera, value))
                    return;

                // The strike line moves with it, and so does what the highway shows ahead of it.
                _camera = value;
                _upright.Camera = value;
                ShareShownLookAhead();
                InvalidateVisual();
                RaiseStanding();
            }
        }

        /// <inheritdoc />
        public event Action? GemsInvalidated;

        // The preview has nothing that follows the pointer to stand up.
        /// <inheritdoc />
        public event Action? GemMarksInvalidated
        {
            add { }
            remove { }
        }

        /// <summary>Has the highway drawn again, and what stands up on it.</summary>
        private void Redraw()
        {
            InvalidateVisual();
            RaiseStanding();
        }

        private void RaiseStanding()
        {
            if (_camera is null)
                return;

            GemsInvalidated?.Invoke();
        }

        /// <inheritdoc />
        public void PaintGems(DrawingContext context)
        {
            if (_camera is null || Scene is not { Lanes.Count: > 0 } scene || Bounds.Width <= 0 || Bounds.Height <= 0)
                return;

            double now = NowSeconds;
            double lookAhead = EffectiveLookAhead;
            double strike = Bounds.Height * StrikeFraction;
            double gem = GemSizeOf(strike);
            for (int i = 0; i < scene.Lanes.Count; i++)
            {
                double overview = FadeOf(i).Opacity;
                if (overview >= 1)
                    continue;

                var bounds = HighwayRect(scene, i);
                var lane = scene.Lanes[i];
                var projection = ProjectionOf(lane, bounds, now, lookAhead);
                using (context.PushOpacity(1 - overview))
                {
                    DrawNotes(context, lane, projection, gem, now, projection.Start, projection.End, AreNotesOutlined, _uprightNotes, NotePass.Notes, Bounds.Height * FadeFraction);
                    DrawStandingLabel(context, lane.Label, bounds);
                }
            }
        }

        /// <inheritdoc />
        public void PaintGemMarks(DrawingContext context)
        {
        }

        /// <summary>The highway's name, standing up at the near end of its plane, as large as the plane is there, to be read.</summary>
        private void DrawStandingLabel(DrawingContext context, string label, Rect bounds)
        {
            if (_camera is not { } camera)
                return;

            var text = _labels.Get(label, 10.5, LabelBrush, Math.Max(1, bounds.Width - 4));
            var foot = new Point(bounds.X + (bounds.Width / 2), Bounds.Height - 2);
            var at = camera.Project(foot);
            double scale = camera.ScaleAt(foot.Y);
            using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(at.X, at.Y)))
                text.Draw(context, new Point(-Math.Min(text.Width, bounds.Width) / 2, -text.Height));
        }

        /// <summary>The column a lane is drawn in: six-fret's black and white frets share three.</summary>
        private static int Column(HighwayLane lane, int noteLane) => HighwayPalette.Column(lane.Kind, lane.LaneCount, noteLane);

        private static bool IsBlackFret(HighwayLane lane, ChartNote note) => HighwayPalette.IsBlackFret(lane.Kind, note.Lane);

        private static Color TargetColor(HighwayLane lane, int column) => HighwayPalette.TargetColor(lane.Kind, lane.LaneCount, column);

        private static Color HitColor(HighwayLane lane, int column) => HighwayPalette.HitColor(lane.Kind, lane.LaneCount, column);

        private static Color NoteColor(HighwayLane lane, ChartNote note) => HighwayPalette.NoteColor(lane.Kind, lane.LaneCount, note);

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance", Justification = "<Pending>")]
        private static IBrush VerticalGradient(params (double Offset, Color Color)[] stops) => new ImmutableLinearGradientBrush(
            [.. stops.Select(s => new ImmutableGradientStop(s.Offset, s.Color))],
            startPoint: new RelativePoint(0.5, 0, RelativeUnit.Relative),
            endPoint: new RelativePoint(0.5, 1, RelativeUnit.Relative));

        private IBrush Brush(Color color) => _gems.Brush(color);

        private IPen Pen(Color color, double thickness) => _gems.Pen(color, thickness);

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

        /// <summary>Asks to be called back when the screen is next drawn, while there's a highway to draw.</summary>
        private void RequestFrame()
        {
            if (_isFrameRequested || Scene is not { Lanes.Count: > 0 } || !IsVisible || TopLevel.GetTopLevel(this) is not { } topLevel)
                return;

            _isFrameRequested = true;
            topLevel.RequestAnimationFrame(OnFrame);
        }

        private void OnFrame(TimeSpan frame)
        {
            _isFrameRequested = false;
            if (TopLevel.GetTopLevel(this) is null)
                return;

            // Paused, nothing moves, so nothing is redrawn; nor is it while it's scrolled out of view, where a highway fading
            // to or from its whole chart is put where it's going.
            if (!IsEffectivelyVisible || !IsInViewport)
            {
                for (int i = 0; i < _fades.Count; i++)
                    _fades[i].Finish(IsOverviewShown(i));
                RequestFrame();
                return;
            }

            bool isFading = false;
            for (int i = 0; i < _fades.Count; i++)
                isFading |= _fades[i].Step(IsOverviewShown(i), frame);

            var clock = Clock?.Invoke() ?? TimeSpan.Zero;
            if (clock != _drawnAt || isFading)
            {
                _drawnAt = clock;
                Redraw();
            }

            RequestFrame();
        }

        /// <summary>A highway's whole-chart marks, and the highway, size and length of song they were laid out for.</summary>
        private sealed record OverviewMarks(HighwayLane Lane, Size Size, double Timeline, List<(Rect Rect, Color Color)> Marks);
    }
}
