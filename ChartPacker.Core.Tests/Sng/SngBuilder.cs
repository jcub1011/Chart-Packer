using System.Buffers.Binary;
using System.Text;

namespace ChartPacker.Core.Tests.Sng;

/// <summary>
/// Builds <c>.sng</c> bytes following the layout in sng-spec §3–§4, with hooks to break any part
/// of it for fixtures. File data is written exactly as given, without masking; the reader never
/// looks at it.
/// </summary>
internal sealed class SngBuilder
{
    private readonly List<(int KeyLen, byte[] Key, int ValueLen, byte[] Value)> _metadata = [];
    private readonly List<(byte[] Name, byte[] Data)> _files = [];

    public byte[] Identifier { get; set; } = "SNGPKG"u8.ToArray();

    public uint Version { get; set; } = 1;

    public byte[] Mask { get; set; } = Enumerable.Range(0x10, 16).Select(b => (byte)b).ToArray();

    public ulong? MetadataLen { get; set; }

    public ulong? MetadataCount { get; set; }

    /// <summary>Zero bytes appended to the metadata section and counted in the default <c>metadataLen</c>.</summary>
    public int MetadataPadding { get; set; }

    public ulong? FileMetaLen { get; set; }

    public ulong? FileCount { get; set; }

    public ulong? FileDataLen { get; set; }

    /// <summary><c>contentsIndex</c> to write for a file (by position), instead of the contiguous default.</summary>
    public Dictionary<int, ulong> OffsetOverrides { get; } = [];

    /// <summary><c>contentsLen</c> to write for a file (by position), instead of its data length.</summary>
    public Dictionary<int, ulong> LengthOverrides { get; } = [];

    /// <summary>Bytes written after the file data.</summary>
    public byte[] Trailing { get; set; } = [];

    public SngBuilder AddMetadata(string key, string value) =>
        AddRawMetadata(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(value));

    public SngBuilder AddRawMetadata(byte[] key, byte[] value, int? keyLen = null, int? valueLen = null)
    {
        _metadata.Add((keyLen ?? key.Length, key, valueLen ?? value.Length, value));
        return this;
    }

    public SngBuilder AddFile(string name, byte[] data) => AddRawFile(Encoding.UTF8.GetBytes(name), data);

    public SngBuilder AddRawFile(byte[] name, byte[] data)
    {
        _files.Add((name, data));
        return this;
    }

    public byte[] Build()
    {
        var metadata = new List<byte>();
        AppendUInt64(metadata, MetadataCount ?? (ulong)_metadata.Count);
        foreach (var (keyLen, key, valueLen, value) in _metadata)
        {
            AppendInt32(metadata, keyLen);
            metadata.AddRange(key);
            AppendInt32(metadata, valueLen);
            metadata.AddRange(value);
        }
        metadata.AddRange(new byte[MetadataPadding]);

        // Offsets depend on the index size, which doesn't depend on the offsets' values.
        int indexSize = sizeof(ulong) + _files.Sum(f => 1 + f.Name.Length + 16);
        ulong dataStart = (ulong)(26 + 8 + metadata.Count + 8 + indexSize + 8);

        var index = new List<byte>();
        AppendUInt64(index, FileCount ?? (ulong)_files.Count);
        ulong next = dataStart;
        for (int i = 0; i < _files.Count; i++)
        {
            var (name, data) = _files[i];
            index.Add((byte)name.Length);
            index.AddRange(name);
            AppendUInt64(index, LengthOverrides.GetValueOrDefault(i, (ulong)data.Length));
            AppendUInt64(index, OffsetOverrides.GetValueOrDefault(i, next));
            next += (ulong)data.Length;
        }

        var bytes = new List<byte>();
        bytes.AddRange(Identifier);
        AppendUInt32(bytes, Version);
        bytes.AddRange(Mask);
        AppendUInt64(bytes, MetadataLen ?? (ulong)metadata.Count);
        bytes.AddRange(metadata);
        AppendUInt64(bytes, FileMetaLen ?? (ulong)index.Count);
        bytes.AddRange(index);
        AppendUInt64(bytes, FileDataLen ?? (ulong)_files.Sum(f => f.Data.Length));
        foreach (var (_, data) in _files)
            bytes.AddRange(data);
        bytes.AddRange(Trailing);
        return bytes.ToArray();
    }

    private static void AppendUInt32(List<byte> bytes, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        bytes.AddRange(buffer);
    }

    private static void AppendInt32(List<byte> bytes, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        bytes.AddRange(buffer);
    }

    private static void AppendUInt64(List<byte> bytes, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        bytes.AddRange(buffer);
    }
}
