using System.Globalization;
using ChartPacker.Resources;

namespace ChartPacker.Formatting
{
    /// <summary>Formats byte counts for display, e.g. "42 KB" or "4.1 MB", using 1024-byte units.</summary>
    public static class ByteSizeFormatter
    {
        private const double Kilo = 1024;

        public static string Format(ulong bytes)
        {
            var culture = CultureInfo.CurrentCulture;
            if (bytes < Kilo)
                return string.Format(culture, Strings.SizeBytesFormat, bytes);

            double value = bytes / Kilo;
            if (value < Kilo)
                return string.Format(culture, Strings.SizeKilobytesFormat, value);

            value /= Kilo;
            if (value < Kilo)
                return string.Format(culture, Strings.SizeMegabytesFormat, value);

            return string.Format(culture, Strings.SizeGigabytesFormat, value / Kilo);
        }
    }
}
