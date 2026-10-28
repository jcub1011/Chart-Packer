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
    /// The highway preview of a star power layout: the chart's notes, tinted where the kept star power covers them, and the
    /// phrases drawn behind them, without changing the chart.
    /// </summary>
    internal static class StarPowerPreviewScene
    {
        /// <summary>
        /// The preview of <paramref name="key"/>'s highway with <paramref name="playing"/> over it: the star power phrases
        /// the result would have, in ticks, earliest first.
        /// </summary>
        public static HighwayScene Of(EditorSession session, ChartTrackKey key, IReadOnlyList<Phrase> playing)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(playing);

            var game = session.GameTrack(key);
            var notes = new ChartNote[game.Notes.Count];
            for (int i = 0; i < notes.Length; i++)
            {
                var note = game.Notes[i];
                bool inside = playing.Any(p => p.Covers(note.Tick));
                notes[i] = note with { Flags = (note.Flags & ~NoteFlags.StarPower) | (inside ? NoteFlags.StarPower : NoteFlags.None) };
            }

            var bands = playing
                .Select(p => new StarPowerBand(session.AudioAt(p.Tick), session.AudioAt(p.CoverEnd)))
                .ToList();
            var kind = ChartInstruments.KindOf(key.Instrument);
            var label = string.Format(
                CultureInfo.CurrentCulture,
                Strings.LaneTitleFormat,
                ChartLabels.InstrumentName(key.Instrument),
                ChartLabels.DifficultyName(key.Difficulty));
            var lane = new HighwayLane(
                kind,
                HighwayLane.LaneCountOf(kind, session.IsFiveLaneDrums),
                label,
                notes,
                session.TempoMap,
                session.Delay,
                starPowerBands: bands);
            return new HighwayScene([lane], session.TempoMap, session.Delay);
        }
    }
}
