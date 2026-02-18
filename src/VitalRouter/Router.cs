using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using VitalRouter.Internal;

namespace VitalRouter
{
public interface ICommandPublisher
{
    ValueTask PublishAsync<T>(T command, CancellationToken cancellation = default) where T : ICommand;
    ICommandPublisher WithFilter(ICommandInterceptor interceptor);
}

public interface ICommandSubscribable
{
    Subscription Subscribe(ICommandSubscriber subscriber);
    Subscription Subscribe(IAsyncCommandSubscriber subscriber);
    void Unsubscribe(ICommandSubscriber subscriber);
    void Unsubscribe(IAsyncCommandSubscriber subscriber);
    ICommandSubscribable WithFilter(ICommandInterceptor interceptor);
}

public interface ICommandSubscriber
{
    void Receive<T>(T command, PublishContext context) where T : ICommand;
}

public interface IAsyncCommandSubscriber
{
    ValueTask ReceiveAsync<T>(T command, PublishContext context) where T : ICommand;
}

public sealed partial class Router : ICommandPublisher, ICommandSubscribable, IDisposable
{
    public static readonly Router Default = new();

    public static Func<IAsyncLock> AsyncLockFactory { get; private set; } = () => new SemaphoreSlimAsyncLock();
    public static Func<CancellationToken, ValueTask> YieldAction { get; private set; } = async _ => await Task.Yield();
    public static Action<string> Logger { get; private set; } = Console.WriteLine;

    public static void RegisterAsyncLock(Func<IAsyncLock> asyncLockFactory) => AsyncLockFactory = asyncLockFactory;
    public static void RegisterYieldAction(Func<CancellationToken, ValueTask> yieldAction) => YieldAction = yieldAction;

    readonly FreeList<ICommandSubscriber> subscribers = new(8);
    readonly FreeList<IAsyncCommandSubscriber> asyncSubscribers = new(8);
    readonly FreeList<ICommandInterceptor> interceptors = new(8);

    bool disposed;
    bool hasInterceptor;

    readonly PublishCore publishCore;

    [Preserve]
    public Router()
    {
        publishCore = new PublishCore(this);
    }

    public ValueTask PublishAsync<T>(T command, CancellationToken cancellation = default)
        where T : ICommand
    {
        ValueTask task;
        PublishContext context;
        if (hasInterceptor)
        {
            var c = PublishContext<T>.Rent(interceptors, publishCore, cancellation);
            context = c;
            task = c.PublishAsync(command);
        }
        else
        {
            context = PublishContext.Rent(cancellation);
            task = publishCore.ReceiveAsync(command, context);
        }

        if (task.IsCompletedSuccessfully)
        {
            context.Return();
            return task;
        }

        return ContinueAsync(task, context);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        async ValueTask ContinueAsync(ValueTask x, PublishContext c)
        {
            try
            {
                await x;
            }
            finally
            {
                c.Return();
            }
        }
    }

    public Subscription Subscribe(ICommandSubscriber subscriber)
    {
        subscribers.Add(subscriber);
        return new Subscription(this, subscriber);
    }

    public Subscription Subscribe(IAsyncCommandSubscriber subscriber)
    {
        asyncSubscribers.Add(subscriber);
        return new Subscription(this, subscriber);
    }

    public void Unsubscribe(ICommandSubscriber subscriber)
    {
        subscribers.Remove(subscriber);
    }

    public void Unsubscribe(IAsyncCommandSubscriber subscriber)
    {
        asyncSubscribers.Remove(subscriber);
    }

    public void UnsubscribeAll()
    {
        subscribers.Clear();
        asyncSubscribers.Clear();
    }

    public void AddFilter(ICommandInterceptor interceptor)
    {
        hasInterceptor = true;
        interceptors.Add(interceptor);
    }

    public void RemoveFilter(ICommandInterceptor interceptor)
    {
        RemoveFilter(x => x == interceptor);
    }

    public void RemoveFilter(Func<ICommandInterceptor, bool> predicate)
    {
        var span = interceptors.AsSpan();
        var count = 0;
        for (var i = span.Length - 1; i >= 0; i--)
        {
            if (interceptors[i] is { } x)
            {
                count++;
                if (predicate(x))
                {
                    interceptors.RemoveAt(i);
                    count--;
                }
            }
        }
        hasInterceptor = count > 0;
    }

