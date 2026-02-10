#if VITALROUTER_UNITASK_INTEGRATION
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;

namespace VitalRouter
{
public class UniTaskAsyncLock : IAsyncLock
{
    readonly Queue<AutoResetUniTaskCompletionSource> asyncWaitingQueue = new();
    readonly object syncRoot = new();
    const int maxResourceCount = 1;

    int currentResourceCount = 1;
    int waitCount;
    int countOfWaitersPulsedToWake;
    bool disposed;

    public ValueTask WaitAsync(CancellationToken cancellationToken = default)
    {
        CheckDispose();
        lock (syncRoot)
        {
            if (currentResourceCount > 0)
            {
                currentResourceCount--;
                return UniTask.CompletedTask;
            }

            var source = AutoResetUniTaskCompletionSource.Create();
            asyncWaitingQueue.Enqueue(source);
            return source.Task;
        }
    }

    public void Wait()
    {
        CheckDispose();
        var lockTaken = false;

        try
        {
            // Perf: first spin wait for the count to be positive.
            if (Volatile.Read(ref currentResourceCount) == 0)
            {
                // Monitor.Enter followed by Monitor.Wait is much more expensive than waiting on an event as it involves another
                // spin, contention, etc. The usual number of spin iterations that would otherwise be used here is increased to
                // lessen that extra expense of doing a proper wait.
                // var spinCount = SpinWait.SpinCountforSpinBeforeWait * 4;
                var spinCount = 35 * 4;
                SpinWait spinner = default;
                while (spinner.Count < spinCount)
                {
                    spinner.SpinOnce();
                    if (Volatile.Read(ref currentResourceCount) != 0)
                    {
                        break;
                    }
                }
            }

            // Fallback to monitor
            Monitor.Enter(syncRoot, ref lockTaken);
            waitCount++;

            // Wait until the resource becomes available
            while (currentResourceCount == 0)
            {
                Monitor.Wait(syncRoot, Timeout.Infinite);
                if (countOfWaitersPulsedToWake != 0)
                    countOfWaitersPulsedToWake--;
            }
            currentResourceCount--;
        }
        finally
        {
            // Release the lock
            if (lockTaken)
            {
                waitCount--;
                Monitor.Exit(syncRoot);
            }
        }
    }

    public void Release()
    {
        CheckDispose();

        AutoResetUniTaskCompletionSource? asyncWaiterToRelease = null;
        lock (syncRoot)
        {
            if (currentResourceCount >= maxResourceCount)
            {
                throw new InvalidOperationException();
            }

            // Try to hand off directly to an async waiter first
            if (asyncWaitingQueue.TryDequeue(out var waitingTask))
            {
                asyncWaiterToRelease = waitingTask;
            }
            // Or wake a synchronous waiter
            else if (waitCount > countOfWaitersPulsedToWake)
            {
                currentResourceCount++;
                countOfWaitersPulsedToWake++;
                Monitor.Pulse(syncRoot);
            }
            // Otherwise just make the resource available
            else
            {
                currentResourceCount++;
            }
        }
        // Signal async waiter outside the lock to avoid potential re-entrant deadlock
        asyncWaiterToRelease?.TrySetResult();
    }

    public void Dispose()
    {
        disposed = true;
        while (asyncWaitingQueue.TryDequeue(out var waitingTask))
        {
            waitingTask.TrySetCanceled();
        }
    }

    void CheckDispose()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(UniTaskAsyncLock));
        }
    }
}
}
#endif