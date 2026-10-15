using ChartShared.Audio.Decoding;
using ChartShared.Core.Jobs;
using ChartShared.Core.Packing;
using ChartShared.Core.Scanning;
using ChartShared.Core.Sng;
using Concentus;
using Concentus.Enums;

namespace ChartShared.Audio.Encoding;

/// <summary>How far one file's transcode has got, from 0 to 1.</summary>
/// <param name="Stem">The stem being transcoded.</param>
/// <param name="Fraction">How far its transcode has got.</param>
public sealed record TranscodeFileProgress(string Stem, double Fraction);

/// <summary>Transcodes song audio to Ogg Opus at 48 kHz with Concentus (managed, AOT-safe).</summary>
/// <remarks>
/// Sources decode through <see cref="AudioDecoder"/> (whatever the extension says: wav, ogg,
/// mp3 or opus); more than two channels fold to stereo as the playback mixer folds them;
/// other rates convert to 48 kHz (swresample when bundled, linear interpolation otherwise).
/// </remarks>
public static class OpusTranscoder
{
    /// <summary>Opus always encodes at 48 kHz.</summary>
    public const int OutputRate = 48000;

    /// <summary>Whether transcoding can run: the managed encoder is always bundled.</summary>
    public static bool IsAvailable => true;

    private const int FrameSize = 960;
    private const int DecodeFrames = 4800;
    private const int MaxPacketBytes = 4000;

    /// <summary>Transcodes <paramref name="sourcePath"/> to the Ogg Opus file <paramref name="targetPath"/>.</summary>
    /// <param name="bitrateKbps">The bitrate in kbps per stereo stem (mono uses half).</param>
    /// <param name="progress">Receives the file's fraction; may be <see langword="null"/>.</param>
    /// <exception cref="InvalidDataException">The file isn't audio, has no audio, or can't be encoded.</exception>
    /// <exception cref="IOException">A file can't be read or written.</exception>
    /// <exception cref="UnauthorizedAccessException">Access is denied.</exception>
    /// <exception cref="OperationCanceledException">Transcoding was cancelled.</exception>
    public static void TranscodeFile(
        string sourcePath,
        string targetPath,
        int bitrateKbps,
        IProgress<TranscodeFileProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);

        string stem = Path.GetFileNameWithoutExtension(targetPath);
        int stereoBps = Math.Clamp(bitrateKbps * 1000, 8000, 512000);

        using var source = AudioDecoder.Open(
            new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read), sourcePath);
        if (source.Channels < 1)
            throw new InvalidDataException($"'{sourcePath}' has no audio channels.");

        int channels = Math.Min(source.Channels, 2);
        int bitrate = channels == 1 ? Math.Max(stereoBps / 2, 8000) : stereoBps;

        IOpusEncoder encoder;
        try
        {
            encoder = OpusCodecFactory.CreateEncoder(OutputRate, channels, OpusApplication.OPUS_APPLICATION_AUDIO);
            encoder.Bitrate = bitrate;
        }
        catch (Exception ex) when (AudioDecoder.IsDecoderFault(ex))
        {
            throw AudioDecoder.Damaged(ex);
        }

        int lookahead = encoder.Lookahead;

        using var resampler = source.SampleRate == OutputRate ? null : Resampler.Open(source.SampleRate, OutputRate, source.Channels);

        // The resampler never emits more than fits; anything held back waits for the next chunk,
        // so the buffer is capped (16× a decode chunk covers any real rate, e.g. 8 kHz to 48 kHz).
        int resampledFrames = source.SampleRate == OutputRate
            ? DecodeFrames
            : Math.Min((int)((long)DecodeFrames * OutputRate / source.SampleRate), DecodeFrames * 16) + 128;
        var decoded = new float[DecodeFrames * source.Channels];
        var converted = new float[resampledFrames * source.Channels];
        var pending = new List<float>();
        var packet = new byte[MaxPacketBytes];
        long validFrames = 0;
        long decodedTotal = 0;
        long decodedFrames = 0;
        bool sourceEnded = false;

        try
        {
            using var output = new FileStream(targetPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            using var writer = new OggOpusWriter(output, channels, lookahead, source.SampleRate);

            Report(0);
            while (!sourceEnded)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int read = source.ReadFrames(decoded);
                if (read == 0)
                    sourceEnded = true;

                if (resampler is null)
                {
                    FoldToChannels(decoded.AsSpan(0, read * source.Channels), source.Channels, channels, pending);
                }
                else
                {
                    int frames = resampler.Convert(
                        decoded.AsSpan(0, read * source.Channels), converted, sourceEnded);
                    FoldToChannels(converted.AsSpan(0, frames * source.Channels), source.Channels, channels, pending);
                }

                while (pending.Count >= FrameSize * channels)
                {
                    int length = EncodeFrame(pending, packet, encoder, channels);
                    pending.RemoveRange(0, FrameSize * channels);
                    validFrames += FrameSize;
                    decodedTotal += FrameSize;
                    writer.WriteAudioPacket(packet.AsSpan(0, length), decodedTotal);
                }

                decodedFrames += read;
                if (source.LengthFrames > 0)
                    Report(Math.Clamp((double)decodedFrames / source.LengthFrames, 0, 1));
            }

            // The last partial frame is padded with silence.
            if (pending.Count > 0)
            {
                validFrames += pending.Count / channels;
                pending.AddRange(new float[(FrameSize * channels) - pending.Count]);
                int length = EncodeFrame(pending, packet, encoder, channels);
                pending.Clear();
                decodedTotal += FrameSize;
                writer.WriteAudioPacket(packet.AsSpan(0, length), decodedTotal);
            }

            if (validFrames == 0)
                throw new InvalidDataException($"'{sourcePath}' has no audio.");

            // The encoder holds back its lookahead: flush it with silence so every valid
            // frame comes out, then trim the flush (and the padding) with the final granule.
            var silence = new float[FrameSize * channels];
            while (decodedTotal < lookahead + validFrames)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int length = EncodeSilence(silence, packet, encoder);
                decodedTotal += FrameSize;
                writer.WriteAudioPacket(packet.AsSpan(0, length), decodedTotal);
            }

            writer.Finish(lookahead + validFrames);
            Report(1);
        }
        catch
        {
            TryDelete(targetPath);
            throw;
        }

        void Report(double fraction) => progress?.Report(new TranscodeFileProgress(stem, Math.Clamp(fraction, 0, 1)));
    }

    private static int EncodeSilence(float[] silence, byte[] packet, IOpusEncoder encoder)
    {
        int length;
        try
        {
            length = encoder.Encode(silence.AsSpan(), FrameSize, packet.AsSpan(), packet.Length);
        }
        catch (Exception ex) when (AudioDecoder.IsDecoderFault(ex))
        {
            throw AudioDecoder.Damaged(ex);
        }

        if (length <= 0)
            throw new InvalidDataException("An audio frame couldn't be encoded to Opus.");
        return length;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
    private static int EncodeFrame(List<float> pending, byte[] packet, IOpusEncoder encoder, int channels)
    {
        float[] frame = pending.GetRange(0, FrameSize * channels).ToArray();
        int length;
        try
        {
            length = encoder.Encode(frame.AsSpan(), FrameSize, packet.AsSpan(), packet.Length);
        }
        catch (Exception ex) when (AudioDecoder.IsDecoderFault(ex))
        {
            throw AudioDecoder.Damaged(ex);
        }

        if (length <= 0)
            throw new InvalidDataException("An audio frame couldn't be encoded to Opus.");
        return length;
    }

    /// <summary>Mono passes through, stereo passes through, more channels fold even/odd as playback folds them.</summary>
    private static void FoldToChannels(ReadOnlySpan<float> input, int fromChannels, int toChannels, List<float> output)
    {
        int frames = input.Length / fromChannels;
        if (fromChannels == toChannels)
        {
            output.AddRange(input.ToArray());
            return;
        }

        if (fromChannels == 1 && toChannels == 2)
        {
            for (int i = 0; i < frames; i++)
            {
                output.Add(input[i]);
                output.Add(input[i]);
            }

            return;
        }

        if (fromChannels > 2 && toChannels == 2)
        {
            int leftCount = (fromChannels + 1) / 2;
            int rightCount = fromChannels / 2;
            for (int i = 0; i < frames; i++)
            {
                float left = 0, right = 0;
                for (int c = 0; c < fromChannels; c++)
                {
                    if (c % 2 == 0)
                        left += input[(i * fromChannels) + c];
                    else
                        right += input[(i * fromChannels) + c];
                }

                output.Add(left / leftCount);
                output.Add(right / rightCount);
            }

            return;
        }

        throw new InvalidDataException($"Audio with {fromChannels} channels can't be transcoded.");
    }
}

