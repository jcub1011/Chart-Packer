namespace ChartPacker.Audio.Playback;

/// <summary>
/// Which frame of the mix each frame written to the output stands for, over the last few changes of speed. Frames
/// written at one speed make one segment, its frames standing for evenly spaced frames of the mix between its ends.
/// </summary>
/// <remarks>
/// <para>Stretched, the frames played follow the grains, so a segment is a few milliseconds of output and the mix can
/// even step back a little from one to the next (<see cref="Dsp.TimeStretcher.PlayedPosition"/>). Fed where the speed
/// says the mix has got to instead (<see cref="Dsp.TimeStretcher.SourcePosition"/>), a segment runs on for as long as the
/// speed does.</para>
/// <para>Keeps the latest <see cref="Capacity"/> segments, far more than an output queues. Not thread-safe.</para>
/// </remarks>
internal sealed class PositionMap
{
    internal const int Capacity = 256;

    // A ring, oldest at _first.
    private readonly Segment[] _segments = new Segment[Capacity];
    private int _first;
    private int _count;

    /// <summary>Starts again: output frame <paramref name="outputFrame"/> stands for <paramref name="sourceFrame"/>, and what came before is forgotten.</summary>
    public void Reset(long outputFrame, double sourceFrame)
    {
        _first = 0;
        _count = 1;
        _segments[0] = new Segment(outputFrame, 0, sourceFrame, sourceFrame);
    }

    /// <summary>
    /// The <paramref name="frames"/> frames written from <paramref name="outputFrame"/> on stand for the mix from
    /// <paramref name="sourceStart"/> to <paramref name="sourceEnd"/>.
    /// </summary>
    public void Add(long outputFrame, int frames, double sourceStart, double sourceEnd)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frames);
        if (_count > 0)
        {
            ref var last = ref _segments[(_first + _count - 1) % Capacity];

            // Nothing written yet since a reset: this takes its place.
            if (last.Frames == 0)
            {
                last = new Segment(outputFrame, frames, sourceStart, sourceEnd);
                return;
            }

            // Carrying straight on at the same speed, it's more of the same segment.
            bool follows = outputFrame == last.Output + last.Frames && Math.Abs(sourceStart - last.SourceEnd) < 1e-6;
            double slope = (sourceEnd - sourceStart) / frames;
            if (follows && Math.Abs(slope - last.Slope) < 1e-9)
            {
                last.Frames += frames;
                last.SourceEnd = sourceEnd;
                return;
            }
        }

        if (_count == Capacity)
        {
            _first = (_first + 1) % Capacity;
            _count--;
        }

        _segments[(_first + _count) % Capacity] = new Segment(outputFrame, frames, sourceStart, sourceEnd);
        _count++;
    }

    /// <summary>
    /// The frame of the mix <paramref name="outputFrame"/> stands for, which may be between frames: before what's kept,
    /// where that starts; past what's been written, where it ends.
    /// </summary>
    public double SourceAt(double outputFrame)
    {
        if (_count == 0)
            return 0;

        for (int i = _count - 1; i >= 0; i--)
        {
            var segment = _segments[(_first + i) % Capacity];
            if (outputFrame < segment.Output)
                continue;
            if (segment.Frames == 0)
                return segment.Source;

            double into = Math.Min(outputFrame - segment.Output, segment.Frames);
            return segment.Source + ((segment.SourceEnd - segment.Source) * into / segment.Frames);
        }

        return _segments[_first].Source;
    }

    /// <summary>
    /// The first output frame kept that stands for <paramref name="sourceFrame"/> of the mix, between frames where it
    /// falls between two: before what's kept, where that starts; past what's been written, where it ends.
    /// </summary>
    public double OutputAt(double sourceFrame)
    {
        if (_count == 0)
            return 0;

        for (int i = 0; i < _count; i++)
        {
            var segment = _segments[(_first + i) % Capacity];
            if (i == 0 && sourceFrame <= Math.Min(segment.Source, segment.SourceEnd))
                return segment.Output;
            if (segment.Frames == 0 || sourceFrame < Math.Min(segment.Source, segment.SourceEnd) || sourceFrame > Math.Max(segment.Source, segment.SourceEnd))
                continue;

            double span = segment.SourceEnd - segment.Source;
            return span == 0 ? segment.Output : segment.Output + ((sourceFrame - segment.Source) / span * segment.Frames);
        }

        var last = _segments[(_first + _count - 1) % Capacity];
        return last.Output + last.Frames;
    }

    private record struct Segment(long Output, long Frames, double Source, double SourceEnd)
    {
        public readonly double Slope => (SourceEnd - Source) / Frames;
    }
}
