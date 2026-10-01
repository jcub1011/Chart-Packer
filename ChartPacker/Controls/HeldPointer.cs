using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Input;

namespace ChartPacker.Controls
{
    /// <summary>
    /// Holds the pointer still while something is dragged, so a drag can go on as far as it needs without the pointer reaching
    /// the edge of the screen. Only on Windows, where the pointer can be put back where it was; elsewhere it moves as usual.
    /// </summary>
    internal static class HeldPointer
    {
        /// <summary>The pointer while it's held, so it isn't seen jumping back.</summary>
        public static readonly Cursor Hidden = new(StandardCursorType.None);

        /// <summary>Whether the pointer can be held here.</summary>
        public static bool CanHold => OperatingSystem.IsWindows();

        /// <summary>Puts the pointer back at <paramref name="screen"/>, in screen pixels; the move that makes comes back with no distance.</summary>
        public static void Return(PixelPoint screen) => _ = SetCursorPos(screen.X, screen.Y);

        [DllImport("user32.dll")]
        private static extern int SetCursorPos(int x, int y);
    }

    /// <summary>
    /// A fine drag of a scrubbed lane or the seek bar: how far the pointer moves each time, with it held where the fine drag
    /// began, hidden, so it doesn't run off as the drag goes on.
    /// </summary>
    internal sealed class FineDrag(InputElement owner)
    {
        // Where the pointer was last, in the owner; while it's held, where it's held.
        private Point _last;
        private PixelPoint _screen;
        private Cursor? _cursor;

        public bool IsActive { get; private set; }

        /// <summary>Starts a fine drag with the pointer at <paramref name="at"/>, in the owner; already started, it goes on as it is.</summary>
        public void Start(Point at)
        {
            if (IsActive)
                return;

            IsActive = true;
            _last = at;
            if (HeldPointer.CanHold)
            {
                _screen = owner.PointToScreen(at);
                _cursor = owner.Cursor;
                owner.Cursor = HeldPointer.Hidden;
            }
        }

        /// <summary>How far the pointer has moved to <paramref name="at"/> since it was last seen, putting it back while it's held.</summary>
        public Vector Move(Point at)
        {
            var moved = new Vector(at.X - _last.X, at.Y - _last.Y);
            if (HeldPointer.CanHold)
                HeldPointer.Return(_screen);
            else
                _last = at;
            return moved;
        }

        /// <summary>Ends the fine drag, showing the pointer again where it was held.</summary>
        public void Stop()
        {
            if (!IsActive)
                return;

            IsActive = false;
            if (HeldPointer.CanHold)
                owner.Cursor = _cursor;
        }
    }
}
