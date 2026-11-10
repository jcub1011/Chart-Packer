using BenchmarkDotNet.Attributes;
using ChartShared.Audio.Playback;
using ChartShared.Audio.Synthesis;

namespace PlaybackBench;

/// <summary>What the playback thread asks for for each 20 ms chunk while hit sounds and the metronome are on.</summary>
[MemoryDiagnoser]
public class AudioChunkBenchmarks
{
    private const int SampleRate = 44100;
    private const int Chunk = SampleRate / 50;

    private CombinedSchedule _schedule = null!;
    private readonly List<ScheduledSound> _into = [];
    private long _at;

    [GlobalSetup]
    public void Setup()
    {
        var hits = new List<NoteHit>();
        for (int i = 0; i < 4000; i++)
            hits.Add(new NoteHit(1 + (i * 0.08), i % 3 == 0 ? SynthHit.Kick : SynthHit.Snare));

        var clicks = new List<NoteHit>();
        for (int i = 0; i < 1000; i++)
            clicks.Add(new NoteHit(1 + (i * 0.25), SynthHit.Kick));

        _schedule = new CombinedSchedule((new HitSoundSchedule(hits, SampleRate), 0.8f), (new HitSoundSchedule(clicks, SampleRate), 0.6f));
        _at = SampleRate;
    }

    /// <summary>One chunk's sounds, stepping along the song so the chunks have notes in them as often as a real song's do.</summary>
    [Benchmark]
    public int SoundsBetween()
    {
        _into.Clear();
        _schedule.SoundsBetween(_at, _at + Chunk, _into);
        _at += Chunk;
        if (_at > 300L * SampleRate)
            _at = SampleRate;
        return _into.Count;
    }
}
