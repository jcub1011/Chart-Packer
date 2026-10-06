using ChartPacker.Core.Charts;
using ChartPacker.Core.Tests.Charts;

namespace ChartBuilder.Tests.Charting;

/// <summary>A golden <c>notes.mid</c>, and the song.ini settings it's read with.</summary>
internal sealed record MidiFixture(string Name, byte[] Midi, ChartReadOptions Options)
{
    public override string ToString() => Name;
}

/// <summary>
/// Golden <c>notes.mid</c> files, built with <see cref="MidiBuilder"/> at 480 ticks per beat, that between them hold every
/// part, variant and kind of note in charter-spec §7, for the loader, game view, writer and lint tests to share.
/// </summary>
/// <remarks>
/// With the default settings a note is a sustain when held for 160 ticks or more, and a natural HOPO when it follows the
/// note before by no more than 161 ticks, or 170 under Rock Band's threshold (see
/// <see cref="ChartReadOptions.MidiHopoThresholds"/>).
/// </remarks>
internal static class MidiFixtures
{
    private const int Beat = 480;

    // Before All, which uses them: static properties are set in the order they're declared.
    /// <summary>The settings <see cref="FourLaneDrums"/> is read with: song.ini's <c>pro_drums = False</c>.</summary>
    public static ChartReadOptions FourLaneDrumsOptions { get; } = new(ProDrums: false);

    /// <summary>The settings <see cref="MultiplierNote103"/> is read with: song.ini's <c>multiplier_note = 103</c>.</summary>
    public static ChartReadOptions MultiplierNote103Options { get; } = new(MultiplierNote: 103);

    /// <summary>The settings <see cref="SongIniSettings"/> is read with: a sustain cutoff of 60 and eighth-note HOPOs.</summary>
    public static ChartReadOptions SongIniSettingsOptions { get; } = new(SustainCutoff: 60, EighthNoteHopo: true);

    /// <summary>Every fixture.</summary>
    public static IReadOnlyList<MidiFixture> All { get; } =
    [
        new(nameof(LeadGuitar), LeadGuitar(), ChartReadOptions.Default),
        new(nameof(ForcingAndTaps), ForcingAndTaps(), ChartReadOptions.Default),
        new(nameof(RhythmAndCoop), RhythmAndCoop(), ChartReadOptions.Default),
        new(nameof(SixFret), SixFret(), ChartReadOptions.Default),
        new(nameof(FourLaneDrums), FourLaneDrums(), FourLaneDrumsOptions),
        new(nameof(ProDrums), ProDrums(), ChartReadOptions.Default),
        new(nameof(FiveLaneDrums), FiveLaneDrums(), ChartReadOptions.Default),
        new(nameof(TempoMap), TempoMap(), ChartReadOptions.Default),
        new(nameof(RawEvents), RawEvents(), ChartReadOptions.Default),
        new(nameof(LegacyNames), LegacyNames(), ChartReadOptions.Default),
        new(nameof(DuplicateTrack), DuplicateTrack(), ChartReadOptions.Default),
        new(nameof(Passthrough), Passthrough(), ChartReadOptions.Default),
        new(nameof(MultiplierNote103), MultiplierNote103(), MultiplierNote103Options),
        new(nameof(SongIniSettings), SongIniSettings(), SongIniSettingsOptions),
    ];

    /// <summary>Each fixture's name, for <c>[DynamicData]</c>; <see cref="Named"/> gives the fixture.</summary>
    public static IEnumerable<object[]> Names => All.Select(f => new object[] { f.Name });

    public static MidiFixture Named(string name) => All.Single(f => f.Name == name);

    /// <summary>A song's tempo track, named <c>Song</c>, at 120 BPM in 4/4 from tick 0.</summary>
    private static MidiBuilder Song() => new MidiBuilder(Beat).Track("Song").Tempo(0, 120).TimeSignature(0, 4, 2);

