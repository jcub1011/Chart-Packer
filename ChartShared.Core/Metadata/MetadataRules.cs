using System.Buffers;

namespace ChartShared.Core.Metadata;

/// <summary>The characters metadata can't hold, so it can round-trip through <c>song.ini</c> (sng-spec §6.1).</summary>
public static class MetadataRules
{
    private static readonly SearchValues<char> ForbiddenInKey = SearchValues.Create(";\r\n=\0");
    private static readonly SearchValues<char> ForbiddenInValue = SearchValues.Create(";\r\n\0");

    /// <summary>Whether <paramref name="key"/> contains <c>;</c>, <c>\r</c>, <c>\n</c>, <c>=</c> or NUL (SNG020).</summary>
    public static bool KeyHasForbiddenCharacter(string key) => key.AsSpan().ContainsAny(ForbiddenInKey);

    /// <summary>Whether <paramref name="value"/> contains <c>;</c>, <c>\r</c>, <c>\n</c> or NUL (SNG021). Values may contain <c>=</c>.</summary>
    public static bool ValueHasForbiddenCharacter(string value) => value.AsSpan().ContainsAny(ForbiddenInValue);

    /// <summary>Whether the pair can be written to <c>song.ini</c> and read back unchanged.</summary>
    public static bool CanRoundTrip(string key, string value) =>
        key.Length > 0 && !KeyHasForbiddenCharacter(key) && !ValueHasForbiddenCharacter(value);
}
