using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ChartShared.UI.Theming;

namespace ChartShared.UI.Controls.Highway
{
    /// <summary>
    /// Hosts a highway, and while <see cref="IsPerspective"/>, tilts it back as in YARG and Clone Hero. The highway stays the plane
    /// it always draws, taller than the host and tilted by the <see cref="PerspectiveCamera"/> with Avalonia's own 3D transform, so
    /// the pointer reaches it already undone from the tilt and whatever it does with the pointer works as it does flat. Above it the
    /// host stands the notes up (<see cref="IPerspectiveContent"/>), and, given a <see cref="FadeBrush"/>, over both fades the plane's
    /// far end into the background, so it doesn't look as if it just ends.
    /// </summary>
    /// <remarks>
    /// Its content is the highway, a control that is an <see cref="IPerspectiveContent"/>. Not perspective, the host lays the highway
    /// out as a plain panel does.
    /// </remarks>
    public sealed class PerspectiveHost : Panel
    {
        public static readonly StyledProperty<bool> IsPerspectiveProperty =
            AvaloniaProperty.Register<PerspectiveHost, bool>(nameof(IsPerspective));

        public static readonly StyledProperty<IBrush?> FadeBrushProperty =
            AvaloniaProperty.Register<PerspectiveHost, IBrush?>(nameof(FadeBrush));

        // The notes, and above them what follows the pointer, so the pointer's moving doesn't draw every note again.
        private readonly GemOverlay _overlay = new(GemLayer.Notes) { ZIndex = 1 };
        private readonly GemOverlay _marks = new(GemLayer.Marks) { ZIndex = 2 };
        private readonly Border _fade = new() { ZIndex = 3, IsHitTestVisible = false, IsVisible = false };

        // A brush recoloured in place, as a themed one fades, leaves the fade in its old colour: build it again.
        private readonly BrushWatch _fadeWatch;
        private PerspectiveCamera? _camera;

        static PerspectiveHost()
        {
            ClipToBoundsProperty.OverrideDefaultValue<PerspectiveHost>(true);
            AffectsMeasure<PerspectiveHost>(IsPerspectiveProperty);
            AffectsArrange<PerspectiveHost>(IsPerspectiveProperty, FadeBrushProperty);
        }

        public PerspectiveHost()
        {
            _fadeWatch = new BrushWatch(InvalidateArrange);
            Children.Add(_overlay);
            Children.Add(_marks);
            Children.Add(_fade);
        }

        /// <summary>Whether the highway is tilted back, with its notes standing up on it.</summary>
        public bool IsPerspective
        {
            get => GetValue(IsPerspectiveProperty);
            set => SetValue(IsPerspectiveProperty, value);
        }

        /// <summary>
        /// What the far end of the highway fades into: the background behind it, whose colour is used. None, and the host adds no fade,
        /// as for a highway that fades itself.
        /// </summary>
        public IBrush? FadeBrush
        {
            get => GetValue(FadeBrushProperty);
            set => SetValue(FadeBrushProperty, value);
        }

        // The highway, as the control it is and as what it draws standing notes through.
        private (Control Control, IPerspectiveContent Content)? Highway =>
            Children.FirstOrDefault(static c => c is IPerspectiveContent) is { } control ? (control, (IPerspectiveContent)control) : null;

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == FadeBrushProperty)
                _fadeWatch.Watch(FadeBrush);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var host = new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : 0, double.IsFinite(availableSize.Height) ? availableSize.Height : 0);
            var camera = CameraFor(host);
            var highway = Highway?.Control;
            foreach (var child in Children)
                child.Measure(ReferenceEquals(child, highway) ? (camera is null ? availableSize : new Size(host.Width, camera.PlaneHeight)) : host);
            return host;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var camera = CameraFor(finalSize);
            if (Highway is var (control, content))
            {
                content.Camera = camera;
                _overlay.Content = content;
                _marks.Content = content;
                if (camera is null)
                {
                    control.ClearValue(RenderTransformProperty);
                    control.ClearValue(RenderTransformOriginProperty);
                    control.Arrange(new Rect(finalSize));
                }
                else
                {
                    // The plane is tilted back about its bottom centre, the strike end, and reaches up past the host's top.
                    control.RenderTransformOrigin = new RelativePoint(0.5, 1, RelativeUnit.Relative);
                    control.RenderTransform = camera.Transform;
                    control.Arrange(new Rect(0, camera.PlaneTop, finalSize.Width, camera.PlaneHeight));
                }
            }

            _overlay.IsVisible = camera is not null;
            _overlay.Arrange(new Rect(finalSize));
            _marks.IsVisible = camera is not null;
            _marks.Arrange(new Rect(finalSize));
            _fade.Arrange(new Rect(finalSize));
            UpdateFade(camera);
            return finalSize;
        }

        /// <summary>The camera for a host of <paramref name="size"/>, kept while the size is; none when not perspective, or with no room.</summary>
        private PerspectiveCamera? CameraFor(Size size)
        {
            if (!IsPerspective || size.Width < 1 || size.Height < 1)
                return _camera = null;
            if (_camera is null || _camera.HostWidth != size.Width || _camera.HostHeight != size.Height)
                _camera = new PerspectiveCamera(size.Width, size.Height);
            return _camera;
        }

        /// <summary>A gradient down the host, from the background over the plane's far end to clear nearer.</summary>
        private void UpdateFade(PerspectiveCamera? camera)
        {
            if (camera is null || FadeBrush is not ISolidColorBrush { Color: var color })
            {
                _fade.IsVisible = false;
                return;
            }

            var stops = new GradientStops();
            foreach (var (offset, alpha) in camera.FadeStops())
                stops.Add(new GradientStop(Color.FromArgb((byte)Math.Round(alpha * color.A), color.R, color.G, color.B), offset));
            _fade.Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = stops,
            };
            _fade.IsVisible = true;
        }

        /// <summary>What a layer above the tilted highway draws.</summary>
        private enum GemLayer
        {
            /// <summary>The notes the highway stands up.</summary>
            Notes,

            /// <summary>What follows the pointer, standing up: the place tool's ghost, and the notes marked.</summary>
            Marks,
        }

        /// <summary>A layer above the tilted highway that what it stands up is drawn in.</summary>
        private sealed class GemOverlay : Control
        {
            private readonly GemLayer _layer;
            private IPerspectiveContent? _content;

            public GemOverlay(GemLayer layer)
            {
                _layer = layer;
                IsHitTestVisible = false;
            }

            public IPerspectiveContent? Content
            {
                get => _content;
                set
                {
                    if (ReferenceEquals(_content, value))
                        return;

                    if (_content is not null)
                    {
                        _content.GemsInvalidated -= InvalidateVisual;
                        _content.GemMarksInvalidated -= InvalidateVisual;
                    }

                    _content = value;
                    if (_content is not null)
                    {
                        // Each layer follows its own: the notes are drawn again as they change, the marks as the pointer does.
                        if (_layer == GemLayer.Notes)
                            _content.GemsInvalidated += InvalidateVisual;
                        else
                            _content.GemMarksInvalidated += InvalidateVisual;
                    }

                    InvalidateVisual();
                }
            }

            public override void Render(DrawingContext context)
            {
                if (_layer == GemLayer.Notes)
                    _content?.PaintGems(context);
                else
                    _content?.PaintGemMarks(context);
            }
        }
    }
}