    /// <summary>
    /// <c>PART GUITAR</c>: every lane and open notes (<c>[ENHANCED_OPENS]</c>) on every difficulty, chords, a disjoint
    /// chord, an extended sustain, 1-tick and long sustains either side of the cutoff, natural HOPOs at spacings either
    /// side of each threshold, star power, a solo and a big rock ending.
    /// </summary>
    public static byte[] LeadGuitar()
    {
        var midi = Song()
            .Track("PART GUITAR")
            .Text(0, "[ENHANCED_OPENS]");

        // Expert: green 96 to orange 100, open 95.
        midi.Note(0, 96, 1) // A 1-tick sustain: a plain note.
            .Note(480, 97, 900) // An extended sustain, held under the notes after it.
            .Note(600, 98) // 120 after: a HOPO.
            .Note(760, 99) // 160 after.
            .Note(921, 100) // 161 after: a HOPO at YARG's threshold, not past it.
            .Note(1086, 95) // 165 after: an open, a HOPO only at Rock Band's.
            .Note(1256, 96) // 170 after.
            .Note(1427, 97) // 171 after: a strum.
            .Note(1547, 97) // The same note again: a strum.
            .Note(1920, 96, 240).Note(1920, 98, 240) // A chord.
            .Note(2400, 97, 480).Note(2400, 99, 120) // A disjoint chord.
            .Note(2880, 100, 160) // Held for the cutoff: a sustain.
            .Note(3360, 99, 159) // A tick less: a plain note.
            .Note(3840, 96, 1920); // A long sustain.

        // Hard, Medium and Easy: each lane, an open and a chord.
        foreach (int b in (int[])[84, 72, 60])
        {
            for (int lane = 0; lane < 5; lane++)
                midi.Note(lane * Beat, b + lane);
            midi.Note(2400, b - 1).Note(2880, b, 240).Note(2880, b + 2, 240);
        }

        return midi
            .Note(0, 116, 1920) // Star power.
            .Note(1920, 103, 960) // A solo.
            .Note(5760, 96, 960)
            .Note(5760, 120, 960).Note(5760, 121, 960).Note(5760, 122, 960).Note(5760, 123, 960).Note(5760, 124, 960) // A big rock ending.
            .Build();
    }

    /// <summary>
    /// <c>PART BASS</c>: force HOPO and force strum markers over single notes, chords and several chords, both on one
    /// chord, taps as the 104 marker and as Phase Shift sysex for a difficulty and for all, and Phase Shift opens for a
    /// difficulty and for all.
    /// </summary>
    public static byte[] ForcingAndTaps() => Song()
        .Track("PART BASS")
        // Expert: green 96 to orange 100, force HOPO 101, force strum 102.
        .Note(0, 96)
        .Note(480, 98).Note(480, 101, 1) // A strum forced HOPO.
        .Note(960, 99).Note(1080, 100).Note(1080, 102, 10) // A natural HOPO forced strum.
        .Note(1440, 96).Note(1440, 97).Note(1440, 101, 10) // A chord forced HOPO.
        .Note(1920, 97).Note(2040, 98).Note(2160, 99).Note(1920, 104, 280) // A tap marker over three notes.
        .Note(2880, 96).PhaseShift(2880, 3, MidiLayout.SysexTap, true).PhaseShift(2890, 3, MidiLayout.SysexTap, false)
        .Note(3360, 100).PhaseShift(3360, 0xFF, MidiLayout.SysexTap, true).PhaseShift(3370, 0xFF, MidiLayout.SysexTap, false)
        .Note(3840, 96).PhaseShift(3840, 3, MidiLayout.SysexOpen, true).PhaseShift(3850, 3, MidiLayout.SysexOpen, false)
        .Note(4320, 97).PhaseShift(4320, 0xFF, MidiLayout.SysexOpen, true).PhaseShift(4330, 0xFF, MidiLayout.SysexOpen, false)
        .Note(4800, 98).Note(4800, 101, 1).Note(4800, 102, 1) // Forced both ways: HOPO wins.
        .Note(5280, 97).Note(5280, 101, 1).Note(5280, 104, 1) // Forced and tapped: tap wins.
        // Hard: green 84, force HOPO 89; its notes under the shared tap marker and all-difficulty sysex.
        .Note(0, 84).Note(240, 85).Note(480, 86).Note(480, 89, 1)
        .Note(1920, 84).Note(3360, 88).Note(4320, 85)
        .Build();

