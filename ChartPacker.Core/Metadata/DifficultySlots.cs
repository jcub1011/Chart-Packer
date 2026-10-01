using ChartPacker.Core.Merging;

namespace ChartPacker.Core.Metadata;

/// <summary>The instrument an icon of a difficulty shows.</summary>
public enum DifficultyGlyph
{
    Guitar,
    Bass,
    Rhythm,
    Drums,
    ProDrums,
    Keys,
    ProKeys,
    Vocals,
    Harmonies,
    Dance,
    Band,
}

/// <summary>The mark that tells a variant of an instrument apart, such as pro or six-fret.</summary>
public enum DifficultyModifier
{
    None,
    Coop,
    Pro,
    Pro22,
    Ghl,
    PhaseShift,
}

/// <summary>A song.ini difficulty, the chart part it rates, and how it's drawn.</summary>
/// <param name="Key">The <c>diff_*</c> key, lower case.</param>
/// <param name="PartId">
/// The <see cref="MidiPart.Id"/> it rates, or <see langword="null"/> for <c>diff_band</c>, which
/// rates the whole chart.
/// </param>
/// <param name="Glyph">The instrument drawn.</param>
/// <param name="Modifier">The variant's mark.</param>
public sealed record DifficultySlot(string Key, string? PartId, DifficultyGlyph Glyph, DifficultyModifier Modifier);

/// <summary>Every known song.ini difficulty, in the order they're shown.</summary>
public static class DifficultySlots
{
    public static IReadOnlyList<DifficultySlot> All { get; } =
    [
        Slot("diff_guitar", DifficultyGlyph.Guitar),
        Slot("diff_guitar_coop", DifficultyGlyph.Guitar, DifficultyModifier.Coop),
        Slot("diff_rhythm", DifficultyGlyph.Rhythm),
        Slot("diff_bass", DifficultyGlyph.Bass),
        Slot("diff_drums", DifficultyGlyph.Drums),
        Slot("diff_drums_real", DifficultyGlyph.ProDrums, DifficultyModifier.Pro),
        Slot("diff_drums_real_ps", DifficultyGlyph.ProDrums, DifficultyModifier.PhaseShift),
        Slot("diff_keys", DifficultyGlyph.Keys),
        Slot("diff_keys_real", DifficultyGlyph.ProKeys, DifficultyModifier.Pro),
        Slot("diff_keys_real_ps", DifficultyGlyph.ProKeys, DifficultyModifier.PhaseShift),
        Slot("diff_vocals", DifficultyGlyph.Vocals),
        Slot("diff_vocals_harm", DifficultyGlyph.Harmonies),
        Slot("diff_guitar_real", DifficultyGlyph.Guitar, DifficultyModifier.Pro),
        Slot("diff_guitar_real_22", DifficultyGlyph.Guitar, DifficultyModifier.Pro22),
        Slot("diff_bass_real", DifficultyGlyph.Bass, DifficultyModifier.Pro),
        Slot("diff_bass_real_22", DifficultyGlyph.Bass, DifficultyModifier.Pro22),
        Slot("diff_guitarghl", DifficultyGlyph.Guitar, DifficultyModifier.Ghl),
        Slot("diff_bassghl", DifficultyGlyph.Bass, DifficultyModifier.Ghl),
        Slot("diff_rhythm_ghl", DifficultyGlyph.Rhythm, DifficultyModifier.Ghl),
        Slot("diff_guitar_coop_ghl", DifficultyGlyph.Guitar, DifficultyModifier.Ghl),
        Slot("diff_dance", DifficultyGlyph.Dance),
        Slot("diff_band", DifficultyGlyph.Band),
    ];

    private static readonly HashSet<string> Keys = new(All.Select(s => s.Key), StringComparer.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="key"/> is one of <see cref="All"/>'s keys, ignoring case.</summary>
    public static bool Contains(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Keys.Contains(key);
    }

    private static DifficultySlot Slot(string key, DifficultyGlyph glyph, DifficultyModifier modifier = DifficultyModifier.None) =>
        new(key, MidiPartCatalog.OwnerOf(key)?.Id, glyph, modifier);
}
