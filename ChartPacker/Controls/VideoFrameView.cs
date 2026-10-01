using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ChartPacker.Core.Metadata;
using ChartPacker.Video;

namespace ChartPacker.Controls
{
    /// <summary>
    /// Draws a <see cref="VideoPlayback"/>, fitted inside the control like an image with
    /// <see cref="Stretch.Uniform"/>. Each frame the screen is drawn, it asks the playback for the
    /// frame due at <see cref="Clock"/> mapped through <see cref="Timeline"/>, so the video follows
    /// the clock whether it's running, paused or being dragged.
    /// </summary>
    /// <example><c>&lt;ctl:VideoFrameView Playback="{Binding Background.Video}" Timeline="{Binding Background.Timeline}" Clock="{Binding Background.Clock}" /&gt;</c></example>
    public sealed class VideoFrameView : Control
    {
        public static readonly StyledProperty<VideoPlayback?> PlaybackProperty =
            AvaloniaProperty.Register<VideoFrameView, VideoPlayback?>(nameof(Playback));

        public static readonly StyledProperty<VideoTimeline?> TimelineProperty =
            AvaloniaProperty.Register<VideoFrameView, VideoTimeline?>(nameof(Timeline));

        public static readonly StyledProperty<Func<TimeSpan>?> ClockProperty =
            AvaloniaProperty.Register<VideoFrameView, Func<TimeSpan>?>(nameof(Clock));

        private WriteableBitmap? _bitmap;
        private int _version;
        private bool _isFrameRequested;
        private bool _isInViewport = true;

        static VideoFrameView()
        {
            AffectsRender<VideoFrameView>(PlaybackProperty);
        }

        public VideoFrameView() => EffectiveViewportChanged += (_, e) => _isInViewport = e.EffectiveViewport.Intersects(new Rect(Bounds.Size));

        /// <summary>The video to draw, or <see langword="null"/> for nothing.</summary>
        public VideoPlayback? Playback
        {
            get => GetValue(PlaybackProperty);
            set => SetValue(PlaybackProperty, value);
        }

        /// <summary>How the video lines up with <see cref="Clock"/>; none means it plays alongside it.</summary>
        public VideoTimeline? Timeline
        {
            get => GetValue(TimelineProperty);
            set => SetValue(TimelineProperty, value);
        }

        /// <summary>Where the audio is; zero when not set.</summary>
        public Func<TimeSpan>? Clock
        {
            get => GetValue(ClockProperty);
            set => SetValue(ClockProperty, value);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == PlaybackProperty)
            {
                // A new video starts blank rather than showing the last one's frame.
                _bitmap?.Dispose();
                _bitmap = null;
                _version = 0;
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
            _bitmap?.Dispose();
            _bitmap = null;
            _version = 0;
        }

        public override void Render(DrawingContext context)
        {
            if (_bitmap is not { } bitmap || Bounds.Width <= 0 || Bounds.Height <= 0)
                return;

            var size = bitmap.Size;
            double scale = Math.Min(Bounds.Width / size.Width, Bounds.Height / size.Height);
            var drawn = new Size(size.Width * scale, size.Height * scale);
            var at = new Point((Bounds.Width - drawn.Width) / 2, (Bounds.Height - drawn.Height) / 2);
            context.DrawImage(bitmap, new Rect(size), new Rect(at, drawn));
        }

        /// <summary>Asks to be called back when the screen is next drawn, while there's a video to follow.</summary>
        private void RequestFrame()
        {
            if (_isFrameRequested || Playback is null || TopLevel.GetTopLevel(this) is not { } topLevel)
                return;

            _isFrameRequested = true;
            topLevel.RequestAnimationFrame(OnFrame);
        }

        private void OnFrame(TimeSpan _)
        {
            _isFrameRequested = false;
            if (Playback is not { } playback || TopLevel.GetTopLevel(this) is null)
                return;

            // Out of sight, the video isn't asked to keep up, so it isn't decoded; it seeks when it's seen again.
            if (!IsEffectivelyVisible || !_isInViewport)
            {
                RequestFrame();
                return;
            }

            var clock = Clock?.Invoke() ?? TimeSpan.Zero;
            playback.RequestTime((Timeline ?? VideoTimeline.Default).Map(clock, playback.Duration));

            if (playback.Width > 0 && playback.Height > 0 && CopyFrame(playback))
                InvalidateVisual();

            RequestFrame();
        }

        /// <summary>Copies the playback's newest frame into the bitmap drawn.</summary>
        /// <returns>Whether there was a newer frame.</returns>
        private bool CopyFrame(VideoPlayback playback)
        {
            int width = playback.Width;
            int height = playback.Height;
            if (!playback.HasFrameNewerThan(_version))
                return false;

            if (_bitmap is null || _bitmap.PixelSize.Width != width || _bitmap.PixelSize.Height != height)
            {
                _bitmap?.Dispose();
                _bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            }

            // Straight into the bitmap.
            using var buffer = _bitmap.Lock();
            return playback.TryCopyFrame(buffer.Address, buffer.RowBytes, width, height, ref _version);
        }
    }
}