    /// <summary><c>PART RHYTHM</c> and <c>PART GUITAR COOP</c>: every lane, a chord and a sustain, and co-op's star power.</summary>
    public static byte[] RhythmAndCoop()
    {
        var midi = Song().Track("PART RHYTHM");
        for (int lane = 0; lane < 5; lane++)
            midi.Note(lane * Beat, 96 + lane).Note(lane * Beat, 72 + lane);
        midi.Note(2400, 96, 480).Note(2400, 99, 480).Note(2400, 84, 480);

        midi.Track("PART GUITAR COOP");
        for (int lane = 0; lane < 5; lane++)
            midi.Note(lane * 240, 96 + lane).Note(lane * Beat, 60 + lane);
        return midi.Note(1440, 97, 960).Note(1440, 98, 960).Note(0, 116, 720).Build();
    }

    /// <summary>
    /// The four six-fret parts: Guitar with every lane and the open note, barre chords, natural HOPOs, forcing and a tap
    /// marker, and star power; the others a few notes each.
    /// </summary>
    public static byte[] SixFret()
    {
        // Expert: open 94, white 1-3 95-97, black 1-3 98-100, force HOPO 101, force strum 102.
        var midi = Song()
            .Track("PART GUITAR GHL")
            .Note(0, 94).Note(480, 95).Note(600, 96).Note(720, 97).Note(840, 98).Note(960, 99).Note(1080, 100)
            .Note(1440, 95).Note(1440, 98) // A barre chord: white 1 and black 1.
            .Note(1920, 96).Note(1920, 99).Note(1920, 97, 480).Note(1920, 100, 480)
            .Note(2400, 95).Note(2400, 96).Note(2400, 101, 1) // A chord forced HOPO.
            .Note(2520, 97).Note(2520, 102, 1) // A natural HOPO forced strum.
            .Note(2880, 98).Note(2880, 104, 1) // A tap.
            .Note(0, 116, 960);
        foreach (int b in (int[])[82, 70, 58])
            midi.Note(0, b).Note(480, b + 1).Note(960, b + 4).Note(960, b + 1).Note(1440, b + 6, 480);

        return midi
            .Track("PART BASS GHL").Note(0, 94).Note(480, 98, 480)
            .Track("PART RHYTHM GHL").Note(0, 95).Note(120, 99)
            .Track("PART GUITAR COOP GHL").Note(0, 100).Note(480, 82).Note(480, 70).Note(480, 58)
            .Build();
    }

    /// <summary>
    /// Four-lane <c>PART DRUMS</c>, read with <see cref="FourLaneDrumsOptions"/>: every pad, the kick and the 2x kick on
    /// every difficulty, a fill, a roll, a trill reaching down to Easy, and star power. No tom markers, which would make
    /// them pro drums.
    /// </summary>
    public static byte[] FourLaneDrums()
    {
        var midi = Song().Track("PART DRUMS");
        foreach (int b in (int[])[96, 84, 72, 60])
        {
            midi.Note(0, b).Note(0, b + 1).Note(240, b + 2).Note(480, b + 3).Note(720, b + 4).Note(720, b);
        }

        return midi
            .Note(960, 95) // The 2x kick.
            .Note(1920, 97).Note(1920, 120, 480).Note(1920, 121, 480).Note(1920, 122, 480).Note(1920, 123, 480).Note(1920, 124, 480) // A fill.
            .Note(2400, 126, 240, velocity: 100).Note(2400, 98).Note(2520, 98) // A roll.
            .Note(2880, 127, 240, velocity: 25).Note(2880, 97).Note(2940, 98).Note(3000, 97).Note(2880, 61).Note(2940, 62) // A trill.
            .Note(0, 116, 960)
            .Build();
    }

