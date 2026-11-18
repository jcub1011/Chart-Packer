using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using Avalonia.VisualTree;
using Serilog;

namespace ChartShared.UI.Controls.Skia
{
    /// <summary>What a <see cref="SkiaHost"/> draws: asked to record a frame's drawing, on the UI thread.</summary>
    public interface ISkiaContent
    {
        void Record(IHighwayCanvas canvas, SkiaHost host);
    }

    /// <summary>
    /// A control whose drawing SkiaSharp does on the render thread, through a composition visual of its own, rather than
    /// Avalonia recording it: <see cref="Redraw"/> has its <see cref="ISkiaContent"/> record a <see cref="DrawList"/>, and
    /// the render thread draws the latest one it's been handed, for as many frames as it's shown.
    /// </summary>
    /// <remarks>
    /// <para>The drawing is recorded just before Avalonia sends the frame's changes to the render thread, after layout, and
    /// goes in the same batch as them, so it's drawn with the same frame as everything else that changed, such as a layer's
    /// translation. Three things go in the batch with it:</para>
    /// <list type="bullet">
    /// <item>The list itself, as the visual's message, so the render thread takes up exactly the drawing made for that batch.
    /// <see cref="Redraw"/> sends a message first, before the batch is being made, which has the list recorded during it go in
    /// that batch too.</item>
    /// <item>Where it reaches, as the visual's offset and size: the render thread works out what's to be drawn again, and what
    /// a layer kept as a picture holds, from those before it reads any message.</item>
    /// <item>A render option that changes nothing, flipped each time, which is what has the render thread draw the visual
    /// again that same frame. A message is read after it has worked out what to draw, so the visual asking to be drawn again
    /// from one would be drawn a frame late, by when the rest of the frame has moved on: the lanes jumped.</item>
    /// </list>
    /// <para>Lists go round: the render thread gives back the one it was drawing when it takes up the next, and the UI thread
    /// records into those. None is made or let go once there are enough of them. It isn't hit-tested: the control it's in
    /// takes the pointer, if anything does.</para>
    /// </remarks>
    public sealed class SkiaHost : Control
    {
        private static readonly object Coming = new();
        private static readonly object Release = new();
        private static readonly RenderOptions Even = new() { RequiresFullOpacityHandling = null };
        private static readonly RenderOptions Odd = new() { RequiresFullOpacityHandling = false };
        private static long s_sequence;
        private static long s_playTicks;
        private static long s_plays;

        private readonly ISkiaContent _content;
        private readonly Action _record;
        private Handler? _handler;
        private CompositionCustomVisual? _visual;
        private Compositor? _compositor;
        private bool _queued;
        private bool _odd;

        public SkiaHost(ISkiaContent content)
        {
            _content = content;
            _record = Record;
            IsHitTestVisible = false;
        }

        /// <summary>
        /// How many drawings the render thread has played since the program began, every host's, and how long they took in all, in
        /// <see cref="Stopwatch"/> ticks: a readout takes the difference between two readings.
        /// </summary>
        public static (long Plays, long Ticks) Played => (Interlocked.Read(ref s_plays), Interlocked.Read(ref s_playTicks));

        /// <summary>Has the drawing recorded again, for the next frame Avalonia sends.</summary>
        public void Redraw()
        {
            if (_visual is null || _queued)
                return;
            _queued = true;

            // In this order: the recording, then the visual's messages, each run just before the batch is made. The message sent now
            // is the one the list recorded joins, so they're in the same batch.
            _compositor!.RequestCompositionUpdate(_record);
            _visual.SendHandlerMessage(Coming);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            Attach();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            if (_visual is not null)
            {
                // The render thread lets go of its drawings, and the pictures in them, as the visual goes.
                _visual.SendHandlerMessage(Release);
                ElementComposition.SetElementChildVisual(this, null);
            }

            _visual = null;
            _handler = null;
            _compositor = null;
            _queued = false;
            base.OnDetachedFromVisualTree(e);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            if (_visual is null)
                Attach();
            return finalSize;
        }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            Redraw();
        }

