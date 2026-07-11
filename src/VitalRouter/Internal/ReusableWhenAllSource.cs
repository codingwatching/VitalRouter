using System;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace VitalRouter.Internal
{
    static class ContinuationSentinel
    {
        public static readonly Action<object?> AvailableContinuation = _ => { };
        public static readonly Action<object?> CompletedContinuation = _ => { };
    }

    class ReusableWhenAllSource : IValueTaskSource
    {
        class AwaiterNode
        {
            ReusableWhenAllSource parent = default!;
            ValueTaskAwaiter awaiter;

            readonly Action continuation;

            public AwaiterNode()
            {
                continuation = OnCompleted;
            }

            public static void RegisterUnsafeOnCompleted(ReusableWhenAllSource parent, ValueTaskAwaiter awaiter)
            {
                var node = ContextPool<AwaiterNode>.Rent();
                node.parent = parent;
                node.awaiter = awaiter;
                node.awaiter.UnsafeOnCompleted(node.continuation);
            }

            void OnCompleted()
            {
                var p = parent;
                var a = awaiter;
                parent = null!;
                awaiter = default;

                ContextPool<AwaiterNode>.Return(this);
                try
                {
                    a.GetResult();
                    p.IncrementSuccessfully();
                }
                catch (Exception ex)
                {
                    p.error = ExceptionDispatchInfo.Capture(ex);
                    p.TryInvokeContinuation();
                }
            }
        }

        static readonly ContextCallback ExecContextCallback = ExecutionContextCallback;
        static readonly SendOrPostCallback SyncContextCallback = SynchronizationContextCallback;

        public short Version { get; private set; }

        Action<object?> continuation = ContinuationSentinel.AvailableContinuation;
        Action<object?>? invokeContinuation;
        object? continuationState;
        ExecutionContext? executionContext;
        SynchronizationContext? synchronizationContext;
        ExceptionDispatchInfo? error;
        int taskCount;
        int completedCount;

        public static ReusableWhenAllSource Run(ReadOnlySpan<ValueTask?> tasks)
        {
            var source = ContextPool<ReusableWhenAllSource>.Rent();
            source.Reset(tasks.Length);

            foreach (var task in tasks)
            {
                if (task is { } t)
                {
                    source.AddTask(t);
                }
                else
                {
                    source.IncrementSuccessfully();
                }
            }
            return source;
        }

        public void Reset(int taskCount)
        {
            // Reset/update state for the next use/await of this instance.
            if (++Version == short.MaxValue) Version = 0;
            this.taskCount = taskCount;
            completedCount = 0;
            error = null;
            executionContext = null;
            synchronizationContext = null;
            continuation = ContinuationSentinel.AvailableContinuation;
            continuationState = null;
        }

        // Starts an "uncommitted" session: pending tasks may be registered via
        // AddPending before the total count is known. completedCount can never
        // equal -1, so completions that arrive before Commit only count up.
        public void ResetUncommitted()
        {
            Reset(-1);
        }

        // Registers a task that is known to be still pending.
        public void AddPending(ValueTask task)
        {
            AwaiterNode.RegisterUnsafeOnCompleted(this, task.GetAwaiter());
        }

        // Finalizes an uncommitted session once all pending tasks are registered.
        public void Commit(int totalTaskCount, ExceptionDispatchInfo? capturedError)
        {
            if (capturedError != null)
            {
                error = capturedError;
            }
            // Full fence so that either this thread observes the final completedCount,
            // or the completing thread observes the committed taskCount (or both).
            Interlocked.Exchange(ref taskCount, totalTaskCount);
            if (capturedError != null || Volatile.Read(ref completedCount) == totalTaskCount)
            {
                TryInvokeContinuation();
            }
        }

        public void AddTask(ValueTask task)
        {
            if (task.IsCompletedSuccessfully)
            {
                IncrementSuccessfully();
            }
            else if (task.IsFaulted)
            {
                try
                {
                    task.GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    error = ExceptionDispatchInfo.Capture(ex);
                }
            }
            else
            {
                AwaiterNode.RegisterUnsafeOnCompleted(this, task.GetAwaiter());
            }
        }

        public ValueTaskSourceStatus GetStatus(short token)
        {
            if (error != null)
            {
                return error.SourceException is OperationCanceledException
                    ? ValueTaskSourceStatus.Canceled
                    : ValueTaskSourceStatus.Faulted;
            }
            if (completedCount == taskCount)
            {
                return ValueTaskSourceStatus.Succeeded;
            }
            return ValueTaskSourceStatus.Pending;
        }

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            var c = Interlocked.CompareExchange(ref this.continuation, continuation, ContinuationSentinel.AvailableContinuation);
            if (c == ContinuationSentinel.CompletedContinuation)
            {
                continuation(state);
                return;
            }

            if (c != ContinuationSentinel.AvailableContinuation)
            {
                throw new InvalidOperationException("does not allow multiple await.");
            }

            if ((flags & ValueTaskSourceOnCompletedFlags.FlowExecutionContext) != 0)
            {
                executionContext = ExecutionContext.Capture();
            }
            if ((flags & ValueTaskSourceOnCompletedFlags.UseSchedulingContext) != 0)
            {
                synchronizationContext = SynchronizationContext.Current;
            }
            continuationState = state;

            if (GetStatus(token) != ValueTaskSourceStatus.Pending)
            {
                TryInvokeContinuation();
            }
        }

        public void GetResult(short token)
        {
            try
            {
                if (Version != token)
                {
                    throw new InvalidOperationException($"Invalid operation. Expected token {Version} but was {token}.");
                }
                error?.Throw();
            }
            finally
            {
                ReturnToPool();
            }
        }

        public void IncrementSuccessfully()
        {
            if (Interlocked.Increment(ref completedCount) == Volatile.Read(ref taskCount))
            {
                TryInvokeContinuation();
            }
        }

        void ReturnToPool()
        {
            Reset(0);
            ContextPool<ReusableWhenAllSource>.Return(this);
        }

        void TryInvokeContinuation()
        {
            var c = Interlocked.Exchange(ref continuation, ContinuationSentinel.CompletedContinuation);
            if (c != ContinuationSentinel.AvailableContinuation && c != ContinuationSentinel.CompletedContinuation)
            {
                // var spinWait = new SpinWait();
                // while (continuationState == null) // worst case, state is not set yet so wait.
                // {
                //     spinWait.SpinOnce();
                // }

                if (executionContext != null)
                {
                    invokeContinuation = c;
                    ExecutionContext.Run(executionContext, ExecContextCallback, this);
                }
                else if (synchronizationContext != null)
                {
                    invokeContinuation = c;
                    synchronizationContext.Post(SyncContextCallback, this);
                }
                else
                {
                    c(continuationState);
                }
            }
        }

        static void ExecutionContextCallback(object? state)
        {
            var self = (ReusableWhenAllSource)state!;
            if (self.synchronizationContext != null)
            {
                self.synchronizationContext.Post(SyncContextCallback, self);
            }
            else
            {
                var invokeContinuation = self.invokeContinuation!;
                var invokeState = self.continuationState;
                self.invokeContinuation = null;
                self.continuationState = null;
                invokeContinuation(invokeState);
            }
        }

        static void SynchronizationContextCallback(object? state)
        {
            var self = (ReusableWhenAllSource)state!;
            var invokeContinuation = self.invokeContinuation!;
            var invokeState = self.continuationState;
            self.invokeContinuation = null;
            self.continuationState = null;
            invokeContinuation(invokeState);
        }
    }

    // Zero-allocation when-all accumulator.
    // Tasks that have already completed synchronously are folded away on the spot:
    // if every task completed successfully, Build() returns a default ValueTask;
    // if exactly one task is pending, it is returned as-is. A pooled
    // ReusableWhenAllSource is rented only when two or more tasks are pending.
    struct WhenAllBuilder
    {
        ReusableWhenAllSource? whenAll;
        ValueTask firstPending;
        bool hasFirstPending;
        int pendingCount;
        ExceptionDispatchInfo? error;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(ValueTask task)
        {
            if (task.IsCompletedSuccessfully)
            {
                return;
            }
            AddSlow(task);
        }

        void AddSlow(ValueTask task)
        {
            if (task.IsCompleted) // faulted or canceled
            {
                try
                {
                    task.GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    error = ExceptionDispatchInfo.Capture(ex);
                }
                return;
            }
            if (!hasFirstPending)
            {
                firstPending = task;
                hasFirstPending = true;
                return;
            }
            if (whenAll == null)
            {
                whenAll = ContextPool<ReusableWhenAllSource>.Rent();
                whenAll.ResetUncommitted();
                whenAll.AddPending(firstPending);
                pendingCount = 1;
            }
            whenAll.AddPending(task);
            pendingCount++;
        }

        public ValueTask Build()
        {
            if (whenAll != null)
            {
                whenAll.Commit(pendingCount, error);
                return new ValueTask(whenAll, whenAll.Version);
            }
            if (error != null)
            {
                return hasFirstPending
                    ? AwaitThenThrowAsync(firstPending, error)
                    : new ValueTask(Task.FromException(error.SourceException));
            }
            return hasFirstPending ? firstPending : default;
        }

        static async ValueTask AwaitThenThrowAsync(ValueTask pending, ExceptionDispatchInfo error)
        {
            try
            {
                await pending;
            }
            catch
            {
                // Matches ReusableWhenAllSource semantics: a single captured error wins.
            }
            error.Throw();
        }
    }
}
