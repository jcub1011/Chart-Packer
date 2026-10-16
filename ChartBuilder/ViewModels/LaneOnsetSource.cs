using System.Collections.Generic;
using System.Linq;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charting.Layout;
using ChartShared.Core.Charts;

namespace ChartBuilder.ViewModels
{
    /// <summary>
    /// The onsets snap and nudge to (charter-spec C14 items 2 and 3): those of the audio the lane showing the track is drawn
    /// over, at that lane's sensitivity, every drum band's together. A track no lane shows takes its part's own stems at the
    /// default sensitivity. Until they're found there are none, and asking starts finding them.
    /// </summary>
    internal sealed class LaneOnsetSource(ChartEditorViewModel editor) : IOnsetSource
    {
        public IReadOnlyList<double> OnsetsFor(ChartTrackKey key)
        {
            var lanes = editor.Lanes.All;
            var lane = lanes.FirstOrDefault(l => l.Track == key) ?? lanes.FirstOrDefault(l => l.Track?.Instrument == key.Instrument);
            var options = lane?.Audio.Options ?? LaneAudioOptions.Default;
            if (editor.Audio.SourceFor(options, key.Instrument) is not { } source)
                return [];
            bool drums = ChartInstruments.KindOf(key.Instrument) == ChartInstrumentKind.Drums;
            return editor.Audio.Analysis.OnsetTimesAt(source, drums, options.OnsetSensitivity);
        }
    }
}
