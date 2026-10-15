namespace ChartPacker.Core.Validation;

/// <summary>Rule IDs from the validation rule catalogue (project spec §6). IDs are stable and never reused.</summary>
public static class SngRules
{
    /// <summary>The first 6 bytes aren't <c>SNGPKG</c>.</summary>
    public const string Sng001 = "SNG001";

    /// <summary><c>version</c> isn't 1.</summary>
    public const string Sng002 = "SNG002";

    /// <summary>A section length, string length or file range goes past the end of the file.</summary>
    public const string Sng003 = "SNG003";

    /// <summary><c>metadataLen</c> or <c>fileMetaLen</c> doesn't equal the bytes the entries use.</summary>
    public const string Sng010 = "SNG010";

    /// <summary>A negative <c>keyLen</c> or <c>valueLen</c>.</summary>
    public const string Sng011 = "SNG011";

    /// <summary><c>metadataCount</c> or <c>fileCount</c> can't fit in the declared section length.</summary>
    public const string Sng012 = "SNG012";

    /// <summary>Invalid UTF-8, or a <c>0x00</c> byte, in a key, value or filename.</summary>
    public const string Sng013 = "SNG013";

    /// <summary>A key contains <c>;</c>, <c>\r</c>, <c>\n</c> or <c>=</c>.</summary>
    public const string Sng020 = "SNG020";

    /// <summary>A value contains <c>;</c>, <c>\r</c> or <c>\n</c>.</summary>
    public const string Sng021 = "SNG021";

    /// <summary>A duplicate key, compared ignoring case.</summary>
    public const string Sng022 = "SNG022";

    /// <summary>An empty key.</summary>
    public const string Sng023 = "SNG023";

    /// <summary>A known key's value doesn't parse as its expected type (e.g. <c>diff_guitar = hard</c>).</summary>
    public const string Sng024 = "SNG024";

    /// <summary>A legacy key (<c>frets</c>, <c>track</c>, <c>star_power_note</c>, <c>pro_drum</c>), with its replacement suggested.</summary>
    public const string Sng025 = "SNG025";

    /// <summary>A path segment breaks the filename rules (forbidden or control character, <c>..</c>, trailing <c>.</c> or space, reserved name, empty segment).</summary>
    public const string Sng030 = "SNG030";

    /// <summary>The stored filename is longer than 255 UTF-8 bytes.</summary>
    public const string Sng031 = "SNG031";

    /// <summary>A duplicate filename, compared ignoring case.</summary>
    public const string Sng032 = "SNG032";

    /// <summary>The filename would resolve to a path outside the extraction folder.</summary>
    public const string Sng033 = "SNG033";

    /// <summary>A file range is outside the FileData section, or two file ranges overlap.</summary>
    public const string Sng040 = "SNG040";

    /// <summary>Gaps between files, or files not stored in index order.</summary>
    public const string Sng041 = "SNG041";

    /// <summary>The sum of <c>contentsLen</c> values doesn't equal <c>fileDataLen</c>.</summary>
    public const string Sng042 = "SNG042";

    /// <summary>There are bytes after the end of the FileData section.</summary>
    public const string Sng043 = "SNG043";

    /// <summary>There is no registered audio stem.</summary>
    public const string Sng052 = "SNG052";

    /// <summary><c>song.ini</c> is stored as a file.</summary>
    public const string Sng053 = "SNG053";

    /// <summary>A known boolean key isn't exactly <c>True</c> or <c>False</c> (e.g. <c>true</c>, <c>1</c>).</summary>
    public const string Sng060 = "SNG060";

    /// <summary>An empty value. It is dropped when packing.</summary>
    public const string Sng061 = "SNG061";

    /// <summary>A key of a part the chart has no notes for. It is dropped when packing.</summary>
    public const string Sng062 = "SNG062";

    /// <summary>There is no <c>song.ini</c>.</summary>
    public const string Sng070 = "SNG070";

    /// <summary><c>song.ini</c> has no <c>[song]</c> section (or can't be read).</summary>
    public const string Sng071 = "SNG071";

    /// <summary>A file or folder was excluded as OS or tool junk.</summary>
    public const string Sng072 = "SNG072";

    /// <summary>A subfolder was skipped because including subfolders is turned off.</summary>
    public const string Sng073 = "SNG073";
}
