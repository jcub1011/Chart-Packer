using System.Runtime.CompilerServices;
using ChartPacker.Core.Scanning;

namespace ChartPacker.Core.Library;

/// <summary>Whether a filter wants songs that have something, songs that don't, or doesn't care.</summary>
public enum FilterPresence
{
    /// <summary>The filter is off.</summary>
    Either,

    /// <summary>Only songs that have it.</summary>
    Includes,

    /// <summary>Only songs that don't have it.</summary>
    Excludes,
}

/// <summary>How a <see cref="SetFilter{TFlags}"/>'s included items combine.</summary>
public enum SetMatch
{
    /// <summary>A song must have every included item.</summary>
    All,

    /// <summary>A song must have at least one included item.</summary>
    Any,
}

/// <summary>The parts a chart can have, one per <see cref="Merging.MidiPartCatalog.Known"/> part, in its order.</summary>
[Flags]
public enum LibraryInstruments
{
    None = 0,
    Guitar = 1 << 0,
    GuitarCoop = 1 << 1,
    Rhythm = 1 << 2,
    Bass = 1 << 3,
    Drums = 1 << 4,
    Keys = 1 << 5,
    ProKeys = 1 << 6,
    Vocals = 1 << 7,
    Harmonies = 1 << 8,
    ProGuitar = 1 << 9,
    ProGuitar22 = 1 << 10,
    ProBass = 1 << 11,
    ProBass22 = 1 << 12,
    GuitarGhl = 1 << 13,
    BassGhl = 1 << 14,
    RhythmGhl = 1 << 15,
    GuitarCoopGhl = 1 << 16,
    Dance = 1 << 17,
}

/// <summary>The difficulties a chart has notes for, on any of its parts.</summary>
[Flags]
public enum LibraryDifficulties
{
    None = 0,
    Easy = 1 << 0,
    Medium = 1 << 1,
    Hard = 1 << 2,
    Expert = 1 << 3,
}

/// <summary>A song's audio stems, grouped: <see cref="Drums"/> is <c>drums</c> and <c>drums_1</c>–<c>drums_4</c>, and so on.</summary>
[Flags]
public enum LibraryStems
{
    None = 0,
    Song = 1 << 0,
    Guitar = 1 << 1,
    Rhythm = 1 << 2,
    Bass = 1 << 3,
    Keys = 1 << 4,
    Drums = 1 << 5,
    Vocals = 1 << 6,
    Crowd = 1 << 7,
    Preview = 1 << 8,
}

/// <summary>A filter on a set of items a song may have, such as its instruments.</summary>
/// <param name="Included">Items the song must have: every one of them, or at least one, as <paramref name="Match"/> says.</param>
/// <param name="Excluded">Items the song must not have, whatever <paramref name="Match"/> says.</param>
/// <param name="Match">How the included items combine.</param>
public readonly record struct SetFilter<TFlags>(TFlags Included, TFlags Excluded, SetMatch Match = SetMatch.All)
    where TFlags : struct, Enum
{
    /// <summary>The filter narrows the list: something is included or excluded.</summary>
    public bool IsActive => MaskOf(Included) != 0 || MaskOf(Excluded) != 0;

    /// <summary>Whether a song with <paramref name="value"/> passes; one whose value isn't known doesn't, unless the filter is off.</summary>
    public bool Matches(TFlags? value)
    {
        if (!IsActive)
            return true;
        if (value is not { } v)
            return false;

        ulong has = MaskOf(v);
        ulong included = MaskOf(Included);
        if ((has & MaskOf(Excluded)) != 0)
            return false;

        return Match == SetMatch.All
            ? (has & included) == included
            : included == 0 || (has & included) != 0;
    }

    private static ulong MaskOf(TFlags value) => Unsafe.SizeOf<TFlags>() switch
    {
        1 => Unsafe.As<TFlags, byte>(ref value),
        2 => Unsafe.As<TFlags, ushort>(ref value),
        4 => Unsafe.As<TFlags, uint>(ref value),
        _ => Unsafe.As<TFlags, ulong>(ref value),
    };
}

/// <summary>Helpers for the library's content filters.</summary>
public static class LibraryContentFilters
{
    /// <summary>Whether a song that has something (<see langword="null"/> when that isn't known) passes <paramref name="presence"/>.</summary>
    public static bool PresenceMatches(FilterPresence presence, bool? has) =>
        presence == FilterPresence.Either || (has is { } h && h == (presence == FilterPresence.Includes));

    /// <summary>The group a stem belongs to, e.g. <see cref="LibraryStems.Drums"/> for <c>drums_2</c>; <see cref="LibraryStems.None"/> for a name that isn't a stem.</summary>
    public static LibraryStems StemGroupOf(string stem)
    {
        ArgumentNullException.ThrowIfNull(stem);
        if (!AudioStems.IsStem(stem))
            return LibraryStems.None;

        int underscore = stem.IndexOf('_', StringComparison.Ordinal);
        string group = underscore < 0 ? stem : stem[..underscore];
        return group.ToLowerInvariant() switch
        {
            "song" => LibraryStems.Song,
            "guitar" => LibraryStems.Guitar,
            "rhythm" => LibraryStems.Rhythm,
            "bass" => LibraryStems.Bass,
            "keys" => LibraryStems.Keys,
            "drums" => LibraryStems.Drums,
            "vocals" => LibraryStems.Vocals,
            "crowd" => LibraryStems.Crowd,
            "preview" => LibraryStems.Preview,
            _ => LibraryStems.None,
        };
    }
}
