using System.Security.Cryptography;

namespace ChartPacker.Core.Sng;

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

        Span<byte> key = stackalloc byte[Period];
        for (int i = 0; i < Period; i++)
            key[i] = (byte)(i ^ mask[i & 0xF]);

        int phase = (int)(startIndex & 0xFF);
        for (int j = 0; j < data.Length; j++)
            data[j] ^= key[(phase + j) & 0xFF];
    }
}
