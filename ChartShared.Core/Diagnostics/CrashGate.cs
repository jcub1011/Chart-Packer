namespace ChartShared.Core.Diagnostics;

/// <summary>What is done with an exception nothing caught on the UI thread.</summary>
public enum CrashResponse
{
    /// <summary>The crash dialog is shown for it, and the app carries on.</summary>
    Show,

    /// <summary>The crash dialog is already open: it's counted there, and the app carries on.</summary>
    Absorb,

    /// <summary>It's left unhandled, and the app ends.</summary>
    LetCrash,
}

/// <summary>
/// Decides, for each exception nothing caught on the UI thread, whether the crash dialog is shown for it, it's counted in
/// the one open, or it ends the app. Used on the UI thread only.
/// </summary>
/// <remarks>
/// While the dialog is open, any more are counted in it. Once it has been drawn, however many come (a timer that throws
/// on every tick, say), the user can still choose. Until then, after <see cref="DrawGrace"/> one is left to end the app,
/// as an error raised on every frame (in layout, say) could keep the dialog from ever being drawn. When the user chooses
/// to quit, the app keeps working while its windows ask about unsaved changes, so an error then shows the dialog again;
/// once it's shutting down, any error ends it. A quit that fails shows the dialog again the first time, and ends the app
/// after that.
/// </remarks>
public sealed class CrashGate
{
    /// <summary>How long the dialog has to be drawn in, while more exceptions come, before one ends the app.</summary>
    public static readonly TimeSpan DrawGrace = TimeSpan.FromSeconds(5);

    /// <summary>How many of the exceptions that come after the one the open dialog shows are logged.</summary>
    public const int MaxLogged = 10;

    private enum State
    {
        Idle,
        Showing,
        Drawn,
        Quitting,
    }

    private readonly TimeProvider _clock;
    private State _state;
    private long _shownAt;
    private bool _quitFailed;

    /// <param name="clock">What times the dialog's drawing; the system's clock when null.</param>
    public CrashGate(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>The exceptions that came after the one the open dialog shows.</summary>
    public int Extra { get; private set; }

    /// <summary>What to do with an exception that has just reached the dispatcher.</summary>
    public CrashResponse Next()
    {
        switch (_state)
        {
            case State.Idle:
                _state = State.Showing;
                _shownAt = _clock.GetTimestamp();
                Extra = 0;
                return CrashResponse.Show;
            case State.Drawn:
            case State.Showing when _clock.GetElapsedTime(_shownAt) < DrawGrace:
                Extra++;
                return CrashResponse.Absorb;
            default:
                return CrashResponse.LetCrash;
        }
    }

    /// <summary>The open dialog has been drawn, so the user can choose however many more exceptions come.</summary>
    public void Drawn()
    {
        if (_state == State.Showing)
            _state = State.Drawn;
    }

    /// <summary>
    /// The dialog has closed: the next exception shows it again. Whether the user chose to quit doesn't matter yet, as the
    /// windows may still ask about unsaved changes, or be kept open.
    /// </summary>
    public void Closed() => _state = State.Idle;

    /// <summary>The app is shutting down, so any exception is left to end it.</summary>
    public void ShuttingDown() => _state = State.Quitting;

    /// <summary>Quitting threw. Whether the dialog is shown for it: the first time; after that it ends the app.</summary>
    public bool QuitFailed()
    {
        if (!_quitFailed)
        {
            _quitFailed = true;
            _state = State.Idle;
            return true;
        }

        _state = State.Quitting;
        return false;
    }

    /// <summary>The dialog couldn't be shown, so the exception is left to end the app.</summary>
    public void Failed() => _state = State.Quitting;
}
