namespace ChartPacker.Services
{
    /// <summary>User settings persisted to <c>settings.json</c>. See spec §8.</summary>
    public sealed class AppSettings
    {
        public const double DefaultLibraryPaneWidth = 320;
        public const double MinLibraryPaneWidth = 200;
        public const int DefaultScanMaxDepth = 2;
        public const int MaxScanMaxDepth = 10;

        /// <summary>Width of the library (left) panel, in device-independent pixels.</summary>
        public double LibraryPaneWidth { get; set; } = DefaultLibraryPaneWidth;

        /// <summary>How many folder levels below the picked folder the library scan searches.</summary>
        public int ScanMaxDepth { get; set; } = DefaultScanMaxDepth;
    }
}
