using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace VitalRouter
{
public static class SubscribableAnonymousExtensions
{
    public static Subscription Subscribe<T>(this ICommandSubscribable subscribable, Action<T, PublishContext> callback)
        where T : ICommand
    {
        return subscribable.Subscribe(new AnonymousSubscriber<T>(callback));
    }

    [Obsolete("Use SubscribeAwait instead")]
    public static Subscription Subscribe<T>(
        this ICommandSubscribable subscribable,
        PublishContinuation<T> callback)
        where T : ICommand
    {
        return subscribable.Subscribe(new AsyncAnonymousSubscriber<T>(callback));
    }

    public static Subscription SubscribeAwait<T>(
        this ICommandSubscribable subscribable,
        PublishContinuation<T> callback,
        CommandOrdering? ordering = null)
        where T : ICommand
    {
        return subscribable.Subscribe(new AsyncAnonymousSubscriber<T>(callback, ordering));
    }
}

// Non-generic base classes for the anonymous subscriber wrappers.
//
// The publish hot path (Router.PublishCore.ReceiveAsync) runs in shared generic
// code (T is usually a reference type). A type test against the generic wrapper
// (`is AnonymousSubscriber<T>`) requires a runtime generic dictionary lookup per
// element, which the JIT cannot always optimize away. Testing against these
// non-generic bases plus a reference comparison of the pre-computed CommandType
// keeps the loop free of any generic-dependent runtime helpers.
abstract class AnonymousSubscriberBase : ICommandSubscriber
{
    internal readonly Type CommandType;
    internal readonly Delegate Callback; // Action<T, PublishContext>

    protected AnonymousSubscriberBase(Type commandType, Delegate callback)
    {
        CommandType = commandType;
        Callback = callback;
    }

    public abstract void Receive<T>(T command, PublishContext context) where T : ICommand;
}

abstract class AsyncAnonymousSubscriberBase : IAsyncCommandSubscriber
{
    internal readonly Type CommandType;
    internal readonly Delegate Callback; // PublishContinuation<T>
    internal readonly ICommandInterceptor? Ordering;

    protected AsyncAnonymousSubscriberBase(Type commandType, Delegate callback, ICommandInterceptor? ordering)
    {
        CommandType = commandType;
        Callback = callback;
        Ordering = ordering;
    }

    public abstract ValueTask ReceiveAsync<T>(T command, PublishContext context) where T : ICommand;
}

sealed class AsyncAnonymousSubscriber<T> : AsyncAnonymousSubscriberBase where T : ICommand
{
    public AsyncAnonymousSubscriber(PublishContinuation<T> callback, CommandOrdering? ordering = null)
        : base(typeof(T), callback, ordering switch
        {
            CommandOrdering.Sequential => new SequentialOrdering(),
            CommandOrdering.Drop => new DropOrdering(),
            CommandOrdering.Switch => new SwitchOrdering(),
            _ => null,
        })
    {
    }

    public override ValueTask ReceiveAsync<TReceive>(TReceive command, PublishContext context)
    {
        if (typeof(TReceive) == typeof(T))
        {
            var callback = Unsafe.As<PublishContinuation<T>>(Callback);
            if (Ordering != null)
            {
#if UNITY_2022_2_OR_NEWER
                PublishContinuation<TReceive> c = (cmd, ctx) =>
                {
                    return callback.Invoke(global::Unity.Collections.LowLevel.Unsafe.UnsafeUtility.As<TReceive, T>(ref cmd), ctx);
                };
#else
                var c = Unsafe.As<PublishContinuation<TReceive>>(callback);
#endif
                return Ordering.InvokeAsync(command, context, c);
            }
            var commandCasted = Unsafe.As<TReceive, T>(ref command);
            return callback(commandCasted, context);
        }
        return default;
    }
}

sealed class AnonymousSubscriber<T> : AnonymousSubscriberBase where T : ICommand
{
    public AnonymousSubscriber(Action<T, PublishContext> callback)
        : base(typeof(T), callback)
    {
    }

    public override void Receive<TReceive>(TReceive command, PublishContext context)
    {
        if (typeof(TReceive) == typeof(T))
        {
            var commandCasted = Unsafe.As<TReceive, T>(ref command);
            Unsafe.As<Action<T, PublishContext>>(Callback).Invoke(commandCasted, context);
        }
    }
}
}
