using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ChartBuilder.Resources;
using ChartPacker.Core.Charting;
using ChartPacker.Core.Charting.Commands;
using ChartPacker.Core.Charts;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// The report of a drum conversion (charter-spec C6 item 8), for the Messages dialog: the notes it left out or made the
    /// other of cymbal and tom, with their times. Lint can't see notes no longer in the chart, so this is the only place
    /// they're listed.
    /// </summary>
    internal static class DrumConversionText
    {
        /// <summary>The conversion that <paramref name="command"/> ran, in it or one of its steps; null when it ran none.</summary>
        public static ConvertDrums? ConversionIn(IChartCommand command) => command switch
        {
            ConvertDrums convert => convert,
            CompositeCommand composite => composite.Commands.Select(ConversionIn).LastOrDefault(static c => c is not null),
            _ => null,
        };

        /// <summary>Whether <paramref name="report"/> has anything the author should read: notes, or drums that were 4-lane.</summary>
        public static bool NeedsReading(DrumConversionReport report) => report.Notes.Count > 0 || report.FromPlainFourLane;

        /// <summary>
        /// The report: what was done, then, when the drums were 4-lane, where their cymbals and toms came from, then a line
        /// for each note, hardest difficulty first and then by time.
        /// </summary>
        /// <param name="audioAt">The time in the audio of a tick, in seconds.</param>
        public static string Format(ConvertDrums conversion, DrumConversionReport report, Func<long, double> audioAt)
        {
            var lines = new List<string>
            {
                report.Notes.Count > 0
                    ? string.Format(CultureInfo.CurrentCulture, Strings.DrumConversionReportFormat, VariantName(conversion.To), report.Notes.Count)
                    : string.Format(CultureInfo.CurrentCulture, Strings.DrumConversionDoneFormat, VariantName(conversion.To)),
            };
            if (report.FromPlainFourLane)
                lines.Add(Strings.DrumConversionFromFourLane);

            // The pads are named as they were before the conversion.
            bool fromFiveLane = conversion.To != DrumsVariant.FiveLane;
            foreach (var note in report.Notes.OrderByDescending(static n => n.Difficulty).ThenBy(static n => n.Tick).ThenBy(static n => n.Pad))
            {
                string pad = PropertiesViewModel.LaneName(ChartInstrumentKind.Drums, new EditableNote(note.Tick, EditableNote.PlainLength, note.Pad), fromFiveLane);
                if (!fromFiveLane && note.Pad is >= TomMarker.FirstPad and <= TomMarker.LastPad)
                    pad = string.Format(CultureInfo.CurrentCulture, note.Tom ? Strings.DrumConversionTomFormat : Strings.DrumConversionCymbalFormat, pad);
                string outcome = note.Outcome switch
                {
                    DrumConversionOutcome.LeftOut => Strings.DrumConversionLeftOut,
                    _ => note.Tom ? Strings.DrumConversionMadeCymbal : Strings.DrumConversionMadeTom,
                };
                string time = SongTime.Format(TimeSpan.FromSeconds(Math.Max(0, audioAt(note.Tick))), decimals: 3);
                lines.Add(string.Format(CultureInfo.CurrentCulture, Strings.DrumConversionLineFormat, note.Difficulty, time, pad, outcome));
            }

            return string.Join(Environment.NewLine, lines);
        }

        private static string VariantName(DrumsVariant variant) => variant switch
        {
            DrumsVariant.FourLane => Strings.DrumsFourLane,
            DrumsVariant.Pro => Strings.DrumsPro,
            _ => Strings.DrumsFiveLane,
        };
    }
}
