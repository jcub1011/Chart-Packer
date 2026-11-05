using System.Globalization;
using ChartShared.Core.Charting;
using ChartShared.UI.Resources;

namespace ChartShared.UI.ViewModels
{
    /// <summary>How a highway's length reads beside the slider that sets it: the one place that says which of the two it shows and how.</summary>
    public static class HighwayLengthText
    {
        /// <summary>
        /// <paramref name="length"/> as the slider's readout shows it, in the mode in use: the look-ahead, e.g. "1.5 s", or the
        /// speed, e.g. "300 px/s".
        /// </summary>
        public static string Of(HighwayLength length) => length.IsPixelsPerSecond
            ? string.Format(CultureInfo.CurrentCulture, SharedStrings.PixelsPerSecondFormat, length.PixelsPerSecond)
            : string.Format(CultureInfo.CurrentCulture, SharedStrings.LookAheadFormat, length.LookAhead);
    }
}
