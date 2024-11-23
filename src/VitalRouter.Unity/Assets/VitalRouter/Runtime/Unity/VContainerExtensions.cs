#if VITALROUTER_VCONTAINER_INTEGRATION
using System;
using System.Collections.Generic;
using VContainer;
using VContainer.Unity;
using VitalRouter.Internal;

namespace VitalRouter.VContainer
{
class RoutingDisposable : IDisposable
{
    readonly IObjectResolver container;
    readonly IReadOnlyList<MapRoutesInfo> routes;

    public RoutingDisposable(IObjectResolver container, IReadOnlyList<MapRoutesInfo> routes)
    {
        this.container = container;
        this.routes = routes;
    }

    public void Dispose()
    {
        for (var i = 0; i < routes.Count; i++)
        {
            var instance = container.Resolve(routes[i].Type);
            routes[i].UnmapRoutesMethod.Invoke(instance, null);
        }
    }
}

public class RoutingBuilder
{
    public InterceptorStackBuilder Filters { get; } = new();
    public bool Isolated { get; set; }
    public CommandOrdering Ordering { get; set; }

    internal readonly List<MapRoutesInfo> MapRoutesInfos = new();
    internal readonly List<RoutingBuilder> Subsequents = new();

    readonly IContainerBuilder containerBuilder;

    public RoutingBuilder(IContainerBuilder containerBuilder)
    {
        this.containerBuilder = containerBuilder;
    }

    public void Map<T>()
    {
        if (!containerBuilder.Exists(typeof(T)))
        {
            if (typeof(UnityEngine.Component).IsAssignableFrom(typeof(T)))
            {
                containerBuilder.RegisterComponentOnNewGameObject(typeof(T), Lifetime.Singleton);
            }
            else
            {
                containerBuilder.Register<T>(Lifetime.Singleton);
            }
        }
        MapRoutesInfos.Add(MapRoutesInfo.Analyze(typeof(T)));
    }

    public RegistrationBuilder Map<T>(T instance) where T : class
    {
        MapRoutesInfos.Add(MapRoutesInfo.Analyze(typeof(T)));
        return containerBuilder.RegisterInstance(instance);
    }

    public RegistrationBuilder MapEntryPoint<T>()
    {
        MapRoutesInfos.Add(MapRoutesInfo.Analyze(typeof(T)));
        return containerBuilder.RegisterEntryPoint<T>();
    }

    public RegistrationBuilder MapComponent<T>(T instance) where T : UnityEngine.Component
    {
        MapRoutesInfos.Add(MapRoutesInfo.Analyze(typeof(T)));
        return containerBuilder.RegisterComponent(instance);
    }

    public RegistrationBuilder MapComponentOnNewGameObject<T>() where T : UnityEngine.Component
    {
        MapRoutesInfos.Add(MapRoutesInfo.Analyze(typeof(T)));
        return containerBuilder.RegisterComponentOnNewGameObject<T>(Lifetime.Singleton);
    }

    public ComponentRegistrationBuilder MapComponentInHierarchy<T>() where T : UnityEngine.Component
    {
        MapRoutesInfos.Add(MapRoutesInfo.Analyze(typeof(T)));
        return containerBuilder.RegisterComponentInHierarchy<T>();
    }

    public ComponentRegistrationBuilder MapComponentInNewPrefab<T>(T prefab) where T : UnityEngine.Component
    {
        MapRoutesInfos.Add(MapRoutesInfo.Analyze(typeof(T)));
        return containerBuilder.RegisterComponentInNewPrefab(prefab, Lifetime.Singleton);
    }

    public RoutingBuilder FanOut(Action<RoutingBuilder> configure)
    {
        var subsequent = new RoutingBuilder(containerBuilder);
        configure(subsequent);
        Subsequents.Add(subsequent);
        return this;
    }
}

public static class VContainerExtensions
{
    public static void RegisterVitalRouter(this IContainerBuilder builder, Action<RoutingBuilder> configure)
    {
        builder.RegisterVitalRouterCore(null, configure);
    }

    public static void RegisterVitalRouter(this IContainerBuilder builder, Router router, Action<RoutingBuilder> configure)
    {
        builder.RegisterVitalRouterCore(router, configure);
    }

