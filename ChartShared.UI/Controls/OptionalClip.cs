using System;
using Avalonia;
using Avalonia.Media;

namespace ChartShared.UI.Controls
{
    /// <summary>
    /// A clip pushed on a drawing context, or not, disposed in a <c>using</c> either way and without the boxing a <c>using</c> over an
    /// interface would do: for drawing that can be asked to leave its clip out.
    /// </summary>
    public readonly struct OptionalClip : IDisposable
    {
        private readonly DrawingContext.PushedState _state;
        private readonly bool _pushed;

        /// <param name="push">Whether to clip to <paramref name="rect"/>.</param>
        public OptionalClip(DrawingContext context, Rect rect, bool push)
        {
            _pushed = push;
            _state = push ? context.PushClip(rect) : default;
        }

        public void Dispose()
        {
            if (_pushed)
                _state.Dispose();
        }
    }
}
