using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using ChartBuilder.ViewModels;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using ChartShared.UI.Controls.Highway;

namespace ChartBuilder.Controls.Editing
{
    // A perspective highway: the canvas is the plane, which a PerspectiveHost tilts back, with its lanes, lines and sustain
    // tails drawn on it as they always are; the notes stand up in layers of the host's above it, which ask for them to be
    // drawn here (PaintGems, and PaintGemMarks for what follows the pointer) through the camera. Pointers reach the canvas
    // already undone from the tilt, by Avalonia, so the tools need to know nothing of it; only a gem, which stands up from
    // where the pointer finds its floor, is looked for (OnGem).
    public abstract partial class EditorCanvas : IPerspectiveContent
    {
        // How far a hit's tolerance is let grow, as a share of its pixels on the plane, where the tilt crowds the plane's
        // distances together towards its far end.
        private const double MinDepthScaleForHits = 0.35;

        // The smallest a label standing up beside the plane is drawn, as a share of its size, so the far ones can still be read.
        private const double MinLabelScale = 0.75;

        private readonly UprightGemPainter _upright;
        private readonly NotePainter _uprightNotes;
        private PerspectiveCamera? _camera;

        // How far, on the plane, the pointer was from the floor of the gem it pressed, so the drag that follows moves the note
        // by the pointer's movement and not by that as well.
        private Vector _pressOffset;

        /// <summary>
        /// The camera that tilts this canvas back, while it is a perspective highway; null while it is flat. Set by the host that
        /// tilts it, which also draws the notes this canvas stands up (<see cref="PaintGems"/>) over it.
        /// </summary>
        public PerspectiveCamera? Camera
        {
            get => _camera;
            set
            {
                if (ReferenceEquals(_camera, value))
                    return;

                // The notes are drawn in the layer on the plane, with their tails, or only the tails: both again.
                _camera = value;
                _upright.Camera = value;
                _pressOffset = default;

                // Tilted, the layers are not kept as pictures and their pages have no pixel limit.
                UpdatePaging();
                InvalidateLayers();
            }
        }

        /// <summary>Raised when the notes standing up on a perspective highway are to be drawn again.</summary>
        public event Action? GemsInvalidated;

        /// <summary>Raised when what follows the pointer, standing up on a perspective highway, is to be drawn again.</summary>
        public event Action? GemMarksInvalidated;

        private void RedrawGems()
        {
            if (_camera is not null)
                GemsInvalidated?.Invoke();
        }

        private void RedrawGemMarks()
        {
            if (_camera is not null)
                GemMarksInvalidated?.Invoke();
        }

        /// <summary>What is drawn standing up on a perspective highway: the lane as the canvas has laid it out now.</summary>
        private readonly record struct GemScene(EditorSession Session, EditorLaneViewModel Lane, ChartTrackKey Key, EditableTrack Track, EditablePart Part, IReadOnlyList<RailKind> Rows, LaneSlots Slots, LaneProjection Screen);

        /// <summary>The lane as it is to be drawn standing up, or none: not a perspective highway, or nothing to edit.</summary>
        private GemScene? SceneOfGems()
        {
            if (_camera is null || Session is not { } session || _lane is not { Vocal: null } lane || lane.Track is not { } key || session.TrackOf(key) is not { } track)
                return null;

            var size = Bounds.Size;
            var slots = session.SlotsOf(key.Instrument, View);
            var part = session.Chart.PartOf(key.Instrument)!;
            var rows = PhraseRail.RowsOf(part.Kind);
            Extras = ExtrasOf(size, key);
            var screen = Project(size, slots, session.Viewport, lane.Length, rows.Count);
            _upright.Camera = _camera;
            return new GemScene(session, lane, key, track, part, rows, slots, screen);
        }