    static void RegisterVitalRouterCore(this IContainerBuilder builder, Router? routerInstance, Action<RoutingBuilder> configure)
    {
        var routing = new RoutingBuilder(builder);
        configure(routing);

        if (routerInstance != null!)
        {
            builder.RegisterInstance(routerInstance)
                .AsImplementedInterfaces()
                .AsSelf();
        }
        else if (routing.Isolated || !builder.Exists(typeof(Router), findParentScopes: true))
        {
            builder.Register<Router>(Lifetime.Singleton)
                .AsImplementedInterfaces()
                .AsSelf();
        }

        builder.RegisterVitalRouterInterceptors(routing);
        builder.RegisterVitalRouterDisposable(routing);

        FanOutInterceptor? fanOut = null;
        if (routing.Subsequents.Count > 0)
        {
            fanOut = new FanOutInterceptor();
            foreach (var subsequent in routing.Subsequents)
            {
                var subsequentRouter = new Router();
                builder.RegisterVitalRouterRecursive(subsequentRouter, subsequent);
                fanOut.Add(subsequentRouter);
            }
        }

        builder.RegisterBuildCallback(container =>
        {
            var router = container.Resolve<Router>();
            InvokeMapRoutes(router, routing, container);

            if (fanOut != null)
            {
                router.AddFilter(fanOut);
            }
        });
    }

    static void RegisterVitalRouterRecursive(this IContainerBuilder builder, Router routerInstance, RoutingBuilder routing)
    {
        builder.RegisterVitalRouterInterceptors(routing);
        builder.RegisterVitalRouterDisposable(routing);

        FanOutInterceptor? fanOut = null;
        if (routing.Subsequents.Count > 0)
        {
            fanOut = new FanOutInterceptor();
            foreach (var subsequent in routing.Subsequents)
            {
                var subsequentRouter = new Router();
                builder.RegisterVitalRouterRecursive(subsequentRouter, subsequent);
                fanOut.Add(subsequentRouter);
            }
        }

        builder.RegisterBuildCallback(container =>
        {
            InvokeMapRoutes(routerInstance, routing, container);
            if (fanOut != null)
            {
                routerInstance.AddFilter(fanOut);
            }
        });
    }

    static void RegisterVitalRouterInterceptors(this IContainerBuilder builder, RoutingBuilder routing)
    {
        switch (routing.Ordering)
        {
            case CommandOrdering.Sequential:
                routing.Filters.Add<SequentialOrdering>();
                break;
            case CommandOrdering.Parallel:
                break;
            case CommandOrdering.Drop:
                routing.Filters.Add<DropOrdering>();
                break;
            case CommandOrdering.Switch:
                routing.Filters.Add<SwitchOrdering>();
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        foreach (var interceptorType in routing.Filters.Types)
        {
            builder.Register(interceptorType, Lifetime.Singleton);
        }

        for (var i = 0; i < routing.MapRoutesInfos.Count; i++)
        {
            var info = routing.MapRoutesInfos[i];
            for (var paramIndex = 1; paramIndex < info.ParameterInfos.Length; paramIndex++)
            {
                var interceptorType = info.ParameterInfos[paramIndex].ParameterType;
                if (!builder.Exists(interceptorType))
                {
                    builder.Register(interceptorType, Lifetime.Singleton);
                }
            }
        }
    }

    static void RegisterVitalRouterDisposable(this IContainerBuilder builder, RoutingBuilder routing)
    {
        builder.Register(container => new RoutingDisposable(container, routing.MapRoutesInfos), Lifetime.Scoped);
    }

    static void InvokeMapRoutes(Router router, RoutingBuilder routing, IObjectResolver container)
    {
        foreach (var interceptorType in routing.Filters.Types)
        {
            router.AddFilter((ICommandInterceptor)container.Resolve(interceptorType));
        }

        for (var i = 0; i < routing.MapRoutesInfos.Count; i++)
        {
            var info = routing.MapRoutesInfos[i];
            var instance = container.Resolve(info.Type);

            // TODO: more optimize
            var parameters = CappedArrayPool<object>.Shared8Limit.Rent(info.ParameterInfos.Length);
            try
            {
                parameters[0] = router;
                for (var paramIndex = 1; paramIndex < parameters.Length; paramIndex++)
                {
                    parameters[paramIndex] = container.Resolve(info.ParameterInfos[paramIndex].ParameterType);
                }
                info.MapToMethod.Invoke(instance, parameters);
            }
            finally
            {
                CappedArrayPool<object>.Shared8Limit.Return(parameters);
            }
        }

    }
}
}
#endif
