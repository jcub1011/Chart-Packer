namespace ChartShared.Core.Charting.Editing;

// The Alignment tool's press in a lane (charter-spec C3 item 7): the part in focus, the vocal parts or an instrument's
// difficulties, shifted or stretched in time (Alignment), the dragged edge snapping to the grid while snap is on.
public sealed partial class EditorTools
{
    private const string LaneNotAligned = "This lane has no notes being aligned: press in a lane whose notes are outlined.";

    // With Shift held, a drag moves the notes this much of the way the pointer does, for fine work.
    private const double FineDrag = 0.25;

    /// <summary>
    /// What a press of the Alignment tool at <paramref name="point"/> in a vocal lane takes hold of, or null when it isn't
    /// the tool, the lane's part isn't aligned, or there's nothing to align.
    /// </summary>
    public AlignGrip? AlignGripAt(VocalPoint point) =>
        _session.Tool == EditorTool.Align && _session.Alignment.Aligns(point.Track) ? _session.Alignment.GripAt(point.Tick, point.Tolerance) : null;

    /// <summary>
    /// What a press of the Alignment tool at <paramref name="point"/> in an instrument's lane takes hold of, or null when
    /// it isn't the tool, the lane's track isn't aligned, or there's nothing to align.
    /// </summary>
    public AlignGrip? AlignGripAt(ChartPoint point) =>
        _session.Tool == EditorTool.Align && _session.Alignment.Aligns(point.Track) ? _session.Alignment.GripAt(point.Tick, point.Tolerance) : null;

    // A press in a lane, which takes hold only where the lane's part or track is one aligned (aligned), as the lane draws
    // the span and handles only there.
    private Gesture? PressAlign(bool aligned, double tick, double tolerance)
    {
        var alignment = _session.Alignment;

        // An offset or speed typed and still shown is kept first, so the drag goes on from it and the field's wait
        // doesn't keep the drag's halfway.
        if (alignment.IsPreviewing)
            alignment.Commit();

        if (alignment.Span is not { } span || alignment.BaseSpan is not { } baseSpan)
        {
            _session.Status = Alignment.NothingToAlign;
            return null;
        }

        if (!aligned || alignment.GripAt(tick, tolerance) is not { } grip)
        {
            _session.Status = LaneNotAligned;
            return null;
        }

        return new AlignGesture(this, tick, tolerance, grip, span, baseSpan);
    }

    /// <summary>
    /// Drags what's being aligned: by its body, shifted in time; by the start's handle, stretched with the last note's end
    /// kept where it is; by the end's handle, stretched with the first note kept where it is. Each as far as the pointer
    /// moves in the audio's time, or a quarter of it with Shift held. While snap is on, the edge dragged (the first note,
    /// or the handle) goes to the grid's nearest step; the notes between go to the nearest tick. Nothing moves until the
    /// pointer has, so a click changes nothing. It follows the pointer in a vocal lane or an instrument's alike.
    /// </summary>
    private sealed class AlignGesture : Gesture
    {
        private readonly AlignGrip _grip;
        private readonly (double Start, double End) _base;
        private readonly double _speed;
        private readonly double _pressedTick;
        private readonly double _tolerance;

        // Where the pointer was, and where the handles were, when the drag last changed between fine and not.
        private double _anchorPointer;
        private (double Start, double End) _anchor;
        private bool _fine;
        private bool _moved;

        public AlignGesture(EditorTools tools, double tick, double tolerance, AlignGrip grip, (double Start, double End) span, (double Start, double End) baseSpan)
            : base(tools)
        {
            _grip = grip;
            _base = baseSpan;
            _speed = Session.Alignment.Speed;
            _pressedTick = tick;
            _tolerance = tolerance;
            _anchorPointer = Session.AudioAt(tick);
            _anchor = span;
            _fine = tools._modifiers.HasFlag(EditorModifiers.Shift);
        }

        private Alignment Alignment => Session.Alignment;

        public override void Drag(ChartPoint point) => DragTo(point.Tick);

        public override void Drag(VocalPoint point) => DragTo(point.Tick);

        public override void Release(VocalPoint point) => Finish(point.Tick);

        public override void Release(ChartPoint point) => Finish(point.Tick);

        public override void Cancel() => Alignment.CancelPreview();

        private void Finish(double tick)
        {
            DragTo(tick);
            if (_moved)
                Alignment.Commit();
            else
                Alignment.CancelPreview();
        }

        private void DragTo(double tick)
        {
            double pointer = Session.AudioAt(tick);
            bool fine = Tools._modifiers.HasFlag(EditorModifiers.Shift);
            if (fine != _fine)
            {
                // Shift pressed or let go: the drag goes on from here at the other rate, rather than jumping.
                _fine = fine;
                _anchorPointer = pointer;
                _anchor = Alignment.Span ?? _anchor;
                return;
            }

            _moved |= Math.Abs(tick - _pressedTick) > _tolerance;
            if (!_moved)
                return;

            double delta = (pointer - _anchorPointer) * (fine ? FineDrag : 1);
            double length = _base.End - _base.Start;
            var (offset, speed) = _grip switch
            {
                AlignGrip.Start => Stretched(Snapped(_anchor.Start + delta), _anchor.End, keepEnd: true),
                AlignGrip.End => Stretched(_anchor.Start, Snapped(_anchor.End + delta), keepEnd: false),
                _ => (Snapped(_anchor.Start + delta) - _base.Start, _speed),
            };
            Alignment.Preview(offset, speed, by: this);

            // The dragged edge's time put on the grid's nearest step while snap is on; left as it is while it's off.
            double Snapped(double seconds) =>
                Tools.Snap.IsOn ? Session.AudioAt(Tools.Snap.Nearest(Session.TickAt(seconds), Tools.Map)) : seconds;

            // The offset and speed that put the first note at start and the last note's end at end, the speed kept within
            // bounds by moving the handle being dragged, so the other stays where it is.
            (double Offset, double Speed) Stretched(double start, double end, bool keepEnd)
            {
                double span = end - start;
                double speed = length <= 0 ? _speed
                    : span <= 0 ? Alignment.MaxSpeed
                    : Math.Clamp(length / span, Alignment.MinSpeed, Alignment.MaxSpeed);
                if (keepEnd && length > 0)
                    start = end - (length / speed);
                return (start - _base.Start, speed);
            }
        }
    }
}
