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

    /// <summary>A file range is outside the FileData section, or two file ranges overlap.</summary>
    public const string Sng040 = "SNG040";

    /// <summary>Gaps between files, or files not stored in index order.</summary>
    public const string Sng041 = "SNG041";

    /// <summary>The sum of <c>contentsLen</c> values doesn't equal <c>fileDataLen</c>.</summary>
    public const string Sng042 = "SNG042";

    /// <summary>There are bytes after the end of the FileData section.</summary>
    public const string Sng043 = "SNG043";
}
