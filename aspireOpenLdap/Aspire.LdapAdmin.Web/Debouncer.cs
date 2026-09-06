namespace Aspire.LdapAdmin.Web;

/// <summary>Runs only the latest action after its delay.</summary>
internal sealed class Debouncer : IDisposable
{
    private readonly Lock _gate = new();
    private CancellationTokenSource? _pending;
    private bool _disposed;

    public async Task DebounceAsync(TimeSpan delay, Func<Task> action)
    {
        CancellationTokenSource current;
        CancellationTokenSource? previous;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _pending;
            current = new CancellationTokenSource();
            _pending = current;
        }

        previous?.Cancel();
        previous?.Dispose();

        try
        {
            // The callback and ownership check must retain the caller's serialized context.
            await Task.Delay(delay, current.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested)
        {
            return;
        }

        Task actionTask;
        try
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_pending, current) || _disposed)
                {
                    return;
                }

                // Invoke while ownership is held so Dispose either cancels first or observes
                // that the callback has begun; there is no gap between those states.
                _pending = null;
                actionTask = action();
            }
            await actionTask.ConfigureAwait(true);
        }
        finally
        {
            current.Dispose();
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? pending;
        lock (_gate)
        {
            _disposed = true;
            pending = _pending;
            _pending = null;
        }
        pending?.Cancel();
        pending?.Dispose();
    }
}
