using System.Buffers.Binary;

namespace ChartShared.Audio.Decoding;

/// <summary>
/// Decodes a RIFF WAVE file: integer PCM of 8, 16, 24 or 32 bits, or 32- or 64-bit IEEE float,
/// in the plain or <c>WAVE_FORMAT_EXTENSIBLE</c> header.
/// </summary>
public sealed class WavSource : IPcmSource
{
    private const ushort FormatPcm = 1;
    private const ushort FormatFloat = 3;
    private const ushort FormatExtensible = 0xFFFE;

    // More than any real song audio has; the header field allows 65535.
    private const int MaxChannels = 32;

    private readonly Stream _stream;
    private readonly long _dataStart;
    private readonly long _frameCount;
    private readonly int _blockAlign;
    private readonly int _bits;
    private readonly bool _isFloat;
    private byte[] _bytes = [];
    private long _frame;

    /// <exception cref="InvalidDataException">The stream isn't a WAVE file this can decode.</exception>
    public WavSource(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;

        Span<byte> header = stackalloc byte[12];
        if (!TryReadAt(0, header) || !header[..4].SequenceEqual("RIFF"u8) || !header[8..].SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Not a WAVE file.");

        // Chunks follow the header, each padded to an even length.
        ushort format = 0;
        long position = 12;
        long? dataStart = null;
        long dataLength = 0;
        Span<byte> chunk = stackalloc byte[8];
        Span<byte> fmt = stackalloc byte[40];
        while (dataStart is null && TryReadAt(position, chunk))
        {
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            long body = position + 8;
            if (chunk[..4].SequenceEqual("fmt "u8))
            {
                if (length < 16)
                    throw new InvalidDataException("The WAVE format chunk is too short.");

                var fields = fmt[..(int)Math.Min(length, (uint)fmt.Length)];
                if (!TryReadAt(body, fields))
                    throw new InvalidDataException("The WAVE format chunk is cut short.");

                format = BinaryPrimitives.ReadUInt16LittleEndian(fields);
                Channels = BinaryPrimitives.ReadUInt16LittleEndian(fields[2..]);
                SampleRate = BinaryPrimitives.ReadInt32LittleEndian(fields[4..]);
                _blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fields[12..]);
                _bits = BinaryPrimitives.ReadUInt16LittleEndian(fields[14..]);

                // The extensible header puts the real format in the first two bytes of its subformat GUID.
                if (format == FormatExtensible && fields.Length >= 26)
                    format = BinaryPrimitives.ReadUInt16LittleEndian(fields[24..]);
            }
            else if (chunk[..4].SequenceEqual("data"u8))
            {
                dataStart = body;
                dataLength = length;
            }

            position = body + length + (length & 1);
        }

        if (format == 0)
            throw new InvalidDataException("The WAVE file has no format chunk.");
        if (dataStart is null)
            throw new InvalidDataException("The WAVE file has no data chunk.");

        _isFloat = format == FormatFloat;
        bool supported = format switch
        {
            FormatPcm => _bits is 8 or 16 or 24 or 32,
            FormatFloat => _bits is 32 or 64,
            _ => false,
        };
        if (!supported)
            throw new InvalidDataException($"WAVE format {format} with {_bits}-bit samples isn't supported.");
        if (Channels is 0 or > MaxChannels || SampleRate <= 0 || _blockAlign != Channels * (_bits / 8))
            throw new InvalidDataException("The WAVE format chunk is inconsistent.");

        // A streamed or truncated file can claim more data than there is.
        _dataStart = dataStart.Value;
        _frameCount = Math.Min(dataLength, Math.Max(0, stream.Length - _dataStart)) / _blockAlign;
    }

    public int SampleRate { get; }

    public int Channels { get; }

    public long LengthFrames => _frameCount;

    public int ReadFrames(Span<float> buffer)
    {
        int frames = (int)Math.Min(buffer.Length / Channels, _frameCount - _frame);
        if (frames <= 0)
            return 0;

        int byteCount = frames * _blockAlign;
        if (_bytes.Length < byteCount)
            _bytes = new byte[byteCount];

        var bytes = _bytes.AsSpan(0, byteCount);
        _stream.Position = _dataStart + (_frame * _blockAlign);
        _stream.ReadExactly(bytes);
        Convert(bytes, buffer[..(frames * Channels)]);
        _frame += frames;
        return frames;
    }

    public void SeekToFrame(long frame)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        _frame = Math.Min(frame, _frameCount);
    }

    public void Dispose() => _stream.Dispose();

    private void Convert(ReadOnlySpan<byte> bytes, Span<float> samples)
    {
        switch (_bits)
        {
            case 8:
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = (bytes[i] - 128) / 128f;
                break;
            case 16:
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes[(i * 2)..]) / 32768f;
                break;
            case 24:
                for (int i = 0; i < samples.Length; i++)
                {
                    int b = i * 3;
                    int value = (bytes[b] | (bytes[b + 1] << 8) | (bytes[b + 2] << 16)) << 8 >> 8;
                    samples[i] = value / 8388608f;
                }
                break;
            case 32 when _isFloat:
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes[(i * 4)..]);
                break;
            case 32:
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes[(i * 4)..]) / 2147483648f;
                break;
            case 64:
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = (float)BinaryPrimitives.ReadDoubleLittleEndian(bytes[(i * 8)..]);
                break;
        }
    }

    private bool TryReadAt(long position, Span<byte> buffer)
    {
        if (position + buffer.Length > _stream.Length)
            return false;

        _stream.Position = position;
        _stream.ReadExactly(buffer);
        return true;
    }
}
