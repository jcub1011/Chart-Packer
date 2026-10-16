using ChartShared.Audio.Decoding;
using ChartShared.Audio.Mixing;
using ChartShared.Audio.Playback;
using ChartShared.Audio.Synthesis;
using static ChartShared.Core.Tests.Audio.TestAudio;

namespace ChartShared.Core.Tests.Audio;

[TestClass]
public sealed class HitSoundScheduleTests
{
    private const int Rate = 48000;

    [TestMethod]
    public void EachHitStartsAtItsNotesFrameAndAChordSoundsOncePerKind()
    {
        var schedule = new HitSoundSchedule(
            [
                new NoteHit(1.0, SynthHit.Fret),
                new NoteHit(0.5, SynthHit.Fret),
                new NoteHit(0.5, SynthHit.Fret),
                new NoteHit(0.5, SynthHit.Kick),
                new NoteHit(-0.1, SynthHit.Fret),
            ],
            Rate);

        var found = new List<ScheduledSound>();
        schedule.SoundsBetween(0, 2 * Rate, found);

        Assert.AreEqual(3, schedule.Count);
        Assert.AreSequenceEqual([Rate / 2L, Rate / 2L, Rate], found.Select(s => s.SourceFrame).ToList());
        Assert.AreNotSame(found[0].Sound, found[1].Sound, "The kick and the fret each sound.");
        Assert.AreSame(found[0].Sound, found[2].Sound, "Each kind's sound is made once.");
    }

    [TestMethod]
    public void ACombinedScheduleGivesEachPartItsVolumeInOrder()
    {
        var clicks = new MetronomeSchedule(new Core.Charts.TempoMap(480, [], []), TimeSpan.Zero, TimeSpan.FromSeconds(1.2), Rate);
        var hits = new HitSoundSchedule([new NoteHit(0.25, SynthHit.Snare)], Rate);
        var combined = new CombinedSchedule((clicks, 0.5f), (hits, 0.25f), (new HitSoundSchedule([], Rate), 0));

        var found = new List<ScheduledSound>();
        combined.SoundsBetween(0, Rate, found);

        Assert.AreSequenceEqual([0L, Rate / 4L, Rate / 2L], found.Select(s => s.SourceFrame).ToList());
        Assert.AreSequenceEqual([0.5f, 0.25f, 0.5f], found.Select(s => s.Gain).ToList());
        Assert.IsTrue(new CombinedSchedule((hits, 0)).IsEmpty);
    }

    [TestMethod]
    public void HitSoundsLandOnTheirNotesInTheMix()
    {
        // Charter-spec C10 item 4: laid over the mix after the stretcher, sample-exact, as the metronome is.
        using var output = new FakeOutput();
        using var player = new StemPlayer(new StemMixer([KeyValuePair.Create("song", (IPcmSource)new FakeSource(Rate, 2, Rate, (_, _) => 0f))]), output)
        {
            Sounds = new HitSoundSchedule([new NoteHit(0.5, SynthHit.Snare)], Rate),
        };

        player.Play();
        PlayFor(output, Rate * 3 / 4);
        WaitUntil(() => output.QueuedFrames > 0);

        float[] written = output.WrittenLeft();
        Assert.IsTrue(written[..(Rate / 2)].All(sample => sample == 0f), "A hit sounded before its note.");
        Assert.Contains(sample => sample != 0f, written[(Rate / 2)..((Rate / 2) + 10)], "The hit didn't start on its note.");
    }
}
