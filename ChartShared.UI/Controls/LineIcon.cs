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
    /// <remarks>The geometry is drawn as authored, on a 16×16 grid, without stretching.</remarks>
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

        protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

        public override void Render(DrawingContext context)
        {
            if (Data is not { } data || Foreground is not { } brush)
                return;

            var pen = new Pen(brush, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            context.DrawGeometry(null, pen, data);
        }
    }
}
