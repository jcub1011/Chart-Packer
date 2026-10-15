using ChartPacker.Audio.Playback;

namespace ChartPacker.Audio.Synthesis;

/// <summary>A note's hit sound, at a time in the audio.</summary>
/// <param name="AudioSeconds">When the note reaches the strike line, in audio time (<c>audio = chart + delay</c>).</param>
/// <param name="Kind">The sound: a fret, or the drum pad hit.</param>
public readonly record struct NoteHit(double AudioSeconds, SynthHit Kind);

/// <summary>
/// Hit sounds: a short sound on each note as it reaches the strike line (charter-spec C10 item 4), laid over playback
/// after the time-stretcher through <see cref="StemPlayer.Sounds"/>, as the metronome is.
/// </summary>
/// <remarks>
/// The notes of a chord sound once for each kind of sound, not once per note. The hits are worked out once, when it's
/// made; a changed chart needs a new schedule.
/// </remarks>
public sealed class HitSoundSchedule : ISoundSchedule
{
    private readonly long[] _frames;
    private readonly SynthSound[] _sounds;

    /// <param name="hits">The notes' hits, in any order. Those before the audio starts are left out.</param>
    /// <param name="sampleRate">The mix's rate, which the frames are counted at.</param>
    public HitSoundSchedule(IEnumerable<NoteHit> hits, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(hits);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);

        var sounds = new Dictionary<SynthHit, SynthSound>();
        var placed = new SortedSet<(long Frame, SynthHit Kind)>();
        foreach (var hit in hits)
        {
            if (hit.AudioSeconds < 0 || !double.IsFinite(hit.AudioSeconds))
                continue;
            placed.Add(((long)Math.Round(hit.AudioSeconds * sampleRate), hit.Kind));
            if (!sounds.ContainsKey(hit.Kind))
                sounds[hit.Kind] = Synth.Hit(hit.Kind, sampleRate);
        }

        _frames = [.. placed.Select(static p => p.Frame)];
        _sounds = [.. placed.Select(p => sounds[p.Kind])];
    }

    /// <summary>How many hits there are.</summary>
    public int Count => _frames.Length;

    public void SoundsBetween(long fromSourceFrame, long toSourceFrame, List<ScheduledSound> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        int index = Array.BinarySearch(_frames, fromSourceFrame);
        if (index < 0)
            index = ~index;
        while (index > 0 && _frames[index - 1] == fromSourceFrame)
            index--;
        for (; index < _frames.Length && _frames[index] < toSourceFrame; index++)
            into.Add(new ScheduledSound(_frames[index], _sounds[index]));
    }
}

/// <summary>
/// Several schedules laid over playback together, each at its own volume: the metronome and the hit sounds, which have
/// separate volumes (charter-spec §10).
/// </summary>
public sealed class CombinedSchedule : ISoundSchedule
{
    private readonly (ISoundSchedule Schedule, float Gain)[] _parts;
    private readonly List<ScheduledSound> _found = [];
    private readonly Lock _lock = new();

    /// <param name="parts">Each schedule and its volume, from 0. Those at 0 are left out.</param>
    public CombinedSchedule(params (ISoundSchedule Schedule, float Gain)[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        _parts = [.. parts.Where(static p => p.Schedule is not null && p.Gain > 0)];
    }

    /// <summary>Whether there's nothing to play.</summary>
    public bool IsEmpty => _parts.Length == 0;

    public void SoundsBetween(long fromSourceFrame, long toSourceFrame, List<ScheduledSound> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        int start = into.Count;
        lock (_lock)
        {
            foreach (var (schedule, gain) in _parts)
            {
                _found.Clear();
                schedule.SoundsBetween(fromSourceFrame, toSourceFrame, _found);
                foreach (var sound in _found)
                    into.Add(sound with { Gain = sound.Gain * gain });
            }
        }

        // In order, as the player asks.
        into.Sort(start, into.Count - start, Comparer<ScheduledSound>.Create(static (a, b) => a.SourceFrame.CompareTo(b.SourceFrame)));
    }
}
