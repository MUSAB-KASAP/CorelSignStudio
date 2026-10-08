using System.Collections.Concurrent;

namespace CorelSignStudio.Corel;

internal sealed class StaThreadDispatcher : IAsyncDisposable
{
    private readonly BlockingCollection<IWorkItem> _queue = new();
    private readonly Thread _thread;
    private int _disposed;

    public StaThreadDispatcher(string name)
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = name,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new WorkItem<T>(action, completion, cancellationToken);

        try
        {
            _queue.Add(item, cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            throw new ObjectDisposedException(nameof(StaThreadDispatcher), exception);
        }

        return completion.Task;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.CompleteAdding();
        await Task.Run(_thread.Join).ConfigureAwait(false);
        _queue.Dispose();
    }

    private void Run()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            item.Execute();
        }
    }

    private interface IWorkItem
    {
        void Execute();
    }

    private sealed class WorkItem<T>(
        Func<T> action,
        TaskCompletionSource<T> completion,
        CancellationToken cancellationToken) : IWorkItem
    {
        public void Execute()
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                completion.TrySetResult(action());
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }
    }
}

