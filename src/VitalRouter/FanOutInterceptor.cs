using System.Collections.Generic;
using System.Threading.Tasks;
using VitalRouter.Internal;

namespace VitalRouter
{
public class FanOutInterceptor : ICommandInterceptor
{
    readonly List<ICommandPublisher> subsequents = new();

    public void Add(ICommandPublisher publisher)
    {
        subsequents.Add(publisher);
    }

    public async ValueTask InvokeAsync<T>(T command, PublishContext context, PublishContinuation<T> next)
        where T : ICommand
    {
        await next(command, context);

        var whenAll = new WhenAllBuilder();
        foreach (var x in subsequents)
        {
            whenAll.Add(x.PublishAsync(command, context.CancellationToken));
        }
        await whenAll.Build();
    }
}
}
