using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Unicode;
using ChartShared.Core.Resources;
using ChartShared.Core.Validation;
using Microsoft.Win32.SafeHandles;

namespace ChartShared.Core.Sng;

/// <summary>
/// Reads the header, metadata and file index of a <c>.sng</c> without reading any file data, and
/// reports every structural problem as a <see cref="Finding"/> rather than throwing.
/// </summary>
/// <remarks>
/// <para>
/// Written from <c>docs/sng-spec.md</c>. The field order, and the checks the reference reader
/// makes (identifier, version 1, negative lengths), match <c>SngLib/SngSerializer.cs</c>
/// <c>LoadSngFile</c> in <see href="https://github.com/mdsitton/SngFileFormat">mdsitton/SngFileFormat</see>
/// at commit <c>6683799</c> (MIT License, © Matthew Sitton; see <c>THIRD-PARTY-NOTICES.md</c>).
/// </para>
/// <para>
/// Unlike the reference reader, this one locates each section by its declared length
/// (sng-spec A3), checks UTF-8 strictly (A11), checks file ranges (A5, A6), and checks every
/// declared length against the real file size before allocating anything.
/// </para>
/// </remarks>
public static class SngReader
{
    /// <summary>Size of the fixed header: identifier, version and mask.</summary>
    public const int HeaderSize = 26;

    private const uint SupportedVersion = 1;
    private const int LengthFieldSize = 8;
    private const int MinPairSize = 4 + 4;
    private const int MinFileEntrySize = 1 + 8 + 8;

    // How much of the file's start is read in one go: enough for the header, metadata and index of a
    // typical .sng, so they don't each cost a read (and, on a hard drive, possibly a seek).
    internal const int PrefixSize = 64 * 1024;

    private static ReadOnlySpan<byte> Identifier => "SNGPKG"u8;

