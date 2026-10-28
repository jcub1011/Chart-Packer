using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ChartBuilder.Resources;
using ChartShared.Core.Charting;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;
using ChartShared.UI.Controls.Highway;
using ChartShared.UI.ViewModels;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// The highway preview of a generator's proposals: the chart's notes, tinted where kept star power covers them, and the
    /// phrases drawn behind them as bands, without changing the chart.
    /// </summary>
    internal static class ProposalPreviewScene
    {
        /// <summary>
        /// The preview of <paramref name="key"/>'s highway with <paramref name="playing"/> over it: the phrases the result
        /// would have, in ticks, earliest first. Star power tints the notes it covers; fills are only bands.
        /// </summary>
        public static HighwayScene Of(EditorSession session, ChartTrackKey key, IReadOnlyList<Phrase> playing, bool starPower)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(playing);

            var game = session.GameTrack(key);
            var notes = new ChartNote[game.Notes.Count];
            for (int i = 0; i < notes.Length; i++)
            {
                var note = game.Notes[i];
                notes[i] = starPower
                    ? note with
                    {
                        Flags = (note.Flags & ~NoteFlags.StarPower)
                            | (playing.Any(p => p.Covers(note.Tick)) ? NoteFlags.StarPower : NoteFlags.None),
                    }
                    : note;
            }

            var kind = starPower ? HighwayBandKind.StarPower : HighwayBandKind.Proposal;
            var bands = playing
                .Select(p => new HighwayBand(session.AudioAt(p.Tick), session.AudioAt(p.CoverEnd), kind))
                .ToList();
            var instrument = ChartInstruments.KindOf(key.Instrument);
            var label = string.Format(
                CultureInfo.CurrentCulture,
                Strings.LaneTitleFormat,
                ChartLabels.InstrumentName(key.Instrument),
                ChartLabels.DifficultyName(key.Difficulty));
            var lane = new HighwayLane(
                instrument,
                HighwayLane.LaneCountOf(instrument, session.IsFiveLaneDrums),
                label,
                notes,
                session.TempoMap,
                session.Delay,
                bands: bands);
            return new HighwayScene([lane], session.TempoMap, session.Delay);
        }
    }
}
