using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ChartPacker.Audio.Synthesis;

namespace ChartBuilder.Controls
{
    /// <summary>
    /// The calibration tool's light and highway (charter-spec C11 item 1): a tile that lights for 60 ms on each beat, and
    /// a note that scrolls down a small highway to reach the strike line on each beat. The first beat of each bar lights
    /// in another colour and has a wider note. Drawn every frame, at the beats' times on <see cref="Clock"/>.
    /// </summary>
    public sealed class CalibrationView : Control
    {
        public static readonly StyledProperty<CalibrationBeats?> BeatsProperty =
            AvaloniaProperty.Register<CalibrationView, CalibrationBeats?>(nameof(Beats));

        public static readonly StyledProperty<Func<double>?> ClockProperty =
            AvaloniaProperty.Register<CalibrationView, Func<double>?>(nameof(Clock));

        public static readonly StyledProperty<bool> ShowLightProperty =
            AvaloniaProperty.Register<CalibrationView, bool>(nameof(ShowLight), true);

        public static readonly StyledProperty<bool> ShowHighwayProperty =
            AvaloniaProperty.Register<CalibrationView, bool>(nameof(ShowHighway), true);

        // How long the light stays lit after each beat.
        private const double FlashSeconds = 0.060;

        // How long a note takes to scroll from the top of the highway to the strike line.
        private const double TravelSeconds = 1.2;

        // How far down the highway the strike line is.
        private const double StrikeAt = 0.85;

        private const double Gap = 24;

        private static readonly IBrush DarkTile = new ImmutableSolidColorBrush(Color.Parse("#1B1C26"));
        private static readonly IBrush LitTile = new ImmutableSolidColorBrush(Color.Parse("#F4F4F8"));
        private static readonly IBrush MarkedTile = new ImmutableSolidColorBrush(Color.Parse("#A78BFA"));
        private static readonly IBrush Lane = new ImmutableSolidColorBrush(Color.Parse("#14151D"));
        private static readonly IBrush Note = new ImmutableSolidColorBrush(Color.Parse("#D8D8E0"));
        private static readonly IPen Border = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#3A3C4E")), 1);
        private static readonly IPen StrikeLine = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#F4F4F8")), 3);

        private bool _frameRequested;
        private bool _attached;

        static CalibrationView()
        {
            AffectsRender<CalibrationView>(BeatsProperty, ShowLightProperty, ShowHighwayProperty);
        }

        public CalibrationBeats? Beats
        {
            get => GetValue(BeatsProperty);
            set => SetValue(BeatsProperty, value);
        }

        /// <summary>Now, in seconds on the clock the beats are on.</summary>
        public Func<double>? Clock
        {
            get => GetValue(ClockProperty);
            set => SetValue(ClockProperty, value);
        }

        public bool ShowLight
        {
            get => GetValue(ShowLightProperty);
            set => SetValue(ShowLightProperty, value);
        }

        public bool ShowHighway
        {
            get => GetValue(ShowHighwayProperty);
            set => SetValue(ShowHighwayProperty, value);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _attached = true;
            RequestFrame();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _attached = false;
        }

        public override void Render(DrawingContext context)
        {
            // Filled, if invisibly, so a click anywhere on the view is a tap.
            context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
            if (Beats is not { } beats || Clock is not { } clock)
                return;

            double now = clock();
            double height = Bounds.Height;
            double tile = Math.Max(0, Math.Min(height, (Bounds.Width - Gap) * 0.6));
            double lane = tile * 0.45;
            double width = (ShowLight ? tile : 0) + (ShowHighway ? lane : 0) + (ShowLight && ShowHighway ? Gap : 0);
            double x = Math.Max(0, (Bounds.Width - width) / 2);

            if (ShowLight)
            {
                DrawLight(context, beats, now, new Rect(x, (height - tile) / 2, tile, tile));
                x += tile + Gap;
            }

            if (ShowHighway)
                DrawHighway(context, beats, now, new Rect(x, 0, lane, height));
        }

        private static void DrawLight(DrawingContext context, CalibrationBeats beats, double now, Rect bounds)
        {
            long beat = beats.BeatAtOrBefore(now);
            bool lit = beats.Contains(beat) && now - beats.TimeOf(beat) < FlashSeconds;
            var fill = !lit ? DarkTile : beats.IsMarked(beat) ? MarkedTile : LitTile;
            context.DrawRectangle(fill, Border, bounds, 8, 8);
        }

        private static void DrawHighway(DrawingContext context, CalibrationBeats beats, double now, Rect bounds)
        {
            context.DrawRectangle(Lane, Border, bounds, 4, 4);
            double strikeY = bounds.Top + (bounds.Height * StrikeAt);

            using (context.PushClip(bounds))
            {
                // The notes from just past the line to the top of the highway.
                long last = beats.BeatAtOrBefore(now + TravelSeconds);
                for (long beat = Math.Max(0, beats.BeatAtOrBefore(now - 0.2)); beat <= last; beat++)
                {
                    if (!beats.Contains(beat))
                        continue;

                    double y = strikeY - ((beats.TimeOf(beat) - now) / TravelSeconds * (strikeY - bounds.Top));
                    bool marked = beats.IsMarked(beat);
                    double noteWidth = bounds.Width * (marked ? 0.85 : 0.5);
                    var note = new Rect(bounds.Center.X - (noteWidth / 2), y - 5, noteWidth, 10);
                    context.DrawRectangle(marked ? MarkedTile : Note, null, note, 3, 3);
                }
            }

            context.DrawLine(StrikeLine, new Point(bounds.Left, strikeY), new Point(bounds.Right, strikeY));
        }

        /// <summary>Has the view drawn again every frame while it's showing.</summary>
        private void RequestFrame()
        {
            if (_frameRequested || TopLevel.GetTopLevel(this) is not { } top)
                return;

            _frameRequested = true;
            top.RequestAnimationFrame(_ =>
            {
                _frameRequested = false;
                if (!_attached)
                    return;

                InvalidateVisual();
                RequestFrame();
            });
        }
    }
}