/// <summary>Transcodes pack plans' non-Opus audio stems to Opus, for <see cref="SongJob"/>.</summary>
public sealed class OpusPackTranscoder : IPackTranscoder
{
    public bool IsAvailable => OpusTranscoder.IsAvailable;

    public Task<IReadOnlyList<SngSourceFile>> TranscodeAsync(
        PackPlan plan,
        string tempFolder,
        int bitrateKbps,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(tempFolder);

        return Task.Run(() => Transcode(plan, tempFolder, bitrateKbps, progress, cancellationToken), cancellationToken);
    }

    private static IReadOnlyList<SngSourceFile> Transcode(
        PackPlan plan,
        string tempFolder,
        int bitrateKbps,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var included = new List<SngSourceFile>(plan.Included.Count);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Which entries need transcoding, so progress counts real work.
        int jobs = plan.Included.Count(f =>
            AudioStems.StemOf(f.StoredName) is not null
            && !Path.GetExtension(f.StoredName).Equals(".opus", StringComparison.OrdinalIgnoreCase));

        int done = 0;
        foreach (SngSourceFile file in plan.Included)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? stem = AudioStems.StemOf(file.StoredName);
            if (stem is null || Path.GetExtension(file.StoredName).Equals(".opus", StringComparison.OrdinalIgnoreCase))
            {
                if (!taken.Add(file.StoredName))
                    throw new InvalidDataException($"'{file.StoredName}' is packed twice.");
                included.Add(file);
                continue;
            }

            string storedName = stem + ".opus";
            if (!taken.Add(storedName))
                throw new InvalidDataException($"'{file.StoredName}' and another stem would both pack as '{storedName}'.");

            string target = Path.Combine(tempFolder, storedName);
            int finished = done;
            var fileProgress = progress is null
                ? null
                : new Progress<TranscodeFileProgress>(p => progress.Report(new ExportProgress(
                    ExportPhase.Transcoding, finished, jobs, stem,
                    jobs == 0 ? 1 : Math.Clamp((finished + p.Fraction) / jobs, 0, 1))));
            OpusTranscoder.TranscodeFile(file.SourcePath, target, bitrateKbps, fileProgress, cancellationToken);
            done++;
            progress?.Report(new ExportProgress(
                ExportPhase.Transcoding, done, jobs, stem,
                jobs == 0 ? 1 : Math.Clamp((double)done / jobs, 0, 1)));
            included.Add(new SngSourceFile(storedName, target, new FileInfo(target).Length));
        }

        return included;
    }
}
