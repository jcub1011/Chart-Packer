using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using ChartShared.Core.Resources;

namespace ChartShared.Core.Metadata;

/// <summary>The kind of value a known key expects (sng-spec §6.2, plus the compound forms some keys use).</summary>
public enum KnownKeyType
{
    /// <summary>Any text.</summary>
    String,

    /// <summary>An optional sign followed by digits, e.g. <c>-150</c>.</summary>
    Integer,

    /// <summary><c>True</c> or <c>False</c>.</summary>
    Boolean,

    /// <summary>A hex colour, e.g. <c>#FF8800</c>.</summary>
    ColorCode,

    /// <summary>Two integers separated by a space, e.g. <c>55000 85000</c>.</summary>
    IntegerPair,

    /// <summary>4 to 6 semitone offsets from standard tuning, optionally followed by a quoted name.</summary>
    ProGuitarTuning,
}

/// <summary>A metadata key other tools already recognise.</summary>
/// <param name="Key">The key, in its canonical lowercase form.</param>
/// <param name="Type">The kind of value it expects.</param>
public sealed record KnownKey(string Key, KnownKeyType Type)
{
    /// <summary>A short, localised description of what the key does.</summary>
    public string Description => KnownKeyDescriptions.ResourceManager.GetString(Key, CultureInfo.CurrentUICulture) ?? "";
}

