namespace QuickTranslate.Infrastructure.Ocr;

/// <summary>Serializes session users and defers final cleanup until their lease ends.</summary>
internal sealed class OcrOperationGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly object _stopLock = new();
    private int _stopping;
    private Task? _stopTask;

    internal bool IsStopping => Volatile.Read(ref _stopping) != 0;

    internal async ValueTask<IDisposable> EnterAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(IsStopping, this);
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        if (IsStopping)
        {
            _semaphore.Release();
            throw new ObjectDisposedException(nameof(OcrOperationGate));
        }
        return new Lease(_semaphore);
    }

    internal Task StopAsync(Action cleanup)
    {
        TaskCompletionSource completion;
        lock (_stopLock)
        {
            if (_stopTask != null) return _stopTask;
            Volatile.Write(ref _stopping, 1);
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopTask = completion.Task;
        }
        _ = StopCoreAsync(cleanup, completion);
        return completion.Task;
    }

    private async Task StopCoreAsync(Action cleanup, TaskCompletionSource completion)
    {
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            cleanup();
            completion.SetResult();
        }
        catch (Exception ex)
        {
            completion.SetException(ex);
        }
        finally
        {
            _semaphore.Release();
        }
        // Do not dispose the semaphore: waiters already queued before Stop must
        // acquire, observe stopping, and release it without a disposal race.
    }

    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;
        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