        private void Attach()
        {
            if (_visual is not null || ElementComposition.GetElementVisual(this) is not { } element)
                return;

            _compositor = element.Compositor;
            _handler = new Handler();
            _visual = _compositor.CreateCustomVisual(_handler);
            _visual.ClipToBounds = false;
            _visual.Size = default;
            ElementComposition.SetElementChildVisual(this, _visual);
            Redraw();
        }

        private void Record()
        {
            _queued = false;
            if (_handler is not { } handler || _visual is not { } visual)
                return;

            var list = handler.Take();
            list.Reset();
            list.GreyscaleText = IsOnKeptPicture();
            _content.Record(list, this);
            list.Sequence = Interlocked.Increment(ref s_sequence);

            // Where it reaches, out to whole units, as the visual's place and size; and the render option flipped, so it's drawn
            // again this frame. Whole units, so a layer kept as a picture has its pixels on the same grid page after page, and its thin
            // lines and text don't change their smoothing as the next page is drawn.
            var bounds = list.Bounds;
            double left = Math.Floor(bounds.Left), top = Math.Floor(bounds.Top);
            list.Origin = new Point(left, top);
            visual.Offset = new Vector3D(left, top, 0);
            visual.Size = bounds.Width > 0 && bounds.Height > 0 ? new Vector(Math.Ceiling(bounds.Right) - left, Math.Ceiling(bounds.Bottom) - top) : default;
            _odd = !_odd;
            visual.RenderOptions = _odd ? Odd : Even;
            visual.SendHandlerMessage(list);
        }

        /// <summary>
        /// Whether this is drawn on a picture Avalonia keeps of a control it's in, a layer cached as a bitmap without ClearType: Avalonia
        /// draws text there in greyscale, and so does this.
        /// </summary>
        private bool IsOnKeptPicture()
        {
            for (Visual? visual = this; visual is not null; visual = visual.GetVisualParent())
            {
                if (visual.CacheMode is BitmapCache { EnableClearType: false })
                    return true;
            }

            return false;
        }

        /// <summary>The render thread's side: takes up each drawing it's handed, gives back the last, and draws the latest.</summary>
        private sealed class Handler : CompositionCustomVisualHandler
        {
            private static bool s_warned;

            private readonly Lock _lock = new();
            private readonly Stack<DrawList> _free = new();
            private readonly DrawListPlayer _player = new();
            private DrawList? _front;
            private bool _released;

            /// <summary>A list to record into, on the UI thread: one given back, or a new one while there are too few going round.</summary>
            public DrawList Take()
            {
                lock (_lock)
                    return _free.TryPop(out var list) ? list : new DrawList();
            }

            public override void OnMessage(object message)
            {
                if (message is DrawList list)
                {
                    lock (_lock)
                    {
                        if (_released)
                        {
                            list.Reset();
                            return;
                        }

                        // Done with: given back for the UI thread to record into, which empties it then.
                        if (_front is { } done)
                            _free.Push(done);
                        _front = list;
                    }
                }
                else if (ReferenceEquals(message, Release))
                {
                    lock (_lock)
                    {
                        _released = true;
                        _front?.Reset();
                        _front = null;
                        while (_free.TryPop(out var free))
                            free.Reset();
                    }

                    _player.Dispose();
                }
            }

            public override void OnRender(ImmediateDrawingContext context)
            {
                if (_released || _front is not { Count: > 0 } front)
                    return;

                if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature)
                {
                    if (!s_warned)
                    {
                        s_warned = true;
                        Log.Warning("The renderer lends no SkiaSharp canvas; the highways can't be drawn");
                    }

                    return;
                }

                // The visual is placed where the drawing reaches (SkiaHost.Record): drawn from there, it's moved back by as much.
                long started = Stopwatch.GetTimestamp();
                var origin = front.Origin;
                using (var lease = feature.Lease())
                    _player.Play(front, lease.SkCanvas, lease.CurrentOpacity, (float)-origin.X, (float)-origin.Y);
                Interlocked.Add(ref s_playTicks, Stopwatch.GetTimestamp() - started);
                Interlocked.Increment(ref s_plays);
            }
        }
    }
}
