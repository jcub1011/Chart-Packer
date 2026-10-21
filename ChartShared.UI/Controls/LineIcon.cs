using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace ChartShared.UI.Controls
{
    /// <summary>
    /// A 16×16 line icon: <see cref="Data"/> is stroked, not filled, in the inherited text colour, so it
    /// follows its button through hover, pressed and disabled the way text does.
    /// </summary>
    /// <remarks>
    /// The geometry is authored on a 16×16 grid. Given a <c>Width</c> and <c>Height</c>, the shape is scaled to fit and
    /// centred, but the line keeps its <see cref="StrokeThickness"/>, so icons of every size have the same weight.
    /// </remarks>
    /// <example><c>&lt;ctl:LineIcon Data="{StaticResource SaveStroke}" /&gt;</c></example>
    public sealed class LineIcon : Control
    {
        private const double Size = 16;

        public static readonly StyledProperty<Geometry?> DataProperty =
            AvaloniaProperty.Register<LineIcon, Geometry?>(nameof(Data));

        public static readonly StyledProperty<IBrush?> ForegroundProperty =
            TextElement.ForegroundProperty.AddOwner<LineIcon>();

        public static readonly StyledProperty<double> StrokeThicknessProperty =
            AvaloniaProperty.Register<LineIcon, double>(nameof(StrokeThickness), 1.5);

        static LineIcon()
        {
            AffectsRender<LineIcon>(DataProperty, ForegroundProperty, StrokeThicknessProperty);
            VerticalAlignmentProperty.OverrideDefaultValue<LineIcon>(Avalonia.Layout.VerticalAlignment.Center);
        }

        public Geometry? Data
        {
            get => GetValue(DataProperty);
            set => SetValue(DataProperty, value);
        }

        public IBrush? Foreground
        {
            get => GetValue(ForegroundProperty);
            set => SetValue(ForegroundProperty, value);
        }

        public double StrokeThickness
        {
            get => GetValue(StrokeThicknessProperty);
            set => SetValue(StrokeThicknessProperty, value);
        }

        /// <summary>The line icon in the app's resources keyed <paramref name="key"/>, or null if there's none.</summary>
        public static Geometry? Find(string? key) =>
            key is not null && Application.Current?.TryFindResource(key, out object? found) == true ? found as Geometry : null;

        /// <summary>
        /// A new icon for the line icon keyed <paramref name="key"/>, or null if there's none. A control can be in one place
        /// only, so each menu item needs its own.
        /// </summary>
        public static LineIcon? For(string? key) => Find(key) is { } data ? new LineIcon { Data = data } : null;

        protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

        public override void Render(DrawingContext context)
        {
            if (Data is not { } data || Foreground is not { } brush)
                return;

            double scale = Math.Min(Bounds.Width, Bounds.Height) / Size;
            if (scale <= 0)
                return;

            // The pen is under the scale too, so it's thinned by as much as the shape is grown.
            var pen = new Pen(brush, StrokeThickness / scale, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            var fit = Matrix.CreateScale(scale, scale)
                * Matrix.CreateTranslation((Bounds.Width - Size * scale) / 2, (Bounds.Height - Size * scale) / 2);
            using (context.PushTransform(fit))
                context.DrawGeometry(null, pen, data);
        }
    }
}
