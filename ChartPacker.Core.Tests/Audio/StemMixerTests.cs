using ChartPacker.Audio.Decoding;
using ChartPacker.Audio.Mixing;
using static ChartPacker.Core.Tests.Audio.TestAudio;

namespace ChartPacker.Core.Tests.Audio;

[TestClass]
public sealed class StemMixerTests
{
    private const int Rate = 48000;

    [TestMethod]
    public void AudibleStemsAreSummed()
    {
        var mixer = Mixer(("guitar", Constant(0.2f)), ("bass", Constant(0.3f)));

        float[] output = Mix(mixer, 100);

        Assert.AreEqual(0.5f, output[0], 1e-6f);
        Assert.AreEqual(0.5f, output[^1], 1e-6f);
    }

    [TestMethod]
    public void AMutedStemIsLeftOut()
    {
        var mixer = Mixer(("guitar", Constant(0.2f)), ("bass", Constant(0.3f)));
        mixer.Stems[1].IsMuted = true;

        Assert.AreEqual(0.2f, Mix(mixer, 10)[0], 1e-6f);
    }

    [TestMethod]
    public void AStemsVolumeScalesItInTheMix()
    {
        var mixer = Mixer(("guitar", Constant(0.2f)), ("bass", Constant(0.4f)));
        mixer.Stems[1].Volume = 0.5f;

        Assert.AreEqual(0.4f, Mix(mixer, 10)[0], 1e-6f);
    }

    [TestMethod]
    public void AStemAtZeroVolumeIsSilentButKeepsTime()
    {
        var mixer = Mixer(("guitar", Ramp()), ("bass", Ramp()));
        mixer.Stems[1].Volume = 0f;
        Mix(mixer, 1000);

        mixer.Stems[0].Volume = 0f;
        mixer.Stems[1].Volume = 1f;

        Assert.AreEqual(RampValue(1000), Mix(mixer, 1)[0], 1e-6f);
    }

    [TestMethod]
    public void AVolumeThatIsNegativeOrNotANumberIsRepaired()
    {
        var stem = Mixer(("guitar", Constant(0.2f))).Stems[0];

        stem.Volume = -1f;
        Assert.AreEqual(0f, stem.Volume);
        stem.Volume = float.NaN;
        Assert.AreEqual(1f, stem.Volume);
    }

    [TestMethod]
    public void SoloedStemsAreTheOnlyOnesHeardEvenWhenMuted()
    {
        var mixer = Mixer(("guitar", Constant(0.2f)), ("bass", Constant(0.3f)), ("drums", Constant(0.1f)));
        mixer.Stems[1].IsSolo = true;
        mixer.Stems[1].IsMuted = true;
        mixer.Stems[2].IsSolo = true;

        Assert.AreEqual(0.4f, Mix(mixer, 10)[0], 1e-6f);
    }

    [TestMethod]
    public void MutedStemsKeepTimeSoUnmutingPicksUpWhereTheOthersAre()
    {
        var mixer = Mixer(("guitar", Ramp()), ("bass", Ramp()));
        mixer.Stems[1].IsMuted = true;
        Mix(mixer, 1000);

        mixer.Stems[0].IsMuted = true;
        mixer.Stems[1].IsMuted = false;

        Assert.AreEqual(RampValue(1000), Mix(mixer, 1)[0], 1e-6f);
    }

    [TestMethod]
    public void TheSongIsAsLongAsItsLongestStemAndShorterOnesEndInSilence()
    {
        var mixer = Mixer(("guitar", Constant(0.25f, frames: 100)), ("song", Constant(0.5f, frames: 250)));

        Assert.AreEqual(250, mixer.LengthFrames);
        float[] first = Mix(mixer, 200);
        Assert.AreEqual(0.75f, first[2 * 99], 1e-6f);
        Assert.AreEqual(0.5f, first[2 * 100], 1e-6f);

        var buffer = new float[2 * 200];
        Assert.AreEqual(50, mixer.Mix(buffer));
        Assert.AreEqual(0, mixer.Mix(buffer));
        Assert.IsTrue(mixer.IsEnded);
    }

