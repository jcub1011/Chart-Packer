using System.Numerics;
using System.Security.Cryptography;

namespace ChartShared.Core.Sng;

/// <summary>The XOR mask applied to every file's data (sng-spec §5).</summary>
/// <remarks>
/// Each file is masked on its own, with <c>i</c> counted from 0 at that file's first byte:
/// <c>data[i] ^= xorMask[i % 16] ^ (i &amp; 0xFF)</c>. The operation is its own inverse, so the same
/// routine masks and unmasks. The key repeats every 256 bytes, which is what the lookup table holds.
/// </remarks>
public static class SngMask
{
    /// <summary>Length of the mask stored in the header.</summary>
    public const int MaskSize = 16;

    private const int Period = 256;

    // The widest vector there is (AVX-512's), so the key's buffer can be a constant size.
    private const int MaxVectorBytes = 64;

    /// <summary>Returns a fresh random mask. Any value is valid (sng-spec §5).</summary>
    public static byte[] Generate()
    {
        byte[] mask = new byte[MaskSize];
        RandomNumberGenerator.Fill(mask);
        return mask;
    }

    /// <summary>
    /// Masks or unmasks <paramref name="data"/> in place. <paramref name="startIndex"/> is the
    /// position of <c>data[0]</c> within its file, so a file can be processed in chunks.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="mask"/> isn't <see cref="MaskSize"/> bytes.</exception>
    public static void Apply(Span<byte> data, ReadOnlySpan<byte> mask, long startIndex = 0)
    {
        if (mask.Length != MaskSize)
            throw new ArgumentException($"The mask must be {MaskSize} bytes.", nameof(mask));
        ArgumentOutOfRangeException.ThrowIfNegative(startIndex);

        // The key as data[0] meets it, with enough of its start again on the end that a whole vector can be read from
        // any place in it without wrapping.
        int phase = (int)(startIndex & 0xFF);
        Span<byte> key = stackalloc byte[Period + MaxVectorBytes];
        for (int i = 0; i < key.Length; i++)
        {
            int at = (phase + i) & 0xFF;
            key[i] = (byte)(at ^ mask[at & 0xF]);
        }

        int j = 0;
        if (Vector.IsHardwareAccelerated && Vector<byte>.Count <= MaxVectorBytes)
        {
            int width = Vector<byte>.Count;
            for (; j <= data.Length - width; j += width)
            {
                var slot = data.Slice(j, width);
                (new Vector<byte>(slot) ^ new Vector<byte>(key.Slice(j & 0xFF, width))).CopyTo(slot);
            }
        }

        for (; j < data.Length; j++)
            data[j] ^= key[j & 0xFF];
    }
}
