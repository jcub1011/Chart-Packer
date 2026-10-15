using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ChartPacker.Resources;
using ChartShared.Audio.Waveforms;
using ChartShared.Core.Scanning;

namespace ChartPacker.ViewModels
{
    /// <summary>A track a lane's waveform can be drawn from; <see cref="Stem"/> is <see langword="null"/> for the lane's own.</summary>
    public sealed record WaveformOption(string? Stem, string Label);

    /// <summary>
    /// Which of a song's tracks a highway or the lyrics lane draws the waveform of: those its instrument is
    /// played against (see <see cref="LaneStems"/>), or another picked in their place.
    /// </summary>
    internal static class LaneWaveformChoice
    {
        /// <summary>
        /// Whether <paramref name="player"/> has <paramref name="entry"/>'s tracks: it's given them after the lanes are
        /// given the song, and has another song's until then.
        /// </summary>
        public static bool HasTracksOf(PlayerViewModel player, SongEntry? entry) => entry is not null && entry == player.LoadedEntry;

        /// <summary>The tracks <paramref name="player"/> has of <paramref name="entry"/>; none while it has another song's, see <see cref="HasTracksOf"/>.</summary>
        public static IReadOnlyList<SongTrack> TracksOf(PlayerViewModel player, SongEntry? entry) =>
            HasTracksOf(player, entry) ? [.. player.Tracks.Select(t => t.Track)] : [];

        /// <summary>The tracks a lane can be drawn over: one of each of the song's stems but the preview, the first of each.</summary>
        public static StemChoices StemsOf(IReadOnlyList<SongTrack> tracks)
        {
            var stems = tracks
                .Where(t => !t.Stem.Equals(AudioStems.Preview, StringComparison.OrdinalIgnoreCase))
                .GroupBy(t => t.Stem, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            return new StemChoices(stems, [.. stems.Select(t => new WaveformOption(t.Stem, t.StoredName))]);
        }

        /// <summary>
        /// The picker's options for a lane preferring <paramref name="preferred"/>: its own first, labelled with
        /// the tracks that is, then each of <paramref name="stems"/>.
        /// </summary>
        public static IReadOnlyList<WaveformOption> OptionsFor(StemChoices stems, IReadOnlyList<IReadOnlyList<string>> preferred)
        {
            var own = LaneStems.Pick(preferred, stems.Tracks.Select(t => t.Stem), null);
            string auto = own.Count == 0
                ? Strings.LaneWaveformAutoNone
                : string.Format(CultureInfo.CurrentCulture, Strings.LaneWaveformAutoFormat, string.Join(
                    Strings.LaneWaveformTrackSeparator,
                    own.Select(stem => stems.Tracks.First(t => t.Stem.Equals(stem, StringComparison.OrdinalIgnoreCase)).StoredName)));

            return [new WaveformOption(null, auto), .. stems.Options];
        }

        /// <summary>The stems drawn for a lane preferring <paramref name="preferred"/>, with <paramref name="chosen"/> picked in place of its own.</summary>
        public static IReadOnlyList<string> StemsFor(PlayerViewModel player, IReadOnlyList<IReadOnlyList<string>> preferred, string? chosen) =>
            LaneStems.Pick(preferred, player.Tracks.Select(t => t.Track.Stem), chosen);

        /// <summary>The waveform <paramref name="player"/> has read of <paramref name="stems"/>, together; <see langword="null"/> when there's none to draw.</summary>
        public static Waveform? Resolve(PlayerViewModel player, IReadOnlyList<string> stems) =>
            player.LaneWaveformOf(stems) is { Count: > 0 } waveform ? waveform : null;

        /// <summary>Where <paramref name="chosen"/> is among <paramref name="options"/>: its own, the first, when it isn't one of them.</summary>
        public static int IndexOf(IReadOnlyList<WaveformOption> options, string? chosen)
        {
            for (int i = 0; i < options.Count; i++)
            {
                if (string.Equals(options[i].Stem, chosen, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return 0;
        }
    }

    /// <summary>A song's tracks a lane can be drawn over, with an option for each, shared by every lane's picker.</summary>
    internal sealed record StemChoices(IReadOnlyList<SongTrack> Tracks, IReadOnlyList<WaveformOption> Options);
}
