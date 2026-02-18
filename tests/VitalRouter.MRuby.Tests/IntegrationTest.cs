using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MRubyCS;
using MRubyCS.Compiler;
using NUnit.Framework;

namespace VitalRouter.MRuby.Tests;

[TestFixture]
public class IntegrationTest
{
    MRubyState mrb = default!;
    MRubyCompiler compiler = default!;
    Router router = default!;
    TestCommandRecorder recorder = default!;

    [SetUp]
    public void SetUp()
    {
        mrb = MRubyState.Create();
        compiler = MRubyCompiler.Create(mrb);
        router = new Router();
        recorder = new TestCommandRecorder();
        router.Subscribe(recorder);

        mrb.DefineVitalRouter(x =>
        {
            x.AddCommand<TestCommand>("test");
            x.AddCommand<MoveCommand>("move");
        });
    }

    [TearDown]
    public void TearDown()
    {
        compiler.Dispose();
        router.Dispose();
    }

    [Test]
    public async Task ExecuteSingleCommand()
    {
        var irep = compiler.Compile("cmd :test, value: 42");
        await mrb.ExecuteAsync(router, irep);

        Assert.That(recorder.Received, Has.Count.EqualTo(1));
        var cmd = (TestCommand)recorder.Received[0];
        Assert.That(cmd.Value, Is.EqualTo(42));
    }

    [Test]
    public async Task ExecuteMultipleCommands()
    {
        var script = @"
cmd :test, value: 1
cmd :move, id: 'hero', x: 10, y: 20
cmd :test, value: 2
";
        var irep = compiler.Compile(script);
        await mrb.ExecuteAsync(router, irep);

        Assert.That(recorder.Received, Has.Count.EqualTo(3));

        var first = (TestCommand)recorder.Received[0];
        Assert.That(first.Value, Is.EqualTo(1));

        var move = (MoveCommand)recorder.Received[1];
        Assert.That(move.Id, Is.EqualTo("hero"));
        Assert.That(move.X, Is.EqualTo(10));
        Assert.That(move.Y, Is.EqualTo(20));

        var last = (TestCommand)recorder.Received[2];
        Assert.That(last.Value, Is.EqualTo(2));
    }

    [Test]
    public async Task SharedStateReadWrite()
    {
        // Ruby side writes, C# side reads
        var writeScript = @"
state[:greeting] = 'hello'
state[:count] = 99
";
        var irep = compiler.Compile(writeScript);
        await mrb.ExecuteAsync(router, irep);

        var sharedVars = mrb.GetSharedVariables();
        Assert.That(sharedVars.GetOrDefault<string>("greeting"), Is.EqualTo("hello"));
        Assert.That(sharedVars.GetOrDefault<int>("count"), Is.EqualTo(99));

        // C# side writes, Ruby side reads via cmd handler
        sharedVars.Set("from_csharp", 777);

        var readScript = @"
cmd :test, value: state[:from_csharp]
";
        var irep2 = compiler.Compile(readScript);
        await mrb.ExecuteAsync(router, irep2);

        Assert.That(recorder.Received, Has.Count.EqualTo(1));
        var cmd = (TestCommand)recorder.Received[0];
        Assert.That(cmd.Value, Is.EqualTo(777));
    }

    [Test]
    public async Task PublishContextHasMRubyState()
    {
        MRubyState? capturedState = null;
        MRubySharedVariableTable? capturedVars = null;

        var contextCapture = new ContextCaptureSubscriber(ctx =>
        {
            capturedState = ctx.MRuby();
            capturedVars = ctx.MRubySharedVariables();
        });
        router.Subscribe(contextCapture);

        var irep = compiler.Compile("cmd :test, value: 1");
        await mrb.ExecuteAsync(router, irep);

        Assert.That(capturedState, Is.Not.Null);
        Assert.That(capturedState, Is.SameAs(mrb));
        Assert.That(capturedVars, Is.Not.Null);
    }

    [Test]
    public void CommandNotFound()
    {
        var irep = compiler.Compile("cmd :unknown_command, value: 1");

        var ex = Assert.ThrowsAsync<MRubyRoutingException>(async () =>
        {
            await mrb.ExecuteAsync(router, irep);
        });
        Assert.That(ex!.Message, Does.Contain("unknown_command"));
    }

    [Test]
    public void CancellationStopsScript()
    {
        var cts = new CancellationTokenSource();

        // Subscribe a handler that cancels after receiving the first command
        var cancelOnFirst = new CancelOnFirstSubscriber(cts);
        router.Subscribe(cancelOnFirst);

        var script = @"
cmd :test, value: 1
cmd :test, value: 2
cmd :test, value: 3
";
        var irep = compiler.Compile(script);

        Assert.ThrowsAsync<TaskCanceledException>(async () =>
        {
            await mrb.ExecuteAsync(router, irep, cts.Token);
        });
    }

    [Test]
    public async Task ConcurrentScriptsOnSameRouter()
    {
        var mrb2 = MRubyState.Create();
        using var compiler2 = MRubyCompiler.Create(mrb2);

        mrb2.DefineVitalRouter(x =>
        {
            x.AddCommand<TestCommand>("test");
            x.AddCommand<MoveCommand>("move");
        });

        var irep1 = compiler.Compile(@"
cmd :test, value: 1
cmd :test, value: 2
cmd :test, value: 3
");
        var irep2 = compiler2.Compile(@"
cmd :move, id: 'npc', x: 100, y: 200
cmd :test, value: 99
");

        await Task.WhenAll(
            mrb.ExecuteAsync(router, irep1).AsTask(),
            mrb2.ExecuteAsync(router, irep2).AsTask()
        );

        var received = recorder.Received;
        Assert.That(received, Has.Count.EqualTo(5));

        var testValues = received.OfType<TestCommand>().Select(c => c.Value).OrderBy(v => v).ToList();
        Assert.That(testValues, Is.EqualTo(new[] { 1, 2, 3, 99 }));

        var moves = received.OfType<MoveCommand>().ToList();
        Assert.That(moves, Has.Count.EqualTo(1));
        Assert.That(moves[0].Id, Is.EqualTo("npc"));
        Assert.That(moves[0].X, Is.EqualTo(100));
        Assert.That(moves[0].Y, Is.EqualTo(200));
    }

    class ContextCaptureSubscriber(Action<PublishContext> onReceive) : IAsyncCommandSubscriber
    {
        public ValueTask ReceiveAsync<T>(T command, PublishContext context) where T : ICommand
        {
            onReceive(context);
            return default;
        }
    }

    class CancelOnFirstSubscriber(CancellationTokenSource cts) : IAsyncCommandSubscriber
    {
        public ValueTask ReceiveAsync<T>(T command, PublishContext context) where T : ICommand
        {
            cts.Cancel();
            return default;
        }
    }
}