    /// <summary>
    /// Pro <c>PART DRUMS</c>: cymbals and toms on yellow, blue and green, tom markers spanning several notes, accents
    /// and ghosts after <c>[ENABLE_CHART_DYNAMICS]</c> (velocity on a kick, which the games ignore), the 2x kick, a fill,
    /// a roll and a trill with their velocities, disco flip mixes as the writer writes them and as a game reads another
    /// spelling, free text, and star power.
    /// </summary>
    public static byte[] ProDrums() => Song()
        .Track("PART DRUMS")
        .Text(0, "[ENABLE_CHART_DYNAMICS]")
        // Expert: kick 96, red 97, yellow 98, blue 99, green 100; 2x kick 95.
        .Note(0, 96, velocity: 127).Note(0, 98, velocity: 127) // An accented kick, which reads as a plain kick, and an accent.
        .Note(240, 97, velocity: 1).Note(240, 99) // A ghost.
        .Note(480, 100, velocity: 1).Note(480, 95, velocity: 127) // A ghost cymbal and a 2x kick.
        .Note(720, 98).Note(720, 99).Note(720, 100) // Toms on every pad.
        .Note(720, 110, 960).Note(720, 111, 1).Note(720, 112, 240)
        .Note(960, 98).Note(960, 100).Note(1200, 98).Note(1440, 98) // Yellow toms under the marker; green a cymbal as its marker ends.
        .Note(1680, 98).Note(1680, 99) // Cymbals again, the marker let go.
        .Text(2880, "[mix 3 drums0d]").Note(2880, 97).Note(2880, 98).Note(3120, 97, velocity: 127).Note(3120, 98, velocity: 1)
        .Text(3360, "[mix 3 drums0]").Note(3360, 97).Note(3360, 98)
        .Note(3840, 120, 480).Note(3840, 121, 480).Note(3840, 122, 480).Note(3840, 123, 480).Note(3840, 124, 480).Note(3840, 96) // A fill.
        .Note(4320, 126, 480, velocity: 35).Note(4320, 99).Note(4440, 99).Note(4560, 99) // A roll down to Medium.
        .Note(4800, 127, 480, velocity: 100).Note(4800, 98).Note(4920, 99).Note(5040, 98) // A trill on Expert.
        .Text(5280, "[idle]")
        .Note(0, 116, 960)
        // Hard: kick 84, red 85, yellow 86, blue 87, green 88; disco flip on.
        .Note(0, 84).Note(240, 85, velocity: 1).Note(720, 86).Note(1680, 87)
        .Text(2880, "[mix 2 drums0d]").Note(2880, 85).Note(2880, 86)
        .Note(4320, 87).Note(4440, 87)
        // Medium: disco flip in a spelling the writer doesn't write, kept as text, which the games read all the same.
        .Note(0, 72).Note(720, 74)
        .Text(2880, "[mix_1_drums0d]").Note(2880, 73).Note(2880, 74)
        // Easy.
        .Note(0, 60).Note(480, 61).Note(960, 62).Note(1440, 63).Note(1920, 64)
        .Build();

    /// <summary>
    /// Five-lane <c>PART DRUMS</c>, detected from its first fifth-pad note: every pad on every difficulty, the 2x kick, and
    /// a tom marker after it, which changes nothing.
    /// </summary>
    public static byte[] FiveLaneDrums()
    {
        var midi = Song().Track("PART DRUMS");
        foreach (int b in (int[])[96, 84, 72, 60])
        {
            for (int pad = 5; pad >= 0; pad--)
                midi.Note(pad * 240, b + pad);
        }

        return midi.Note(1440, 95).Note(1440, 98).Note(1440, 110, 240).Note(1920, 101).Note(1920, 100).Build();
    }

    /// <summary>
    /// Tempo changes, time signatures and an anchor, with notes and sustains across them; the tempo track's other
    /// events; and <c>EVENTS</c> with sections.
    /// </summary>
    public static byte[] TempoMap() => new MidiBuilder(Beat)
        .Track("Song")
        .Tempo(0, 120).TimeSignature(0, 4, 2)
        .Meta(0, 0x59, [0xFE, 0])
        .Tempo(1920, 150).Text(1920, "[chartpacker_anchor 2000000]")
        .TimeSignature(3840, 6, 3).Tempo(3840, 90.5)
        .Meta(5280, 0x58, [7, 3])
        .Text(5760, "tempo track text")
        .Track("PART GUITAR")
        .Note(0, 96).Note(1800, 97, 480).Note(1920, 98).Note(3840, 99, 960).Note(5280, 100).Note(5400, 96)
        .Track("EVENTS")
        .Text(0, "[music_start]").Text(0, "[section Intro]").Text(1920, "[section Verse 1]").Meta(3840, 0x06, "[prc_chorus]"u8.ToArray())
        .Text(7680, "[end]")
        .Build();

