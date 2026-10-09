using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ChartBuilder.Resources;
using ChartPacker.Core.Charting.Layout;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Scanning;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// The audio a lane is drawn over, or analysed for its onsets: one stem, several drawn together (split drums), or the
    /// whole mix (charter-spec C12 item 1).
    /// </summary>
    /// <param name="Key">Tells sources apart: the same tracks give the same key.</param>
    /// <param name="Tracks">The song's tracks it's made of.</param>
    /// <param name="IsMix">It's the whole mix, rather than the part's own stems.</param>
    public sealed record AudioSource(string Key, IReadOnlyList<SongTrack> Tracks, bool IsMix)
    {
        /// <summary>The source's name, as the lane's audio picker shows it: the stems', or "Mix".</summary>
        public string Label => IsMix ? Strings.LaneAudioMix : string.Join(" + ", Tracks.Select(t => t.Stem));

        public bool Equals(AudioSource? other) => other is not null && Key == other.Key;

        public override int GetHashCode() => Key.GetHashCode(StringComparison.Ordinal);

        /// <summary>The whole song as it plays: every track but the preview, or null when there are none.</summary>
        public static AudioSource? MixOf(IReadOnlyList<SongTrack> tracks)
        {
            var heard = tracks.Where(t => !t.Stem.Equals(AudioStems.Preview, StringComparison.OrdinalIgnoreCase)).ToList();
            return heard.Count == 0 ? null : Of(heard, isMix: true);
        }

        /// <summary>
        /// The stems <paramref name="instrument"/>'s lane is drawn over by default: its own (<see cref="LaneStems.For"/>),
        /// the first group the song has any of, else the mix.
        /// </summary>
        public static AudioSource? AutoOf(ChartInstrument instrument, IReadOnlyList<SongTrack> tracks)
        {
            foreach (var group in LaneStems.For(instrument))
            {
                var present = tracks.Where(t => group.Contains(t.Stem, StringComparer.OrdinalIgnoreCase)).ToList();
                if (present.Count > 0)
                    return Of(present, isMix: false);
            }

            return MixOf(tracks);
        }

        /// <summary>
        /// The audio <paramref name="options"/> picks for <paramref name="instrument"/>'s lane: the stem chosen, the mix, or
        /// the part's own (<see cref="AutoOf"/>), which a stem the song no longer has falls back to.
        /// </summary>
        public static AudioSource? Resolve(LaneAudioOptions options, ChartInstrument instrument, IReadOnlyList<SongTrack> tracks)
        {
            ArgumentNullException.ThrowIfNull(options);
            if (options.Source == LaneAudioOptions.MixSource)
                return MixOf(tracks);
            if (options.Source is { } stem && tracks.Where(t => t.Stem.Equals(stem, StringComparison.OrdinalIgnoreCase)).ToList() is { Count: > 0 } chosen)
                return Of(chosen, isMix: false);
            return AutoOf(instrument, tracks);
        }

        /// <summary>The choices a lane's audio picker offers: the part's own, the mix, then each stem.</summary>
        public static IReadOnlyList<AudioSourceChoice> ChoicesFor(ChartInstrument instrument, IReadOnlyList<SongTrack> tracks)
        {
            var choices = new List<AudioSourceChoice>();
            if (AutoOf(instrument, tracks) is { } auto)
                choices.Add(new AudioSourceChoice(null, string.Format(CultureInfo.CurrentCulture, Strings.LaneAudioAutoFormat, auto.Label)));
            if (MixOf(tracks) is not null)
                choices.Add(new AudioSourceChoice(LaneAudioOptions.MixSource, Strings.LaneAudioMix));
            foreach (string stem in tracks.Select(t => t.Stem).Where(s => !s.Equals(AudioStems.Preview, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase))
                choices.Add(new AudioSourceChoice(stem, stem));
            return choices;
        }

        /// <summary>One track on its own, as its row in Chart settings' Tracks draws it.</summary>
        public static AudioSource Single(SongTrack track) => Of([track], isMix: false);

        // A staged track is keyed by the file it plays too, so one replacing a stored track of the same name isn't taken for it.
        private static AudioSource Of(IReadOnlyList<SongTrack> tracks, bool isMix) =>
            new(string.Join("|", tracks.Select(t => t.StagedSourcePath is { } staged ? t.StoredName + ">" + staged : t.StoredName).Order(StringComparer.OrdinalIgnoreCase)), tracks, isMix);
    }

    /// <summary>One of a lane's audio picker's choices: <see cref="LaneAudioOptions.Source"/>'s value, and its name.</summary>
    public sealed record AudioSourceChoice(string? Source, string Label);
}
