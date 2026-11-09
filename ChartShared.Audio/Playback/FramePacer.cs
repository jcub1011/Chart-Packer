namespace ChartShared.Audio.Playback;

/// <summary>
/// The moment each frame is drawn for, on an even beat of the screen's frames, from the moments it's asked as the frames are
/// drawn: for drawing what moves with the song as evenly as the screen shows it.
/// </summary>
/// <remarks>
/// <para>The screen shows a frame every so often, evenly; the program is asked for one each time, and gets round to drawing
/// it a little after, sooner or later as it's busy. Drawn for the moment it gets round to it, a highway moves on unevenly
/// from frame to frame, though the screen shows them evenly: a judder. Drawn for the screen's beat it moves on evenly.</para>
/// <para>It learns the time between the screen's frames from the first few, then puts each new one on the next beat, or
/// the one after for a frame missed, drawn a little toward when it was really asked, so the beat follows the screen's however the two
/// clocks drift. Asked again within half a frame, it's the same frame, so everything drawn in a frame is drawn for one
/// moment. Asked between frames, as by a pointer moving the view, it's that moment, and the beat is left as it was. Asked
/// after a long while, as after a pause, it starts the beat again from then.</para>
/// <para>It goes on timing the frames once it has the beat, and learns it again when the screen's has changed, as when the
/// window is moved to a screen of another rate: kept, a faster screen's frames would be taken two at a time for one.</para>
/// <para>For drawing only: a key pressed or a tap is timed by the moment it happened, not the frame's.</para>
/// <para>Not thread-safe; for the thread that draws.</para>
/// </remarks>
public sealed class FramePacer
{
    // How many frames' times it learns the beat from, and checks it by after; the most a frame can take to count among them;
    // and how long after a read the next must come to be timed, and, until it has the beat, to be a frame of its own rather
    // than the same one, as read by another view drawn in it.
    private const int LearnFrames = 16;
    private const double MostFrameSeconds = 0.1;
    private const double LearningSameFrameSeconds = 0.003;

    // How near a whole number of beats, as a share of the beat, a frame's time must be to fit it, as near as a frame must be
    // asked to its beat to be drawn on it; and how many of the times must fit a beat for it to be the screen's.
    private const double FitShare = 0.25;
    private const double LeastFitShare = 0.75;

    // How far from the beat it has, as a share of it, the times between frames must say the screen's is before it learns it
    // again, twice running, the second time the same as the first; nearer, the beat's pull follows it. Twice, so frames
    // got round to unusually unevenly for a while aren't taken for a new screen.
    private const double NewBeatShare = 0.1;

    // How far toward when a frame was really asked each one's moment is drawn, and its beat with it: a little, so the frames
    // follow the screen without taking on the program's unevenness. The beat's pull is the quarter of the square of the
    // moment's that has them settle as fast as they can without overshooting, and swinging to and fro.
    private const double PhasePull = 0.05;
    private const double BeatPull = PhasePull * PhasePull / 4;

    // How long without a frame before the beat is started again from the next.
    private const double LostSeconds = 0.25;

    // How far from the beat, as a share of it, a frame can be asked for and be drawn on it; and how many asked for further off
    // in a row before the beat is started again from the last. One asked for between frames, as by a pointer moving the view,
    // is no frame of the screen's, and drawn on the beat, the next frame would be taken for the same one.
    private const double OffBeatShare = 0.25;
    private const int OffBeatsToRestart = 3;

    private readonly double[] _timed = new double[LearnFrames];
    private int _timedCount;
    private bool _started;
    private double _frame;
    private double _asked;
    private double _lastTimed;
    private double _beat;
    private int _offBeats;

    // The beat the times between frames last said the screen's was, some way from the one it has; 0 when they last agreed.
    private double _newBeat;

    /// <summary>The time between frames it has learned, in seconds; 0 until it has.</summary>
    public double FrameSeconds { get; private set; }

    /// <summary>
    /// The moment to draw the frame being drawn for, asked at <paramref name="now"/>, as a timestamp of a clock that counts
    /// <paramref name="frequency"/> a second, as <paramref name="now"/> is.
    /// </summary>
    public long Next(long now, long frequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        double seconds = (double)now / frequency;
        return (long)Math.Round(NextSeconds(seconds) * frequency);
    }

    /// <summary>
    /// The screen's beat, from the times between <paramref name="gaps"/>' reads: 0 when no beat fits enough of them.
    /// </summary>
    /// <remarks>
    /// <para>Not the commonest time: a program that keeps up with only every other frame would have that taken for the beat, and
    /// the frames between drawn twice over. So half the commonest is tried too, when the commonest fits most of them, and kept
    /// when it fits more.</para>
    /// <para>Nor the shortest: a view that reads the clock as it's drawn, after the others have in the frame's first moments,
    /// splits that frame's time in two, and the shorter part isn't a frame. So when no beat fits the times one by one, they're
    /// tried two by two.</para>
    /// <para>Timing alone can't tell a read halfway through every frame from a screen twice as fast. Taken for the faster,
    /// those views are drawn half a frame on from the rest; the other way, every other frame of a faster screen is drawn
    /// again, which judders. So a beat that fits the times one by one is kept, the shorter.</para>
    /// </remarks>
    internal static double BeatOf(ReadOnlySpan<double> gaps)
    {
        Span<double> sums = stackalloc double[gaps.Length];
        Span<double> sorted = stackalloc double[gaps.Length];
        double best = 0, bestFit = 0;

        // In order of preference: a later way kept only when it fits more. Two by two only when no beat fits the times one by one:
        // even frames' times two by two are two beats, and fit better than one by one, the unevenness of each pair's middle
        // read gone.
        for (int group = 1; group <= 2; group++)
        {
            if (group > 1 && bestFit >= LeastFitShare)
                break;

            for (int phase = 0; phase < group; phase++)
            {
                int count = 0;
                for (int i = phase; i + group <= gaps.Length; i += group)
                {
                    double sum = 0;
                    for (int j = 0; j < group; j++)
                        sum += gaps[i + j];
                    sums[count++] = sum;
                }

                if (count == 0)
                    continue;
                var times = sums[..count];
                var ordered = sorted[..count];
                times.CopyTo(ordered);
                ordered.Sort();
                double median = count % 2 == 1 ? ordered[count / 2] : (ordered[(count / 2) - 1] + ordered[count / 2]) / 2;

                double whole = Fit(times, median, out double beat);
                Keep(whole, beat);
                if (whole >= 0.5)
                {
                    double half = Fit(times, median / 2, out beat);
                    Keep(half, beat);
                }
            }
        }

        return bestFit >= LeastFitShare ? best : 0;

        void Keep(double fit, double beat)
        {
            if (fit > bestFit)
            {
                bestFit = fit;
                best = beat;
            }
        }
    }

