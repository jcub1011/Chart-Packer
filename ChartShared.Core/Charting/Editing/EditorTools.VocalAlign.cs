namespace ChartShared.Core.Charting.Editing;

// The Vocal Alignment tool's press in a vocal part's lane (charter-spec C3 item 7): every vocal part at once, shifted or
// stretched in time (VocalAlignment), the dragged edge snapping to the grid while snap is on.
public sealed partial class EditorTools
{
    private const string NothingToAlign = "There are no vocal notes or lyrics to align.";

    // With Shift held, a drag moves the notes this much of the way the pointer does, for fine work.
    private const double FineDrag = 0.25;

    /// <summary>
    /// What a press of the Vocal Alignment tool at <paramref name="point"/> takes hold of, or null when it isn't the tool,
    /// or there's nothing to align.
    /// </summary>
    public VocalAlignGrip? VocalAlignGripAt(VocalPoint point) =>
        _session.Tool == EditorTool.VocalAlign ? _session.VocalAlignment.GripAt(point.Tick, point.Tolerance) : null;

    private Gesture? PressVocalAlign(VocalPoint point)
    {
        if (_session.VocalAlignment.GripAt(point.Tick, point.Tolerance) is not { } grip
            || _session.VocalAlignment.Span is not { } span
            || _session.VocalAlignment.BaseSpan is not { } baseSpan)
        {
            _session.Status = NothingToAlign;
            return null;
        }

        return new VocalAlignGesture(this, point, grip, span, baseSpan);
    }

    /// <summary>
    /// Drags every vocal part being aligned: by its body, shifted in time; by the start's handle, stretched with the last
    /// note's end kept where it is; by the end's handle, stretched with the first note kept where it is. Each as far as the
    /// pointer moves in the audio's time, or a quarter of it with Shift held. While snap is on, the edge dragged (the first
    /// note, or the handle) goes to the grid's nearest step; the notes between go to the nearest tick. Nothing moves until
    /// the pointer has, so a click changes nothing.
    /// </summary>
    private sealed class VocalAlignGesture : Gesture
    {
        private readonly VocalAlignGrip _grip;
        private readonly (double Start, double End) _base;
        private readonly double _speed;
        private readonly double _pressedTick;
        private readonly double _tolerance;

        // Where the pointer was, and where the handles were, when the drag last changed between fine and not.
        private double _anchorPointer;
        private (double Start, double End) _anchor;
        private bool _fine;
        private bool _moved;

        public VocalAlignGesture(EditorTools tools, VocalPoint pressed, VocalAlignGrip grip, (double Start, double End) span, (double Start, double End) baseSpan)
            : base(tools)
        {
            _grip = grip;
            _base = baseSpan;
            _speed = Session.VocalAlignment.Speed;
            _pressedTick = pressed.Tick;
            _tolerance = pressed.Tolerance;
            _anchorPointer = Session.AudioAt(pressed.Tick);
            _anchor = span;
            _fine = tools._modifiers.HasFlag(EditorModifiers.Shift);
        }

        private VocalAlignment Alignment => Session.VocalAlignment;

        public override void Drag(ChartPoint point)
        {
        }

        public override void Drag(VocalPoint point)
        {
            double pointer = Session.AudioAt(point.Tick);
            bool fine = Tools._modifiers.HasFlag(EditorModifiers.Shift);
            if (fine != _fine)
            {
                // Shift pressed or let go: the drag goes on from here at the other rate, rather than jumping.
                _fine = fine;
                _anchorPointer = pointer;
                _anchor = Alignment.Span ?? _anchor;
                return;
            }

            _moved |= Math.Abs(point.Tick - _pressedTick) > _tolerance;
            if (!_moved)
                return;

            double delta = (pointer - _anchorPointer) * (fine ? FineDrag : 1);
            double length = _base.End - _base.Start;
            var (offset, speed) = _grip switch
            {
                VocalAlignGrip.Start => Stretched(Snapped(_anchor.Start + delta), _anchor.End, keepEnd: true),
                VocalAlignGrip.End => Stretched(_anchor.Start, Snapped(_anchor.End + delta), keepEnd: false),
                _ => (Snapped(_anchor.Start + delta) - _base.Start, _speed),
            };
            Alignment.Preview(offset, speed);

            // The dragged edge's time put on the grid's nearest step while snap is on; left as it is while it's off.
            double Snapped(double seconds) =>
                Tools.Snap.IsOn ? Session.AudioAt(Tools.Snap.Nearest(Session.TickAt(seconds), Tools.Map)) : seconds;

            // The offset and speed that put the first note at start and the last note's end at end, the speed kept within
            // bounds by moving the handle being dragged, so the other stays where it is.
            (double Offset, double Speed) Stretched(double start, double end, bool keepEnd)
            {
                double span = end - start;
                double speed = length <= 0 ? _speed
                    : span <= 0 ? VocalAlignment.MaxSpeed
                    : Math.Clamp(length / span, VocalAlignment.MinSpeed, VocalAlignment.MaxSpeed);
                if (keepEnd && length > 0)
                    start = end - (length / speed);
                return (start - _base.Start, speed);
            }
        }

        public override void Release(VocalPoint point)
        {
            Drag(point);
            if (_moved)
                Alignment.Commit();
            else
                Alignment.CancelPreview();
        }

        public override void Release(ChartPoint point) => Alignment.CancelPreview();

        public override void Cancel() => Alignment.CancelPreview();
    }
}
