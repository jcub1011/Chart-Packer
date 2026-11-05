using System;
using Avalonia.Media;

namespace ChartShared.UI.Controls.Highway
{
    /// <summary>
    /// A highway a <see cref="PerspectiveHost"/> can tilt back: it is the plane, drawing itself flat as it always has, and it draws
    /// the notes standing up on it in the layers the host puts above it, through the <see cref="Camera"/> that places them.
    /// </summary>
    /// <remarks>
    /// Each layer has its own pass so that what changes seldom is not drawn again with what changes often: the notes are drawn
    /// again as they change or the song moves, what follows the pointer as the pointer does. A highway with nothing for a layer
    /// draws nothing in it and never raises its event.
    /// </remarks>
    public interface IPerspectiveContent
    {
        /// <summary>
        /// The camera that tilts the highway back, while it is in perspective; null while it is flat. Set by the host, which also
        /// draws the notes the highway stands up.
        /// </summary>
        PerspectiveCamera? Camera { get; set; }

        /// <summary>Raised when the notes standing up are to be drawn again.</summary>
        event Action? GemsInvalidated;

        /// <summary>Raised when what follows the pointer, standing up, is to be drawn again.</summary>
        event Action? GemMarksInvalidated;

        /// <summary>Draws the notes standing up on the plane, and the text beside it.</summary>
        void PaintGems(DrawingContext context);

        /// <summary>Draws what follows the pointer, as much of it as stands up.</summary>
        void PaintGemMarks(DrawingContext context);
    }
}