    /// <summary>
    /// The share of <paramref name="times"/> within <see cref="FitShare"/> of a whole number of <paramref name="beat"/>s, or that
    /// run together with those after them to one beat; and <paramref name="refined"/>, the beat they say, from those that fit.
    /// </summary>
    private static double Fit(ReadOnlySpan<double> times, double beat, out double refined)
    {
        refined = 0;
        if (beat <= 0)
            return 0;

        int fits = 0;
        double total = 0, beats = 0;
        for (int i = 0; i < times.Length; i++)
        {
            double share = times[i] / beat;
            double whole = Math.Round(share);
            if (whole >= 1 && Math.Abs(share - whole) <= FitShare)
            {
                fits++;
                total += times[i];
                beats += whole;
                continue;
            }

            // A part of a frame's time, split by a read late in it: it and those after it that make one beat are that frame's.
            double sum = times[i];
            int last = i;
            while (sum / beat < 1 - FitShare && last + 1 < times.Length)
                sum += times[++last];
            if (last > i && Math.Abs((sum / beat) - 1) <= FitShare)
            {
                fits += last - i + 1;
                total += sum;
                beats++;
                i = last;
            }
        }

        if (fits == 0)
            return 0;
        refined = total / beats;
        return (double)fits / times.Length;
    }

    private double NextSeconds(double now)
    {
        if (!_started)
        {
            _started = true;
            _frame = _asked = _lastTimed = now;
            return now;
        }

        // Asked again soon after the frame was, it's the same frame: by when it was asked, not the moment it's drawn for,
        // which can be up to half a frame from it.
        double sinceAsked = now - _asked;
        if (sinceAsked > LostSeconds || sinceAsked < -LostSeconds)
        {
            _frame = _asked = _lastTimed = now;
            return now;
        }

        // Each read a little after the last is timed, to learn the beat from, and once it's learned, to see that the screen's
        // hasn't changed. Learned, the frame is drawn for when it was asked, and the beat starts from it.
        double sinceTimed = now - _lastTimed;
        if (sinceTimed >= LearningSameFrameSeconds)
        {
            _lastTimed = now;
            if (sinceTimed <= MostFrameSeconds && Time(sinceTimed))
            {
                _frame = _asked = now;
                return now;
            }
        }

        // Learning the beat, each frame is drawn for when it's asked.
        if (_beat <= 0)
        {
            if (sinceAsked < LearningSameFrameSeconds)
                return _frame;
            _frame = _asked = now;
            return now;
        }

        if (sinceAsked < _beat / 2)
            return _frame;

        // The beat it's nearest, at least the next; drawn for a little toward when it was asked, and the beat with it. Far off
        // the beat, it's asked for between frames, and drawn for when it's asked, the beat left as it was; unless frames keep
        // coming off it, when the screen's beat has moved, and it starts again from here.
        double frames = Math.Max(1, Math.Round((now - _frame) / _beat));
        double due = _frame + (frames * _beat);
        double late = now - due;
        if (Math.Abs(late) > _beat * OffBeatShare)
        {
            if (++_offBeats < OffBeatsToRestart)
                return now;
            _offBeats = 0;
            _frame = _asked = now;
            return now;
        }

        _offBeats = 0;
        _frame = due + (late * PhasePull);
        _beat += late * BeatPull / frames;
        FrameSeconds = _beat;
        _asked = now;
        return _frame;
    }

    /// <summary>
    /// Adds <paramref name="gap"/> to the times between frames; with enough of them, takes up the beat they say, or, once it has
    /// one, forgets it to learn it again when they say the screen's is some way from it. Whether it has just learned the beat.
    /// </summary>
    private bool Time(double gap)
    {
        _timed[_timedCount++] = gap;
        if (_timedCount < LearnFrames)
            return false;

        _timedCount = 0;
        double beat = BeatOf(_timed);
        if (beat <= 0)
            return false;

        if (_beat > 0)
        {
            if (Math.Abs((beat / _beat) - 1) <= NewBeatShare)
            {
                _newBeat = 0;
                return false;
            }

            // Said twice, the screen's beat has changed: learned again from the next frames, all of the new beat, rather than
            // from these, some of which may be of the old.
            if (_newBeat > 0 && Math.Abs((beat / _newBeat) - 1) <= NewBeatShare)
            {
                _beat = 0;
                _newBeat = 0;
                FrameSeconds = 0;
                _offBeats = 0;
            }
            else
            {
                _newBeat = beat;
            }

            return false;
        }

        _beat = beat;
        FrameSeconds = beat;
        return true;
    }
}
