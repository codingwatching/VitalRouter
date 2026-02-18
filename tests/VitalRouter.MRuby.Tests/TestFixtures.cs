using System.Collections.Generic;
using System.Threading.Tasks;
using MRubyCS.Serializer;
using VitalRouter;

namespace VitalRouter.MRuby.Tests;

[MRubyObject]
public partial struct TestCommand : ICommand
{
    public int Value;
}

[MRubyObject]
public partial struct MoveCommand : ICommand
{
    public string Id;
    public int X;
    public int Y;
}

public class TestCommandRecorder : IAsyncCommandSubscriber
{
    public readonly List<ICommand> Received = new();

    public ValueTask ReceiveAsync<T>(T command, PublishContext context) where T : ICommand
    {
        Received.Add(command);
        return default;
    }
}
