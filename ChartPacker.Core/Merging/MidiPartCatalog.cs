using ChartPacker.Core.Charts;

namespace ChartPacker.Core.Merging;

/// <summary>
/// A part a chart can have, one or more MIDI tracks that go together, e.g. the three harmony tracks,
/// and the song.ini keys that describe it.
/// </summary>
/// <param name="Id">A stable identifier: <c>guitar</c>, <c>drums</c>, …, or the track name for a part that isn't known.</param>
/// <param name="TrackNames">Its tracks, in upper case.</param>
/// <param name="Keys">The song.ini keys that describe it (its difficulties and settings), lower case.</param>
public sealed record MidiPart(string Id, IReadOnlyList<string> TrackNames, IReadOnlyList<string> Keys)
{
    /// <summary>Whether the part is one of <see cref="MidiPartCatalog.Known"/>.</summary>
    public bool IsKnown => MidiPartCatalog.Known.Any(p => p.Id == Id);

    /// <summary>
    /// Whether <paramref name="trackName"/> is one of its tracks, ignoring case and surrounding space; but a track
    /// <see cref="MidiLayout.TrackNames"/> lists only by its exact name, as YARG reads no other.
    /// </summary>
    public bool Has(string trackName)
    {
        ArgumentNullException.ThrowIfNull(trackName);
        string name = trackName.Trim();
        if (!TrackNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            return false;
        return MidiLayout.TrackNames.ContainsKey(trackName) || !MidiLayout.TrackNames.ContainsKey(name.ToUpperInvariant());
    }
}

/// <summary>The parts of a chart's MIDI file, and the tracks that aren't part of any (events, venue, beat).</summary>
public static class MidiPartCatalog
{
    /// <summary>Every known part, in the order a merged chart lists them.</summary>
    public static IReadOnlyList<MidiPart> Known { get; } =
    [
        new("guitar", ["PART GUITAR", "T1 GEMS"], ["diff_guitar", "guitar_type"]),
        new("guitar_coop", ["PART GUITAR COOP"], ["diff_guitar_coop"]),
        new("rhythm", ["PART RHYTHM"], ["diff_rhythm"]),
        new("bass", ["PART BASS"], ["diff_bass", "bass_type", "sysex_open_bass"]),
        new("drums", ["PART DRUMS", "PART DRUMS_2X"],
        [
            "diff_drums", "diff_drums_real", "diff_drums_real_ps", "pro_drums", "pro_drum", "five_lane_drums", "drum_fallback_blue",
            "kit_type", "sysex_high_hat_ctrl", "sysex_rimshot", "eof_midi_import_drum_accent_velocity", "eof_midi_import_drum_ghost_velocity",
        ]),
        new("keys", ["PART KEYS"], ["diff_keys", "keys_type"]),
        new("pro_keys", ["PART REAL_KEYS_X", "PART REAL_KEYS_H", "PART REAL_KEYS_M", "PART REAL_KEYS_E", "PART KEYS_ANIM_LH", "PART KEYS_ANIM_RH"],
            ["diff_keys_real", "diff_keys_real_ps", "real_keys_lane_count_right", "real_keys_lane_count_left"]),
        new("vocals", ["PART VOCALS"], ["diff_vocals", "vocal_gender", "vocal_scroll_speed", "lyrics"]),
        new("harmonies", ["HARM1", "HARM2", "HARM3"], ["diff_vocals_harm"]),
        new("pro_guitar", ["PART REAL_GUITAR"], ["diff_guitar_real", "real_guitar_tuning"]),
        new("pro_guitar_22", ["PART REAL_GUITAR_22"], ["diff_guitar_real_22", "real_guitar_22_tuning"]),
        new("pro_bass", ["PART REAL_BASS"], ["diff_bass_real", "real_bass_tuning"]),
        new("pro_bass_22", ["PART REAL_BASS_22"], ["diff_bass_real_22", "real_bass_22_tuning"]),
        new("guitar_ghl", ["PART GUITAR GHL"], ["diff_guitarghl"]),
        new("bass_ghl", ["PART BASS GHL"], ["diff_bassghl"]),
        new("rhythm_ghl", ["PART RHYTHM GHL"], ["diff_rhythm_ghl"]),
        new("guitar_coop_ghl", ["PART GUITAR COOP GHL"], ["diff_guitar_coop_ghl"]),
        new("dance", ["PART DANCE"], ["diff_dance", "dance_type"]),
    ];

    private static readonly Dictionary<string, MidiPart> ByTrack = Known
        .SelectMany(p => p.TrackNames.Select(t => KeyValuePair.Create(t, p)))
        .ToDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The part <paramref name="trackName"/> belongs to: a known part, or for any other
    /// <c>PART</c> track a part of its own; <see langword="null"/> for a track that isn't a part.
    /// </summary>
    public static MidiPart? PartOf(string? trackName)
    {
        if (trackName is null)
            return null;
        string name = trackName.Trim();
        if (ByTrack.TryGetValue(name, out var part))
            return part;
        return name.StartsWith("PART ", StringComparison.OrdinalIgnoreCase) ? new MidiPart(name.ToUpperInvariant(), [name.ToUpperInvariant()], []) : null;
    }

    /// <summary>Where <paramref name="part"/> is listed; parts that aren't known go last, by name.</summary>
    public static int OrderOf(MidiPart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        int index = Known.ToList().FindIndex(p => p.Id == part.Id);
        return index < 0 ? Known.Count : index;
    }

    /// <summary>The instruments the chart reader models among <paramref name="part"/>'s tracks, e.g. drums for the drums part.</summary>
    public static IReadOnlyList<ChartInstrument> InstrumentsOf(MidiPart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        return [.. part.TrackNames.Where(MidiLayout.TrackNames.ContainsKey).Select(t => MidiLayout.TrackNames[t]).Distinct()];
    }

    /// <summary>The known part that owns the song.ini key <paramref name="key"/>, ignoring case.</summary>
    public static MidiPart? OwnerOf(string key) =>
        Known.FirstOrDefault(p => p.Keys.Contains(key, StringComparer.OrdinalIgnoreCase));
}
