namespace ChartPacker.Audio.Playback;

/// <summary>
/// Which frame of the mix each frame written to the output stands for, over the last few changes of speed. Frames
/// written at one speed make one segment, its frames standing for evenly spaced frames of the mix between its ends.
/// </summary>
/// <remarks>Keeps the latest <see cref="Capacity"/> segments, far more than an output queues. Not thread-safe.</remarks>
internal sealed class PositionMap
{
    private const int Capacity = 64;

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
    /// The frame of the mix <paramref name="outputFrame"/> stands for: before what's kept, where that starts; past what's
    /// been written, where it ends.
    /// </summary>
    public double SourceAt(long outputFrame)
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

            long into = Math.Min(outputFrame - segment.Output, segment.Frames);
            return segment.Source + ((segment.SourceEnd - segment.Source) * into / segment.Frames);
        }

        return _segments[_first].Source;
    }

    private record struct Segment(long Output, long Frames, double Source, double SourceEnd)
    {
        public readonly double Slope => (SourceEnd - Source) / Frames;
    }
}
