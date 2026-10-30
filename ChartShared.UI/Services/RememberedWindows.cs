using System.Collections.Generic;

namespace ChartShared.UI.Services
{
    /// <summary>How a window was shown when it last closed, as <see cref="Views.WindowMemory"/> keeps it in a program's settings.</summary>
    public sealed record SavedWindow
    {
        /// <summary>Its normal size, in device-independent pixels. Null for none yet.</summary>
        public double? Width { get; set; }

        public double? Height { get; set; }

        /// <summary>Its normal top left, in screen pixels: kept only for a window that opens where it was. Null for none.</summary>
        public int? X { get; set; }

        public int? Y { get; set; }

        /// <summary>Whether it closed maximized, so it opens that way.</summary>
        public bool Maximized { get; set; }

        /// <summary>Whether it closed full screen, so it opens that way.</summary>
        public bool FullScreen { get; set; }
    }

    /// <summary>The windows <see cref="Views.WindowMemory"/> remembers, kept in a program's settings.</summary>
    public interface IRememberedWindows
    {
        /// <summary>How each window was shown when it last closed, by its type's full name. Null for none yet.</summary>
        Dictionary<string, SavedWindow>? Windows { get; set; }
    }
}
