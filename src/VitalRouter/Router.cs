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

    // Anonymous (delegate-based) subscribers are kept in their own lists so the
    // publish loop can dispatch to them with just a Type comparison + delegate call,
    // with no per-element type test (see AnonymousSubscriberBase).
    readonly FreeList<AnonymousSubscriberBase> anonymousSubscribers = new(8);
    readonly FreeList<AsyncAnonymousSubscriberBase> asyncAnonymousSubscribers = new(8);
    readonly FreeList<ICommandSubscriber> subscribers = new(8);
    readonly FreeList<IAsyncCommandSubscriber> asyncSubscribers = new(8);
    // Cumulative interceptor chain from root to this router. Used when this router
    // is the direct publish entry point (Router.PublishAsync).
    readonly FreeList<ICommandInterceptor> interceptors = new(8);
    // Interceptors added locally at this node only. Used when this router is reached
    // via fan-out from its parent (ancestor filters have already been applied by the
    // parent's pipeline, so we must not re-run them here).
    readonly FreeList<ICommandInterceptor> localInterceptors = new(8);
    readonly FreeList<Router> childRouters = new(4);

    Router? parentRouter;

    bool disposed;
    bool hasInterceptor;
    bool hasLocalInterceptor;

    readonly PublishCore publishCore;

    // This class is intended to be resolved via Dependency Injection (DI).
    // Please note that adding constructors may break the DI functionality.
    [Preserve]
    public Router()
    {
        publishCore = new PublishCore(this);
    }

    /// <summary>
    /// Wires this router as a child of `parent` and snapshots the parent's cumulative
    /// </summary>
    /// <param name="parent"></param>
    /// <remarks>
    /// filter chain into this router's `interceptors`.
    /// </remarks>
    void AttachToParent(Router parent)
    {
        parentRouter = parent;
        foreach (var inherited in parent.interceptors.AsSpan())
        {
            if (inherited != null)
            {
                interceptors.Add(inherited);
                hasInterceptor = true;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask PublishAsync<T>(T command, CancellationToken cancellation = default)
        where T : ICommand
    {
        return PublishInternalAsync(command, cancellation, hasInterceptor, interceptors);
    }

    // Called when this router is reached via fan-out from its parent. The parent's
    // pipeline has already run ancestor filters, so we apply only the locally added
    // ones (avoiding double execution of inherited filters).
    ValueTask FanOutAsync<T>(T command, CancellationToken cancellation)
        where T : ICommand
    {
        return PublishInternalAsync(command, cancellation, hasLocalInterceptor, localInterceptors);
    }

    ValueTask PublishInternalAsync<T>(
        T command,
        CancellationToken cancellation,
        bool hasFilters,
        FreeList<ICommandInterceptor> filters)
        where T : ICommand
    {
        if (hasFilters)
        {
            var filterContext = PublishContext<T>.Rent(filters, publishCore, cancellation);
            var filterTask = filterContext.PublishAsync(command);
            if (filterTask.IsCompletedSuccessfully)
            {
                filterTask.GetAwaiter().GetResult(); // consume, returning any pooled task source
                filterContext.Return();
                return default;
            }
            return ContinueAsync(filterTask, filterContext);
        }

        var context = PublishContext.Rent(cancellation);
        var task = publishCore.ReceiveAsync(command, context);
        if (task.IsCompletedSuccessfully)
        {
            task.GetAwaiter().GetResult(); // consume, returning any pooled task source
            context.ReturnToPool(); // non-virtual: instance is exactly PublishContext here
            return default;
        }
        return ContinueAsync(task, context);

        static async ValueTask ContinueAsync(ValueTask x, PublishContext c)
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
        if (subscriber is AnonymousSubscriberBase anonymous)
        {
            anonymousSubscribers.Add(anonymous);
        }
        else
        {
            subscribers.Add(subscriber);
        }
        return new Subscription(this, subscriber);
    }

    public Subscription Subscribe(IAsyncCommandSubscriber subscriber)
    {
        if (subscriber is AsyncAnonymousSubscriberBase anonymous)
        {
            asyncAnonymousSubscribers.Add(anonymous);
        }
        else
        {
            asyncSubscribers.Add(subscriber);
        }
        return new Subscription(this, subscriber);
    }

    public void Unsubscribe(ICommandSubscriber subscriber)
    {
        if (subscriber is AnonymousSubscriberBase anonymous)
        {
            anonymousSubscribers.Remove(anonymous);
        }
        else
        {
            subscribers.Remove(subscriber);
        }
    }

    public void Unsubscribe(IAsyncCommandSubscriber subscriber)
    {
        if (subscriber is AsyncAnonymousSubscriberBase anonymous)
        {
            asyncAnonymousSubscribers.Remove(anonymous);
        }
        else
        {
            asyncSubscribers.Remove(subscriber);
        }
    }

    public void UnsubscribeAll()
    {
        anonymousSubscribers.Clear();
        asyncAnonymousSubscribers.Clear();
        subscribers.Clear();
        asyncSubscribers.Clear();
    }

    public void AddFilter(ICommandInterceptor interceptor)
    {
        hasInterceptor = true;
        interceptors.Add(interceptor);
        hasLocalInterceptor = true;
        localInterceptors.Add(interceptor);
    }

    public void RemoveFilter(ICommandInterceptor interceptor)
    {
        RemoveFilter(x => x == interceptor);
    }

    public void RemoveFilter(Func<ICommandInterceptor, bool> predicate)
    {
        hasInterceptor = RemoveMatchingInterceptors(interceptors, predicate);
        hasLocalInterceptor = RemoveMatchingInterceptors(localInterceptors, predicate);
    }

    static bool RemoveMatchingInterceptors(FreeList<ICommandInterceptor> list, Func<ICommandInterceptor, bool> predicate)
    {
        var span = list.AsSpan();
        var count = 0;
        for (var i = span.Length - 1; i >= 0; i--)
        {
            if (list[i] is { } x)
            {
                count++;
                if (predicate(x))
                {
                    list.RemoveAt(i);
                    count--;
                }
            }
        }
        return count > 0;
    }

    public void RemoveAllFilters()
    {
        interceptors.Clear();
        hasInterceptor = false;
        localInterceptors.Clear();
        hasLocalInterceptor = false;
    }

    /// <summary>
    /// Returns a derived child router that owns the given filter.
    /// </summary>
    /// <remarks>
    /// The returned router represents the chain <c>parent → ... → this → newFilter</c>.
    /// Publishing directly on the returned router runs the full cumulative chain
    /// before reaching its subscribers — the same way an Rx <c>Where</c> chain
    /// applies every predicate from the source down to the subscription site.
    /// Commands published on the parent also reach subscribers registered on the
    /// child (each filter in the tree is invoked exactly once per publish).
    /// Note: the parent's filter list is snapshotted at this call; subsequent
    /// <c>AddFilter</c> on an ancestor will not retroactively affect existing
    /// children's cumulative chain.
    /// </remarks>
    public Router WithFilter(ICommandInterceptor interceptor)
    {
        var child = new Router();
        child.AttachToParent(this);
        child.AddFilter(interceptor);
        childRouters.Add(child);
        return child;
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
            var parent = parentRouter;
            if (parent != null)
            {
                parent.RemoveChildRouter(this);
                parentRouter = null;
            }
        }
    }

    void RemoveChildRouter(Router child)
    {
        childRouters.Remove(child);
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
            // Hoisted out of the loops: in shared generic code typeof(T) is a runtime
            // lookup. The loops below must stay free of generic-dependent type tests
            // (see AnonymousSubscriberBase).
            var commandType = typeof(T);

            var anonymousSubscribers = source.anonymousSubscribers.AsSpan();
            for (var i = anonymousSubscribers.Length - 1; i >= 0; i--)
            {
                var anonymous = anonymousSubscribers[i];
                if (anonymous != null && ReferenceEquals(anonymous.CommandType, commandType))
                {
                    Unsafe.As<Action<T, PublishContext>>(anonymous.Callback).Invoke(command, context);
                }
            }

            var subscribers = source.subscribers.AsSpan();
            for (var i = subscribers.Length - 1; i >= 0; i--)
            {
                subscribers[i]?.Receive(command, context);
            }

            var asyncAnonymousSubscribers = source.asyncAnonymousSubscribers.AsSpan();
            var asyncSubscribers = source.asyncSubscribers.AsSpan();
            var childRouters = source.childRouters.AsSpan();
            if (asyncAnonymousSubscribers.IsEmpty && asyncSubscribers.IsEmpty && childRouters.IsEmpty)
            {
                return default;
            }

            var whenAll = new WhenAllBuilder();

            for (var i = asyncAnonymousSubscribers.Length - 1; i >= 0; i--)
            {
                var anonymous = asyncAnonymousSubscribers[i];
                if (anonymous != null && ReferenceEquals(anonymous.CommandType, commandType))
                {
                    var callback = Unsafe.As<PublishContinuation<T>>(anonymous.Callback);
                    whenAll.Add(anonymous.Ordering is { } ordering
                        ? ordering.InvokeAsync(command, context, callback)
                        : callback(command, context));
                }
            }

            for (var i = asyncSubscribers.Length - 1; i >= 0; i--)
            {
                if (asyncSubscribers[i] is { } x)
                {
                    whenAll.Add(x.ReceiveAsync(command, context));
                }
            }

            for (var i = childRouters.Length - 1; i >= 0; i--)
            {
                if (childRouters[i] is { } child)
                {
                    whenAll.Add(child.FanOutAsync(command, context.CancellationToken));
                }
            }

            return whenAll.Build();
        }
    }
}
}