    [TestMethod]
    public void WhenALengthIsntKnownTheSongEndsWhenEveryStemHas()
    {
        var mixer = Mixer(("guitar", new FakeSource(Rate, 2, 300, (_, _) => 0.1f) { LengthFrames = -1 }));

        Assert.AreEqual(-1, mixer.LengthFrames);
        int total = 0, frames;
        var buffer = new float[2 * 128];
        while ((frames = mixer.Mix(buffer)) > 0)
            total += frames;

        Assert.IsTrue(total >= 300 && total < 300 + 256, $"{total} frames were mixed.");
        Assert.IsTrue(mixer.IsEnded);
    }

    [TestMethod]
    public void AMinimumLengthPlaysOnInSilencePastTheStemsThenEnds()
    {
        var mixer = Mixer(("song", Constant(0.5f, frames: 100)));
        mixer.MinimumLengthFrames = 250;

        float[] output = Mix(mixer, 200);
        Assert.AreEqual(0.5f, output[2 * 99], 1e-6f);
        Assert.AreEqual(0f, output[2 * 100]);

        var buffer = new float[2 * 200];
        Assert.AreEqual(50, mixer.Mix(buffer));
        Assert.AreEqual(0, mixer.Mix(buffer));
        Assert.IsTrue(mixer.IsEnded);
        Assert.AreEqual(100, mixer.LengthFrames);
    }

    [TestMethod]
    public void SeekingPastTheStemsButInsideTheMinimumLengthPlaysSilenceFromThere()
    {
        var mixer = Mixer(("song", Constant(0.5f, frames: 100)));
        mixer.MinimumLengthFrames = 1000;

        mixer.Seek(600);

        Assert.AreEqual(600, mixer.PositionFrames);
        Assert.AreEqual(0f, Mix(mixer, 10)[0]);
        var buffer = new float[2 * 1000];
        Assert.AreEqual(390, mixer.Mix(buffer));

        // Seeking back into the audio plays it again.
        mixer.Seek(50);
        Assert.AreEqual(0.5f, Mix(mixer, 1)[0], 1e-6f);
    }

    [TestMethod]
    public void SeekingPastTheMinimumLengthStopsAtItsEnd()
    {
        var mixer = Mixer(("song", Constant(0.5f, frames: 100)));
        mixer.MinimumLengthFrames = 300;

        mixer.Seek(5000);

        Assert.AreEqual(300, mixer.PositionFrames);
    }

    [TestMethod]
    public void AMinimumLengthShorterThanTheStemsChangesNothing()
    {
        var mixer = Mixer(("song", Constant(0.5f, frames: 250)));
        mixer.MinimumLengthFrames = 100;

        var buffer = new float[2 * 1000];
        Assert.AreEqual(250, mixer.Mix(buffer));
        Assert.AreEqual(0, mixer.Mix(buffer));
        Assert.IsTrue(mixer.IsEnded);
    }

    [TestMethod]
    public void WhenALengthIsntKnownTheMinimumLengthStillPlaysOut()
    {
        var mixer = Mixer(("guitar", new FakeSource(Rate, 2, 300, (_, _) => 0.1f) { LengthFrames = -1 }));
        mixer.MinimumLengthFrames = 1000;

        int total = 0, frames;
        var buffer = new float[2 * 128];
        while ((frames = mixer.Mix(buffer)) > 0)
            total += frames;

        Assert.AreEqual(1000, total);
        Assert.IsTrue(mixer.IsEnded);
    }

    [TestMethod]
    public void MonoIsSentToBothSides()
    {
        var mixer = Mixer(("vocals", new FakeSource(Rate, 1, 100, (_, _) => 0.25f)));

        float[] output = Mix(mixer, 1);

        Assert.AreEqual(0.25f, output[0], 1e-6f);
        Assert.AreEqual(0.25f, output[1], 1e-6f);
    }

    [TestMethod]
    public void MoreThanTwoChannelsAreFoldedIntoTwo()
    {
        var mixer = Mixer(("song", new FakeSource(Rate, 4, 100, (_, c) => (c + 1) / 10f)));

        float[] output = Mix(mixer, 1);

        Assert.AreEqual((0.1f + 0.3f) / 2, output[0], 1e-6f);
        Assert.AreEqual((0.2f + 0.4f) / 2, output[1], 1e-6f);
    }

    [TestMethod]
    public void TheSumIsClipped()
    {
        var mixer = Mixer(("guitar", Constant(0.8f)), ("bass", Constant(0.8f)));

        Assert.AreEqual(1f, Mix(mixer, 1)[0]);
    }

