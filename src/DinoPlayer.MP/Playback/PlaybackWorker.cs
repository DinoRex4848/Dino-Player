namespace DinoPlayer.MP.Playback;

// Owns the lifetime of a native context. Wakeups and the transition to stopping
// share a lock, so no caller can wake a context once native destruction starts.
internal sealed class PlaybackWorker : IDisposable
{
    private readonly object _sync = new();
    private readonly Queue<Operation> _operations = new();
    private readonly Action _poll;
    private readonly Action _wake;
    private readonly Action _destroy;
    private readonly Action<Exception> _error;
    private readonly Thread _thread;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _stopping;
    private Exception? _failure;

    public PlaybackWorker(Action poll, Action wake, Action destroy, Action<Exception> error)
    {
        _poll = poll; _wake = wake; _destroy = destroy; _error = error;
        _thread = new Thread(Run) { IsBackground = true, Name = "libmpv event loop" };
    }

    public bool IsStopping { get { lock (_sync) return _stopping; } }
    public bool HasPendingOperations { get { lock (_sync) return _operations.Count > 0; } }
    public Task Completion => _completion.Task;
    public void Start() => _thread.Start();

    public Task<T> InvokeAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            if (_stopping)
                completion.TrySetException(_failure ?? new ObjectDisposedException(nameof(PlaybackWorker)));
            else
            {
                _operations.Enqueue(new Operation(() => completion.TrySetResult(action()), e => completion.TrySetException(e)));
                _wake();
            }
        }
        return completion.Task;
    }

    private void Run()
    {
        try
        {
            while (true)
            {
                // Bound each batch so pointer/property events cannot be starved.
                for (var i = 0; i < 32; i++)
                {
                    Operation? operation;
                    lock (_sync)
                    {
                        if (_stopping) return;
                        if (!_operations.TryDequeue(out operation)) break;
                    }
                    try { operation.Execute(); }
                    catch (Exception exception) { operation.Fail(exception); }
                }
                lock (_sync) { if (_stopping) return; }
                _poll();
            }
        }
        catch (Exception exception)
        {
            StopAccepting(exception);
            try { _error(exception); }
            catch { /* Error reporting must never prevent native teardown. */ }
        }
        finally
        {
            StopAccepting(_failure ?? new ObjectDisposedException(nameof(PlaybackWorker)));
            try { _destroy(); }
            catch (Exception exception)
            {
                try { _error(exception); } catch { }
            }
            finally { _completion.TrySetResult(); }
        }
    }

    private void StopAccepting(Exception exception)
    {
        lock (_sync)
        {
            _stopping = true;
            _failure ??= exception;
            while (_operations.TryDequeue(out var pending)) pending.Fail(exception);
        }
    }

    public Task StopAsync()
    {
        lock (_sync)
        {
            if (!_stopping)
            {
                StopAccepting(new ObjectDisposedException(nameof(PlaybackWorker)));
                _wake();
            }
        }
        return Completion;
    }

    public void Dispose() => _ = StopAsync();
    private sealed record Operation(Action Execute, Action<Exception> Fail);
}
