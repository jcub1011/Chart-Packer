using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace ChartShared.UI.Controls
{
    /// <summary>
    /// Holds the pointer still while something is dragged, so a drag can go on as far as it needs without the pointer reaching
    /// the edge of the screen. Only on Windows, where the pointer can be put back where it was; elsewhere it moves as usual.
    /// </summary>
    public static class HeldPointer
    {
        /// <summary>The pointer while it's held, so it isn't seen jumping back.</summary>
        public static readonly Cursor Hidden = new(StandardCursorType.None);

        /// <summary>Whether the pointer can be held here.</summary>
        public static bool CanHold => OperatingSystem.IsWindows();

        /// <summary>Puts the pointer back at <paramref name="screen"/>, in screen pixels; the move that makes comes back with no distance.</summary>
        public static void Return(PixelPoint screen) => _ = SetCursorPos(screen.X, screen.Y);

        /// <summary>
        /// How far the pointer is from <paramref name="screen"/>, where it's held, in <paramref name="owner"/>'s units. It's read from the
        /// pointer itself, in whole screen pixels, rather than turned back from where an event says it is in the owner, which can come out
        /// a pixel short; so the move that putting the pointer back makes is exactly none, and one that arrives late after it reads none too.
        /// </summary>
        public static Vector From(Visual owner, PixelPoint screen)
        {
            if (!GetCursorPos(out var pixel))
                return default;

            double scale = TopLevel.GetTopLevel(owner)?.RenderScaling ?? 1;
            return new Vector((pixel.X - screen.X) / scale, (pixel.Y - screen.Y) / scale);
        }

        [DllImport("user32.dll")]
        private static extern int SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out Win32Point point);

        [StructLayout(LayoutKind.Sequential)]
        private struct Win32Point
        {
            public int X;
            public int Y;
        }
    }

    /// <summary>
    /// A drag of a scrubbed lane, or a fine one of the seek bar: how far the pointer moves each time, with it held where the
    /// drag began, hidden, so it doesn't run off as the drag goes on.
    /// </summary>
    public sealed class FineDrag(InputElement owner)
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
            if (HeldPointer.CanHold)
            {
                var held = HeldPointer.From(owner, _screen);
                HeldPointer.Return(_screen);
                return held;
            }

            var moved = new Vector(at.X - _last.X, at.Y - _last.Y);
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