    public void RemoveAllFilters()
    {
        interceptors.Clear();
    }

    public Router WithFilter(ICommandInterceptor interceptor)
    {
        var filtered = Clone();
        filtered.AddFilter(interceptor);
        return filtered;
    }

    // TODO:
    public bool HasInterceptor() => HasFilter();
    public bool HasInterceptor<T>() where T : class, ICommandInterceptor => HasFilter<T>();

    public bool HasFilter() => hasInterceptor;

    public bool HasFilter<T>() where T : class, ICommandInterceptor => FindFilter<T>() != null;

    public bool HasFilter<T>(Func<T, bool> predicate)
        where T : class, ICommandInterceptor =>
        FindFilter(predicate) != null;

    public bool HasFilter<T, TState>(Func<T, TState, bool> predicate, TState state)
        where T : class, ICommandInterceptor =>
        FindFilter(predicate, state) != null;

    public T? FindFilter<T>() where T : class, ICommandInterceptor =>
        FindFilter(x => x is T) as T;

    public T? FindFilter<T>(Func<T, bool> predicate) where T : class, ICommandInterceptor
    {
        foreach (var interceptorOrNull in interceptors.AsSpan())
        {
            if (interceptorOrNull is T x && predicate(x))
            {
                return x;
            }
        }
        return null;
    }

    public T? FindFilter<T, TState>(Func<T, TState, bool> predicate, TState state)
        where T : class, ICommandInterceptor
    {
        foreach (var interceptorOrNull in interceptors.AsSpan())
        {
            if (interceptorOrNull is T x && predicate(x, state))
            {
                return x;
            }
        }
        return null;
    }

    public ICommandInterceptor? FindFilter(Func<ICommandInterceptor, bool> predicate)
    {
        foreach (var interceptorOrNull in interceptors.AsSpan())
        {
            if (interceptorOrNull is { } x && predicate(x))
            {
                return x;
            }
        }
        return null;
    }

    ICommandPublisher ICommandPublisher.WithFilter(ICommandInterceptor interceptor) => WithFilter(interceptor);
    ICommandSubscribable ICommandSubscribable.WithFilter(ICommandInterceptor interceptor) => WithFilter(interceptor);

    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            UnsubscribeAll();
            RemoveAllFilters();
        }
    }

    Router Clone()
    {
        var result = new Router();
        foreach (var interceptor in interceptors.AsSpan())
        {
            if (interceptor != null)
            {
                result.AddFilter(interceptor);
            }
        }
        return result;
    }

    readonly struct PublishCore : IAsyncCommandSubscriber
    {
        readonly Router source;

        public PublishCore(Router source)
        {
            this.source = source;
        }

        public ValueTask ReceiveAsync<T>(T command, PublishContext context) where T : ICommand
        {
            // var subscribers = source.subscribers.AsSpan();
            var subscribers = source.subscribers.Values;
            for (var i = source.subscribers.LastIndex; i >= 0; i--)
            {
                switch (subscribers[i])
                {
                    case AnonymousSubscriber<T> x: // devirtualization
                        x.ReceiveInternal(command, context);
                        break;
                    case { } x:
                        x.Receive(command, context);
                        break;
                }
            }

            var asyncSubscribersLastIndex = source.asyncSubscribers.LastIndex;
            if (asyncSubscribersLastIndex < 0) return default;

            var asyncSubscribers = source.asyncSubscribers.Values;
            var whenAll = ContextPool<ReusableWhenAllSource>.Rent();
            whenAll.Reset(asyncSubscribersLastIndex + 1);
            for (var i = asyncSubscribersLastIndex; i >= 0; i--)
            {
                switch (asyncSubscribers[i])
                {
                    case AsyncAnonymousSubscriber<T> x: // Devirtualization
                        whenAll.AddTask(x.ReceiveInternalAsync(command, context));
                        break;
                    case { } x:
                        whenAll.AddTask(x.ReceiveAsync(command, context));
                        break;
                    default:
                        whenAll.IncrementSuccessfully();
                        break;
                }
            }
            return new ValueTask(whenAll, whenAll.Version);
        }
    }
}
}