    [TestMethod]
    public void TheRateMostStemsHaveIsUsed()
    {
        Assert.AreEqual(44100, StemMixer.CommonRate([44100, 48000, 44100]));
        Assert.AreEqual(48000, StemMixer.CommonRate([44100, 48000]));
        Assert.AreEqual(48000, StemMixer.CommonRate([]));

        var mixer = Mixer(("guitar", Constant(0f, rate: 44100)), ("bass", Constant(0f, rate: 44100)), ("song", Constant(0f, rate: 48000)));
        Assert.AreEqual(44100, mixer.OutputRate);
    }

    [TestMethod]
    public void AStemAtAnotherRateIsResampledInTime()
    {
        // 24 kHz into 48 kHz: every source frame spans two output frames.
        var source = new FakeSource(24000, 1, 1000, (n, _) => n / 1000f);
        var mixer = new StemMixer([KeyValuePair.Create("guitar", (IPcmSource)source)], outputRate: Rate);

        Assert.AreEqual(2000, mixer.LengthFrames);
        float[] output = Mix(mixer, 400);
        Assert.AreEqual(0.100f, output[2 * 200], 1e-4f);
        Assert.AreEqual(0.1005f, output[2 * 201], 1e-4f);
    }

    [TestMethod]
    public void SeekingMovesEveryStemToTheSameTime()
    {
        var resampled = new FakeSource(24000, 2, 24000, (n, _) => n / 24000f);
        var mixer = new StemMixer(
            [KeyValuePair.Create("guitar", (IPcmSource)Ramp()), KeyValuePair.Create("bass", (IPcmSource)resampled)],
            outputRate: Rate);
        Mix(mixer, 5000);

        mixer.Seek(12000);
        mixer.Stems[1].IsMuted = true;
        float guitar = Mix(mixer, 1)[0];
        mixer.Seek(12000);
        mixer.Stems[0].IsMuted = true;
        mixer.Stems[1].IsMuted = false;
        float bass = Mix(mixer, 1)[0];

        Assert.AreEqual(12001, mixer.PositionFrames);
        Assert.AreEqual(RampValue(12000), guitar, 1e-6f);
        Assert.AreEqual(0.25f, bass, 1e-4f); // 12000 of 48000 is a quarter of a second.
    }

    [TestMethod]
    public void ADamagedStemGoesQuietAndTheRestPlayOnInStep()
    {
        var damaged = Constant(0.3f);
        var mixer = Mixer(("guitar", Ramp()), ("bass", damaged));
        Mix(mixer, 100);

        // What's already decoded still plays; the damage shows at the next read of the file.
        damaged.IsDamaged = true;
        float[] output = Mix(mixer, 3000);

        Assert.AreEqual("Damaged.", mixer.Stems[1].Error);
        Assert.AreEqual(RampValue(3099), output[^2], 1e-6f);
        Assert.AreEqual(3100, mixer.PositionFrames);
    }

    [TestMethod]
    public void EachChangeToAStemsSwitchesIsCounted()
    {
        var mixer = Mixer(("guitar", Constant(0.2f)), ("bass", Constant(0.3f)));
        int version = mixer.SwitchVersion;

        mixer.Stems[0].IsMuted = true;
        mixer.Stems[1].IsSolo = true;
        mixer.Stems[1].Volume = 0.5f;
        Assert.AreEqual(version + 3, mixer.SwitchVersion);

        // Set to what they are already, nothing changes.
        mixer.Stems[0].IsMuted = true;
        mixer.Stems[1].IsSolo = true;
        mixer.Stems[1].Volume = 0.5f;
        Assert.AreEqual(version + 3, mixer.SwitchVersion);
    }

    private static FakeSource Constant(float value, int frames = 48000, int rate = Rate) => new(rate, 2, frames, (_, _) => value);

    private static FakeSource Ramp() => new(Rate, 2, 48000, (n, _) => RampValue(n));

    private static float RampValue(long frame) => frame / 48000f;

    private static StemMixer Mixer(params (string Name, IPcmSource Source)[] stems) =>
        new([.. stems.Select(s => KeyValuePair.Create(s.Name, s.Source))]);

    private static float[] Mix(StemMixer mixer, int frames)
    {
        var buffer = new float[frames * 2];
        Assert.AreEqual(frames, mixer.Mix(buffer));
        return buffer;
    }
}
