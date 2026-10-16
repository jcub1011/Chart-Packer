using ChartShared.Audio.Playback;
using ChartShared.Core.Charts;

namespace ChartShared.Audio.Synthesis;

/// <summary>
/// The metronome: a click on each beat of a tempo map, and an accented click on each measure's first beat
/// (charter-spec C10 item 3). Laid over playback after the time-stretcher, through <see cref="StemPlayer.Sounds"/>.
/// </summary>
/// <remarks>
/// A beat is placed in audio time, at its chart time plus the song's delay (<c>audio = chart + delay</c>,
/// charter-spec §4.4.1), so it lands where the chart's beat lines are drawn. Beats before the audio starts are left
/// out. The beats are worked out once, when it's made; a changed tempo map needs a new schedule.
/// </remarks>
public sealed class MetronomeSchedule : ISoundSchedule
{
    private readonly long[] _frames;
    private readonly bool[] _measures;
    private readonly SynthSound _beat;
    private readonly SynthSound _measure;

    /// <param name="tempoMap">The chart's tempo map.</param>
    /// <param name="delay">How much later than its ticks say the chart plays against the audio (<c>SongChartSettings.DelayOf</c>).</param>
    /// <param name="length">How long the audio is: there are no clicks past it.</param>
    /// <param name="sampleRate">The mix's rate, which the frames are counted at.</param>
    public MetronomeSchedule(TempoMap tempoMap, TimeSpan delay, TimeSpan length, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(tempoMap);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        _beat = Synth.Click(accented: false, sampleRate);
        _measure = Synth.Click(accented: true, sampleRate);

        var frames = new List<long>();
        var measures = new List<bool>();
        long lastTick = (long)Math.Ceiling(tempoMap.TickAt((length - delay).TotalSeconds));
        if (lastTick >= 0)
        {
            foreach (var line in tempoMap.BeatLines(0, lastTick))
            {
                double seconds = tempoMap.SecondsAt(line.Tick) + delay.TotalSeconds;
                if (seconds < 0 || seconds > length.TotalSeconds)
                    continue;

                frames.Add((long)Math.Round(seconds * sampleRate));
                measures.Add(line.IsMeasure);
            }
        }

        _frames = [.. frames];
        _measures = [.. measures];
    }

    /// <summary>How many clicks there are.</summary>
    public int Count => _frames.Length;

    /// <summary>The frame of the mix click <paramref name="index"/> starts at.</summary>
    public long FrameAt(int index) => _frames[index];

    /// <summary>Whether click <paramref name="index"/> is on a measure's first beat.</summary>
    public bool IsMeasureAt(int index) => _measures[index];

    public void SoundsBetween(long fromSourceFrame, long toSourceFrame, List<ScheduledSound> into)
    {
        ArgumentNullException.ThrowIfNull(into);

        int index = Array.BinarySearch(_frames, fromSourceFrame);
        if (index < 0)
            index = ~index;
        else
        {
            // The first of several clicks on one frame, should two beats round to it.
            while (index > 0 && _frames[index - 1] == fromSourceFrame)
                index--;
        }

        for (; index < _frames.Length && _frames[index] < toSourceFrame; index++)
            into.Add(new ScheduledSound(_frames[index], _measures[index] ? _measure : _beat));
    }
}
