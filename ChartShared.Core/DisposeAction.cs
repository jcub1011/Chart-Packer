namespace ChartShared.Core;

/// <summary>Runs an action when it's disposed, once however often it is: to stop listening, say.</summary>
/// <param name="dispose">What disposing it does.</param>
public sealed class DisposeAction(Action dispose) : IDisposable
{
    private Action? _dispose = dispose;

    public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
}
