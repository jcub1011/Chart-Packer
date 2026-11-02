using System.Buffers.Binary;
using ChartBuilder.Tests.Charting;
using ChartBuilder.ViewModels;
using ChartShared.Core.Charting;
using ChartShared.Core.Charts;
using ChartShared.Core.Scanning;
using ChartShared.Core.Tests.Audio;

namespace ChartBuilder.Tests.Editing;

/// <summary>
/// What a save does to the audio it holds (charter-spec C10): it lets go of the files the save replaces, then opens
/// them again, and a song that was playing carries on rather than coming back paused.
/// </summary>
[TestClass]
public sealed class PlaybackReleaseTests
{
    [TestMethod]
    public async Task ReloadingAfterAReleaseCarriesOnPlaying()
    {
        DirectoryInfo folder = Directory.CreateTempSubdirectory("chartbuilder-playback-");
        try
        {
            File.WriteAllBytes(Path.Combine(folder.FullName, "song.wav"), Wav(seconds: 1));
            using var playback = new PlaybackViewModel(Song(folder.FullName), _ => new FakeOutput(), null);

            await playback.OpenAsync();
            Assert.IsTrue(playback.IsReady, "The song's audio opens.");

            playback.PlayPauseCommand.Execute(null);
            Assert.IsTrue(playback.IsPlaying);

            // As a save that replaces a track does: it lets go of the audio, then opens it again once it's written.
            playback.Release();
            Assert.IsFalse(playback.IsPlaying, "Letting go of the audio stops it while the save writes.");

            await playback.ReloadAsync(playback.Tracks.Select(r => r.Track).ToList());

            Assert.IsTrue(playback.IsPlaying, "Opening the audio again carries on playing.");
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task ReloadingAfterAReleaseStaysPausedWhenItWasPaused()
    {
        DirectoryInfo folder = Directory.CreateTempSubdirectory("chartbuilder-playback-");
        try
        {
            File.WriteAllBytes(Path.Combine(folder.FullName, "song.wav"), Wav(seconds: 1));
            using var playback = new PlaybackViewModel(Song(folder.FullName), _ => new FakeOutput(), null);

            await playback.OpenAsync();
            Assert.IsTrue(playback.IsReady, "The song's audio opens.");

            playback.Release();
            await playback.ReloadAsync(playback.Tracks.Select(r => r.Track).ToList());

            Assert.IsFalse(playback.IsPlaying, "A song that wasn't playing doesn't start by itself.");
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    // The song open on the folder, with the one track it stores, as the editor would have it.
    private static EditorSong Song(string folder)
    {
        IReadOnlyList<KeyValuePair<string, string>> pairs = [KeyValuePair.Create("name", "Song")];
        return EditorSong.Of(new OpenedSong(
            new SongEntry(SongEntryKind.SongFolder, folder, "Song", "Song"),
            pairs,
            ["song.wav"],
            "notes.mid",
            new ChartLoadResult(ChartSettingsCommandTests.NewChart(ChartInstrument.Guitar), []),
            ChartReadOptions.From(pairs),
            SongChartSettings.DelayOf(pairs, (TimeSpan?)null),
            null,
            new ChartFileStamp("notes.mid", 1, DateTime.UnixEpoch)));
    }

    // A WAV of a quiet tone, which the decoder opens like any stem.
    private static byte[] Wav(int seconds, int sampleRate = 48000, int channels = 1)
    {
        const int Bits = 16;
        int frames = seconds * sampleRate;
        int dataLength = frames * channels * (Bits / 8);
        byte[] wav = new byte[44 + dataLength];
        var span = wav.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], wav.Length - 8);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..], (ushort)channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], sampleRate * channels * (Bits / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..], (ushort)(channels * (Bits / 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..], Bits);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataLength);
        for (int i = 0; i < frames; i++)
        {
            short sample = (short)Math.Round(0.1 * short.MaxValue * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
            BinaryPrimitives.WriteInt16LittleEndian(span[(44 + (i * channels * (Bits / 8)))..], sample);
        }

        return wav;
    }
}
