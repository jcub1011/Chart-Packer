using System;
using Avalonia;
using Avalonia.Media;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Editing;
using ChartShared.UI.Controls.Highway;

namespace ChartBuilder.Controls.Editing
{
    // A perspective highway: the canvas is the plane, which a PerspectiveHost tilts back, with its lanes, lines and sustain
    // tails drawn on it as they always are; the notes stand up in a layer of the host's above it, which asks for them to be
    // drawn here (PaintGems) through the camera. Pointers reach the canvas already undone from the tilt, by Avalonia, so the
    // tools need to know nothing of it.
    public abstract partial class EditorCanvas
    {
        private readonly UprightGemPainter _upright;
        private readonly NotePainter _uprightNotes;
        private PerspectiveCamera? _camera;

        /// <summary>
        /// The camera that tilts this canvas back, while it is a perspective highway; null while it is flat. Set by the host that
        /// tilts it, which also draws the notes this canvas stands up (<see cref="PaintGems"/>) over it.
        /// </summary>
        internal PerspectiveCamera? Camera
        {
            get => _camera;
            set
            {
                if (ReferenceEquals(_camera, value))
                    return;

                // The notes are drawn in the layer on the plane, with their tails, or only the tails: both again.
                _camera = value;
                _upright.Camera = value;
                InvalidateLayers();
            }
        }

        /// <summary>Raised when the notes standing up on a perspective highway are to be drawn again.</summary>
        internal event Action? GemsInvalidated;

        private void RedrawGems()
        {
            if (_camera is not null)
                GemsInvalidated?.Invoke();
        }

        /// <summary>
        /// Draws the notes standing up on the plane: what the host's layer above the tilted canvas shows. The notes are placed on
        /// the canvas's own projection, which is the plane's, and <see cref="Camera"/> puts each where it is on the screen.
        /// </summary>
        internal void PaintGems(DrawingContext context)
        {
            if (_camera is null || Session is not { } session || _lane is not { Vocal: null } lane || lane.Track is not { } key || session.TrackOf(key) is not { } track)
                return;

            var size = Bounds.Size;
            var slots = session.SlotsOf(key.Instrument, View);
            var part = session.Chart.PartOf(key.Instrument)!;
            var rows = PhraseRail.RowsOf(part.Kind);
            Extras = ExtrasOf(size, key);
            var screen = Project(size, slots, session.Viewport, lane.Length, rows.Count);
            var (fromTick, toTick) = TicksOf(session, screen);
            _upright.Camera = _camera;
            DrawNoteSets(context, session, lane, key, track, part, screen, slots, fromTick, toTick, _uprightNotes, NotePass.Notes);
        }

        /// <summary>The marks of a forced or tapped chord, beside it, as <see cref="DrawForcing"/> draws them but standing up: H, S and T, the smaller the further they are.</summary>
        private void DrawForcingUpright(DrawingContext context, LaneProjection projection, LaneSlots slots, double along, ChordFlags flags)
        {
            if (_camera is not { } camera)
                return;

            string marks = (flags.HasFlag(ChordFlags.ForceHopo) ? "H" : "") + (flags.HasFlag(ChordFlags.ForceStrum) ? "S" : "") + (flags.HasFlag(ChordFlags.Tap) ? "T" : "");
            var text = Label(marks, 9, AccentBrush ?? DefaultAccentBrush);
            double edge = projection.LaneFrom + (projection.LaneSize * slots.Slots.Count);
            var at = camera.Project(new Point(edge + 2, along));
            double scale = camera.ScaleAt(along);

            // Drawn at the size of the lanes there, from the point the lane's edge is at, raised to about a gem's height.
            using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(at.X, at.Y)))
            {
                var corner = new Point(0, -text.Height);
                context.FillRectangle(BadgeBrush, new Rect(corner, new Size(text.Width, text.Height)).Inflate(1));
                text.Draw(context, corner);
            }
        }
    }
}