    /// <summary>Reads <paramref name="path"/> on the thread pool.</summary>
    /// <exception cref="IOException">The file can't be opened or read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    /// <exception cref="OperationCanceledException">The read was cancelled.</exception>
    public static Task<SngReadResult> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Task.Run(() => Read(path, cancellationToken), cancellationToken);
    }

    /// <summary>Reads <paramref name="path"/>. Malformed content produces findings; only I/O failures throw.</summary>
    /// <exception cref="IOException">The file can't be opened or read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    /// <exception cref="OperationCanceledException">The read was cancelled.</exception>
    public static SngReadResult Read(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Read(handle, cancellationToken);
    }

    /// <summary>Reads the <c>.sng</c> open as <paramref name="handle"/>, which stays open. Malformed content produces findings; only I/O failures throw.</summary>
    /// <remarks>Lets a caller read the index and then the files it lists through one handle, so they're of the same file.</remarks>
    /// <exception cref="IOException">The file can't be read.</exception>
    /// <exception cref="OperationCanceledException">The read was cancelled.</exception>
    public static SngReadResult Read(SafeFileHandle handle, CancellationToken cancellationToken = default)
    {
        return Read(SngInput.Of(handle), cancellationToken);
    }

    /// <summary>
    /// Reads the <c>.sng</c> in <paramref name="stream"/>, which must be readable and seekable, and stays open
    /// with its position moved. Malformed content produces findings; only I/O failures throw.
    /// </summary>
    /// <remarks>For a <c>.sng</c> that isn't a file of its own, such as the stream Explorer hands a thumbnail handler.</remarks>
    /// <exception cref="ArgumentException"><paramref name="stream"/> can't be read or can't seek.</exception>
    /// <exception cref="IOException">The stream can't be read.</exception>
    /// <exception cref="OperationCanceledException">The read was cancelled.</exception>
    public static SngReadResult Read(Stream stream, CancellationToken cancellationToken = default) =>
        Read(SngInput.Of(stream), cancellationToken);

    /// <summary>Reads the <c>.sng</c> in <paramref name="input"/>, so a caller can read its stored files from the same input.</summary>
    /// <exception cref="IOException">The input can't be read.</exception>
    /// <exception cref="OperationCanceledException">The read was cancelled.</exception>
    internal static SngReadResult Read(SngInput input, CancellationToken cancellationToken = default) =>
        new Parser(input, cancellationToken).Parse();

    private sealed class Parser(SngInput input, CancellationToken cancellationToken)
    {
        private readonly long _length = input.Length;
        // The file's first bytes, read once; reads inside them are served from here.
        private byte[] _prefix = [];
        private readonly List<Finding> _findings = [];
        private readonly List<SngMetadataPair> _metadata = [];
        private readonly List<SngFileEntry> _files = [];
        // Byte offset of each file's index entry, parallel to _files, so findings can point at it.
        private readonly List<long> _fileEntryOffsets = [];
        private SngHeader? _header;

        public SngReadResult Parse()
        {
            ReadPrefix();
            ParseSections();
            return new SngReadResult(_header, _metadata, _files, _findings);
        }

        private void ParseSections()
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_length < HeaderSize)
            {
                Error(SngRules.Sng003, 0, FindingMessages.HeaderTruncatedFormat, _length);
                return;
            }

            Span<byte> header = stackalloc byte[HeaderSize];
            ReadExact(0, header);

            if (!header[..Identifier.Length].SequenceEqual(Identifier))
            {
                Error(SngRules.Sng001, 0, FindingMessages.BadIdentifier);
                return;
            }

            uint version = BinaryPrimitives.ReadUInt32LittleEndian(header[6..]);
            _header = new SngHeader(version, header[10..].ToArray());
            if (version != SupportedVersion)
            {
                // Nothing after the header can be trusted to follow the version 1 layout.
                Error(SngRules.Sng002, 6, FindingMessages.UnsupportedVersionFormat, version);
                return;
            }

            long metadataPos = HeaderSize;
            if (!TryReadSection(metadataPos, FindingMessages.SectionMetadata, out byte[]? metadata))
                return;
            ParseMetadata(metadata, metadataPos);

            cancellationToken.ThrowIfCancellationRequested();

            long indexPos = metadataPos + LengthFieldSize + metadata.Length;
            if (!TryReadSection(indexPos, FindingMessages.SectionFileIndex, out byte[]? index))
                return;
            // File ranges are only checked against an index that parsed cleanly; otherwise the
            // findings would just be echoes of the index problem.
            if (!ParseFileIndex(index, indexPos))
                return;

            cancellationToken.ThrowIfCancellationRequested();

            CheckFileData(indexPos + LengthFieldSize + index.Length);
        }

        /// <summary>
        /// Reads the section whose <c>uint64</c> length field is at <paramref name="pos"/>. The
        /// returned buffer holds the bytes after the length field, including the count.
        /// </summary>
        private bool TryReadSection(long pos, string name, [NotNullWhen(true)] out byte[]? section)
        {
            section = null;
            if (pos > _length - LengthFieldSize)
            {
                Error(SngRules.Sng003, pos, FindingMessages.SectionTruncatedFormat, name);
                return false;
            }

            ulong declared = ReadUInt64(pos);
            if (declared > (ulong)(_length - pos - LengthFieldSize))
            {
                Error(SngRules.Sng003, pos, FindingMessages.SectionPastEndFormat, name, declared);
                return false;
            }

            if (declared > (ulong)Array.MaxLength)
            {
                Error(SngRules.Sng010, pos, FindingMessages.SectionTooLargeFormat, name, declared);
                return false;
            }

            section = new byte[(int)declared];
            ReadExact(pos + LengthFieldSize, section);
            return true;
        }

        /// <summary>Reads a section's leading count and checks it can fit in the section.</summary>
        private bool TryReadCount(byte[] section, long sectionPos, string name, int minEntrySize, out int count)
        {
            count = 0;
            if (section.Length < sizeof(ulong))
            {
                Error(SngRules.Sng010, sectionPos, FindingMessages.LengthMismatchFormat, name, section.Length, sizeof(ulong));
                return false;
            }

            ulong declared = BinaryPrimitives.ReadUInt64LittleEndian(section);
            if (declared > (ulong)((section.Length - sizeof(ulong)) / minEntrySize))
            {
                Error(SngRules.Sng012, sectionPos + LengthFieldSize, FindingMessages.CountTooLargeFormat, name, declared, section.Length);
                return false;
            }

            // Fits in int: it's at most the section length divided by the minimum entry size.
            count = (int)declared;
            return true;
        }

        private void ParseMetadata(byte[] section, long sectionPos)
        {
            string name = FindingMessages.SectionMetadata;
            if (!TryReadCount(section, sectionPos, name, MinPairSize, out int count))
                return;

            long basePos = sectionPos + LengthFieldSize;
            int pos = sizeof(ulong);

            for (int i = 0; i < count; i++)
            {
                if ((i & 1023) == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                int entryPos = pos;
                int number = i + 1;

                if (!TryReadInt32(section, ref pos, out int keyLen))
                {
                    Overrun(number, name, section.Length, basePos + entryPos);
                    return;
                }
                if (keyLen < 0)
                {
                    Error(SngRules.Sng011, basePos + entryPos, FindingMessages.NegativeKeyLengthFormat, number, keyLen);
                    return;
                }
                if (!TrySlice(section, ref pos, keyLen, out ReadOnlySpan<byte> keyBytes))
                {
                    Overrun(number, name, section.Length, basePos + entryPos);
                    return;
                }

                int valueLenPos = pos;
                if (!TryReadInt32(section, ref pos, out int valueLen))
                {
                    Overrun(number, name, section.Length, basePos + entryPos);
                    return;
                }
                if (valueLen < 0)
                {
                    Error(SngRules.Sng011, basePos + valueLenPos, FindingMessages.NegativeValueLengthFormat, number, valueLen);
                    return;
                }
                if (!TrySlice(section, ref pos, valueLen, out ReadOnlySpan<byte> valueBytes))
                {
                    Overrun(number, name, section.Length, basePos + entryPos);
                    return;
                }

                string key = Decode(keyBytes, out bool keyValid);
                if (!keyValid)
                    Error(SngRules.Sng013, basePos + entryPos, FindingMessages.InvalidKeyTextFormat, number);

                string value = Decode(valueBytes, out bool valueValid);
                if (!valueValid)
                    Error(SngRules.Sng013, basePos + valueLenPos, FindingMessages.InvalidValueTextFormat, key);

                _metadata.Add(new SngMetadataPair(key, value, basePos + entryPos));
            }

            if (pos != section.Length)
                Error(SngRules.Sng010, sectionPos, FindingMessages.LengthMismatchFormat, name, section.Length, pos);
        }

        /// <returns>True when every entry parsed and they used exactly the declared length.</returns>
        private bool ParseFileIndex(byte[] section, long sectionPos)
        {
            string name = FindingMessages.SectionFileIndex;
            if (!TryReadCount(section, sectionPos, name, MinFileEntrySize, out int count))
                return false;

            long basePos = sectionPos + LengthFieldSize;
            int pos = sizeof(ulong);

            for (int i = 0; i < count; i++)
            {
                if ((i & 1023) == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                int entryPos = pos;
                int number = i + 1;

                if (!TrySlice(section, ref pos, 1, out ReadOnlySpan<byte> nameLen) ||
                    !TrySlice(section, ref pos, nameLen[0], out ReadOnlySpan<byte> nameBytes) ||
                    !TrySlice(section, ref pos, 2 * sizeof(ulong), out ReadOnlySpan<byte> sizes))
                {
                    Overrun(number, name, section.Length, basePos + entryPos);
                    return false;
                }

                string filename = Decode(nameBytes, out bool nameValid);
                if (!nameValid)
                    Error(SngRules.Sng013, basePos + entryPos, FindingMessages.InvalidFilenameTextFormat, number);

                ulong length = BinaryPrimitives.ReadUInt64LittleEndian(sizes);
                ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(sizes[sizeof(ulong)..]);
                _files.Add(new SngFileEntry(filename, length, offset));
                _fileEntryOffsets.Add(basePos + entryPos);
            }

            if (pos != section.Length)
            {
                Error(SngRules.Sng010, sectionPos, FindingMessages.LengthMismatchFormat, name, section.Length, pos);
                return false;
            }

            return true;
        }

        /// <summary>Checks each file's range against the FileData section whose length field is at <paramref name="dataPos"/>.</summary>
        private void CheckFileData(long dataPos)
        {
            if (dataPos > _length - LengthFieldSize)
            {
                Error(SngRules.Sng003, dataPos, FindingMessages.SectionTruncatedFormat, FindingMessages.SectionFileData);
                return;
            }

            ulong declared = ReadUInt64(dataPos);
            ulong dataStart = (ulong)(dataPos + LengthFieldSize);
            ulong dataEnd = SaturatingAdd(dataStart, declared);
            bool sectionFits = dataEnd <= (ulong)_length;
            if (!sectionFits)
                Error(SngRules.Sng003, dataPos, FindingMessages.SectionPastEndFormat, FindingMessages.SectionFileData, declared);

            ulong sum = 0;
            bool outOfOrder = false;
            ulong previousStart = 0;
            var ranges = new List<(int Index, ulong Start, ulong End)>(_files.Count);

            for (int i = 0; i < _files.Count; i++)
            {
                SngFileEntry file = _files[i];
                ulong end = SaturatingAdd(file.Offset, file.Length);
                sum = SaturatingAdd(sum, file.Length);

                if (end > (ulong)_length)
                {
                    Error(SngRules.Sng003, _fileEntryOffsets[i], FindingMessages.FilePastEndFormat, file.Name, file.Length, file.Offset);
                    continue;
                }

                if (file.Offset < dataStart || end > dataEnd)
                    Error(SngRules.Sng040, _fileEntryOffsets[i], FindingMessages.FileOutsideDataFormat, file.Name, file.Length, file.Offset);

                if (ranges.Count > 0 && file.Offset < previousStart)
                    outOfOrder = true;
                previousStart = file.Offset;
                ranges.Add((i, file.Offset, end));
            }

            // Empty files occupy no bytes, so they can't overlap or leave gaps.
            var occupied = ranges.Where(r => r.End > r.Start).OrderBy(r => r.Start).ThenBy(r => r.Index).ToList();
            bool gaps = false;
            if (occupied.Count > 0)
            {
                gaps = occupied[0].Start > dataStart;

                // The range reaching furthest so far, so a small file inside a large one is still caught.
                var reach = occupied[0];
                foreach (var current in occupied.Skip(1))
                {
                    if (current.Start < reach.End)
                        Error(SngRules.Sng040, _fileEntryOffsets[current.Index], FindingMessages.FilesOverlapFormat, _files[reach.Index].Name, _files[current.Index].Name);
                    else if (current.Start > reach.End)
                        gaps = true;

                    if (current.End > reach.End)
                        reach = current;
                }
            }

            if (gaps)
                Report(SngRules.Sng041, Severity.Warning, (long)dataStart, FindingMessages.FileDataGaps);
            if (outOfOrder)
                Report(SngRules.Sng041, Severity.Warning, (long)dataStart, FindingMessages.FileDataOutOfOrder);
            if (sum != declared)
                Report(SngRules.Sng042, Severity.Warning, dataPos, FindingMessages.FileDataSumMismatchFormat, sum, declared);
            if (sectionFits && dataEnd < (ulong)_length)
                Report(SngRules.Sng043, Severity.Warning, (long)dataEnd, FindingMessages.TrailingBytesFormat, (ulong)_length - dataEnd);
        }

        private void Overrun(int number, string sectionName, int sectionLength, long offset) =>
            Error(SngRules.Sng010, offset, FindingMessages.EntryOverrunFormat, number, sectionName, sectionLength);

        private void Error(string ruleId, long offset, string format, params object[] args) =>
            Report(ruleId, Severity.Error, offset, format, args);

        private void Report(string ruleId, Severity severity, long offset, string format, params object[] args) =>
            _findings.Add(new Finding(ruleId, severity, string.Format(CultureInfo.CurrentCulture, format, args), offset));

        private ulong ReadUInt64(long offset)
        {
            Span<byte> buffer = stackalloc byte[sizeof(ulong)];
            ReadExact(offset, buffer);
            return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
        }

        /// <summary>
        /// Reads up to <see cref="PrefixSize"/> bytes from the start of the file. A file that gets shorter meanwhile
        /// leaves a shorter prefix, and the reads past it fail as they would have without one.
        /// </summary>
        private void ReadPrefix()
        {
            var prefix = new byte[(int)Math.Min(_length, PrefixSize)];
            int filled = 0;
            while (filled < prefix.Length)
            {
                int read = input.Read(prefix.AsSpan(filled), filled);
                if (read == 0)
                    break;
                filled += read;
            }

            _prefix = filled == prefix.Length ? prefix : prefix[..filled];
        }

        /// <summary>Fills <paramref name="buffer"/> from <paramref name="offset"/>. Callers have already checked the range is inside the file.</summary>
        private void ReadExact(long offset, Span<byte> buffer)
        {
            if (offset < _prefix.Length)
            {
                int cached = (int)Math.Min(buffer.Length, _prefix.Length - offset);
                _prefix.AsSpan((int)offset, cached).CopyTo(buffer);
                buffer = buffer[cached..];
                offset += cached;
            }

            input.ReadExactly(buffer, offset);
        }
    }

    private static bool TryReadInt32(byte[] section, ref int pos, out int value)
    {
        if (!TrySlice(section, ref pos, sizeof(int), out ReadOnlySpan<byte> bytes))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        return true;
    }

    private static bool TrySlice(byte[] section, ref int pos, int length, out ReadOnlySpan<byte> slice)
    {
        if (length > section.Length - pos)
        {
            slice = default;
            return false;
        }

        slice = section.AsSpan(pos, length);
        pos += length;
        return true;
    }

    /// <summary>Decodes UTF-8, replacing bad sequences with U+FFFD so the text can still be shown.</summary>
    /// <param name="valid">False when the bytes aren't valid UTF-8 or contain a NUL (sng-spec A11).</param>
    private static string Decode(ReadOnlySpan<byte> bytes, out bool valid)
    {
        valid = Utf8.IsValid(bytes) && !bytes.Contains((byte)0);
        return Encoding.UTF8.GetString(bytes);
    }

    private static ulong SaturatingAdd(ulong a, ulong b) => a > ulong.MaxValue - b ? ulong.MaxValue : a + b;
}