/// <summary>The registry of known metadata keys (sng-spec §6.3).</summary>
/// <remarks>
/// <para>
/// sng-spec §6.3 treats every key in TheNathannator's
/// <see href="https://github.com/TheNathannator/GuitarGame_ChartFormats">GuitarGame_ChartFormats</see>
/// tag lists as taken (CC0). The keys and types here were compiled independently from those lists
/// (the Standard Tags, and the Clone Hero, Phase Shift, Frets on Fire X and Editor on Fire tags) and
/// from the known-keys table in <c>SngCli/KnownKeys.cs</c> in
/// <see href="https://github.com/mdsitton/SngFileFormat">mdsitton/SngFileFormat</see> at commit
/// <c>6683799</c> (MIT License, © Matthew Sitton; see <c>THIRD-PARTY-NOTICES.md</c>). The
/// descriptions are Chart-Packer's own.
/// </para>
/// <para>
/// Where the sources disagree, the tag lists win: <c>vocal_gender</c> is a string
/// (<c>male</c>/<c>female</c>), not a boolean. Keys that record a game's state rather than describe
/// the chart (FoFiX's <c>scores</c>, <c>count</c>, …) are left out, as are the legacy keys in
/// <see cref="LegacyReplacements"/>.
/// </para>
/// </remarks>
public static class KnownKeys
{
    /// <summary>
    /// Every known key, most commonly used first: the keys pinned at the top of the editor (project
    /// spec F5.2), then song info, chart properties, resources and game-specific keys.
    /// </summary>
    public static IReadOnlyList<KnownKey> All { get; } =
    [
        new("name", KnownKeyType.String),
        new("artist", KnownKeyType.String),
        new("album", KnownKeyType.String),
        new("genre", KnownKeyType.String),
        new("year", KnownKeyType.String),
        new("charter", KnownKeyType.String),
        new("song_length", KnownKeyType.Integer),
        new("preview_start_time", KnownKeyType.Integer),
        new("delay", KnownKeyType.Integer),
        new("diff_band", KnownKeyType.Integer),
        new("diff_guitar", KnownKeyType.Integer),
        new("diff_guitarghl", KnownKeyType.Integer),
        new("diff_guitar_coop", KnownKeyType.Integer),
        new("diff_guitar_coop_ghl", KnownKeyType.Integer),
        new("diff_guitar_real", KnownKeyType.Integer),
        new("diff_guitar_real_22", KnownKeyType.Integer),
        new("diff_rhythm", KnownKeyType.Integer),
        new("diff_rhythm_ghl", KnownKeyType.Integer),
        new("diff_bass", KnownKeyType.Integer),
        new("diff_bassghl", KnownKeyType.Integer),
        new("diff_bass_real", KnownKeyType.Integer),
        new("diff_bass_real_22", KnownKeyType.Integer),
        new("diff_drums", KnownKeyType.Integer),
        new("diff_drums_real", KnownKeyType.Integer),
        new("diff_drums_real_ps", KnownKeyType.Integer),
        new("diff_keys", KnownKeyType.Integer),
        new("diff_keys_real", KnownKeyType.Integer),
        new("diff_keys_real_ps", KnownKeyType.Integer),
        new("diff_vocals", KnownKeyType.Integer),
        new("diff_vocals_harm", KnownKeyType.Integer),
        new("diff_dance", KnownKeyType.Integer),
        new("sub_genre", KnownKeyType.String),
        new("preview_end_time", KnownKeyType.Integer),
        new("preview", KnownKeyType.IntegerPair),
        new("rating", KnownKeyType.Integer),
        new("version", KnownKeyType.Integer),
        new("album_track", KnownKeyType.Integer),
        new("playlist", KnownKeyType.String),
        new("sub_playlist", KnownKeyType.String),
        new("playlist_track", KnownKeyType.Integer),
        new("loading_phrase", KnownKeyType.String),
        new("tags", KnownKeyType.String),
        new("modchart", KnownKeyType.Boolean),
        new("lyrics", KnownKeyType.Boolean),
        new("pro_drums", KnownKeyType.Boolean),
        new("five_lane_drums", KnownKeyType.Boolean),
        new("drum_fallback_blue", KnownKeyType.Boolean),
        new("vocal_gender", KnownKeyType.String),
        new("vocal_scroll_speed", KnownKeyType.Integer),
        new("tuning_offset_cents", KnownKeyType.Integer),
        new("real_guitar_tuning", KnownKeyType.ProGuitarTuning),
        new("real_guitar_22_tuning", KnownKeyType.ProGuitarTuning),
        new("real_bass_tuning", KnownKeyType.ProGuitarTuning),
        new("real_bass_22_tuning", KnownKeyType.ProGuitarTuning),
        new("real_keys_lane_count_right", KnownKeyType.Integer),
        new("real_keys_lane_count_left", KnownKeyType.Integer),
        new("hopo_frequency", KnownKeyType.Integer),
        new("eighthnote_hopo", KnownKeyType.Boolean),
        new("sustain_cutoff_threshold", KnownKeyType.Integer),
        new("multiplier_note", KnownKeyType.Integer),
        new("end_events", KnownKeyType.Boolean),
        new("sysex_slider", KnownKeyType.Boolean),
        new("sysex_high_hat_ctrl", KnownKeyType.Boolean),
        new("sysex_rimshot", KnownKeyType.Boolean),
        new("sysex_open_bass", KnownKeyType.Boolean),
        new("sysex_pro_slide", KnownKeyType.Boolean),
        new("guitar_type", KnownKeyType.Integer),
        new("bass_type", KnownKeyType.Integer),
        new("kit_type", KnownKeyType.Integer),
        new("keys_type", KnownKeyType.Integer),
        new("dance_type", KnownKeyType.Integer),
        new("eof_midi_import_drum_accent_velocity", KnownKeyType.Integer),
        new("eof_midi_import_drum_ghost_velocity", KnownKeyType.Integer),
        new("icon", KnownKeyType.String),
        new("cover", KnownKeyType.String),
        new("background", KnownKeyType.String),
        new("video", KnownKeyType.String),
        new("video_loop", KnownKeyType.Boolean),
        new("video_start_time", KnownKeyType.Integer),
        new("video_end_time", KnownKeyType.Integer),
        new("cassettecolor", KnownKeyType.ColorCode),
        new("link_name_a", KnownKeyType.String),
        new("link_name_b", KnownKeyType.String),
        new("banner_link_a", KnownKeyType.String),
        new("banner_link_b", KnownKeyType.String),
        new("tutorial", KnownKeyType.Boolean),
        new("boss_battle", KnownKeyType.Boolean),
        new("unlock_id", KnownKeyType.String),
        new("unlock_require", KnownKeyType.String),
        new("unlock_text", KnownKeyType.String),
        new("unlock_completed", KnownKeyType.Integer),
    ];

    /// <summary>Legacy keys and the key that replaced each one (SNG025), compared ignoring case.</summary>
    public static IReadOnlyDictionary<string, string> LegacyReplacements { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["frets"] = "charter",
        ["track"] = "album_track",
        ["star_power_note"] = "multiplier_note",
        ["pro_drum"] = "pro_drums",
    };

    private static readonly Dictionary<string, KnownKey> ByKey = All.ToDictionary(k => k.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Looks up <paramref name="key"/>, ignoring case.</summary>
    public static bool TryGet(string key, [NotNullWhen(true)] out KnownKey? knownKey)
    {
        ArgumentNullException.ThrowIfNull(key);
        return ByKey.TryGetValue(key, out knownKey);
    }
}