    /// <summary>
    /// Events the model keeps as they are: an animation note, face-off phrases, an unknown sysex event, an unknown Phase
    /// Shift event, a controller, a copyright, a lyric and free text on a part; and a note on <c>EVENTS</c>.
    /// </summary>
    public static byte[] RawEvents() => Song()
        .Track("PART GUITAR")
        .Note(0, 96).Note(480, 97).Note(960, 98)
        .Note(100, 40, 20) // An animation note.
        .Note(480, 105, 960).Note(480, 106, 960) // Face-offs.
        .Raw(600, 0xF0, 5, 1, 2, 3, 4, 0xF7)
        .PhaseShift(700, 3, 9, true).PhaseShift(800, 3, 9, false)
        .Raw(700, 0xB0, 7, 100)
        .Meta(900, 0x02, "(c) me"u8.ToArray())
        .Meta(960, 0x05, "la"u8.ToArray())
        .Text(1000, "[idle]")
        .Track("EVENTS")
        .Text(0, "[music_start]").Note(480, 24, 10).Text(1920, "[end]")
        .Build();

    /// <summary>The legacy track names: <c>T1 GEMS</c> for guitar and <c>PART REAL_DRUMS_PS</c> for drums.</summary>
    public static byte[] LegacyNames() => Song()
        .Track("T1 GEMS").Note(0, 96).Note(480, 97).Note(480, 99)
        .Track("PART REAL_DRUMS_PS").Note(0, 96).Note(0, 98).Note(480, 99).Note(480, 111, 10)
        .Build();

    /// <summary>Two <c>PART BASS</c> tracks: the reader reads the first, and the second passes through.</summary>
    public static byte[] DuplicateTrack() => Song()
        .Track("PART GUITAR").Note(0, 96)
        .Track("PART BASS").Note(0, 97).Note(480, 98)
        .Track("PART BASS").Note(0, 99)
        .Build();

    /// <summary>Tracks the model passes through: <c>PART VOCALS</c>, <c>PART KEYS</c> (which the reader reads), <c>VENUE</c>; and <c>BEAT</c>, which it makes again.</summary>
    public static byte[] Passthrough() => Song()
        .Track("PART VOCALS").Note(0, 60, 240).Meta(0, 0x05, "Hel-"u8.ToArray()).Note(240, 62, 240).Meta(240, 0x05, "lo"u8.ToArray())
        .Track("PART GUITAR").Note(0, 96).Note(480, 98)
        .Track("PART KEYS").Note(0, 97).Note(120, 98)
        .Track("BEAT").Note(0, 12).Note(480, 13)
        .Track("VENUE").Text(0, "[lighting (verse)]")
        .Build();

    /// <summary>
    /// Read with <see cref="MultiplierNote103Options"/>: star power on 103, and a phrase on 116 that the games don't read
    /// as star power.
    /// </summary>
    public static byte[] MultiplierNote103() => Song()
        .Track("PART GUITAR")
        .Note(0, 96).Note(480, 97).Note(1920, 98).Note(2400, 99)
        .Note(0, 103, 960)
        .Note(1920, 116, 960)
        .Build();

    /// <summary>
    /// Read with <see cref="SongIniSettingsOptions"/>: notes either side of a 60-tick sustain cutoff, and of the
    /// eighth-note HOPO threshold (241 ticks, or 240 for other games).
    /// </summary>
    public static byte[] SongIniSettings() => Song()
        .Track("PART GUITAR")
        .Note(0, 96, 59).Note(240, 97, 60).Note(481, 98).Note(722, 99).Note(1200, 100, 61)
        .Build();
}
