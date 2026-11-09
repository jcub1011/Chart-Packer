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
/// <para>For drawing only: a key pressed or a tap is timed by the moment it happened, not the frame's.</para>
/// <para>Not thread-safe; for the thread that draws.</para>
/// </remarks>
public sealed class FramePacer
{
    // How many frames' times it learns the beat from, and the most a frame can take to count among them; and until it has,
    // how long after a frame is first asked for it can be asked for again as the same frame, as by another view drawn in it.
    private const int LearnFrames = 16;
    private const double MostFrameSeconds = 0.1;
    private const double LearningSameFrameSeconds = 0.003;

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

    private readonly double[] _learned = new double[LearnFrames];
    private int _learnedCount;
    private bool _started;
    private double _frame;
    private double _asked;
    private double _beat;
    private int _offBeats;

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

    /// <summary>Forgets the beat, to learn it again from the next frames: as when the screen it's drawn on changes.</summary>
    public void Reset()
    {
        _started = false;
        _offBeats = 0;
        _learnedCount = 0;
        _beat = 0;
        FrameSeconds = 0;
    }

    private double NextSeconds(double now)
    {
        if (!_started)
        {
            _started = true;
            _frame = _asked = now;
            return now;
        }

        // Asked again soon after the frame was, it's the same frame: by when it was asked, not the moment it's drawn for,
        // which can be up to half a frame from it.
        double sinceAsked = now - _asked;
        if (sinceAsked > LostSeconds || sinceAsked < -LostSeconds)
        {
            _frame = _asked = now;
            return now;
        }

        if (sinceAsked < (_beat > 0 ? _beat / 2 : LearningSameFrameSeconds))
            return _frame;

        // Learning the beat, each frame is drawn for when it's asked.
        if (_beat <= 0)
        {
            if (sinceAsked <= MostFrameSeconds)
                _learned[_learnedCount++] = sinceAsked;
            _frame = _asked = now;
            if (_learnedCount == LearnFrames)
                Learn();
            return now;
        }

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

    /// <summary>Takes up the beat from the times between the first frames.</summary>
    private void Learn()
    {
        // The screen's beat is the time between frames one beat apart, not the commonest: a program that keeps up with only
        // every other frame would have that taken for the beat, and the frames between drawn twice over. Those are the times
        // near the shortest, averaged, as each is longer or shorter as its frames were got round to.
        double shortest = _learned.Min();
        double sum = 0;
        int count = 0;
        foreach (double time in _learned)
        {
            if (time < shortest * 1.5)
            {
                sum += time;
                count++;
            }
        }

        _beat = sum / count;
        FrameSeconds = _beat;
    }
}