        /// <summary>
        /// Draws the notes standing up on the plane: what the host's layer above the tilted canvas shows. The notes are placed on
        /// the canvas's own projection, which is the plane's, and <see cref="Camera"/> puts each where it is on the screen. What
        /// it draws is kept, for <see cref="OnGem"/> to find the pointer's note in.
        /// </summary>
        public void PaintGems(DrawingContext context)
        {
            _upright.BeginScene();
            if (SceneOfGems() is not { } scene)
                return;

            // A note is drawn from its start, but stands up from its floor: it's seen until its box has gone below the host's bottom,
            // well after its start has, so the ticks reach back that far, past the screen's start. (The painter holds the real cull.)
            double reach = _upright.ReachBelow(GemSize(scene.Screen)) / scene.Screen.PixelsPerSecond;
            var (fromTick, toTick) = TicksOf(scene.Session, scene.Screen.Widened(reach, 0));

            // All the text stands up, to be read: the measures' numbers, the tempo markers and the sections' names in the margin, a
            // proposed tempo map's labels, and the solos' counts. (The forcing marks are drawn with the notes.)
            var size = Bounds.Size;
            DrawBeats(context, scene.Session, scene.Session.TempoMap, scene.Screen, fromTick, toTick, size, RailPass.Text);
            DrawTempoMarkers(context, scene.Session, scene.Screen, fromTick, toTick, size, RailPass.Text);
            DrawTempoProposal(context, scene.Session, scene.Screen, size, RailPass.Text);
            DrawSections(context, scene.Session, scene.Screen, fromTick, toTick, size, RailPass.Text);
            FindPhrasesShown(scene.Part, fromTick, toTick);
            DrawSoloCounts(context, scene.Session, scene.Track, scene.Screen, fromTick, toTick, upright: true);
            DrawNoteSets(context, scene.Session, scene.Lane, scene.Key, scene.Track, scene.Part, scene.Screen, scene.Slots, fromTick, toTick, _uprightNotes, NotePass.Notes);
        }

        /// <summary>
        /// Draws what follows the pointer, as much of it as stands up on a perspective highway: the place tool's ghost, and the gems the
        /// erase tool would take away and a synchronized region syncs, marked over. The rest, a sustain's tail and the rail, is on the plane.
        /// </summary>
        public void PaintGemMarks(DrawingContext context)
        {
            if (SceneOfGems() is not { } scene)
                return;

            DrawHover(context, scene.Session, scene.Key, scene.Track, scene.Screen, scene.Slots, _upright);
            DrawEraseHover(context, scene.Session, scene.Key, scene.Track, scene.Part, scene.Rows, scene.Screen, scene.Slots, upright: true);
            DrawSyncNotes(context, scene.Session, scene.Key, scene.Track, scene.Screen, scene.Slots, upright: true);
            if (PlaytestOf(scene.Key) is { } run)
                DrawPlaytestMarks(context, scene.Session, scene.Key, run, scene.Slots, scene.Screen, upright: true);
        }

        /// <summary>
        /// Where on the plane the gem under the pointer is: the middle of its floor, the place the tools take a note to be at, when it is
        /// pointed at anywhere on the box that stands up from it; or <paramref name="position"/> itself when none is.
        /// </summary>
        /// <param name="position">A position on the plane, as the canvas is given pointers.</param>
        private Point OnGem(Point position) =>
            _camera is { } camera && _upright.PlaneAt(camera.Project(position)) is { } floor ? floor : position;

        /// <summary>
        /// How near a note's start, or a phrase bar's end in the rail, the pointer hits it, in pixels of the plane. On a perspective highway
        /// the tilt crowds the plane's distances together, so the same distance on the screen is more of the plane, further along it.
        /// </summary>
        private double HitPixelsAt(Point position) =>
            _camera is { } camera ? HitPixels / Math.Clamp(camera.DepthScaleAt(position.Y), MinDepthScaleForHits, 1) : HitPixels;

        /// <summary>
        /// Draws <paramref name="text"/> standing up beside a perspective highway, its bottom on the plane point <paramref name="foot"/>, as
        /// large as the plane is there but no smaller than <see cref="MinLabelScale"/>, so it can be read.
        /// </summary>
        /// <param name="rightAligned">Whether the text ends at <paramref name="foot"/>, rather than starts there.</param>
        /// <param name="raise">How far above the foot, in pixels of the text, its bottom is.</param>
        /// <param name="badge">What to fill behind it with, to set it off; none for none.</param>
        private void DrawStandingLabel(DrawingContext context, TextLayout text, Point foot, bool rightAligned, double raise = 0, IBrush? badge = null)
        {
            if (_camera is not { } camera)
                return;

            var at = camera.Project(foot);
            if (at.Y < -text.Height * 2 || at.Y > camera.HostHeight + (text.Height * 2))
                return;

            double scale = Math.Max(camera.ScaleAt(foot.Y), MinLabelScale);
            using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(at.X, at.Y)))
            {
                var corner = new Point(rightAligned ? -text.Width : 0, -text.Height - raise);
                if (badge is not null)
                    context.FillRectangle(badge, new Rect(corner, new Size(text.Width, text.Height)).Inflate(1));
                text.Draw(context, corner);
            }
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
