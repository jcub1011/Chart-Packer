using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ChartPacker.Audio.Waveforms;
using ChartPacker.Core.Scanning;
using ChartPacker.Resources;

namespace ChartPacker.ViewModels
{
    /// <summary>A track a lane's waveform can be drawn from; <see cref="Stem"/> is <see langword="null"/> for the lane's own.</summary>
    public sealed record WaveformOption(string? Stem, string Label);

    /// <summary>
    /// Which of a song's tracks a highway or the lyrics lane draws the waveform of: the one its instrument is
    /// played against (see <see cref="LaneStems"/>), or another picked in its place.
    /// </summary>
    internal static class LaneWaveformChoice
    {
        /// <summary>
        /// The picker's options for a lane preferring <paramref name="preferred"/>: its own first, labelled with
        /// the track that is, then each of the song's stems but the preview, by the name of its first track.
        /// </summary>
        public static IReadOnlyList<WaveformOption> OptionsFor(IReadOnlyList<SongTrack> tracks, IReadOnlyList<string> preferred)
        {
            var stems = tracks
                .Where(t => !t.Stem.Equals(AudioStems.Preview, StringComparison.OrdinalIgnoreCase))
                .GroupBy(t => t.Stem, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            string? own = LaneStems.Pick(preferred, stems.Select(t => t.Stem), null);
            string auto = own is null
                ? Strings.LaneWaveformAutoNone
                : string.Format(CultureInfo.CurrentCulture, Strings.LaneWaveformAutoFormat, stems.First(t => t.Stem.Equals(own, StringComparison.OrdinalIgnoreCase)).StoredName);

            return [new WaveformOption(null, auto), .. stems.Select(t => new WaveformOption(t.Stem, t.StoredName))];
        }

        /// <summary>
        /// The waveform <paramref name="player"/> has read for a lane preferring <paramref name="preferred"/>, with
        /// <paramref name="chosen"/> picked in place of its own; <see langword="null"/> when there's none to draw.
        /// </summary>
        public static Waveform? Resolve(PlayerViewModel player, IReadOnlyList<string> preferred, string? chosen)
        {
            string? stem = LaneStems.Pick(preferred, player.Tracks.Select(t => t.Track.Stem), chosen);
            return stem is not null && player.WaveformOfStem(stem) is { Count: > 0 } waveform ? waveform : null;
        }

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
}
