using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ChartPacker.Controls
{
    /// <summary>
    /// A size, drawn as one bar in three parts: what's kept, what's saved for certain, and what's only predicted to be
    /// saved. The Optimize window draws one for the library and one on each chart.
    /// </summary>
    /// <remarks>
    /// The parts are shares of the bar's width, 0 to 1, and what they don't cover is left as the track. It draws straight
    /// with its brushes, so it follows the theme as it's recoloured.
    /// </remarks>
    public sealed class SizeBarView : Control
    {
        public static readonly StyledProperty<double> KeptProperty =
            AvaloniaProperty.Register<SizeBarView, double>(nameof(Kept));

        public static readonly StyledProperty<double> SavedProperty =
            AvaloniaProperty.Register<SizeBarView, double>(nameof(Saved));

        public static readonly StyledProperty<double> PredictedProperty =
            AvaloniaProperty.Register<SizeBarView, double>(nameof(Predicted));

        public static readonly StyledProperty<IBrush?> TrackBrushProperty =
            AvaloniaProperty.Register<SizeBarView, IBrush?>(nameof(TrackBrush));

        public static readonly StyledProperty<IBrush?> KeptBrushProperty =
            AvaloniaProperty.Register<SizeBarView, IBrush?>(nameof(KeptBrush));

        public static readonly StyledProperty<IBrush?> SavedBrushProperty =
            AvaloniaProperty.Register<SizeBarView, IBrush?>(nameof(SavedBrush));

        public static readonly StyledProperty<IBrush?> PredictedBrushProperty =
            AvaloniaProperty.Register<SizeBarView, IBrush?>(nameof(PredictedBrush));

        static SizeBarView()
        {
            AffectsRender<SizeBarView>(
                KeptProperty, SavedProperty, PredictedProperty, TrackBrushProperty, KeptBrushProperty, SavedBrushProperty, PredictedBrushProperty);
        }

        /// <summary>The share that's kept: what the size comes down to.</summary>
        public double Kept
        {
            get => GetValue(KeptProperty);
            set => SetValue(KeptProperty, value);
        }

        /// <summary>The share that's saved, as measured or as done.</summary>
        public double Saved
        {
            get => GetValue(SavedProperty);
            set => SetValue(SavedProperty, value);
        }

        /// <summary>The share that's predicted to be saved, from files like these that were measured.</summary>
        public double Predicted
        {
            get => GetValue(PredictedProperty);
            set => SetValue(PredictedProperty, value);
        }

        public IBrush? TrackBrush
        {
            get => GetValue(TrackBrushProperty);
            set => SetValue(TrackBrushProperty, value);
        }

        public IBrush? KeptBrush
        {
            get => GetValue(KeptBrushProperty);
            set => SetValue(KeptBrushProperty, value);
        }

        public IBrush? SavedBrush
        {
            get => GetValue(SavedBrushProperty);
            set => SetValue(SavedBrushProperty, value);
        }

        public IBrush? PredictedBrush
        {
            get => GetValue(PredictedBrushProperty);
            set => SetValue(PredictedBrushProperty, value);
        }

        public override void Render(DrawingContext context)
        {
            var bounds = new Rect(Bounds.Size);
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return;

            double radius = Math.Min(bounds.Height, bounds.Width) / 2;
            using (context.PushGeometryClip(new RectangleGeometry(bounds, radius, radius)))
            {
                if (TrackBrush is { } track)
                    context.FillRectangle(track, bounds);

                double x = 0;
                x = Part(context, KeptBrush, x, Kept, bounds);
                x = Part(context, SavedBrush, x, Saved, bounds);
                Part(context, PredictedBrush, x, Predicted, bounds);
            }
        }

        // Draws one part from x, and gives where the next starts. A part too thin to see still gets a pixel, so a small
        // saving on a large chart doesn't vanish.
        private static double Part(DrawingContext context, IBrush? brush, double x, double share, Rect bounds)
        {
            if (brush is null || !(share > 0) || x >= bounds.Width)
                return x;

            double width = Math.Min(bounds.Width - x, Math.Max(1, Math.Min(1, share) * bounds.Width));
            context.FillRectangle(brush, new Rect(x, 0, width, bounds.Height));
            return x + width;
        }
    }
}
