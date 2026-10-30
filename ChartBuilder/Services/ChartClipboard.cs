using System;
using System.Collections.Generic;
using ChartShared.Core.Charting.Editing;
using ChartShared.Core.Charts;

namespace ChartBuilder.Services
{
    /// <summary>
    /// The editor's clipboard (charter-spec C15 item 3), one for the app, so a passage copied in one chart pastes in the
    /// next. It holds notes as the editor has them, not as the OS clipboard's text, and remembers the lane mapping last
    /// confirmed each way between 5-fret and GHL.
    /// </summary>
    public sealed class ChartClipboard
    {
        private readonly Dictionary<(ChartInstrumentKind, ChartInstrumentKind), LaneMapping> _mappings = [];

        /// <summary>The app's clipboard.</summary>
        public static ChartClipboard Shared { get; } = new();

        /// <summary>Raised when what the clipboard holds changes, which the Edit menu's Paste follows.</summary>
        public event EventHandler? Changed;

        /// <summary>What was last cut or copied; null for nothing.</summary>
        public ChartClip? Current
        {
            get;
            set
            {
                field = value;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>The mapping last confirmed from <paramref name="from"/> to <paramref name="to"/>, or the default one.</summary>
        public LaneMapping MappingFor(ChartInstrumentKind from, ChartInstrumentKind to) =>
            _mappings.TryGetValue((from, to), out var mapping) ? mapping : LaneMapping.Default(from, to);

        /// <summary>Remembers <paramref name="mapping"/> for the next paste the same way.</summary>
        public void Remember(LaneMapping mapping) => _mappings[(mapping.From, mapping.To)] = mapping;
    }
}
